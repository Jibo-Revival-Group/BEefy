using Jibo.Cloud.Application.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SherpaOnnx;

namespace Jibo.Cloud.Infrastructure.Audio;

/// <summary>
/// Shared Zipformer <see cref="OnlineRecognizer"/> used by batch STT and incremental endpointing.
/// </summary>
public sealed class SherpaOnlineRecognizerProvider : IDisposable
{
    private readonly BufferedAudioSttOptions _sttOptions;
    private readonly ListenEndpointingOptions _listenOptions;
    private readonly SherpaModelLocator _modelLocator;
    private readonly ILogger<SherpaOnlineRecognizerProvider> _logger;
    private readonly object _sync = new();
    private OnlineRecognizer? _recognizer;
    private string? _recognizerDirectory;
    private bool _disposed;

    public SherpaOnlineRecognizerProvider(
        BufferedAudioSttOptions sttOptions,
        ListenEndpointingOptions listenOptions,
        SherpaModelLocator modelLocator,
        ILogger<SherpaOnlineRecognizerProvider>? logger = null)
    {
        _sttOptions = BufferedAudioSttPathResolver.Resolve(sttOptions);
        _listenOptions = listenOptions;
        _modelLocator = modelLocator;
        _logger = logger ?? NullLogger<SherpaOnlineRecognizerProvider>.Instance;
    }

    public bool TryResolveModel(out SherpaModelLocator.ModelPaths model)
    {
        model = null!;
        if (!_sttOptions.EnableStreamingSherpa)
            return false;

        try
        {
            var resolved = _modelLocator.Resolve(_sttOptions);
            if (resolved is null)
                return false;
            model = resolved;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Sherpa model unavailable for incremental/batch STT.");
            return false;
        }
    }

    public OnlineRecognizer GetOrCreate(
        SherpaModelLocator.ModelPaths model,
        float? rule3MinUtteranceLengthSeconds = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var rule3 = rule3MinUtteranceLengthSeconds ?? _listenOptions.DefaultMaxUtteranceSeconds;
        lock (_sync)
        {
            if (_recognizer is not null &&
                string.Equals(_recognizerDirectory, model.Directory, StringComparison.OrdinalIgnoreCase))
                return _recognizer;

            _recognizer?.Dispose();
            // Endpoint rules stay on so incremental sessions and batch finalize share one recognizer.
            // Batch calls InputFinished and ignores IsEndpoint.
            _recognizer = CreateRecognizer(model, rule3);
            _recognizerDirectory = model.Directory;
            _logger.LogInformation(
                "Initialized Sherpa streaming recognizer from {Directory}",
                model.Directory);
            return _recognizer;
        }
    }

    public T WithRecognizerLock<T>(Func<T> action)
    {
        lock (_sync)
            return action();
    }

    public void WithRecognizerLock(Action action)
    {
        lock (_sync)
            action();
    }

    private OnlineRecognizer CreateRecognizer(
        SherpaModelLocator.ModelPaths model,
        float rule3MinUtteranceLengthSeconds)
    {
        var config = new OnlineRecognizerConfig();
        config.FeatConfig.SampleRate = 16000;
        config.FeatConfig.FeatureDim = 80;
        config.ModelConfig.Tokens = model.Tokens;
        config.ModelConfig.Transducer.Encoder = model.Encoder;
        config.ModelConfig.Transducer.Decoder = model.Decoder;
        config.ModelConfig.Transducer.Joiner = model.Joiner;
        config.ModelConfig.NumThreads = _sttOptions.WhisperThreads > 0
            ? _sttOptions.WhisperThreads
            : Math.Max(1, Environment.ProcessorCount / 2);
        config.ModelConfig.Provider = "cpu";
        config.DecodingMethod = "greedy_search";
        config.EnableEndpoint = 1;
        config.Rule1MinTrailingSilence = Math.Max(0.1f, _listenOptions.Rule1MinTrailingSilenceSeconds);
        config.Rule2MinTrailingSilence = Math.Max(
            config.Rule1MinTrailingSilence,
            _listenOptions.Rule2MinTrailingSilenceSeconds);
        config.Rule3MinUtteranceLength = Math.Max(1f, rule3MinUtteranceLengthSeconds);

        // Do not attach openjibo-hotwords.txt: this Zipformer English model uses BPE
        // tokens, so word-level hotwords like "Jibo" fail to encode and force a broken
        // modified_beam_search path (see sherpa EncodeBase / InitHotwords warnings).

        return new OnlineRecognizer(config);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_sync)
        {
            _recognizer?.Dispose();
            _recognizer = null;
        }
    }
}
