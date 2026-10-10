using System.Text.RegularExpressions;

namespace Jibo.Cloud.Application.Services;

/// <summary>Every registered chitchat response, with its fixed manifest entities.</summary>
public static class NativeScriptedResponseCatalog
{
    public static IReadOnlyDictionary<string, NativeCommandRegistration> Commands { get; } =
        NativeCommandRegistry.Instance.Commands.Where(c => c.Skill == "chitchat-skill" &&
            c.Memo.ValueKind == System.Text.Json.JsonValueKind.Object && c.Memo.TryGetProperty("mim", out _))
        .Select((command, index) => (Key: $"scripted/{command.Memo.GetProperty("mim").GetString()}/{index}", Command: command))
        .ToDictionary(p => p.Key, p => p.Command, StringComparer.Ordinal);

    public static string Describe(string key)
    {
        var command = Commands[key];
        var mim = command.Memo.GetProperty("mim").GetString()!;
        return Regex.Replace(mim, @"(?<=[a-z])(?=[A-Z])", " ").Replace('_', ' ')
            .Replace("JBO", "Jibo").Replace("USR", "user").Replace("LM", "loop member") + ".";
    }

    public static string Family(string key)
    {
        var parts = Commands[key].Memo.GetProperty("mim").GetString()!.Split('_');
        var count = parts.Length > 2 && parts[1] is "JBO" or "USR" or "LM" or "OTHER" ? 2 : 1;
        return string.Join('_', parts.Take(count));
    }

    public static string[] TopicWords(string key)
    {
        var mim = Commands[key].Memo.GetProperty("mim").GetString()!;
        var prefixLength = Family(key).Length + 1;
        var suffix = prefixLength < mim.Length ? mim[prefixLength..] : mim;
        return Regex.Replace(suffix, @"(?<=[a-z])(?=[A-Z])", " ").Replace('_', ' ')
            .ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }
}
