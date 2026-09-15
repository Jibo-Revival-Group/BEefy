using Jibo.Cloud.Domain.Models;

namespace Jibo.Cloud.Application.Abstractions;

/// <summary>
/// Creates optional incremental STT sessions when model endpointing is enabled.
/// Returns null when disabled or when the streaming model is unavailable.
/// </summary>
public interface IIncrementalSttSessionFactory
{
    bool IsEnabled { get; }

    IIncrementalSttSession? TryCreate(TimeSpan? maxSpeechTimeout);
}
