using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Infrastructure.Caching;
using Microsoft.Extensions.Logging;

namespace Jibo.Cloud.Infrastructure.News;

/// <summary>
/// Free, keyless Google News topic RSS feeds, with each story's photo scraped from the topic's web page.
/// Like the stock report-skill: up to 5 categories, 3 stories for one category, 2 each for two, else 1
/// each (5 random ones when more are on), and only stories with a photo (unless none of a category's stories has one).
/// </summary>
public sealed class GoogleNewsBriefingProvider(
    HttpClient httpClient,
    GoogleNewsOptions options,
    ILogger<GoogleNewsBriefingProvider> logger)
    : INewsBriefingProvider
{
    private const int MaxCategories = 5;
    private const string SourceName = "Google News";
    private const string BrowserUserAgent =
        "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130 Safari/537.36";

    private static readonly Regex UnsafeHeadlineTermPattern = new(
        @"\b(?:murder|homicide|suicide|porn|pornography|sex\s+crime|sexual\s+assault|graphic\s+violence|beheading|massacre)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // shortcut: scrapes Google's page markup (a card's photo, then its "More - <title>" menu label);
    // upgrade to an image API if Google changes the markup and photos stop appearing.
    private static readonly Regex PhotoPattern = new(
        @"<img class=""Quavad[^""]*""[^>]*?\ssrc=""(/api/attachments/[^""]+)""",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex CardTitlePattern = new(
        @"aria-label=""More - ([^""]+)""",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // Each category's top 3 stories (with photos), so any mix of a person's topics reuses them.
    private readonly ConcurrentDictionary<string, (IReadOnlyList<NewsHeadline> Stories, DateTimeOffset ExpiresUtc)> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    public async Task<NewsBriefingSnapshot?> GetBriefingAsync(
        NewsBriefingRequest request,
        CancellationToken cancellationToken = default)
    {
        var categories = ResolveCategories(request.PreferredCategories);
        var perCategory = categories.Length switch { 1 => 3, 2 => 2, _ => 1 };
        var headlines = new List<NewsHeadline>();
        var seenTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? failureStatus = null, failureMessage = null, failureEndpoint = null;
        int? failureStatusCode = null;
        foreach (var category in categories)
        {
            if (_cache.TryGetValue(category, out var cached) && cached.ExpiresUtc > DateTimeOffset.UtcNow)
            {
                headlines.AddRange(cached.Stories.Where(story => seenTitles.Add(story.Title)).Take(perCategory));
                continue;
            }

            var uri = BuildFeedUri(category);
            try
            {
                using var response = await GetAsync(uri, options.UserAgent, cancellationToken);
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
                var feed = ReadHeadlines(document, category);
                var photos = await ReadPhotoPathsAsync(category, response.RequestMessage?.RequestUri ?? uri, cancellationToken);
                var withPhotos = feed.Where(headline => photos.ContainsKey(headline.Title)).ToList();
                var stories = new List<NewsHeadline>();
                foreach (var headline in (withPhotos.Count > 0 ? withPhotos : feed).Take(3))
                    stories.Add(photos.TryGetValue(headline.Title, out var path)
                        ? headline with { Image = await ReadImageAsync(path, cancellationToken) }
                        : headline);
                if (stories.Count > 0)
                    _cache[category] = (stories, HalfHourAlignedCacheExpiry.GetExpiryUtc(DateTimeOffset.UtcNow));
                headlines.AddRange(stories.Where(story => seenTitles.Add(story.Title)).Take(perCategory));
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

        var snapshot = headlines.Count > 0
            ? new NewsBriefingSnapshot(headlines, SourceName, "success")
            : new NewsBriefingSnapshot([], SourceName, failureStatus ?? "empty",
                failureMessage ?? "Google News returned no usable headlines.", failureStatusCode, failureEndpoint);
        logger.LogInformation(
            "Google News briefing. Categories={Categories} HeadlineCount={HeadlineCount} WithPhotos={WithPhotos}",
            string.Join(",", categories), headlines.Count, headlines.Count(headline => headline.Image is not null));
        return snapshot;
    }

    private string[] ResolveCategories(IReadOnlyList<string> preferredCategories)
    {
        static string[] Supported(IEnumerable<string> categories) => categories
            .Where(category => !string.IsNullOrWhiteSpace(category))
            .Select(category => category.Trim().ToLowerInvariant())
            .Where(NewsTopics.All.Contains)
            .Distinct()
            .ToArray();

        var chosen = Supported(preferredCategories);
        if (chosen.Length == 0) chosen = Supported(options.DefaultCategories);
        if (chosen.Length == 0) return ["general"];

        // Stock NewsMimLogic.trimCats: over 5 topics, drop random ones until 5 are left (order kept).
        var kept = chosen.ToList();
        while (kept.Count > MaxCategories) kept.RemoveAt(Random.Shared.Next(kept.Count));
        return [.. kept];
    }

    private Uri BuildFeedUri(string category)
    {
        var path = category == "general" ? "rss" : $"rss/headlines/section/topic/{category.ToUpperInvariant()}";
        return new Uri($"{options.BaseUrl.TrimEnd('/')}/{path}?{Edition}");
    }

    private string Edition =>
        $"hl={Uri.EscapeDataString(options.Language)}&gl={Uri.EscapeDataString(options.Country)}" +
        $"&ceid={Uri.EscapeDataString($"{options.Country}:{options.Language.Split('-')[0]}")}";

    private async Task<HttpResponseMessage> GetAsync(Uri uri, string userAgent, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, uri);
        message.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        return await httpClient.SendAsync(message, cancellationToken);
    }

    /// <summary>Title -> photo path, from the web page of the topic whose feed redirected to <paramref name="feedUri"/>.</summary>
    private async Task<Dictionary<string, string>> ReadPhotoPathsAsync(
        string category, Uri feedUri, CancellationToken cancellationToken)
    {
        var photos = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pageUri = category == "general"
            ? new Uri($"{options.BaseUrl.TrimEnd('/')}/topstories?{Edition}")
            : new Uri(feedUri.GetLeftPart(UriPartial.Path).Replace("/rss/topics/", "/topics/") + feedUri.Query);
        try
        {
            using var response = await GetAsync(pageUri, BrowserUserAgent, cancellationToken);
            if (!response.IsSuccessStatusCode) return photos;

            var page = await response.Content.ReadAsStringAsync(cancellationToken);
            foreach (Match photo in PhotoPattern.Matches(page))
            {
                var after = photo.Index + photo.Length;
                var title = CardTitlePattern.Match(page, after, Math.Min(6000, page.Length - after));
                if (title.Success)
                    photos.TryAdd(Regex.Replace(WebUtility.HtmlDecode(title.Groups[1].Value), @"\s+", " ").Trim(),
                        WebUtility.HtmlDecode(photo.Groups[1].Value).Split('=')[0]);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException ||
                                          !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Google News photos unavailable. Category={Category}", category);
        }

        return photos;
    }

    private async Task<NewsImage?> ReadImageAsync(string attachmentPath, CancellationToken cancellationToken)
    {
        try
        {
            // Redirects to the largest copy Google keeps (about 700x390), CORS-open for the robot.
            using var response = await GetAsync(
                new Uri($"{options.BaseUrl.TrimEnd('/')}{attachmentPath}=-w1280-h720-p-df"),
                options.UserAgent,
                cancellationToken);
            if (!response.IsSuccessStatusCode || response.RequestMessage?.RequestUri is not { } imageUri) return null;

            return ReadImageSize(await response.Content.ReadAsByteArrayAsync(cancellationToken)) is { } size
                ? new NewsImage(imageUri.ToString(), size.Width, size.Height)
                : null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException ||
                                          !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Google News photo failed. Path={Path}", attachmentPath);
            return null;
        }
    }

    /// <summary>Pixel size from a JPEG frame header or a PNG IHDR (the formats Google serves).</summary>
    internal static (int Width, int Height)? ReadImageSize(byte[] bytes)
    {
        if (bytes.Length > 24 && bytes[0] == 0x89 && bytes[1] == (byte)'P' && bytes[2] == (byte)'N' && bytes[3] == (byte)'G')
            return (bytes[16] << 24 | bytes[17] << 16 | bytes[18] << 8 | bytes[19],
                bytes[20] << 24 | bytes[21] << 16 | bytes[22] << 8 | bytes[23]);
        if (bytes.Length < 4 || bytes[0] != 0xFF || bytes[1] != 0xD8) return null;

        for (var i = 2; i + 9 < bytes.Length;)
        {
            if (bytes[i] != 0xFF) return null;
            var marker = bytes[i + 1];
            // SOF0-SOF15 carry the size; C4 (DHT), C8 (JPG) and CC (DAC) share the range but don't.
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                return (bytes[i + 7] << 8 | bytes[i + 8], bytes[i + 5] << 8 | bytes[i + 6]);
            i += 2 + (bytes[i + 2] << 8 | bytes[i + 3]);
        }

        return null;
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
