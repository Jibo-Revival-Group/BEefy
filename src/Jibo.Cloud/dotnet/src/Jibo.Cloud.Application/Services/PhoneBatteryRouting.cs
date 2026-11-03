using System.Text.RegularExpressions;

namespace Jibo.Cloud.Application.Services;

/// "How is my phone's battery?": only the robot knows (BEep reports to it), so @be/phone-battery answers.
public static partial class PhoneBatteryRouting
{
    public static bool IsQuestion(string text)
    {
        var normalized = TranscriptTextNormalizer.NormalizeLooseText(text);
        return Question().IsMatch(normalized) || Status().IsMatch(normalized);
    }

    [GeneratedRegex(@"\b(?:i ?)?phones?\b.*\b(?:battery|charge|charged)\b|\b(?:battery|charge)\b.*\b(?:i ?)?phones?\b")]
    private static partial Regex Question();

    // "what's my phone at", "how's my phone doing", "how is my phone": the whole question, so "how is my phone number" doesn't count.
    [GeneratedRegex(@"\b(?:what(?: s|'s|s| is)|how(?: s|'s|s| is))\s+my\s+(?:i ?)?phone(?:\s+(?:at|doing|looking))?$")]
    private static partial Regex Status();
}
