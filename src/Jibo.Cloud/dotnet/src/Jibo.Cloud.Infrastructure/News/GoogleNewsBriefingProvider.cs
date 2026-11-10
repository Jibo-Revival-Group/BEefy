using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Infrastructure.Caching;
using Microsoft.Extensions.Logging;

namespace Jibo.Cloud.Infrastructure.News;

/// <summary>
/// Free, keyless Google News topic RSS feeds. A briefing takes the top headline of each category
/// in turn (then the next one of each, if more headlines are wanted than there are categories).
/// </summary>
public sealed class GoogleNewsBriefingProvider(
    HttpClient httpClient,
    GoogleNewsOptions options,
    ILogger<GoogleNewsBriefingProvider> logger)
    : INewsBriefingProvider
{
    private const int MaxHeadlines = 5;
    private const string SourceName = "Google News";

    private static readonly Regex UnsafeHeadlineTermPattern = new(
        @"\b(?:murder|homicide|suicide|porn|pornography|sex\s+crime|sexual\s+assault|graphic\s+violence|beheading|massacre)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // "general" is the Top stories feed; the rest are Google News topic sections.
    private static readonly HashSet<string> SupportedCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "general", "world", "nation", "business", "technology", "entertainment", "science", "sports", "health"
    };

    private readonly ConcurrentDictionary<string, (NewsBriefingSnapshot Snapshot, DateTimeOffset ExpiresUtc)> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    public async Task<NewsBriefingSnapshot?> GetBriefingAsync(
        NewsBriefingRequest request,
        CancellationToken cancellationToken = default)
    {
        var categories = ResolveCategories(request.PreferredCategories);
        var count = Math.Clamp(request.MaxHeadlines, 1, MaxHeadlines);
        var cacheKey = $"{count}|{string.Join(",", categories)}";
        if (_cache.TryGetValue(cacheKey, out var cached) && cached.ExpiresUtc > DateTimeOffset.UtcNow)
            return cached.Snapshot;

        var feeds = new List<IReadOnlyList<NewsHeadline>>();
        string? failureStatus = null, failureMessage = null, failureEndpoint = null;
        int? failureStatusCode = null;
        foreach (var category in categories)
        {
            var uri = BuildFeedUri(category);
            try
            {
                using var message = new HttpRequestMessage(HttpMethod.Get, uri);
                message.Headers.TryAddWithoutValidation("User-Agent", options.UserAgent);
                using var response = await httpClient.SendAsync(message, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    failureStatus ??= "http_error";
                    failureMessage ??= $"Category '{category}' returned {(int)response.StatusCode} {response.ReasonPhrase}.";
                    failureStatusCode ??= (int)response.StatusCode;
                    failureEndpoint ??= uri.ToString();
                    logger.LogWarning("Google News feed failed. Category={Category} StatusCode={StatusCode}",
                        category, (int)response.StatusCode);
                    continue;
                }

                var document = XDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                feeds.Add(ReadHeadlines(document, category));
            }
            catch (Exception exception) when (exception is not OperationCanceledException ||
                                              !cancellationToken.IsCancellationRequested)
            {
                failureStatus ??= exception is System.Xml.XmlException ? "schema_error" : "exception";
                failureMessage ??= exception.Message;
                failureEndpoint ??= uri.ToString();
                logger.LogWarning(exception, "Google News feed failed. Category={Category}", category);
            }
        }

        // Top headline of every category first, then each category's next one.
        var headlines = new List<NewsHeadline>(count);
        var seenTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var rank = 0; headlines.Count < count && feeds.Any(feed => rank < feed.Count); rank++)
            foreach (var feed in feeds)
                if (rank < feed.Count && headlines.Count < count && seenTitles.Add(feed[rank].Title))
                    headlines.Add(feed[rank]);

        var snapshot = headlines.Count > 0
            ? new NewsBriefingSnapshot(headlines, SourceName, "success")
            : new NewsBriefingSnapshot([], SourceName, failureStatus ?? "empty",
                failureMessage ?? "Google News returned no usable headlines.", failureStatusCode, failureEndpoint);
        var expiresUtc = headlines.Count > 0
            ? HalfHourAlignedCacheExpiry.GetExpiryUtc(DateTimeOffset.UtcNow)
            : DateTimeOffset.UtcNow.AddSeconds(Math.Max(1, options.FailureCacheTtlSeconds));
        _cache[cacheKey] = (snapshot, expiresUtc);
        logger.LogInformation("Google News briefing. Categories={Categories} HeadlineCount={HeadlineCount}",
            string.Join(",", categories), headlines.Count);
        return snapshot;
    }

    private string[] ResolveCategories(IReadOnlyList<string> preferredCategories)
    {
        static string[] Supported(IEnumerable<string> categories) => categories
            .Where(category => !string.IsNullOrWhiteSpace(category))
            .Select(category => category.Trim().ToLowerInvariant())
            .Where(SupportedCategories.Contains)
            .Distinct()
            .ToArray();

        var requested = Supported(preferredCategories);
        if (requested.Length > 0) return requested;

        var defaults = Supported(options.DefaultCategories);
        return defaults.Length > 0 ? defaults : ["general"];
    }

    private Uri BuildFeedUri(string category)
    {
        var edition = $"hl={Uri.EscapeDataString(options.Language)}&gl={Uri.EscapeDataString(options.Country)}" +
                      $"&ceid={Uri.EscapeDataString($"{options.Country}:{options.Language.Split('-')[0]}")}";
        var path = category == "general" ? "rss" : $"rss/headlines/section/topic/{category.ToUpperInvariant()}";
        return new Uri($"{options.BaseUrl.TrimEnd('/')}/{path}?{edition}");
    }

    private static List<NewsHeadline> ReadHeadlines(XDocument document, string category)
    {
        var headlines = new List<NewsHeadline>();
        foreach (var item in document.Descendants("item"))
        {
            var source = item.Element("source")?.Value.Trim();
            var title = NormalizeHeadlineTitle(item.Element("title")?.Value, source);
            if (title is null || !IsUsableHeadline(title)) continue;

            // The feed has no plain-text summary (its description is just the linked title), and
            // the news skill only speaks titles, so the title doubles as the summary.
            headlines.Add(new NewsHeadline(title, title, category, source, item.Element("link")?.Value.Trim()));
        }

        return headlines;
    }

    private static string? NormalizeHeadlineTitle(string? title, string? source)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;

        var trimmed = Regex.Replace(title, @"\s+", " ").Trim();
        if (!string.IsNullOrWhiteSpace(source) && trimmed.EndsWith($" - {source}", StringComparison.Ordinal))
            trimmed = trimmed[..^(source.Length + 3)].TrimEnd();

        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private static bool IsUsableHeadline(string title)
    {
        var lowered = title.ToLowerInvariant();
        if (lowered.StartsWith("correction:", StringComparison.Ordinal) ||
            lowered.StartsWith("corrected:", StringComparison.Ordinal) ||
            lowered.StartsWith("update:", StringComparison.Ordinal) ||
            lowered.StartsWith("updated:", StringComparison.Ordinal) ||
            lowered.Contains(" correction", StringComparison.Ordinal))
            return false;

        return !UnsafeHeadlineTermPattern.IsMatch(title);
    }
}
