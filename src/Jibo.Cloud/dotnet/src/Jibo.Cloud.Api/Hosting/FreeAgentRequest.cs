using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jibo.Cloud.Infrastructure.Audio;

namespace Jibo.Cloud.Api.Hosting;

internal sealed record FreeAgentRequest(string RequestId, string RobotId, string System, IReadOnlyList<FreeAgentChatMessage> Messages, string? Image = null, string? Context = null)
{
    public const string SystemMessage = "You are Jibo, a friendly social robot. Reply briefly and clearly in plain spoken English.";
    // BEacon 3.2.0+ sends its own instructions (Jibo's persona, the "DO:" actions and installed skills), like paid models get.
    public const int MaxSystemLength = 8000;
    // A request may carry one photo from Jibo's camera (BEacon sends ~640x360, ~15 KB of base64); only then may it be bigger.
    public const int MaxTextBody = 32000, MaxImageLength = 200_000, MaxBody = MaxTextBody + MaxImageLength;
    public static FreeAgentRequest? Parse(string body)
    {
        using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 8 });
        var root = document.RootElement;
        var hasSystem = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("system", out _);
        var hasImage = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("image", out _);
        // BEacon 3.2.1+ sends the per-robot, per-minute part (time, nickname, memory) as context, so system stays the
        // same for every robot and can be prompt-cached.
        var hasContext = hasSystem && root.TryGetProperty("context", out _);
        string[] fields = ["requestId", "robotId", "messages", .. hasSystem ? ["system"] : Array.Empty<string>(), .. hasImage ? ["image"] : Array.Empty<string>(),
            .. hasContext ? ["context"] : Array.Empty<string>()];
        if (!ExactFields(root, fields) ||
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
        string? context = null;
        if (hasContext)
        {
            var value = root.GetProperty("context");
            if (value.ValueKind != JsonValueKind.String) return null;
            context = value.GetString()!;
            // Together no longer than one system prompt could be before.
            if (string.IsNullOrWhiteSpace(context) || system.Length + context.Length > MaxSystemLength || context.Any(c => char.IsControl(c) && c != '\n')) return null;
        }
        string? image = null;
        if (hasImage)
        {
            var value = root.GetProperty("image");
            if (value.ValueKind != JsonValueKind.String) return null;
            image = value.GetString()!;
            var bytes = new byte[image.Length];
            // Only a real JPEG within the cap goes on to Anthropic.
            if (image.Length > MaxImageLength || !Convert.TryFromBase64String(image, bytes, out var size) || size < 3 ||
                bytes[0] != 0xFF || bytes[1] != 0xD8 || bytes[2] != 0xFF) return null;
        }
        else if (Encoding.UTF8.GetByteCount(body) > MaxTextBody) return null;
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
        return new(root.GetProperty("requestId").GetString()!, robotId, system, messages, image, context);
    }
    private static bool ExactFields(JsonElement item, params string[] fields) => item.ValueKind == JsonValueKind.Object &&
        item.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).SequenceEqual(fields.Order(StringComparer.Ordinal));
}
