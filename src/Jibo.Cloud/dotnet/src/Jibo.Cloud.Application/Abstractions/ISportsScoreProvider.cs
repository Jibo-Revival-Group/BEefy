namespace Jibo.Cloud.Application.Abstractions;

public interface ISportsScoreProvider
{
    /// <summary>
    /// A spoken answer about the team named in <paramref name="transcript"/> (today's game, its score, the last
    /// result or the next game), or null when no MLB/NFL/NBA/NHL team is named.
    /// </summary>
    Task<string?> AnswerAsync(string transcript, DateTimeOffset localNow, CancellationToken cancellationToken = default);
}
