using System.Collections.Concurrent;
using System.Net;
using Jibo.Cloud.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace Jibo.Cloud.Infrastructure.Search;

public sealed class WolframAlphaSearchProvider(
    HttpClient httpClient,
    SearchBackendOptions options,
    ILogger<WolframAlphaSearchProvider> logger)
    : IKnowledgeSearchProvider
{
    private static readonly string[] FailurePhrases =
    [
        "did not understand",
        "cannot answer",
        "can't answer",
        "no spoken result available",
        "no short answer available"
    ];

    private readonly ConcurrentDictionary<string, CacheEntry> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    public SearchBackendKind Kind => SearchBackendKind.Wolfram;

    public async Task<KnowledgeSearchResult?> SearchAsync(
        KnowledgeSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.BackendSpec.Kind != SearchBackendKind.Wolfram ||
            string.IsNullOrWhiteSpace(request.BackendSpec.Credential) ||
            string.IsNullOrWhiteSpace(request.Query))
            return null;

        var normalizedQuery = request.Query.Trim();
        var cacheKey = BuildCacheKey(request.BackendSpec, normalizedQuery + "|" + request.LatLong);
        if (TryGetCachedValue(cacheKey, out var cachedResult))
            return cachedResult;

        var result = await AskSpokenAsync(request.BackendSpec.Credential, normalizedQuery, request.LatLong, cancellationToken);
        // Wolfram often has no answer for chatty phrasing ("what's the perfect temperature to cook chicken").
        var simplified = SimplifyQuery(normalizedQuery);
        if (result.Outcome == KnowledgeSearchOutcome.NotFound && simplified != normalizedQuery)
            result = await AskSpokenAsync(request.BackendSpec.Credential, simplified, request.LatLong, cancellationToken);
        if (result.Outcome == KnowledgeSearchOutcome.NotFound)
            result = await AskResultTextAsync(request.BackendSpec.Credential, simplified, request.LatLong, cancellationToken) ?? result;

        SetCachedValue(cacheKey, result,
            result.Outcome == KnowledgeSearchOutcome.Found ? options.CacheTtlSeconds : options.FailureCacheTtlSeconds);
        return result;
    }

    private static readonly string[] FillerPhrases =
    [
        "do you know", "can you tell me", "could you tell me", "tell me", "i want to know", "i wonder",
        "have you ever heard of", "have you heard of", "away from me", "from me", "from here", "perfect", "please", "exactly"
    ];

    internal static string SimplifyQuery(string query)
    {
        var text = " " + query.ToLowerInvariant().Trim().TrimEnd('?', '.', '!') + " ";
        foreach (var phrase in FillerPhrases)
            text = text.Replace(" " + phrase + " ", " ", StringComparison.Ordinal);
        return string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private async Task<KnowledgeSearchResult> AskSpokenAsync(string appId, string query, string? latLong, CancellationToken cancellationToken)
    {
        try
        {
            var requestUri = BuildRequestUri(appId, query, latLong);
            using var response = await httpClient.GetAsync(requestUri, cancellationToken);
            var answerText = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();
            if (!response.IsSuccessStatusCode)
                return ClassifyHttpFailure(response.StatusCode, answerText);

            return IsUsableAnswer(answerText)
                ? new KnowledgeSearchResult(answerText, SearchBackendKind.Wolfram)
                : KnowledgeSearchResult.NotFound(SearchBackendKind.Wolfram);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Wolfram Alpha lookup failed.");
            return KnowledgeSearchResult.Unavailable(SearchBackendKind.Wolfram);
        }
    }

    // Wolfram's text (LLM) API answers some questions the Spoken API skips (e.g. "when did the detroit lions play").
    // Only its "Result:" section is used, spoken as "<input interpretation> is <result>".
    private async Task<KnowledgeSearchResult?> AskResultTextAsync(string appId, string query, string? latLong, CancellationToken cancellationToken)
    {
        try
        {
            var uri = $"https://www.wolframalpha.com/api/v1/llm-api?appid={Uri.EscapeDataString(appId)}&maxchars=800&input={Uri.EscapeDataString(query)}"
                      + (latLong is null ? "" : $"&latlong={Uri.EscapeDataString(latLong)}");
            using var response = await httpClient.GetAsync(uri, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;
            var answer = ReadResultSection(await response.Content.ReadAsStringAsync(cancellationToken));
            return answer is null ? null : new KnowledgeSearchResult(answer, SearchBackendKind.Wolfram);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Wolfram Alpha text lookup failed.");
            return null;
        }
    }

    internal static string? ReadResultSection(string text)
    {
        string? Section(string name)
        {
            var lines = text.Replace("\r", "").Split('\n');
            var start = Array.FindIndex(lines, line => line.Trim() == name + ":");
            if (start < 0) return null;
            var value = string.Join(' ', lines.Skip(start + 1).TakeWhile(line => line.Trim().Length > 0).Select(line => line.Trim()));
            return value.Length == 0 ? null : value;
        }

        var result = Section("Result");
        if (result is null || result.Contains("http", StringComparison.OrdinalIgnoreCase)) return null;
        var input = Section("Input interpretation");
        var answer = input is null ? result : $"{input.Replace(" | ", " ")} is {result.Replace(" | ", ", ")}";
        // Notes like "(NFL team)" read badly aloud.
        answer = System.Text.RegularExpressions.Regex.Replace(answer, @"\s*\((?:[^()]|\([^()]*\))*\)", "").Trim();
        return answer.Length is 0 or > 400 ? null : answer + ".";
    }

    private static KnowledgeSearchResult ClassifyHttpFailure(HttpStatusCode statusCode, string responseBody)
    {
        // Spoken/Short Answers APIs return 501 when the service is up but has no spoken/short answer.
        if (statusCode == HttpStatusCode.NotImplemented || LooksLikeNoAnswer(responseBody))
            return KnowledgeSearchResult.NotFound(SearchBackendKind.Wolfram);

        return KnowledgeSearchResult.Unavailable(SearchBackendKind.Wolfram);
    }

    private static bool LooksLikeNoAnswer(string responseBody) =>
        !string.IsNullOrWhiteSpace(responseBody) && !IsUsableAnswer(responseBody);

    private static Uri BuildRequestUri(string appId, string query, string? latLong = null)
    {
        var endpoint = SearchBackendSettingsResolver.DefaultWolframEndpoint;
        var builder = new UriBuilder(endpoint);
        // Without latlong Wolfram uses the caller's IP: the cloud server, not the robot's home.
        var queryString = $"appid={Uri.EscapeDataString(appId)}&i={Uri.EscapeDataString(query)}" +
                          (latLong is null ? "" : $"&latlong={Uri.EscapeDataString(latLong)}");
        builder.Query = queryString;
        return builder.Uri;
    }

    private static bool IsUsableAnswer(string answerText)
    {
        if (string.IsNullOrWhiteSpace(answerText)) return false;

        var lowered = answerText.ToLowerInvariant();
        return !FailurePhrases.Any(phrase => lowered.Contains(phrase, StringComparison.Ordinal));
    }

    private static string BuildCacheKey(SearchBackendSpec spec, string query)
    {
        return $"wolfram|{spec.Credential}|{query}";
    }

    private bool TryGetCachedValue(string cacheKey, out KnowledgeSearchResult? result)
    {
        result = null;
        if (!_cache.TryGetValue(cacheKey, out var entry) || entry.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            return false;

        result = entry.Result;
        return true;
    }

    private void SetCachedValue(string cacheKey, KnowledgeSearchResult? result, int ttlSeconds)
    {
        _cache[cacheKey] = new CacheEntry(
            result,
            DateTimeOffset.UtcNow.AddSeconds(Math.Max(1, ttlSeconds)));
    }

    private sealed record CacheEntry(KnowledgeSearchResult? Result, DateTimeOffset ExpiresAtUtc);
}