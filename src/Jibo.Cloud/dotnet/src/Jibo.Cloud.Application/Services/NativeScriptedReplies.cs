using Jibo.Cloud.Application.Abstractions;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jibo.Runtime.Abstractions;

namespace Jibo.Cloud.Application.Services;

internal static class NativeScriptedReplies
{
    private static readonly Lazy<IReadOnlyDictionary<string, JsonElement>> Mims = new(() =>
        NativeConversationResources.Instance.Files.Where(p => p.Key.StartsWith("packages/skills/resources/mims/chitchat/", StringComparison.Ordinal)
            && p.Key.EndsWith(".mim", StringComparison.Ordinal)).ToDictionary(p => Path.GetFileNameWithoutExtension(p.Key), p =>
            { using var json = JsonDocument.Parse(p.Value); return json.RootElement.Clone(); }, StringComparer.Ordinal));

    internal static JiboInteractionDecision Build(NativeCommandRegistration entry, NativeParseResult parsed,
        TurnContext turn, JiboExperienceCatalog catalog, IJiboRandomizer randomizer, string? speaker, DateTimeOffset? localTime)
    {
        var id = entry.Memo.ValueKind == JsonValueKind.Object && entry.Memo.TryGetProperty("mim", out var mim) ? mim.GetString() : null;
        if (id is not null && !Mims.Value.ContainsKey(id))
        {
            var candidates = Mims.Value.Keys.Where(k => k.StartsWith(id + "_", StringComparison.Ordinal)).ToArray();
            var categories = NativeConversationResources.Instance.Files.Where(p => p.Key.Contains("/semi_specific_categories/", StringComparison.Ordinal) && p.Key.EndsWith(".csv", StringComparison.Ordinal))
                .Where(p => p.Value.Split('\n').Skip(1).Select(line => line.Split(',')[0].Trim().Trim('"'))
                    .Any(value => parsed.Entities.Values.Any(entity => entity?.ToString() == value)))
                .Select(p => id + "_" + Path.GetFileNameWithoutExtension(p.Key)).Where(candidates.Contains).ToArray();
            id = categories.Length > 0 ? randomizer.Choose(categories) : "CC_Fallback";
        }
        if (id is null || !Mims.Value.TryGetValue(id, out var document))
            return ChitchatStateMachine.BuildNotUnderstoodDecision(turn.NormalizedTranscript ?? turn.RawTranscript ?? "");
        var time = localTime ?? DateTimeOffset.Now;
        var context = LegacyMimScriptedReplyBuilder.BuildScriptedContext(localTime) with { HasSpeaker = !string.IsNullOrWhiteSpace(speaker) };
        var diceA = 1 + (int)(Math.Min(0.999999, randomizer.NextUnitInterval()) * 6);
        var diceB = 1 + (int)(Math.Min(0.999999, randomizer.NextUnitInterval()) * 6);
        var values = parsed.Entities.ToDictionary(p => p.Key, p => p.Value?.ToString() ?? "", StringComparer.OrdinalIgnoreCase);
        values["speaker"] = speaker ?? "";
        values["speaker.firstName"] = speaker ?? "";
        values["referent"] = parsed.Entities.GetValueOrDefault("GivenName")?.ToString() ?? speaker ?? "";
        values["dt.day"] = time.DayOfWeek.ToString(); values["dt.date"] = time.ToString("MMMM d");
        values["dt.dayOfYear"] = time.DayOfYear.ToString();
        values["skill.dice.a"] = diceA.ToString(); values["skill.dice.b"] = diceB.ToString();
        values["skill.dice.a + skill.dice.b"] = (diceA + diceB).ToString();
        values["skill.coin.a"] = randomizer.NextUnitInterval() < .5 ? "heads" : "tails";
        try { NativePromptContext.Populate(turn, values); }
        catch (JsonException) { /* An absent or malformed runtime context cannot prevent a generic response. */ }
        context = context with { Emotion = values.GetValueOrDefault("jibo.emotion")
            ?? NativeConversationValue.Read(turn.Attributes, ChitchatStateMachine.EmotionMetadataKey)?.ToString() };
        var choices = new List<(string Esml, string? PromptId, string? Rule)>();
        foreach (var prompt in document.GetProperty("prompts").EnumerateArray())
        {
            var condition = prompt.TryGetProperty("condition", out var c) ? c.GetString() : null;
            if (!NativePromptContext.Matches(condition, values, context)) continue;
            var source = prompt.GetProperty("prompt").GetString() ?? "";
            var unresolved = false;
            var esml = Regex.Replace(source, @"\$\{([^}]+)\}", match =>
            {
                if (values.TryGetValue(match.Groups[1].Value.Trim(), out var value) && value.Length > 0)
                    return WebUtility.HtmlEncode(value);
                unresolved = true; return "";
            });
            if (unresolved) continue;
            choices.Add((esml, prompt.TryGetProperty("prompt_id", out var promptId) ? promptId.GetString() : null,
                document.TryGetProperty("rule_name", out var rule) ? rule.GetString() : null));
        }
        if (choices.Count == 0)
        {
            if (id == "CC_Fallback") return ChitchatStateMachine.BuildNotUnderstoodDecision(turn.NormalizedTranscript ?? turn.RawTranscript ?? "");
            using var fallback = JsonDocument.Parse("{\"mim\":\"CC_Fallback\"}");
            return Build(entry with { Memo = fallback.RootElement.Clone() }, parsed, turn, catalog, randomizer, speaker, localTime);
        }
        var selected = randomizer.Choose(choices);
        var reply = WebUtility.HtmlDecode(Regex.Replace(selected.Esml, "<[^>]*>", " ")).Trim();
        // Some performances contain animation only. Keep a non-empty speak action so the ESML reaches Nimbus.
        if (reply.Length == 0) reply = " ";
        var payload = new Dictionary<string, object?>
        {
            ["skillId"] = "chitchat-skill", ["nativeCloudDialog"] = true, ["mim_id"] = id, ["prompt_id"] = selected.PromptId,
            ["esml"] = selected.Esml.StartsWith("<speak", StringComparison.Ordinal) ? selected.Esml : "<speak>" + selected.Esml + "</speak>"
        };
        var updates = ScriptedResponseDecisionBuilder.BuildScriptedResponseContextUpdates();
        if (document.TryGetProperty("mim_type", out var type) && type.GetString() == "question" && !string.IsNullOrWhiteSpace(selected.Rule))
            payload["listen_contexts"] = new[] { selected.Rule! };
        return new(parsed.Intent, reply, "chitchat-skill", payload, updates);
    }
}
