using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Application.Services;
using Microsoft.Extensions.Logging;

namespace Jibo.Cloud.Infrastructure.Nlu;

public sealed class JevNluClassifier(HttpClient http, JevNluOptions options,
    ILogger<JevNluClassifier> logger, ITransportMetrics? metrics = null) : INluClassifier
{
    private readonly bool _isConfigured = CheckConfiguration(options, logger);

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
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(options.TimeoutMs);
            using var request = new HttpRequestMessage(HttpMethod.Post, options.Endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
            request.Content = JsonContent.Create(new
            {
                model = options.Model,
                state = new { transcript },
                questions = JevIntentQuestions.Questions
            });
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (!response.IsSuccessStatusCode)
            {
                outcome = "http_error";
                await LogRejectedRequestAsync(response, transcript, deadline.Token);
                return null;
            }
            using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: deadline.Token);
            var root = json.RootElement;
            if (!root.TryGetProperty("answers", out var answers) || answers.ValueKind != JsonValueKind.Object ||
                !TryReadChoice(answers, JevIntentQuestions.GroupQuestion, JevIntentQuestions.GroupCriteria,
                    out var group, out var groupProbability))
            { outcome = "malformed"; return null; }
            if (group == "unknown") { outcome = "unknown"; return null; }
            if (groupProbability < options.MinProbability) { outcome = "low_probability"; return null; }
            if (!JevIntentQuestions.Groups.TryGetValue(group, out var criteria) ||
                !TryReadChoice(answers, group, criteria, out var intent, out var intentProbability))
            { outcome = "malformed"; return null; }
            if (!NluIntentCatalog.IsSupported(intent)) { outcome = "unknown"; return null; }
            // Conservative routing score: confidence must survive both decisions.
            // This product is an acceptance score, not a calibrated probability.
            var selected = groupProbability * intentProbability;
            if (selected < options.MinProbability) { outcome = "low_probability"; return null; }
            var model = root.TryGetProperty("model", out var modelElement) && modelElement.ValueKind == JsonValueKind.String
                ? modelElement.GetString()! : options.Model;
            outcome = "classified";
            logger.LogDebug("Jev classified intent={Intent}, model={Model}, probability={Probability}", intent, model, selected);
            return new NluClassification(intent, selected, "jev", model);
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
