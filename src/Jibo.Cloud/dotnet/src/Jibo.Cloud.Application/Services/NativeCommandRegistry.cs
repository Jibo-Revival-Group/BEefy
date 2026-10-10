using System.Text.Json;

namespace Jibo.Cloud.Application.Services;

public sealed record NativeCommandRegistration(string Skill, string Intent, bool OnRobot,
    JsonElement Constraints, JsonElement Memo);

/// <summary>Executable skill dispatch table, shared by routing and coverage checks.</summary>
public sealed class NativeCommandRegistry
{
    public static NativeCommandRegistry Instance { get; } = new();
    public IReadOnlyList<NativeCommandRegistration> Commands { get; }
    public IReadOnlySet<string> LocalSkills { get; }
    public IReadOnlySet<string> CloudSkills { get; }
    public IReadOnlyDictionary<string, NativeCommandRegistration> ClassifierCommands { get; }
    public static bool RequiresCommandEntities(string intent) => intent is "lightsColor" or "lightsColorGroup"
        or "lightsGroupOn" or "lightsGroupOff" or "lightsGroupUp" or "lightsGroupDown" or "lightsGroupUpCompletely"
        or "lightsGroupWarm" or "lightsGroupCool" or "volumeToValue" or "set" or "start" or "edit" or "ifttt" or "whenIsBirthday" or "whenIsHoliday";
    private NativeCommandRegistry()
    {
        var commands = new List<NativeCommandRegistration>();
        foreach (var (path, source) in NativeConversationResources.Instance.Files.Where(p =>
            p.Key.StartsWith("packages/gateway/resources/skills/", StringComparison.Ordinal) && p.Key.EndsWith("_manifest.json", StringComparison.Ordinal)))
        {
            using var json = JsonDocument.Parse(source);
            var root = json.RootElement;
            var skill = root.GetProperty("id").GetString()!;
            var local = root.TryGetProperty("onRobot", out var onRobot) && onRobot.ValueKind == JsonValueKind.True;
            foreach (var intent in root.GetProperty("intents").EnumerateArray())
                commands.Add(new(skill, intent.GetProperty("name").GetString()!, local,
                    intent.TryGetProperty("entities", out var constraints) ? constraints.Clone() : default,
                    intent.TryGetProperty("memo", out var memo) ? memo.Clone() : default));
        }
        Commands = commands;
        ClassifierCommands = commands.Where(c => c.OnRobot && !RequiresCommandEntities(c.Intent)
            && (c.Constraints.ValueKind != JsonValueKind.Array || c.Constraints.EnumerateArray().All(e => e.GetProperty("name").GetString() == "skill")))
            .DistinctBy(c => (c.Skill, c.Intent)).ToDictionary(c => "native/" + c.Skill[4..] + "/" + c.Intent, c => c, StringComparer.Ordinal);
        LocalSkills = commands.Where(c => c.OnRobot).Select(c => c.Skill).ToHashSet(StringComparer.Ordinal);
        CloudSkills = commands.Where(c => !c.OnRobot).Select(c => c.Skill).ToHashSet(StringComparer.Ordinal);
    }

    public NativeCommandRegistration? Resolve(NativeParseResult result) => Commands
        .Where(c => c.Intent == result.Intent && Matches(c.Constraints, result.Entities)
            && (result.Skill is not ("example-skill" or "template-skill") || c.Skill == result.Skill))
        .Where(c => c.Skill is not ("example-skill" or "template-skill") || result.Skill == c.Skill || result.Intent == "template_skill")
        .OrderByDescending(c => c.Constraints.ValueKind == JsonValueKind.Array ? c.Constraints.GetArrayLength() : 0).FirstOrDefault();

    private static bool Matches(JsonElement constraints, IReadOnlyDictionary<string, object?> entities)
    {
        if (constraints.ValueKind != JsonValueKind.Array) return true;
        foreach (var constraint in constraints.EnumerateArray())
        {
            var name = constraint.GetProperty("name").GetString()!;
            var actual = entities.GetValueOrDefault(name)?.ToString();
            var expected = constraint.GetProperty("value").ToString();
            var operation = constraint.TryGetProperty("matchRule", out var rule) ? rule.GetString() : "EXACT";
            if (string.IsNullOrEmpty(actual)) return false;
            if (expected == "*") continue;
            if (operation == "NOT" ? string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)
                : !string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }
}
