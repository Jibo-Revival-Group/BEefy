using System.Text.RegularExpressions;

namespace Jibo.Cloud.Application.Services;

public static class MusicCommandParser
{
    public sealed record Request(string Query, string? Artist);

    public static bool TryParse(string? transcript, out Request? request)
    {
        request = null;
        var match = Regex.Match(transcript ?? string.Empty,
            @"^\s*(?:(?:hey\s+)?jibo[,!]?\s+)?(?:(?:can|could|would)\s+you\s+)?(?:please\s+)?play\s+(?:the\s+song\s+|the\s+track\s+)?(?<query>.+?)\s*[.!?]*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success) return false;
        var query = match.Groups["query"].Value.Trim().Trim('"', '“', '”');
        if (query.Length is 0 or > 160) return false;
        // Preserve established activities and optional skill launch commands.
        var reserved = query.ToLowerInvariant().TrimEnd('.', '!', '?');
        if (Regex.IsMatch(reserved, @"^(?:(?:the|some|a|me|my)\s+)*(?:radio|music|song|songs|track|game|games|jibotube|jibo tube|jukebox|bad apple|word of the day|trivia|circuit saver|simon says|would you rather|dance|guitar)$"))
            return false;
        var artistAt = query.LastIndexOf(" by ", StringComparison.OrdinalIgnoreCase);
        string? artist = null;
        if (artistAt > 0 && artistAt + 4 < query.Length)
        {
            artist = query[(artistAt + 4)..].Trim().Trim('"', '“', '”');
            query = query[..artistAt].Trim().Trim('"', '“', '”');
        }
        request = new(query, artist);
        return true;
    }
}
