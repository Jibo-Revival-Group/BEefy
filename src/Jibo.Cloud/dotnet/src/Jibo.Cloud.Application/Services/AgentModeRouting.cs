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
        return normalized is "let's chat" or "let s chat" or "lets chat" or "let us chat" or
            "let's have a conversation" or "let s have a conversation" or "lets have a conversation" or "let us have a conversation" or
            "ai mode" or "a i mode" or "agent mode" or "enter ai mode" or "start ai mode";
    }
}
