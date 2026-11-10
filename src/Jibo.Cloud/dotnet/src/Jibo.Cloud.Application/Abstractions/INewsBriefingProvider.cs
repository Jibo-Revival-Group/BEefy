namespace Jibo.Cloud.Application.Abstractions;

public interface INewsBriefingProvider
{
    Task<NewsBriefingSnapshot?> GetBriefingAsync(
        NewsBriefingRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record NewsBriefingRequest(
    IReadOnlyList<string> PreferredCategories,
    int MaxHeadlines = 3);

public sealed record NewsHeadline(
    string Title,
    string? Summary = null,
    string? Category = null,
    string? SourceName = null,
    string? Url = null,
    NewsImage? Image = null);

/// <summary>A headline photo; the stock newsHeadline view scales it from its pixel size.</summary>
public sealed record NewsImage(string Url, int Width, int Height);

public sealed record NewsBriefingSnapshot(
    IReadOnlyList<NewsHeadline> Headlines,
    string? SourceName = null,
    string? ProviderStatus = null,
    string? ProviderMessage = null,
    int? ProviderHttpStatusCode = null,
    string? ProviderEndpoint = null,
    string? ProviderErrorCode = null);