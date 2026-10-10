namespace Jibo.Cloud.Application.Services;

public sealed partial class JiboInteractionService
{
    private static bool IsUnknownNluDecision(JiboInteractionDecision decision) =>
        string.IsNullOrWhiteSpace(decision.IntentName) ||
        decision.IntentName.Equals("unknown", StringComparison.OrdinalIgnoreCase) ||
        decision.IntentName.Equals("not_understood", StringComparison.OrdinalIgnoreCase) ||
        decision.IntentName.Equals("unrecognized", StringComparison.OrdinalIgnoreCase) ||
        decision.IntentName.Equals("no_match", StringComparison.OrdinalIgnoreCase);

    private static bool HasNluRequiredValues(string intent, string localIntent, string transcript,
        IReadOnlyDictionary<string, string> entities, DateTimeOffset? localTime) => intent switch
    {
        "volume_to_value" => ResolveVolumeLevel(transcript, entities) is not null,
        "timer_value" => TryReadStructuredTimerValue(entities) is not null || TryParseTimerValue(transcript, false) is not null,
        "alarm_value" or "alarm_edit_value" => TryReadStructuredAlarmValue(entities) is not null ||
            TryParseAlarmValue(transcript, false, localTime) is not null,
        "radio_genre" => TryResolveRadioGenre(transcript) is not null,
        // These routes depend on typed parsers or entities.
        // A classifier cannot replace their extraction with an invented value.
        "math_query" or "spell_word" or "define_word" or "countdown" or "measurement_conversion" or
        "memory_set_name" or
        "ha_lights_on" or "ha_lights_off" or "ha_climate_set_temp" or "ha_climate_cool_down" or
        "ha_climate_warm_up" or "ha_climate_get_temp" => intent == localIntent,
        _ => true
    };
}
