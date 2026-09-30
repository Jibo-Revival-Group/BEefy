using System.Reflection;

namespace Jibo.Cloud.Application.Services;

/// <summary>
/// Homophone classes from the device NLU word list. Membership is consulted
/// when a frame slot is compared to a catalog surface. The transcript itself
/// is not rewritten.
/// </summary>
internal static class EquivalentWordLexicon
{
    private const string ResourceName = "Jibo.Cloud.Application.eq_words.txt";

    private static readonly Dictionary<string, string> ClassIdByToken = Load();

    internal static bool AreEquivalent(string left, string right)
    {
        if (string.Equals(left, right, StringComparison.Ordinal)) return true;
        if (left.Length == 0 || right.Length == 0) return false;

        return ClassIdByToken.TryGetValue(left, out var leftClass) &&
               ClassIdByToken.TryGetValue(right, out var rightClass) &&
               string.Equals(leftClass, rightClass, StringComparison.Ordinal);
    }

    private static Dictionary<string, string> Load()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
        if (stream is null) return map;

        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (tokens.Length == 0) continue;

            var classId = NormalizeToken(tokens[0]);
            if (classId.Length == 0) continue;

            foreach (var token in tokens)
            {
                var normalized = NormalizeToken(token);
                if (normalized.Length == 0) continue;
                map.TryAdd(normalized, classId);
            }
        }

        return map;
    }

    private static string NormalizeToken(string token) =>
        token.ToLowerInvariant().Replace("'", string.Empty, StringComparison.Ordinal);
}
