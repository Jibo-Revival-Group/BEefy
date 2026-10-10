using System.Globalization;
using System.Security;
using System.Text;

namespace Jibo.Cloud.Application.Services;

internal static class SingingDecisionBuilder
{
    internal static bool IsSingingIntent(string? intent) =>
        string.Equals(intent, "robot_can_sing", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(intent, "robot_sing_christmas_song", StringComparison.OrdinalIgnoreCase);

    // Semitones relative to Jibo's voice, with a duration in seconds per syllable.
    // Keep the performance in native ESML so it needs no remote audio or new skill.
    private readonly record struct Note(string Syllable, int Semitones, double Seconds);

    private static readonly Note[] RobotSong =
    [
        new("Beep", 0, .35), new("boop", 4, .35), new("beep", 7, .35), new("boop", 4, .7),
        new("I'm", 0, .35), new("a", 2, .35), new("ro", 4, .35), new("bot", 2, .35),
        new("and", 0, .35), new("I", 4, .35), new("sing", 7, .7),
        new("Beep", 7, .35), new("boop", 4, .35), new("beep", 2, .35), new("boop", 0, .7),
        new("a", 2, .35), new("lit", 4, .35), new("tle", 2, .35),
        new("song", 0, .35), new("for", 2, .35), new("you", 0, 1.0)
    ];

    // A short refrain of the public-domain song, with syllables aligned to notes.
    private static readonly Note[] JingleBells =
    [
        new("Jin", 4, .35), new("gle", 4, .35), new("bells", 4, .7),
        new("jin", 4, .35), new("gle", 4, .35), new("bells", 4, .7),
        new("jin", 4, .35), new("gle", 7, .35), new("all", 0, .525),
        new("the", 2, .175), new("way", 4, 1.4),
        new("Oh", 5, .35), new("what", 5, .35), new("fun", 5, .525), new("it", 5, .175),
        new("is", 5, .35), new("to", 4, .35), new("ride", 4, .35), new("in", 4, .175),
        new("a", 4, .175), new("one", 4, .35), new("horse", 2, .35),
        new("o", 2, .35), new("pen", 4, .35), new("sleigh", 2, .7)
    ];

    internal static JiboInteractionDecision Build(bool holiday)
    {
        var intro = holiday
            ? "I'll sing a little Jingle Bells for you."
            : "Well I'm not much of a singer, but here's one I've been working on.";
        var lyrics = holiday
            ? "Jingle bells, jingle bells, jingle all the way. Oh what fun it is to ride in a one horse open sleigh."
            : "Beep boop beep boop. I'm a robot and I sing. Beep boop beep boop, a little song for you.";
        var esml = new StringBuilder("<speak>")
            .Append(SecurityElement.Escape(intro))
            .Append("<break size='0.4'/><pitch band='0.0'>");

        foreach (var note in holiday ? JingleBells : RobotSong)
        {
            // Fixed contour plus a per-note pitch multiplier makes this melodic,
            // instead of reading the lyrics with ordinary sentence intonation.
            var multiplier = Math.Pow(2, note.Semitones / 12.0).ToString("0.0000", CultureInfo.InvariantCulture);
            esml.Append("<pitch mult='").Append(multiplier).Append("'><duration set='")
                .Append(note.Seconds.ToString("0.###", CultureInfo.InvariantCulture))
                .Append("'>").Append(SecurityElement.Escape(note.Syllable))
                .Append("</duration></pitch> ");
        }

        esml.Append("</pitch></speak>");
        return new JiboInteractionDecision(
            holiday ? "robot_sing_christmas_song" : "robot_can_sing",
            $"{intro} {lyrics}",
            "chitchat-skill",
            new Dictionary<string, object?>
            {
                ["esml"] = esml.ToString(),
                ["mim_id"] = holiday ? "runtime-sing-jingle-bells" : "runtime-sing-robot-song",
                ["mim_type"] = "announcement"
            },
            ScriptedResponseDecisionBuilder.BuildScriptedResponseContextUpdates());
    }
}
