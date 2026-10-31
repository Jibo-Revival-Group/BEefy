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
        normalized = TranscriptTextNormalizer.StripTrailingCourtesyWords(normalized);
        return IsLetsChat(normalized) ||
            normalized is "let's have a conversation" or "let s have a conversation" or "lets have a conversation" or "let us have a conversation" or
            "ai mode" or "a i mode" or "agent mode" or "enter ai mode" or "start ai mode";
    }

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
