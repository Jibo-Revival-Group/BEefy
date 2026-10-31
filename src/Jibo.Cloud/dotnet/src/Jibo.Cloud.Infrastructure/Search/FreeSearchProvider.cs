using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Infrastructure.Audio;
using Microsoft.Extensions.Logging;

namespace Jibo.Cloud.Infrastructure.Search;

/// <summary>Answers knowledge questions with the Free model on the BEefyER worker (no paid search key needed).</summary>
public sealed class FreeSearchProvider(
    BeefyErBroker broker,
    SearchBackendOptions options,
    ILogger<FreeSearchProvider> logger)
    : IKnowledgeSearchProvider
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);

    public SearchBackendKind Kind => SearchBackendKind.Free;

    public async Task<KnowledgeSearchResult?> SearchAsync(
        KnowledgeSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.BackendSpec.Kind != SearchBackendKind.Free || string.IsNullOrWhiteSpace(request.Query))
            return null;

        var (system, user) = JiboKnowledgeSearchPrompts.BuildChatGptMessages(request.Query, options.LlmInstructions);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Deadline);
        try
        {
            var text = await broker.ChatAsync(
                [new FreeAgentChatMessage("system", system), new FreeAgentChatMessage("user", user)],
                deadline.Token,
                Deadline);
            var answer = KnowledgeSearchResponseFormatter.NormalizeForSpeech(text ?? string.Empty);
            return string.IsNullOrWhiteSpace(answer)
                ? KnowledgeSearchResult.NotFound(SearchBackendKind.Free)
                : new KnowledgeSearchResult(answer, SearchBackendKind.Free);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Free knowledge answer timed out.");
            return KnowledgeSearchResult.Unavailable(SearchBackendKind.Free);
        }
        catch (FreeAgentRelayException exception)
        {
            // free_busy/free_unavailable: the worker is in another chat or offline.
            logger.LogWarning("Free knowledge answer unavailable: {Code}.", exception.Code);
            return KnowledgeSearchResult.Unavailable(SearchBackendKind.Free);
        }
    }
}
