namespace Jibo.Cloud.Application.Abstractions;

public interface IImageSearchProvider
{
    /// <summary>
    /// The top safe-search web image for <paramref name="query"/> as a 1280x720 JPEG URL the robot can load
    /// (plain http, CORS-open), or null when there is none.
    /// </summary>
    Task<string?> FindAsync(string query, CancellationToken cancellationToken = default);
}
