using System.Text.RegularExpressions;

namespace Jibo.Cloud.Application.Services;

/// <summary>
/// A bounded text correction pass for known robot preference questions. It only
/// repairs a favorite slot within a complete question and a known catalog subject.
/// Original ASR text remains available as TurnContext.RawTranscript.
/// </summary>
internal static class AsrGrammarCorrector
{
    private static readonly Regex PossessiveQuestion = new(
        @"^(?<prefix>" + AsrTokenLexicon.QuestionPossessive +
        @")\s+(?<least>least\s+)?(?<slot>" + AsrTokenLexicon.FavoriteSlot +
        @"|fav[a-z]{3,8})\s+(?<attribute>.+)$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        TimeSpan.FromMilliseconds(20));

    private static readonly Regex HaveQuestion = new(
        @"^(?<prefix>do\s+you\s+have\s+an?)\s+(?<least>least\s+)?(?<slot>" +
        AsrTokenLexicon.FavoriteSlot + @"|fav[a-z]{3,8})\s+(?<attribute>.+)$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        TimeSpan.FromMilliseconds(20));

    internal static string Correct(string transcript)
    {
        // Bound work and avoid processing long, open-ended transcripts.
        if (string.IsNullOrWhiteSpace(transcript) || transcript.Length > 256)
            return transcript;

        if (!transcript.Contains("fav", StringComparison.OrdinalIgnoreCase) &&
            !transcript.Contains("flav", StringComparison.OrdinalIgnoreCase))
            return transcript;

        var text = TranscriptTextNormalizer.NormalizeRequestUtterance(transcript)
            .Replace("'", string.Empty, StringComparison.Ordinal);
        Match match;
        try
        {
            match = PossessiveQuestion.Match(text);
            if (!match.Success) match = HaveQuestion.Match(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return transcript;
        }
        if (!match.Success) return transcript;

        var slot = match.Groups["slot"].Value;
        if (!AsrTokenLexicon.FavoriteSlotPattern.IsMatch(slot) &&
            !IsSingleEdit(slot, "favorite"))
            return transcript;

        var attribute = match.Groups["attribute"].Value;
        if (!UtteranceFrameParser.TryCanonicalizePreferenceAttribute(attribute, out var canonical))
            return transcript;

        // Exact recognition requires no rewrite, including British spelling.
        if ((slot is "favorite" or "favourite") && attribute == canonical)
            return transcript;

        return $"{match.Groups["prefix"].Value} {match.Groups["least"].Value}favorite {canonical}";
    }

    private static bool IsSingleEdit(string left, string right)
    {
        if (Math.Abs(left.Length - right.Length) > 1) return false;
        var i = 0;
        var j = 0;
        var edits = 0;
        while (i < left.Length && j < right.Length)
        {
            if (left[i] == right[j]) { i++; j++; continue; }
            if (++edits > 1) return false;
            if (left.Length >= right.Length) i++;
            if (right.Length >= left.Length) j++;
        }
        return edits + (left.Length - i) + (right.Length - j) <= 1;
    }
}
