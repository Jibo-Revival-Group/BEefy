using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Application.Services;
using Jibo.Cloud.Domain.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SherpaOnnx;

namespace Jibo.Cloud.Infrastructure.Audio;

/// <summary>
/// Live Zipformer stream for one robot listen turn. Per-session lock; shares the
/// process-wide <see cref="SherpaOnlineRecognizerProvider"/> for model weights.
/// </summary>
internal sealed class SherpaIncrementalSttSession : IIncrementalSttSession
{
    private readonly SherpaOnlineRecognizerProvider _provider;
    private readonly OnlineRecognizer _recognizer;
    private readonly OnlineStream _stream;
    private readonly OggOpusIncrementalPcmDecoder _pcmDecoder = new();
    private readonly object _sessionLock = new();
    private readonly ILogger _logger;
    private readonly StreamingTranscriptAccumulator _transcript = new();
    private bool _isEndpoint;
    private bool _disposed;
    private readonly bool _ownsLease;

    public SherpaIncrementalSttSession(
        SherpaOnlineRecognizerProvider provider,
        OnlineRecognizer recognizer,
        OnlineStream stream,
        ILogger? logger = null, bool ownsLease = false)
    {
        _provider = provider;
        _recognizer = recognizer;
        _stream = stream;
        _logger = logger ?? NullLogger.Instance;
        _ownsLease = ownsLease;
    }

    public string PartialText
    {
        get
        {
            lock (_sessionLock)
                return _transcript.Text;
        }
    }

    public bool IsEndpoint
    {
        get
        {
            lock (_sessionLock)
                return _isEndpoint;
        }
    }

    public void AcceptFrames(IReadOnlyList<byte[]> frames)
    {
        // Check and decode under the session lock: a concurrent Dispose frees the native stream, and
        // feeding a freed stream crashes the whole process (sherpa-onnx dereferences a null handle).
        lock (_sessionLock)
        {
            if (_disposed) return;
            var pcm = _pcmDecoder.DecodeNewTo16kMono(frames);
            if (pcm.Length == 0)
                return;
            _provider.WithRecognizerLock(() =>
            {
                _stream.AcceptWaveform(16000, pcm);
                while (_recognizer.IsReady(_stream))
                    _recognizer.Decode(_stream);

                var text = _recognizer.GetResult(_stream).Text?.Trim() ?? string.Empty;
                _transcript.Update(AudioTranscriptNormalizer.NormalizeLooseTranscript(text));
                _isEndpoint = _recognizer.IsEndpoint(_stream);
            });
        }
    }

    public void ResetEndpoint()
    {
        lock (_sessionLock)
        {
            if (_disposed) return;
            _provider.WithRecognizerLock(() =>
            {
                if (!_isEndpoint) return;
                _recognizer.Reset(_stream);
                _transcript.Commit();
                _isEndpoint = false;
            });
            _logger.LogDebug("Sherpa incremental endpoint reset; partial={Partial}", _transcript.Text);
        }
    }

    public void Dispose()
    {
        lock (_sessionLock)
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                _stream.Dispose();
            }
            catch
            {
                // ignore dispose races
            }

            _pcmDecoder.Dispose();
            if (_ownsLease) _provider.ReleaseStream();
        }
    }
}

/// <summary>
/// Creates Sherpa incremental sessions when model endpointing + streaming Sherpa are enabled.
/// </summary>
public sealed class SherpaIncrementalSttSessionFactory(
    BufferedAudioSttOptions sttOptions,
    ListenEndpointingOptions listenOptions,
    SherpaOnlineRecognizerProvider recognizerProvider,
    ILogger<SherpaIncrementalSttSessionFactory>? logger = null) : IIncrementalSttSessionFactory
{
    private readonly BufferedAudioSttOptions _sttOptions = BufferedAudioSttPathResolver.Resolve(sttOptions);
    private readonly ILogger<SherpaIncrementalSttSessionFactory> _logger =
        logger ?? NullLogger<SherpaIncrementalSttSessionFactory>.Instance;

    public bool IsEnabled =>
        listenOptions.EnableModelEndpointing && _sttOptions.EnableStreamingSherpa;

    public IIncrementalSttSession? TryCreate(TimeSpan? maxSpeechTimeout)
    {
        if (!IsEnabled)
            return null;

        if (!recognizerProvider.TryResolveModel(out var model))
        {
            _logger.LogDebug("Model endpointing enabled but Sherpa model is unavailable.");
            return null;
        }

        try
        {
            float? rule3 = maxSpeechTimeout is { } max && max > TimeSpan.Zero
                ? (float)max.TotalSeconds
                : null;
            var (recognizer, stream) = recognizerProvider.CreateLeasedStream(model, rule3);
            try { return new SherpaIncrementalSttSession(recognizerProvider, recognizer, stream, _logger, ownsLease: true); }
            catch
            {
                try { stream.Dispose(); }
                finally { recognizerProvider.ReleaseStream(); }
                throw;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create Sherpa incremental STT session.");
            return null;
        }
    }
}
