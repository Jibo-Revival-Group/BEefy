using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jibo.Cloud.Application.Services;
using Microsoft.Extensions.Http;

namespace Jibo.Cloud.Api.Hosting;

internal sealed partial class PhoenixConversationClient(
    IHttpClientFactory httpClientFactory,
    ILogger<PhoenixConversationClient> logger) : IPhoenixConversationClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<JiboInteractionDecision?> TryDecideAsync(string transcript, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(transcript))
            return null;

        try
        {
            var http = httpClientFactory.CreateClient("PhoenixConversation");
            using var parseResponse = await http.PostAsJsonAsync(
                $"{NluBase()}/v1/parse",
                new
                {
                    type = "NLU",
                    data = new { text = transcript }
                },
                JsonOptions,
                cancellationToken);
            if (!parseResponse.IsSuccessStatusCode)
                return null;

            using var parseJson = JsonDocument.Parse(await parseResponse.Content.ReadAsStringAsync(cancellationToken));
            if (!TryReadIntent(parseJson.RootElement, out var intent, out var skillEntity))
                return null;

            var skillId = MapCloudSkill(intent, skillEntity);
            if (skillId is null)
                return null;

            using var skillResponse = await http.PostAsJsonAsync(
                $"{SkillsBase()}/v1/{skillId}/main",
                SkillRequest(skillId, intent, transcript),
                JsonOptions,
                cancellationToken);
            if (!skillResponse.IsSuccessStatusCode)
                return null;

            var body = await skillResponse.Content.ReadAsStringAsync(cancellationToken);
            var spoken = ExtractSpokenText(body);
            if (string.IsNullOrWhiteSpace(spoken))
                return null;

            return new JiboInteractionDecision(
                intent,
                spoken,
                skillId,
                new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["cloudSkill"] = skillId,
                    ["skillId"] = skillId
                });
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or ArgumentException)
        {
            logger.LogDebug(exception, "Phoenix conversation path missed; using the current dispatcher");
            return null;
        }
    }

    private static bool TryReadIntent(JsonElement root, out string intent, out string? skillEntity)
    {
        intent = string.Empty;
        skillEntity = null;
        if (!root.TryGetProperty("data", out var data))
            return false;
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("data", out var nested) &&
            nested.ValueKind == JsonValueKind.Object)
            data = nested;
        if (!data.TryGetProperty("intent", out var intentElement) || intentElement.ValueKind != JsonValueKind.String)
            return false;
        intent = intentElement.GetString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(intent))
            return false;
        if (data.TryGetProperty("entities", out var entities) &&
            entities.ValueKind == JsonValueKind.Object &&
            entities.TryGetProperty("skill", out var skill) &&
            skill.ValueKind == JsonValueKind.String)
            skillEntity = skill.GetString();
        return true;
    }

    internal static string? MapCloudSkill(string intent, string? skillEntity)
    {
        var skill = skillEntity ?? string.Empty;
        if (skill.StartsWith("@be/", StringComparison.OrdinalIgnoreCase) &&
            !skill.Contains("chitchat", StringComparison.OrdinalIgnoreCase))
            return null;

        if (skill.Contains("chitchat", StringComparison.OrdinalIgnoreCase))
            return "chitchat-skill";
        if (skill.Contains("report", StringComparison.OrdinalIgnoreCase) ||
            intent.Contains("Weather", StringComparison.OrdinalIgnoreCase) ||
            intent.Contains("PersonalReport", StringComparison.OrdinalIgnoreCase) ||
            intent.Contains("Calendar", StringComparison.OrdinalIgnoreCase) ||
            intent.Contains("Commute", StringComparison.OrdinalIgnoreCase))
            return "report-skill";
        if (skill.Contains("news", StringComparison.OrdinalIgnoreCase) ||
            intent.Contains("News", StringComparison.OrdinalIgnoreCase))
            return "news";
        if (skill.Contains("answer", StringComparison.OrdinalIgnoreCase) ||
            intent.StartsWith("general", StringComparison.OrdinalIgnoreCase) ||
            intent is "whoIsPerson" or "requestTellAboutThing" or "whatIsThing" or "whatDoesThingMean")
            return "answer-skill";
        if (skill.Contains("color", StringComparison.OrdinalIgnoreCase))
            return "color-skill";
        return null;
    }

    private static object SkillRequest(string skillId, string intent, string transcript) => new
    {
        type = "LISTEN_LAUNCH",
        msgID = "beefy",
        ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        data = new
        {
            general = new { accountID = "beefy", robotID = "beefy", lang = "en-US" },
            runtime = new
            {
                dialog = new { },
                perception = new { },
                loop = new { users = Array.Empty<object>() },
                location = new { lat = 42.36, lng = -71.06, iso = DateTimeOffset.Now.ToString("o") }
            },
            skill = new { id = skillId },
            result = new
            {
                nlu = new { intent, entities = new { }, rules = new[] { "launch" } },
                asr = new { text = transcript, confidence = 1 },
                memo = "Reactive"
            }
        }
    };

    private static string? ExtractSpokenText(string body)
    {
        var match = EsmlPattern().Match(body);
        if (!match.Success)
            return null;
        var raw = Regex.Unescape(match.Groups[1].Value);
        var plain = TagPattern().Replace(raw, " ");
        plain = System.Net.WebUtility.HtmlDecode(plain);
        plain = WhitespacePattern().Replace(plain, " ").Trim();
        return string.IsNullOrWhiteSpace(plain) ? null : plain;
    }

    private static string NluBase() =>
        Environment.GetEnvironmentVariable("BEEFY_PHOENIX_NLU_URL") ?? "http://127.0.0.1:24701";

    private static string SkillsBase() =>
        Environment.GetEnvironmentVariable("BEEFY_PHOENIX_SKILLS_URL") ?? "http://127.0.0.1:24702";

    [GeneratedRegex("\"esml\"\\s*:\\s*\"((?:\\\\.|[^\"\\\\])*)\"", RegexOptions.CultureInvariant)]
    private static partial Regex EsmlPattern();

    [GeneratedRegex("<[^>]+>", RegexOptions.CultureInvariant)]
    private static partial Regex TagPattern();

    [GeneratedRegex("\\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespacePattern();
}
