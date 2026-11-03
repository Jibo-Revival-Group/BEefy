using System.Text.RegularExpressions;

namespace Jibo.Cloud.Application.Services;

/// "How is my phone's battery?": only the robot knows (BEep reports to it), so @be/phone-battery answers.
public static partial class PhoneBatteryRouting
{
    public static bool IsQuestion(string text) => Question().IsMatch(TranscriptTextNormalizer.NormalizeLooseText(text));

    [GeneratedRegex(@"\b(?:i ?)?phones?\b.*\b(?:battery|charge|charged)\b|\b(?:battery|charge)\b.*\b(?:i ?)?phones?\b")]
    private static partial Regex Question();
}
