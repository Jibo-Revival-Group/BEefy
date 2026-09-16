using Jibo.Runtime.Abstractions;

namespace Jibo.Cloud.Application.Services;

public sealed partial class JiboInteractionService
{
    private static JiboInteractionDecision BuildCloudVersionDecision()
    {
        return new JiboInteractionDecision("cloud_version", OpenJiboCloudBuildInfo.SpokenVersion,
            SkillPayload: new Dictionary<string, object?> { ["esml"] = OpenJiboCloudBuildInfo.EsmlVersion });
    }

    private static JiboInteractionDecision BuildRobotFlavorDecision(TurnContext turn)
    {
        return new JiboInteractionDecision(
            "robot_flavor",
            RobotFlavorClassifier.ClassifySpokenReply(turn.FirmwareVersion));
    }

    private static string ResolveSemanticIntent(
        string loweredTranscript,
        DateTimeOffset? referenceLocalTime,
        string? clientIntent,
        IReadOnlyList<string> clientRules,
        IReadOnlyList<string> listenRules,
        IReadOnlyDictionary<string, string> clientEntities,
        string? lastClockDomain,
        string? pendingProactivityOffer,
        bool isYesNoTurn,
        bool isTimerValueTurn,
        bool isAlarmValueTurn,
        bool isSkillOwnedListen)
    {
        var intent = ResolveSemanticIntentCore(
            loweredTranscript,
            referenceLocalTime,
            clientIntent,
            clientRules,
            listenRules,
            clientEntities,
            lastClockDomain,
            pendingProactivityOffer,
            isYesNoTurn,
            isTimerValueTurn,
            isAlarmValueTurn,
            isSkillOwnedListen);

        // Weak "can we / shall we" prefixes are often genuine user requests. If pass 1
        // discarded them as prompt echo, retry without weak-echo suppression.
        if (IsEchoSuppressedIntent(intent) &&
            TranscriptHeuristics.IsLikelyWeakPromptEcho(loweredTranscript))
        {
            var withoutWeakEcho = ResolveSemanticIntentCore(
                loweredTranscript,
                referenceLocalTime,
                clientIntent,
                clientRules,
                listenRules,
                clientEntities,
                lastClockDomain,
                pendingProactivityOffer,
                isYesNoTurn,
                isTimerValueTurn,
                isAlarmValueTurn,
                isSkillOwnedListen,
                suppressWeakPromptEcho: false);

            if (!IsEchoSuppressedIntent(withoutWeakEcho))
                intent = withoutWeakEcho;
        }

        // If still unresolved, retry on a fully normalized utterance (fillers, polite
        // frames, trailing courtesy). Skip when canonical form is unchanged.
        if (!string.Equals(intent, "chat", StringComparison.OrdinalIgnoreCase))
            return intent;

        var canonical = TranscriptTextNormalizer.NormalizeRequestUtterance(loweredTranscript);
        if (string.IsNullOrWhiteSpace(canonical) ||
            string.Equals(canonical, loweredTranscript, StringComparison.Ordinal))
            return intent;

        var normalizedIntent = ResolveSemanticIntentCore(
            canonical,
            referenceLocalTime,
            clientIntent,
            clientRules,
            listenRules,
            clientEntities,
            lastClockDomain,
            pendingProactivityOffer,
            isYesNoTurn,
            isTimerValueTurn,
            isAlarmValueTurn,
            isSkillOwnedListen,
            suppressWeakPromptEcho: false);

        return IsFallbackOrEchoIntent(normalizedIntent) ? intent : normalizedIntent;
    }

    private static bool IsEchoSuppressedIntent(string intent) =>
        string.Equals(intent, "skill_listen", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(intent, "prompt_echo", StringComparison.OrdinalIgnoreCase);

    private static bool IsFallbackOrEchoIntent(string intent) =>
        IsEchoSuppressedIntent(intent) ||
        string.Equals(intent, "chat", StringComparison.OrdinalIgnoreCase);
}