namespace Jibo.Cloud.Domain.Models;

/// <summary>
/// Per-turn incremental speech recognition used for model-driven end-of-speech.
/// </summary>
public interface IIncrementalSttSession : IDisposable
{
    string PartialText { get; }
    bool IsEndpoint { get; }
    void AcceptFrames(IReadOnlyList<byte[]> frames);
    void ResetEndpoint();
}
