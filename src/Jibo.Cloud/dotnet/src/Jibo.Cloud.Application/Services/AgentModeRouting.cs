using Jibo.Runtime.Abstractions;

namespace Jibo.Cloud.Application.Services;

public static class AgentModeRouting
{
    public const string ConversationRule = "agent/conversation";

    public static bool IsConversation(TurnContext turn) =>
        SkillListenOwnership.ReadRuleList(turn, "listenRules")
            .Concat(SkillListenOwnership.ReadRuleList(turn, "clientRules"))
            .Any(rule => string.Equals(rule, ConversationRule, StringComparison.OrdinalIgnoreCase));

    public static bool IsLaunch(string text)
    {
        var normalized = TranscriptTextNormalizer.NormalizeLooseText(text);
        normalized = TranscriptTextNormalizer.StripLeadingPhrases(normalized,
            ["hey jibo", "hello jibo", "hi jibo", "jibo"]);
        normalized = TranscriptTextNormalizer.StripLeadingPhrases(normalized, ["um", "uh", "erm", "hmm", "ah"]); // prod: "um let's chat" x3
        normalized = TranscriptTextNormalizer.StripTrailingCourtesyWords(normalized);
        if (IsLetsChat(normalized) || LetsChatMishearings.Contains(normalized) ||
            normalized is "let's have a conversation" or "let s have a conversation" or "lets have a conversation" or "let us have a conversation")
            return true;
        // "open/turn on/enable agent mode", and prod mishearings of "agent mode".
        normalized = TranscriptTextNormalizer.StripLeadingPhrases(normalized,
            ["turn on", "switch on", "open", "enable", "enter", "start", "launch", "go to", "use"]);
        return AgentModeSounds.Contains(normalized);
    }

    private static readonly HashSet<string> AgentModeSounds =
    [
        "ai mode", "a i mode", "agent mode", "agent", "agent mood", "agent mo", "agent agent mode", "agent modes",
        "aji and mode", "pingent mode", "an able agent", "able agent", "agent mod"
    ];

    // Whole-utterance prod mishearings of "let's chat", each followed by the person saying it again.
    private static readonly HashSet<string> LetsChatMishearings =
    [
        "let's check", "let s check", "lets check", "that's yet", "that s yet", "let's ye", "let s ye", "let's je", "let s je",
        "let's gent", "let s gent", "now let's chuck", "now let s chuck", "let's chuck", "let s chuck", "bless chap", "bless chat",
        "i let chat", "of less chat", "lest yet as yet", "but yet as yet", "that's chat", "that s chat", "just yet let cheert",
        "or bio less chap", "let's chet", "let s chet"
    ];

    // ASR often mishears "let's chat" (prod: "less chat", "list chat", "let's cha", "lis chap", "let's chant").
    // Only the whole phrase counts, so "let's chat about music" or "list" alone never launch.
    private static readonly HashSet<string> LetsSounds = ["let's", "let s", "lets", "let us", "let", "less", "list", "lis", "lest"];
    private static readonly HashSet<string> ChatSounds = ["chat", "cha", "chap", "chant", "chad"];

    private static bool IsLetsChat(string normalized)
    {
        var space = normalized.LastIndexOf(' ');
        if (space <= 0 || !ChatSounds.Contains(normalized[(space + 1)..]))
            return false;
        var lead = normalized[..space];
        if (lead.EndsWith(" have a", StringComparison.Ordinal))
            lead = lead[..^" have a".Length];
        return LetsSounds.Contains(lead);
    }
}
