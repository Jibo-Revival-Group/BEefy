namespace Jibo.Cloud.Application.Services;

public sealed partial class JiboInteractionService
{
    private async Task<JiboInteractionDecision> BuildHaEntityDecisionAsync(
        Jibo.Runtime.Abstractions.TurnContext turn, CancellationToken cancellationToken)
    {
        if (cloudStateStore is null || homeAssistantCommandService is null || homeAssistantPendingClimateStore is null)
            return new("ha_entity_control", "Home Assistant control is not available on this server right now.");
        if (!HomeAssistantEntityCommandParser.TryParse(turn.NormalizedTranscript ?? turn.RawTranscript, out var command))
            return new("ha_entity_control", "Please name the device and what you'd like me to change.");
        var (deviceId, friendlyId) = JiboIdentityResolver.Resolve(turn, cloudStateStore);
        homeAssistantPendingClimateStore.Clear(deviceId, friendlyId);
        var result = await homeAssistantCommandService.DispatchEntityCommandAsync(turn, command, cancellationToken);
        if (result?.NeedsClarification == true && result.Candidates is { Count: > 0 })
        {
            homeAssistantPendingClimateStore.Set(friendlyId ?? deviceId ?? string.Empty,
                new HomeAssistantPendingClimateStore.PendingClimateAction(command.Action, result.Candidates,
                    EntityCommand: command));
            return new("ha_entity_control", $"Which one: {HomeAssistantEntityNameMatcher.FormatCandidateList(result.Candidates)}?");
        }
        return HaEntityResult(result);
    }

    private async Task<JiboInteractionDecision> BuildHaEntityClarificationAsync(
        Jibo.Runtime.Abstractions.TurnContext turn,
        HomeAssistantPendingClimateStore.PendingClimateAction pending, string transcript,
        string? deviceId, string? friendlyId, CancellationToken cancellationToken)
    {
        if (transcript.Trim().ToLowerInvariant() is "cancel" or "never mind" or "nevermind" or "stop" or "no")
        {
            homeAssistantPendingClimateStore!.Clear(deviceId, friendlyId);
            return new("ha_entity_control", "Okay, cancelled.");
        }
        var match = HomeAssistantEntityNameMatcher.FindUnique(transcript, pending.Candidates);
        if (match is null)
            return new("ha_entity_control", $"Which one: {HomeAssistantEntityNameMatcher.FormatCandidateList(pending.Candidates)}?");
        homeAssistantPendingClimateStore!.Clear(deviceId, friendlyId);
        var result = await homeAssistantCommandService!.DispatchEntityCommandAsync(
            turn, pending.EntityCommand!, cancellationToken, match.EntityId);
        return HaEntityResult(result);
    }

    private static JiboInteractionDecision HaEntityResult(HomeAssistantCommandResult? result)
    {
        var reply = result is null ? "I couldn't reach Home Assistant just now." : result.IsOk
            ? "Okay, Home Assistant completed the command." : result.IsNotFound
                ? result.Message == "missing_area" ? "Assign me to an Area in Home Assistant so I can control this room."
                    : "I couldn't find that device or room in Home Assistant."
                : result.Message switch
                {
                    "pairing_required" => "My Home Assistant pairing is missing on this robot. Please pair me using the Yes or No prompt.",
                    "auth_failed" => "Home Assistant rejected my command. Please check my pairing and the server clocks.",
                    "timeout" => "Home Assistant didn't confirm the command in time. It may still have run.",
                    "disconnected" => "I couldn't reach Home Assistant just now.",
                    "unsupported" => "That device doesn't support the requested setting.",
                    "invalid_value" => "That setting is outside the supported values for the device.",
                    "unavailable" => "A requested device is unavailable in Home Assistant.",
                    _ => "Home Assistant couldn't complete the command. Some devices may already have changed."
                };
        return new("ha_entity_control", reply);
    }
}
