using System.Text.Json;
using System.Text.RegularExpressions;
using Jibo.Runtime.Abstractions;

namespace Jibo.Cloud.Application.Services;

internal static class NativePromptContext
{
    internal static void Populate(TurnContext turn, Dictionary<string, string> values)
    {
        var source = NativeConversationValue.Read(turn.Attributes, "context")?.ToString();
        if (string.IsNullOrWhiteSpace(source)) return;
        using var document = JsonDocument.Parse(source);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return;
        if (root.TryGetProperty("data", out var data)) root = data;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("runtime", out var runtime) || runtime.ValueKind != JsonValueKind.Object) return;
        void Flatten(JsonElement node, string prefix)
        {
            if (node.ValueKind == JsonValueKind.Object)
            {
                foreach (var field in node.EnumerateObject()) Flatten(field.Value, prefix.Length == 0 ? field.Name : prefix + "." + field.Name);
            }
            else if (node.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                values[prefix] = node.ToString();
        }
        Flatten(runtime, "");
        foreach (var path in new[] { "dialog.speaker", "dialog.referent", "loop.owner" })
        {
            if (values.TryGetValue(path + ".firstName", out var name))
                values[path.Replace("dialog.", "")] = name;
        }
        var now = DateTimeOffset.Now;
        if (values.TryGetValue("location.iso", out var iso) && DateTimeOffset.TryParse(iso, out var local)) now = local;
        void Person(JsonElement person, string prefix)
        {
            Flatten(person, prefix);
            if (person.TryGetProperty("phoneticName", out var spoken)) values[prefix] = spoken.ToString();
            else if (person.TryGetProperty("firstName", out var first)) values[prefix] = first.ToString();
            if (!person.TryGetProperty("birthdate", out var birth)) return;
            DateTimeOffset birthday;
            if (long.TryParse(birth.ToString(), out var epoch) && epoch is > 0 and <= 253402300799999)
                birthday = DateTimeOffset.FromUnixTimeMilliseconds(epoch);
            else if (!DateTimeOffset.TryParse(birth.ToString(), out birthday)) return;
            var years = now.Year - birthday.Year - (birthday.AddYears(now.Year - birthday.Year) > now ? 1 : 0);
            var days = Math.Max(0, (now - birthday).Days);
            values[prefix + ".birthdate"] = birthday.ToString("MMMM d, yyyy");
            values[prefix + ".birthday"] = birthday.ToString("MMMM d");
            values[prefix + ".isBirthday"] = (birthday.Month == now.Month && birthday.Day == now.Day).ToString().ToLowerInvariant();
            values[prefix + ".age"] = years + " years old";
            values[prefix + ".age.supplemented"] = values[prefix + ".age"];
            foreach (var (unit, number) in new[] { ("years", (double)years), ("days", (double)days), ("minutes", Math.Max(0, (now - birthday).TotalMinutes)), ("seconds", Math.Max(0, (now - birthday).TotalSeconds)) })
            {
                values[prefix + ".age." + unit] = Math.Floor(number).ToString(System.Globalization.CultureInfo.InvariantCulture);
                values[prefix + ".age." + unit + ".supplemented"] = values[prefix + ".age." + unit] + " " + unit + " old";
            }
        }
        if (runtime.TryGetProperty("loop", out var loop) && loop.ValueKind == JsonValueKind.Object)
        {
            if (loop.TryGetProperty("jibo", out var jibo) && jibo.ValueKind == JsonValueKind.Object) Person(jibo, "jibo");
            if (loop.TryGetProperty("users", out var users) && users.ValueKind == JsonValueKind.Array)
            {
                var names = new List<string>();
                foreach (var user in users.EnumerateArray().Where(u => u.ValueKind == JsonValueKind.Object))
                {
                    if (user.TryGetProperty("phoneticName", out var name)) names.Add(name.ToString());
                    else if (user.TryGetProperty("firstName", out name)) names.Add(name.ToString());
                    if (!user.TryGetProperty("id", out var id)) continue;
                    var personId = id.ToString();
                    if (values.GetValueOrDefault("perception.speaker") == personId) Person(user, "speaker");
                    if (values.GetValueOrDefault("dialog.referent") == personId) Person(user, "referent");
                    if (values.GetValueOrDefault("loop.owner") == personId) Person(user, "loop.owner");
                }
                values["loop.count"] = names.Count.ToString();
                values["loop.list"] = string.Join(", ", names);
            }
        }
        if (values.TryGetValue("character.emotion.name", out var emotion)) values["jibo.emotion"] = emotion;
    }

    internal static bool Matches(string? condition, Dictionary<string, string> values, LegacyMimConditionEvaluator.Context context)
    {
        if (string.IsNullOrWhiteSpace(condition)) return true;
        // Reduce runtime predicates to booleans; the existing evaluator handles boolean composition and seasonal ranges.
        var reduced = Regex.Replace(condition, @"(?<key>[\w.]+)\s*(?<op>===|!==|==|!=)\s*(?<value>""[^""]*""|'[^']*'|[\w.]+)", match =>
        {
            var key = match.Groups["key"].Value;
            if (key is "holiday" or "holidayClaim" or "pod" or "podClaim" or "jibo.emotion") return match.Value;
            var raw = match.Groups["value"].Value;
            var expected = raw.StartsWith('\'') || raw.StartsWith('"') ? raw[1..^1] : values.GetValueOrDefault(raw);
            var equal = values.TryGetValue(key, out var actual) && actual == expected;
            return (match.Groups["op"].Value.Contains('!') ? !equal : equal) ? "true" : "false";
        });
        reduced = Regex.Replace(reduced, @"(?<neg>!!|!)\s*(?<key>loop\.owner|location\.city|referent|speaker)(?![\w.])", match =>
        {
            var key = match.Groups["key"].Value;
            var present = values.TryGetValue(key, out var value) && value.Length > 0 || values.Keys.Any(k => k.StartsWith(key + ".", StringComparison.Ordinal));
            var yes = match.Groups["neg"].Value == "!" ? !present : present;
            return yes ? "true" : "false";
        });
        return LegacyMimConditionEvaluator.Matches(reduced, context);
    }
}
