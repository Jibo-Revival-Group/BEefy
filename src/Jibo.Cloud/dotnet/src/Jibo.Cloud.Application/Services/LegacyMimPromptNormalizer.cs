using System.Net;
using System.Text.RegularExpressions;

namespace Jibo.Cloud.Application.Services;

public static class LegacyMimPromptNormalizer
{
    private static readonly Regex PlaceholderPattern = new(
        @"\$\{[^}]+\}",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex SsaEmotionPattern = new(
        @"<ssa\s+cat\s*=\s*['""](?<cat>[^'""]+)['""][^>]*/?>",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AnimBlockPattern = new(
        @"<anim[^>]*>(.*?)</anim>",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex AnimSelfClosingPattern = new(
        @"<anim[^>]*/>",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex StyleBlockPattern = new(
        @"<style[^>]*>(.*?)</style>",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex PitchBlockPattern = new(
        @"<pitch[^>]*>(.*?)</pitch>",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex SsaStripPattern = new(
        @"<ssa[^>]*/?>",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex WhitespacePattern = new(
        @"\s+",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex SpaceBeforePunctuationPattern = new(
        @"\s+([,.;:!?])",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public sealed class Result
    {
        public string Text { get; init; } = string.Empty;
        public string? Emotion { get; init; }
    }

    private static readonly Regex ResidualMarkupPattern = new(
        @"<[^>]+>",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex SpeechBreakPattern = new(
        @"<break\s+size\s*=\s*['""](?<size>\d*\.?\d+)['""]\s*/?>",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Plain SpeakAction text may contain imported legacy tags. Preserve recognized
    // pauses as ESML; remove residual markup before XML-escaping spoken words.
    public static string ToEsmlBody(string? prompt)
    {
        var text = WebUtility.HtmlDecode(prompt ?? string.Empty);
        var builder = new System.Text.StringBuilder();
        var offset = 0;
        foreach (Match pause in SpeechBreakPattern.Matches(text))
        {
            AppendWords(text[offset..pause.Index]);
            builder.Append("<break size='").Append(pause.Groups["size"].Value).Append("'/>");
            offset = pause.Index + pause.Length;
        }
        AppendWords(text[offset..]);
        return builder.ToString().Trim();

        void AppendWords(string words)
        {
            var clean = Normalize(words, preservePlaceholders: false).Text;
            if (clean.Length > 0) builder.Append(clean.Replace("&", "&amp;", StringComparison.Ordinal)
                .Replace("<", "&lt;", StringComparison.Ordinal)
                .Replace(">", "&gt;", StringComparison.Ordinal)).Append(' ');
        }
    }

    public static Result Normalize(string? prompt, bool preservePlaceholders, bool preserveTtsMarkup = false)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return new Result();

        var text = WebUtility.HtmlDecode(prompt);
        string? emotion = null;

        foreach (Match match in SsaEmotionPattern.Matches(text))
            emotion = match.Groups["cat"].Value.Trim();

        text = PitchBlockPattern.Replace(text, match => $" {match.Groups[1].Value} ");
        text = StyleBlockPattern.Replace(text, match => $" {match.Groups[1].Value} ");
        text = AnimBlockPattern.Replace(text, match => $" {match.Groups[1].Value} ");
        text = AnimSelfClosingPattern.Replace(text, " ");
        text = SsaStripPattern.Replace(text, " ");

        if (!preservePlaceholders) text = PlaceholderPattern.Replace(text, " ");
        if (!preserveTtsMarkup) text = ResidualMarkupPattern.Replace(text, " ");

        text = WhitespacePattern.Replace(text, " ").Trim();
        text = SpaceBeforePunctuationPattern.Replace(text, "$1");
        text = WhitespacePattern.Replace(text, " ").Trim();
        text = text.TrimStart('.', ',', ';', ':', '!', '?', ' ');

        return new Result
        {
            Text = text.Trim(),
            Emotion = string.IsNullOrWhiteSpace(emotion) ? null : emotion
        };
    }
}
