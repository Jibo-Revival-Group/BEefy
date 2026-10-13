using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Domain.Models;
using Jibo.Runtime.Abstractions;

namespace Jibo.Cloud.Infrastructure.Audio;

public sealed class BeefyErSessionFactory(
    BeefyErOptions options, BeefyErBroker broker, SherpaIncrementalSttSessionFactory local)
    : IContextualIncrementalSttSessionFactory
{
    public bool IsEnabled => options.Enabled || local.IsEnabled;
    public IIncrementalSttSession? TryCreate(TimeSpan? maxSpeechTimeout)
        => TryCreate("buffered", Guid.NewGuid().ToString("N"), maxSpeechTimeout);

    public IIncrementalSttSession? TryCreate(string sessionId, string turnId, TimeSpan? maxSpeechTimeout)
    {
        if (!options.Enabled) return local.TryCreate(maxSpeechTimeout);
        var remote = broker.TryCreate(sessionId, turnId, maxSpeechTimeout);
        if (remote is null)
        {
            broker.RecordFallback("unavailable_or_busy");
        }
        return new FailoverSession(remote, local, maxSpeechTimeout, broker);
    }

    internal sealed class FailoverSession(
        BeefyErSession? remote, IIncrementalSttSessionFactory localFactory,
        TimeSpan? maxSpeechTimeout, BeefyErBroker broker) : IRemoteIncrementalSttSession
    {
        private IIncrementalSttSession? _local = remote is null ? localFactory.TryCreate(maxSpeechTimeout) : null;
        private IReadOnlyList<byte[]> _frames = [];
        private bool _failedOver = remote is null;
        private bool _disposed;
        public bool IsAvailable { get { EnsureFallback(); return !_failedOver || _local is not null; } }
        public string PartialText { get { EnsureFallback(); return _failedOver ? _local?.PartialText ?? "" : remote!.PartialText; } }
        public bool IsEndpoint { get { EnsureFallback(); return _failedOver ? _local?.IsEndpoint ?? false : remote!.IsEndpoint; } }

        private void EnsureFallback()
        {
            if (_disposed || _failedOver || remote is null || !remote.Failed) return;
            _failedOver = true;
            broker.RecordFallback(remote.Failure);
            remote.Dispose();
            _local = localFactory.TryCreate(maxSpeechTimeout);
            _local?.AcceptFrames(_frames);
        }

        public void AcceptFrames(IReadOnlyList<byte[]> frames)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _frames = frames.ToArray();
            EnsureFallback();
            if (_failedOver) _local?.AcceptFrames(frames);
            else { remote!.AcceptFrames(frames); EnsureFallback(); }
        }

        public void ResetEndpoint()
        {
            EnsureFallback();
            if (_failedOver) _local?.ResetEndpoint(); else remote!.ResetEndpoint();
        }

        public async Task<SttResult> FinishAsync(CancellationToken cancellationToken)
        {
            EnsureFallback();
            if (_failedOver) throw new IOException("Remote recognition failed; use buffered local transcription.");
            try { return await remote!.FinishAsync(cancellationToken); }
            finally { EnsureFallback(); }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            remote?.Dispose();
            _local?.Dispose();
            _frames = [];
        }
    }
}

/// <summary>Uses the live result, falling back to the existing buffered providers on remote failure.</summary>
public sealed class BeefyErSttStrategy(
    BeefyErOptions options, IContextualIncrementalSttSessionFactory factory, IEnumerable<ISttStrategy> localStrategies) : ISttStrategy
{
    public const string SessionAttribute = "remoteIncrementalSttSession";
    public string Name => "beefyer-nemotron-streaming";
    public bool CanHandle(TurnContext turn) => options.Enabled &&
        turn.Attributes.TryGetValue("bufferedAudioFrames", out var frames) && frames is IReadOnlyList<byte[]> { Count: > 0 };

    public async Task<SttResult> TranscribeAsync(TurnContext turn, CancellationToken cancellationToken = default)
    {
        var frames = (IReadOnlyList<byte[]>)turn.Attributes["bufferedAudioFrames"]!;
        turn.Attributes.TryGetValue(SessionAttribute, out var sessionObject);
        var session = sessionObject as IIncrementalSttSession;
        var ownsSession = session is null;
        session ??= factory.TryCreate(turn.SessionId, turn.TurnId, null);
        try
        {
            if (session is IRemoteIncrementalSttSession remote)
            {
                try
                {
                    remote.AcceptFrames(frames);
                    return await remote.FinishAsync(cancellationToken);
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested &&
                                           ex is IOException or TimeoutException or System.Net.WebSockets.WebSocketException)
                {
                    // Reprocess the retained original audio, never a partial remote hypothesis.
                }
            }
            var local = localStrategies.FirstOrDefault(candidate => candidate.CanHandle(turn))
                ?? throw new InvalidOperationException("BEefyER unavailable and no local ASR provider can handle this turn.");
            return await local.TranscribeAsync(turn, cancellationToken);
        }
        finally { if (ownsSession) session?.Dispose(); }
    }
}
