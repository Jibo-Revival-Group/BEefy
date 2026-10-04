namespace Jibo.Cloud.Application.Services;

internal static class AsrCorrectionAcceptance
{
    private static readonly HashSet<string> ProtectedTokens = new(StringComparer.Ordinal)
    {
        "no", "not", "never", "dont", "doesnt", "didnt", "cant", "cannot", "wont", "without", "least",
        "i", "me", "my", "mine", "you", "your", "yours", "we", "us", "our", "they", "their",
        "he", "his", "she", "her", "zero", "one", "two", "three", "four", "five", "six", "seven",
        "eight", "nine", "ten", "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen",
        "seventeen", "eighteen", "nineteen", "twenty", "thirty", "forty", "fifty", "sixty",
        "seventy", "eighty", "ninety", "hundred", "thousand", "million", "am", "pm"
    };
    private static readonly HashSet<string> GrammarInsertions = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "is", "are", "do", "does", "did", "to", "of"
    };

    internal static bool IsConservativeEdit(string original, string corrected)
    {
        if (corrected.Length is 0 or > 256) return false;
        var before = Tokens(original);
        var after = Tokens(corrected);
        if (before.Length is < 2 or > 32 || after.Length is < 2 or > 32) return false;
        if (before.SequenceEqual(after, StringComparer.Ordinal)) return false;
        if (before.Contains("my", StringComparer.Ordinal) &&
            before.Any(word => word is "name" or "names" or "named")) return false;
        // The contextual worker may repair real-word acoustic substitutions only
        // to this bounded command catalog. Preserve ownership, numbers and negation;
        // an initial malformed question word may become a question word.
        var contextual = AsrCommandCatalog.Candidates.Contains(string.Join(' ', after), StringComparer.Ordinal);
        var comparableBefore = before;
        if (contextual && (after[0] is "what" or "which" or "how") &&
            (!IsProtected(before[0]) || before[0] == "my") &&
            before.Length == after.Length && before.Skip(1).SequenceEqual(after.Skip(1)))
            comparableBefore = after;
        if (!comparableBefore.Where(IsProtected).SequenceEqual(after.Where(IsProtected), StringComparer.Ordinal)) return false;

        // The worker verifies acoustic and neural evidence. Independently require
        // bounded whole-phrase alignment here: one content span and one grammar edit.
        var visited = new HashSet<(int Before, int After, int Content, int Grammar, int Anchors)>();
        return Align(0, 0, 0, 0, 0);

        bool Align(int i, int j, int content, int grammar, int anchors)
        {
            if (content > 1 || grammar > 1 || !visited.Add((i, j, content, grammar, anchors))) return false;
            if (i == before.Length && j == after.Length)
                return content + grammar > 0 && anchors >= Math.Max(1, before.Length / 2);
            if (i < before.Length && j < after.Length && before[i] == after[j])
                return Align(i + 1, j + 1, content, grammar, anchors + 1);
            if (i < before.Length && j < after.Length)
            {
                var questionRepair = i == 0 && j == 0 && ReferenceEquals(comparableBefore, after);
                if (questionRepair && Align(i + 1, j + 1, content, grammar + 1, anchors)) return true;
                if (!IsProtected(before[i]) && !IsProtected(after[j]))
                {
                    var functionEdit = GrammarInsertions.Contains(before[i]) && GrammarInsertions.Contains(after[j]);
                    if ((contextual || CharacterDistance(before[i], after[j]) <= 2) &&
                        Align(i + 1, j + 1, content + (functionEdit ? 0 : 1), grammar + (functionEdit ? 1 : 0), anchors))
                        return true;
                    if (contextual && i + 1 < before.Length && !IsProtected(before[i + 1]) &&
                        Align(i + 2, j + 1, content + 1, grammar, anchors)) return true;
                    if (contextual && j + 1 < after.Length && !IsProtected(after[j + 1]) &&
                        Align(i + 1, j + 2, content + 1, grammar, anchors)) return true;
                }
            }
            if (j < after.Length && GrammarInsertions.Contains(after[j]) &&
                Align(i, j + 1, content, grammar + 1, anchors)) return true;
            return i < before.Length && (GrammarInsertions.Contains(before[i]) ||
                (i > 0 && before[i] == before[i - 1])) && Align(i + 1, j, content, grammar + 1, anchors);
        }
    }

    private static string[] Tokens(string value) => TranscriptTextNormalizer.NormalizeLooseText(value)
        .Replace("'", string.Empty, StringComparison.Ordinal).Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .SelectMany(word => word == "whats" ? new[] { "what", "is" } : new[] { word }).ToArray();

    private static bool IsProtected(string token) => ProtectedTokens.Contains(token) || token.Any(char.IsDigit);

    private static int CharacterDistance(string left, string right)
    {
        if (Math.Abs(left.Length - right.Length) > 2) return 3;
        var previous = Enumerable.Range(0, right.Length + 1).ToArray();
        for (var i = 1; i <= left.Length; i++)
        {
            var current = new int[right.Length + 1];
            current[0] = i;
            for (var j = 1; j <= right.Length; j++)
                current[j] = Math.Min(previous[j - 1] + (left[i - 1] == right[j - 1] ? 0 : 1),
                    Math.Min(previous[j] + 1, current[j - 1] + 1));
            previous = current;
        }
        return previous[^1];
    }
}
