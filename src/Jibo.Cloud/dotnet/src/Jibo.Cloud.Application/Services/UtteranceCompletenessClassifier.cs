namespace Jibo.Cloud.Application.Services;

/// <summary>
/// Decides whether a live partial transcript looks like a finished thought.
/// Used only to <em>delay</em> finalization for slow speakers — never rewrites text.
/// </summary>
public static class UtteranceCompletenessClassifier
{
    public sealed record Result(bool IsComplete, string Reason);

    /// <summary>
    /// Closed-class words that almost never end a finished English sentence when
    /// they are the last token. Covers the old literal incomplete-launch lists
    /// ("what's your", "how old are", "my favorite is", …) without enumerating phrases.
    /// </summary>
    private static readonly HashSet<string> DanglingFunctionWords = new(StringComparer.Ordinal)
    {
        // Determiners / possessives
        "a", "an", "the", "my", "your", "his", "her", "its", "our", "their",
        // Prepositions
        "to", "of", "for", "in", "on", "at", "by", "with", "from", "about",
        "into", "onto", "over", "under", "between", "through", "during", "without",
        // Conjunctions
        "and", "or", "but", "nor", "yet", "so", "because", "if", "when", "while",
        "although", "unless", "until", "than", "as",
        // Auxiliaries / copulas / modals
        "is", "are", "am", "was", "were", "be", "been", "being",
        "do", "does", "did", "have", "has", "had",
        "can", "could", "will", "would", "shall", "should", "may", "might", "must",
        // Wh-words (alone or as the last token of an unfinished question)
        "what", "whats", "who", "whom", "whose", "which", "where", "when", "why", "how",
        // Subject pronouns that typically start a clause (not end one)
        "i", "we", "they", "he", "she"
    };

    /// <summary>
    /// Finished name-recall / identity questions that end in a dangling-looking
    /// token (e.g. "who am i") but are complete commands. Must stay ahead of the
    /// dangling-function-word check so AUTO_FINALIZE does not stall them.
    /// Keep in sync with <c>IsNameRecallQuestion</c> phrase coverage.
    /// </summary>
    private static readonly string[] KnownCompleteIdentityQuestions =
    [
        "what is my name",
        "what s my name",
        "whats my name",
        "what's my name",
        "who am i",
        "do you remember my name",
        "do you know me",
        "do you remember me",
        "who is this",
        "can you recognize me"
    ];

    public static Result Classify(
        string? partialTranscript,
        IReadOnlyList<string>? listenRules = null)
    {
        var normalized = TranscriptTextNormalizer.NormalizeLooseText(partialTranscript);
        if (string.IsNullOrWhiteSpace(normalized))
            return new Result(false, "empty_partial");

        if (TranscriptTextNormalizer.IsWakePhraseOnly(normalized) ||
            TranscriptTextNormalizer.HasTerminalWakePhraseWithoutCommand(normalized))
            return new Result(false, "wake_word_only");

        var command = TranscriptTextNormalizer.ExtractWakePhraseCommand(normalized);
        if (string.IsNullOrWhiteSpace(command))
            return new Result(false, "wake_word_only");

        command = TranscriptTextNormalizer.NormalizeLooseText(command);
        if (string.IsNullOrWhiteSpace(command))
            return new Result(false, "wake_word_only");

        if (IsKnownCompleteIdentityQuestion(command))
            return new Result(true, "known_complete_identity_question");

        var tokens = command.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
            return new Result(false, "empty_partial");

        var last = tokens[^1];
        if (DanglingFunctionWords.Contains(last))
            return new Result(false, "dangling_function_word");

        if (listenRules is { Count: > 0 } && IsConstrainedListen(listenRules))
        {
            if (listenRules.Any(IsYesNoRule) &&
                YesNoTranscriptClassifier.Classify(command) is YesNoTranscriptClassification.None)
                return new Result(false, "constrained_yes_no_unmatched");
        }

        return new Result(true, "complete");
    }

    private static bool IsKnownCompleteIdentityQuestion(string command) =>
        KnownCompleteIdentityQuestions.Any(phrase =>
            command.Equals(phrase, StringComparison.Ordinal) ||
            command.Contains(phrase, StringComparison.Ordinal));

    private static bool IsConstrainedListen(IReadOnlyList<string> rules) =>
        rules.Any(rule => IsYesNoRule(rule) ||
                          rule.Contains("launch", StringComparison.OrdinalIgnoreCase) ||
                          rule.StartsWith("shared/", StringComparison.OrdinalIgnoreCase));

    private static bool IsYesNoRule(string rule) =>
        string.Equals(rule, "shared/yes_no", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(rule, "clock/alarm_timer_change", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(rule, "clock/alarm_timer_none_set", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(rule, "create/is_it_a_keeper", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(rule, "settings/download_now_later", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(rule, "surprises-date/offer_date_fact", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(rule, "surprises-ota/want_to_download_now", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(rule, "word-of-the-day/surprise", StringComparison.OrdinalIgnoreCase) ||
        rule.StartsWith("introductions/", StringComparison.OrdinalIgnoreCase);
}
