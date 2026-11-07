using System.Text.RegularExpressions;
using Jibo.Runtime.Abstractions;

namespace Jibo.Cloud.Application.Services;

/// <summary>
/// Whole-utterance repairs for commands the speech recognizer mishears or cuts off, taken from
/// prod transcripts where the person repeated the command right after (e.g. "twark" then "twerk",
/// "place no halation" then "play the song snow halation"). Deterministic, so it works while the
/// Jev classifier is unavailable. Only for Hey Jibo turns; skill-owned listens keep what was heard.
/// </summary>
public static class MisheardCommands
{
    private const string TwerkSounds =
        "twerk|twark|twerek|twick|twic|tweck|tark|turk|torp|tweerk|twirk|twke|twurk|work|to work";

    private static readonly (Regex Pattern, string Replacement)[] Rules =
    [
        // twerk: "twark", "to work", "can you to work", "can you turk"
        // "can you work" is a real question with its own answer, so a lead only goes with the twerk-only sounds.
        (Rx($@"^(?<lead>(?:can|could|will|would) you (?:please )?|do (?:a|the) )(?:{TwerkSounds})$".Replace("|work|", "|")), "${lead}twerk"),
        (Rx($@"^(?:{TwerkSounds})$"), "twerk"),
        // play: "place no halation", "blay sky fall by adele", "lay a song", "pleadel sky fall"
        (Rx(@"^(?:place|blay|plate) (?<rest>.+)$"), "play ${rest}"),
        (Rx(@"^lay (?<rest>(?:a|some|the|me|my|music) ?.*)$"), "play ${rest}"),
        (Rx(@"^pleadel (?<rest>.+)$"), "play adele ${rest}"),
        (Rx(@"^make (?:some )?music$"), "play some music"),
        // time: "the time is it", "but time is it", "where time is it", "why is the time", "was the time"
        (Rx(@"^(?:the|but|where|was|what) time is(?: it)?$"), "what time is it"),
        (Rx(@"^(?:(?:why|where|was) is the time(?: \w+)?|was the time)$"), "what time is it"),
        // pizza: "make a peter", "eat a peter"
        (Rx(@"^(?:make|eat)(?: me| us)? a peter$"), "make a pizza"),
        // snow globe: "b a snow globe", "be a snow gulb", "be a snow"
        (Rx(@"^(?:b|be|bee) a snow(?: g\w*)?$"), "be a snow globe"),
        // coin: "flipper coin", "answer tales"
        (Rx(@"^flipper coin$"), "flip a coin"),
        (Rx(@"^answer (?:tails?|tales)$"), "heads or tails"),
        // talk to google/alexa: "talk to goog", "talk to googall assist", "yes we'll talk to alexe"
        (Rx(@"^(?:(?:yes|okay|ok) )?(?:(?:we'll|we ll|let's|lets|let s) )?talk to goog\w*(?: assist\w*)?$"), "talk to google"),
        (Rx(@"^(?:(?:yes|okay|ok) )?(?:(?:we'll|we ll|let's|lets|let s) )?talk to alex\w*$"), "talk to alexa"),
        // halloween: "happy hollowayne", "happy howowen", "happy hollo wean"
        (Rx(@"^happy (?:hollo|howo|hallo|holla)\w*(?: wean| ween)?$"), "happy halloween"),
        // weather heard as "whether"
        (Rx(@"^(?:whether|the whether)$"), "weather"),
        (Rx(@"^(?:what's |what is |how's )?the temperature outside$"), "what's the temperature outside"),
        // asking for the skills list opens the menu that lists them
        (Rx(@"^(?:show me|open|what are) (?:your|my|the|all) skills$|^(?:what skills do you have|open skills)$"), "open the menu"),
        // cut off at the end
        (Rx(@"^slee$"), "sleep"),
        (Rx(@"^go to sle\w*$"), "go to sleep"),
        (Rx(@"^(?:give me my |my )?personal rep\w*$"), "personal report"),
        (Rx(@"^what's on my calend\w*$|^what s on my calend\w*$"), "what's on my calendar")
    ];

    /// <summary>The repaired command, or null when <paramref name="transcript"/> is not a known mishearing.</summary>
    public static string? Repair(string? transcript)
    {
        var text = TranscriptTextNormalizer.NormalizeLooseText(transcript);
        text = TranscriptTextNormalizer.StripLeadingPhrases(text, "hey jibo", "hey jebo", "hey jivo", "jibo", "uh", "um");
        if (text.Length == 0 || text.Length > 80) return null;

        foreach (var (pattern, replacement) in Rules)
        {
            var match = pattern.Match(text);
            if (!match.Success) continue;
            var repaired = match.Result(replacement).Trim();
            return repaired == text ? null : repaired;
        }

        return null;
    }

    /// <summary>The same turn with its transcript repaired (the heard text stays in RawTranscript).</summary>
    public static TurnContext Apply(TurnContext turn)
    {
        if (SkillListenOwnership.IsSkillOwnedListen(turn) || AgentModeRouting.IsConversation(turn)) return turn;
        var repaired = Repair(turn.NormalizedTranscript ?? turn.RawTranscript);
        if (repaired is null) return turn;

        turn.Attributes["stt:misheardTranscript"] = turn.NormalizedTranscript ?? turn.RawTranscript;
        return new TurnContext
        {
            TurnId = turn.TurnId,
            SessionId = turn.SessionId,
            TimestampUtc = turn.TimestampUtc,
            InputMode = turn.InputMode,
            SourceKind = turn.SourceKind,
            WakePhrase = turn.WakePhrase,
            RawTranscript = turn.RawTranscript,
            NormalizedTranscript = repaired,
            DeviceId = turn.DeviceId,
            HostName = turn.HostName,
            RequestId = turn.RequestId,
            ProtocolService = turn.ProtocolService,
            ProtocolOperation = turn.ProtocolOperation,
            FirmwareVersion = turn.FirmwareVersion,
            ApplicationVersion = turn.ApplicationVersion,
            Locale = turn.Locale,
            TimeZone = turn.TimeZone,
            IsFollowUpEligible = turn.IsFollowUpEligible,
            Attributes = turn.Attributes
        };
    }

    private static Regex Rx(string pattern) =>
        new(pattern, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(50));
}
