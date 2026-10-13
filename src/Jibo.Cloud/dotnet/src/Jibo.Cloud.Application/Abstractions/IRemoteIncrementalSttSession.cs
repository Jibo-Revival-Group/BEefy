using Jibo.Cloud.Domain.Models;
using Jibo.Runtime.Abstractions;

namespace Jibo.Cloud.Application.Abstractions;

/// <summary>A streaming provider that can drain its existing recognition at turn finalization.</summary>
public interface IRemoteIncrementalSttSession : IIncrementalSttSession
{
    Task<SttResult> FinishAsync(CancellationToken cancellationToken);
}

public interface IContextualIncrementalSttSessionFactory : IIncrementalSttSessionFactory
{
    IIncrementalSttSession? TryCreate(string sessionId, string turnId, TimeSpan? maxSpeechTimeout);
}
