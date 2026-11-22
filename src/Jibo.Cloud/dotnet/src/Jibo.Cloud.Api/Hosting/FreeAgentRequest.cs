using System.Text.Json;
using System.Text.RegularExpressions;
using Jibo.Cloud.Infrastructure.Audio;

namespace Jibo.Cloud.Api.Hosting;

internal sealed record FreeAgentRequest(string RequestId, string RobotId, string System, IReadOnlyList<FreeAgentChatMessage> Messages)
{
    public const string SystemMessage = "You are Jibo, a friendly social robot. Reply briefly and clearly in plain spoken English.";
    // BEacon 3.2.0+ sends its own instructions (Jibo's persona, the "DO:" actions and installed skills), like paid models get.
    public const int MaxSystemLength = 8000;
    public static FreeAgentRequest? Parse(string body)
    {
        using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 8 });
        var root = document.RootElement;
        var hasSystem = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("system", out _);
        if (!(hasSystem ? ExactFields(root, "requestId", "robotId", "messages", "system") : ExactFields(root, "requestId", "robotId", "messages")) ||
            root.GetProperty("requestId").ValueKind != JsonValueKind.String ||
            !Regex.IsMatch(root.GetProperty("requestId").GetString()!, @"\A[0-9a-f]{32}\z", RegexOptions.CultureInvariant)) return null;
        if (root.GetProperty("robotId").ValueKind != JsonValueKind.String) return null;
        var robotId = root.GetProperty("robotId").GetString();
        if (string.IsNullOrWhiteSpace(robotId) || robotId.Length > 128 || robotId.Any(char.IsControl)) return null;
        var input = root.GetProperty("messages");
        if (input.ValueKind != JsonValueKind.Array || input.GetArrayLength() is < 1 or > 21 || input.GetArrayLength() % 2 != 1) return null;
        var system = SystemMessage;
        if (hasSystem)
        {
            var value = root.GetProperty("system");
            if (value.ValueKind != JsonValueKind.String) return null;
            system = value.GetString()!;
            if (string.IsNullOrWhiteSpace(system) || system.Length > MaxSystemLength || system.Any(c => char.IsControl(c) && c != '\n')) return null;
        }
        var messages = new List<FreeAgentChatMessage>();
        var index = 0;
        foreach (var item in input.EnumerateArray())
        {
            if (!ExactFields(item, "role", "content") || item.GetProperty("role").ValueKind != JsonValueKind.String ||
                item.GetProperty("content").ValueKind != JsonValueKind.String) return null;
            var role = item.GetProperty("role").GetString();
            var content = item.GetProperty("content").GetString();
            if (role != (index++ % 2 == 0 ? "user" : "assistant") || string.IsNullOrWhiteSpace(content)) return null;
            messages.Add(new(role, content));
        }
        return new(root.GetProperty("requestId").GetString()!, robotId, system, messages);
    }
    private static bool ExactFields(JsonElement item, params string[] fields) => item.ValueKind == JsonValueKind.Object &&
        item.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).SequenceEqual(fields.Order(StringComparer.Ordinal));
}
