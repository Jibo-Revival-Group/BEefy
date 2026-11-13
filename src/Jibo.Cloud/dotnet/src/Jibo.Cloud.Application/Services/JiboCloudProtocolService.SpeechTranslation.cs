using Jibo.Cloud.Domain.Models;

namespace Jibo.Cloud.Application.Services;

public sealed partial class JiboCloudProtocolService
{
    /// <summary>
    /// BEacon's speech language (Settings): the robot sends what it is about to say in English and gets it back
    /// in that language as markup for Jibo's own voice. Same robot credentials as calendar feeds. The robot
    /// speaks English when this fails, so errors carry no detail it needs.
    /// </summary>
    private async Task<ProtocolDispatchResult> HandleTranslateSpeechAsync(ProtocolEnvelope envelope)
    {
        if (AuthorizeMemberCalendar(envelope) is { } rejected) return rejected;
        if (speechTranslator is null) return CalendarError(503, "Speech translation isn't set up on this server.");

        var body = envelope.TryParseBody();
        var language = ReadString(body, "language")?.Trim().ToLowerInvariant();
        var text = ReadString(body, "text");
        if (string.IsNullOrWhiteSpace(language) || language.Length > 8 || string.IsNullOrWhiteSpace(text) || text.Length > 2000)
            return CalendarError(400, "language and text (up to 2000 characters) are required.");

        var speech = await speechTranslator.TryTranslateAsync(language, text);
        return speech is null
            ? CalendarError(503, "That couldn't be translated right now.")
            : ProtocolDispatchResult.Ok(new { language, translation = speech.Translation, parts = speech.Parts });
    }
}
