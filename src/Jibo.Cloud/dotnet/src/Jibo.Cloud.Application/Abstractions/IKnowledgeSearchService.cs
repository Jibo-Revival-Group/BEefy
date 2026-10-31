namespace Jibo.Cloud.Application.Abstractions;

public interface IKnowledgeSearchService
{
    bool IsConfigured { get; }

    Task<KnowledgeSearchResult?> SearchAsync(string query, CancellationToken cancellationToken = default);

    /// <summary>Search near the robot ("lat,long"), e.g. so "how far is New York" is measured from home, not the server.</summary>
    Task<KnowledgeSearchResult?> SearchAsync(string query, string? latLong, CancellationToken cancellationToken = default) =>
        SearchAsync(query, cancellationToken);
}