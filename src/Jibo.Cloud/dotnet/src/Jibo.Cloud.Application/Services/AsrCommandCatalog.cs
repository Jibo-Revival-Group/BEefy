namespace Jibo.Cloud.Application.Services;

/// <summary>Supported command hypotheses for contextual speech recovery, not error replacements.</summary>
public static class AsrCommandCatalog
{
    public static IReadOnlyList<string> Candidates { get; } = UtteranceFrameParser.CorrectionCandidates()
        .Concat(new[] { "what time is it", "what is the time", "what day is it", "what is the date",
            "tell me a joke", "tell me a story", "what is the weather", "what is your name",
            "how old are you", "where are you from", "what can you do" })
        .Distinct(StringComparer.Ordinal).ToArray();
}
