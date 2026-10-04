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
        if (before.Length is < 3 or > 32 || after.Length is < 3 or > 32) return false;
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

        // Bounded word alignment; contextual substitutions additionally require
        // membership in the command catalog and neural/phonetic worker validation.
        // Never accept an unconstrained rewrite merely because it maps to a command.
        var costs = new int[before.Length + 1, after.Length + 1];
        const int rejected = 100;
        for (var i = 1; i <= before.Length; i++) costs[i, 0] = rejected;
        for (var j = 1; j <= after.Length; j++) costs[0, j] = rejected;
        for (var i = 1; i <= before.Length; i++)
        for (var j = 1; j <= after.Length; j++)
        {
            var same = before[i - 1] == after[j - 1];
            var substitution = same ? 0 :
                !IsProtected(before[i - 1]) && !IsProtected(after[j - 1]) &&
                (CharacterDistance(before[i - 1], after[j - 1]) <= 2 || contextual) ? 1 :
                contextual && i == 1 && j == 1 && ReferenceEquals(comparableBefore, after) ? 1 : rejected;
            var insertion = GrammarInsertions.Contains(after[j - 1]) ? 1 : rejected;
            var deletion = GrammarInsertions.Contains(before[i - 1]) ||
                (i > 1 && before[i - 1] == before[i - 2]) ? 1 : rejected;
            costs[i, j] = Math.Min(costs[i - 1, j - 1] + substitution,
                Math.Min(costs[i, j - 1] + insertion, costs[i - 1, j] + deletion));
        }
        var edits = costs[before.Length, after.Length];
        return edits <= 2 && edits <= before.Length / 2;
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
