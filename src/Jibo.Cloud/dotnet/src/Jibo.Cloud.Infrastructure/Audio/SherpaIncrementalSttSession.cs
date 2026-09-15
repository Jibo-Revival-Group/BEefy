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
    private string _partialText = string.Empty;
    private bool _isEndpoint;
    private bool _disposed;

    public SherpaIncrementalSttSession(
        SherpaOnlineRecognizerProvider provider,
        OnlineRecognizer recognizer,
        OnlineStream stream,
        ILogger? logger = null)
    {
        _provider = provider;
        _recognizer = recognizer;
        _stream = stream;
        _logger = logger ?? NullLogger.Instance;
    }

    public string PartialText
    {
        get
        {
            lock (_sessionLock)
                return _partialText;
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
        ObjectDisposedException.ThrowIf(_disposed, this);
        var pcm = _pcmDecoder.DecodeNewTo16kMono(frames);
        if (pcm.Length == 0)
            return;

        lock (_sessionLock)
        {
            _provider.WithRecognizerLock(() =>
            {
                _stream.AcceptWaveform(16000, pcm);
                while (_recognizer.IsReady(_stream))
                    _recognizer.Decode(_stream);

                var text = _recognizer.GetResult(_stream).Text?.Trim() ?? string.Empty;
                _partialText = AudioTranscriptNormalizer.NormalizeLooseTranscript(text);
                _isEndpoint = _recognizer.IsEndpoint(_stream);
            });
        }
    }

    public void ResetEndpoint()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_sessionLock)
        {
            _provider.WithRecognizerLock(() =>
            {
                _recognizer.Reset(_stream);
                _isEndpoint = false;
            });
            _logger.LogDebug("Sherpa incremental endpoint reset; partial={Partial}", _partialText);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_sessionLock)
        {
            try
            {
                _stream.Dispose();
            }
            catch
            {
                // ignore dispose races
            }

            _pcmDecoder.Dispose();
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
            var recognizer = recognizerProvider.GetOrCreate(model, rule3);
            var stream = recognizerProvider.WithRecognizerLock(() => recognizer.CreateStream());
            return new SherpaIncrementalSttSession(recognizerProvider, recognizer, stream, _logger);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create Sherpa incremental STT session.");
            return null;
        }
    }
}
