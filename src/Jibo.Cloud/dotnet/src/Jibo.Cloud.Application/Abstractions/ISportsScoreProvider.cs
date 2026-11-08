namespace Jibo.Cloud.Application.Abstractions;

public interface ISportsScoreProvider
{
    /// <summary>
    /// A spoken answer about the team named in <paramref name="transcript"/> (today's game, its score, the last
    /// result or the next game), or null when no MLB/NFL/NBA/NHL team is named.
    /// </summary>
    Task<SportsAnswer?> AnswerAsync(string transcript, DateTimeOffset localNow, CancellationToken cancellationToken = default);
}

/// <summary>The spoken answer, plus the score card when the game it describes has a score.</summary>
public sealed record SportsAnswer(string Spoken, SportsScoreCard? Card = null);

/// <summary>
/// Away team on the left, home on the right (as on a scoreboard). <see cref="Background"/> is a PNG data URI with
/// each half a top-to-bottom gradient of that team's color; <see cref="Shade"/> is a black overlay PNG that darkens each
/// half toward its outer edge. (Two images because each compresses to ~5 KB; combined it was ~200 KB, sent 4 times
/// in the view, and the robot dropped the message.)
/// </summary>
public sealed record SportsScoreCard(SportsCardSide Away, SportsCardSide Home, string Background, string? Shade = null);

/// <summary><see cref="Logo"/> is a plain-http URL: the robot's browser can't open https on today's servers.</summary>
public sealed record SportsCardSide(string Name, int Score, string Color, string? Logo);
