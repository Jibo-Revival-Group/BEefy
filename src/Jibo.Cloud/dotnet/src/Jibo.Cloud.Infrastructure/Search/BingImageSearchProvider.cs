using System.Net;
using System.Text.RegularExpressions;
using Jibo.Cloud.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace Jibo.Cloud.Infrastructure.Search;

/// <summary>
/// "Show me a picture of ___": the top Bing Images result with SafeSearch on Strict. Google Images only answers
/// browsers that run JavaScript, and its search API takes no new customers. The picture is Bing's own thumbnail
/// copy, cropped to the 1280x720 face: plain http and CORS-open, which the robot's old browser needs.
/// </summary>
public sealed class BingImageSearchProvider(HttpClient httpClient, ILogger<BingImageSearchProvider> logger) : IImageSearchProvider
{
    private static readonly Regex Thumbnail = new("&quot;turl&quot;:&quot;https?://([a-z0-9]+\\.mm\\.bing\\.net)/th\\?id=([A-Za-z0-9._-]+)",
        RegexOptions.CultureInvariant);

    public async Task<string?> FindAsync(string query, CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                "https://www.bing.com/images/search?adlt=strict&form=HDRSC2&first=1&q=" + Uri.EscapeDataString(query));
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
            request.Headers.AcceptLanguage.ParseAdd("en-US");
            // SafeSearch Strict is both the adlt parameter and this cookie, so neither alone can be dropped.
            request.Headers.Add("Cookie", "SRCHHPGUSR=ADLT=STRICT");
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;
            return Pick(await response.Content.ReadAsStringAsync(cancellationToken));
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Image search failed");
            return null;
        }
    }

    internal static string? Pick(string html) => Thumbnail.Match(html) is { Success: true } match
        ? $"http://{match.Groups[1].Value}/th?id={WebUtility.UrlEncode(match.Groups[2].Value)}&w=1280&h=720&c=7&pid=1.7"
        : null;
}
