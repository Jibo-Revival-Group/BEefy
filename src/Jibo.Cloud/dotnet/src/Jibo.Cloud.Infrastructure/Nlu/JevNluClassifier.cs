using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Application.Services;
using Microsoft.Extensions.Logging;

namespace Jibo.Cloud.Infrastructure.Nlu;

public sealed class JevNluClassifier(HttpClient http, JevNluOptions options,
    ILogger<JevNluClassifier> logger, ITransportMetrics? metrics = null,
    TimeProvider? timeProvider = null) : INluClassifier
{
    private readonly bool _isConfigured = CheckConfiguration(options, logger);
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(6);
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, CachedClassification> _cache = new(StringComparer.Ordinal);
    private sealed record CachedClassification(NluClassification? Classification, long CreatedAt);

    private bool TryGetCached(string transcript, out NluClassification? classification)
    {
        lock (_cacheLock)
        {
            // Also discard expired phrases that are never heard again.
            var now = _clock.GetTimestamp();
            foreach (var key in _cache.Where(entry =>
                _clock.GetElapsedTime(entry.Value.CreatedAt, now) >= CacheLifetime).Select(entry => entry.Key).ToArray())
                _cache.Remove(key);
            if (_cache.TryGetValue(transcript, out var cached))
            {
                classification = cached.Classification;
                return true;
            }
        }
        classification = null;
        return false;
    }

    private void Cache(string transcript, NluClassification? classification)
    {
        lock (_cacheLock)
            _cache[transcript] = new(classification, _clock.GetTimestamp());
    }

    private static bool CheckConfiguration(JevNluOptions options, ILogger<JevNluClassifier> logger)
    {
        if (options.Enabled && !options.IsConfigured)
            logger.LogWarning("Jev NLU is enabled but its endpoint, key, model, timeout or probability setting is missing or invalid; using local NLU.");
        return options.IsConfigured;
    }

    public async Task<NluClassification?> ClassifyAsync(string transcript, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_isConfigured || string.IsNullOrWhiteSpace(transcript)) return null;
        var start = Stopwatch.GetTimestamp();
        var outcome = "failure";
        try
        {
            if (TryGetCached(transcript, out var cached))
            {
                outcome = "cache_hit";
                return cached;
            }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(options.TimeoutMs);
            var batches = BuildQuestionBatches(transcript);
            if (batches is null) { outcome = "input_too_large"; return null; }
            var results = await Task.WhenAll(batches.Select(batch => EvaluateBatchAsync(transcript, batch, deadline.Token)));
            var failure = results.FirstOrDefault(result => result.Error is not null);
            if (failure?.Error is not null) { outcome = failure.Error; return null; }
            var best = results.SelectMany(result => result.Candidates)
                .OrderByDescending(candidate => candidate.Probability)
                .ThenBy(candidate => candidate.Intent, StringComparer.Ordinal).FirstOrDefault();
            if (best is null)
            {
                outcome = "unknown";
                // Unknown is a no-match in the existing routing contract. Only reuse it
                // when every leaf group explicitly rules out a match above 95%.
                if (results.All(result => result.ConfidentUnknown)) Cache(transcript, null);
                return null;
            }
            if (best.Probability < options.MinProbability) { outcome = "low_probability"; return null; }
            var intent = best.Intent;
            var selected = best.Probability;
            var model = best.Model;
            outcome = "classified";
            logger.LogDebug("Jev classified intent={Intent}, model={Model}, probability={Probability}", intent, model, selected);
            var classification = new NluClassification(intent, selected, "jev", model);
            Cache(transcript, classification);
            return classification;
        }
        catch (OperationCanceledException)
        {
            outcome = cancellationToken.IsCancellationRequested ? "canceled" : "timeout";
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidOperationException or ArgumentException)
        {
            // Do not log exception bodies: remote messages may echo credentials or speech.
            outcome = "failure";
            return null;
        }
        finally
        {
            var duration = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            (metrics ?? NullTransportMetrics.Instance).TurnPhaseCompleted("nlu", outcome, duration);
            logger.LogDebug("Jev NLU outcome={Outcome}, durationMs={Duration}", outcome, duration);
        }
    }

    private sealed record BatchResult(IReadOnlyList<NluClassification> Candidates, string? Error = null,
        bool ConfidentUnknown = false);

    private async Task<BatchResult> EvaluateBatchAsync(string transcript,
        IReadOnlyDictionary<string, JevIntentQuestions.ChoiceQuestion> questions, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, options.Endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        request.Content = JsonContent.Create(new { model = options.Model, state = new { transcript }, questions });
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode)
        {
            await LogRejectedRequestAsync(response, transcript, token);
            return new([], "http_error");
        }
        using var stream = await response.Content.ReadAsStreamAsync(token);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: token);
        var root = json.RootElement;
        if (!root.TryGetProperty("answers", out var answers) || answers.ValueKind != JsonValueKind.Object)
            return new([], "malformed");
        var model = root.TryGetProperty("model", out var modelElement) && modelElement.ValueKind == JsonValueKind.String
            ? modelElement.GetString()! : options.Model;
        var candidates = new List<NluClassification>();
        var confidentUnknown = true;
        foreach (var (name, question) in questions)
        {
            // The top-level selector is advisory. Its score never gates a leaf match.
            if (name == JevIntentQuestions.GroupQuestion) continue;
            if (!TryReadChoice(answers, name, question.Criteria, out var intent, out var probability))
            {
                confidentUnknown = false;
                continue;
            }
            confidentUnknown &= intent == "unknown" && probability > 0.95;
            if (intent != "unknown" && NluIntentCatalog.IsSupported(intent))
                candidates.Add(new(intent, probability, "jev", model));
        }
        return new(candidates, ConfidentUnknown: confidentUnknown);
    }

    internal static IReadOnlyList<IReadOnlyDictionary<string, JevIntentQuestions.ChoiceQuestion>>? BuildQuestionBatches(string transcript)
    {
        var batches = new List<IReadOnlyDictionary<string, JevIntentQuestions.ChoiceQuestion>>();
        var batch = new Dictionary<string, JevIntentQuestions.ChoiceQuestion>(StringComparer.Ordinal);
        foreach (var (name, question) in JevIntentQuestions.Questions)
        {
            batch.Add(name, question);
            if (FitsContextBudgets(transcript, batch)) continue;
            batch.Remove(name);
            if (batch.Count > 0) batches.Add(batch);
            batch = new(StringComparer.Ordinal) { [name] = question };
            if (!FitsContextBudgets(transcript, batch)) return null;
        }
        if (batch.Count > 0) batches.Add(batch);
        return batches;
    }

    internal static bool FitsContextBudgets(string transcript,
        IReadOnlyDictionary<string, JevIntentQuestions.ChoiceQuestion> questions)
    {
        // UTF-8 byte counts conservatively upper-bound text tokens. This avoids
        // depending on a tokenizer and stays within TypeSafe's 32k/64k limits.
        var stateBytes = JsonSerializer.SerializeToUtf8Bytes(new { transcript }).Length;
        var questionBytes = questions.Values.Select(question => JsonSerializer.SerializeToUtf8Bytes(question).Length).ToArray();
        return questionBytes.All(bytes => stateBytes + bytes < 32_000) &&
            stateBytes + questionBytes.Sum() < 64_000;
    }

    private static bool TryReadChoice(JsonElement answers, string question,
        IReadOnlyDictionary<string, string> criteria, out string choice, out double selected)
    {
        choice = string.Empty;
        selected = 0;
        if (!answers.TryGetProperty(question, out var answer) || answer.ValueKind != JsonValueKind.Object ||
            !answer.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "choice" ||
            !answer.TryGetProperty("choice", out var chosen) || chosen.ValueKind != JsonValueKind.String ||
            !answer.TryGetProperty("probabilities", out var probabilities) || probabilities.ValueKind != JsonValueKind.Object)
            return false;
        choice = chosen.GetString()!;
        if (!criteria.ContainsKey(choice) || !probabilities.TryGetProperty(choice, out var probability) ||
            probability.ValueKind != JsonValueKind.Number || !probability.TryGetDouble(out selected) ||
            !double.IsFinite(selected) || selected is < 0 or > 1)
            return false;
        foreach (var item in probabilities.EnumerateObject())
            if (!criteria.ContainsKey(item.Name) || item.Value.ValueKind != JsonValueKind.Number ||
                !item.Value.TryGetDouble(out var value) || !double.IsFinite(value) || value is < 0 or > 1 || value > selected)
                return false;
        return true;
    }

    private async Task LogRejectedRequestAsync(HttpResponseMessage response, string transcript, CancellationToken token)
    {
        // Read only a bounded error body. Gateway validation details are useful,
        // but a gateway may echo speech or authorization data in its message.
        using var stream = await response.Content.ReadAsStreamAsync(token);
        var buffer = new byte[8193];
        var count = 0;
        while (count < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(count), token);
            if (read == 0) break;
            count += read;
        }
        var detail = "No structured error details returned";
        if (count is > 0 and <= 8192)
        {
            try
            {
                using var json = JsonDocument.Parse(buffer.AsMemory(0, count));
                if (json.RootElement.ValueKind == JsonValueKind.Object &&
                    json.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object &&
                    error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
                {
                    detail = (message.GetString() ?? detail)
                        .Replace(options.ApiKey, "[redacted]", StringComparison.Ordinal)
                        .Replace(transcript, "[redacted transcript]", StringComparison.Ordinal);
                    detail = System.Text.RegularExpressions.Regex.Replace(detail,
                        @"(?i)\b(?:sk[-_][a-z0-9_-]+|bearer\s+\S+)", "[redacted]");
                    detail = string.Concat(detail.Select(c => char.IsControl(c) ? ' ' : c));
                    if (detail.Length > 512) detail = detail[..512];
                }
            }
            catch (JsonException) { /* Do not log HTML or unstructured bodies. */ }
        }
        logger.LogWarning("Jev request rejected: HTTP {Status}, model={Model}, largestChoice={Options}. {Detail}",
            (int)response.StatusCode, options.Model, JevIntentQuestions.Questions.Values.Max(q => q.Criteria.Count), detail);
    }

}
