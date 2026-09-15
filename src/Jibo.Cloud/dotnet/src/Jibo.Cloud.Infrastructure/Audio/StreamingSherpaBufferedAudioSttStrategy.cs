using Jibo.Cloud.Application.Services;
using Jibo.Runtime.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SherpaOnnx;

namespace Jibo.Cloud.Infrastructure.Audio;

/// <summary>
/// In-process streaming Zipformer STT via sherpa-onnx + Concentus Opus decode.
/// Preferred over whisper.cpp when <see cref="BufferedAudioSttOptions.EnableStreamingSherpa"/> is true.
/// </summary>
public sealed class StreamingSherpaBufferedAudioSttStrategy : ISttStrategy
{
    private readonly BufferedAudioSttOptions _options;
    private readonly SherpaModelLocator _modelLocator;
    private readonly ILogger<StreamingSherpaBufferedAudioSttStrategy> _logger;
    private readonly object _recognizerSync = new();
    private OnlineRecognizer? _recognizer;
    private string? _recognizerDirectory;

    public StreamingSherpaBufferedAudioSttStrategy(
        BufferedAudioSttOptions options,
        SherpaModelLocator modelLocator,
        ILogger<StreamingSherpaBufferedAudioSttStrategy>? logger = null)
    {
        _options = BufferedAudioSttPathResolver.Resolve(options);
        _modelLocator = modelLocator;
        _logger = logger ?? NullLogger<StreamingSherpaBufferedAudioSttStrategy>.Instance;
    }

    public string Name => "streaming-sherpa-zipformer";

    public bool CanHandle(TurnContext turn)
    {
        if (!_options.EnableStreamingSherpa)
            return false;

        var frames = ReadBufferedAudioFrames(turn);
        if (frames.Count == 0)
            return false;

        try
        {
            return _modelLocator.Resolve(_options) is not null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Sherpa STT unavailable for turn {TurnId}", turn.TurnId);
            return false;
        }
    }

    public Task<SttResult> TranscribeAsync(TurnContext turn, CancellationToken cancellationToken = default)
    {
        var frames = ReadBufferedAudioFrames(turn);
        if (frames.Count == 0)
            throw new InvalidOperationException("Streaming Sherpa STT requires buffered websocket audio frames.");

        var model = _modelLocator.Resolve(_options)
                    ?? throw new InvalidOperationException("Sherpa streaming model files were not found.");

        cancellationToken.ThrowIfCancellationRequested();
        var pcm = OggOpusPcmDecoder.DecodeTo16kMono(frames);
        if (pcm.Length == 0)
            throw new InvalidOperationException("Streaming Sherpa STT could not decode Opus audio to PCM.");

        var text = Recognize(pcm, model);
        text = AudioTranscriptNormalizer.NormalizeLooseTranscript(text);

        return Task.FromResult(new SttResult
        {
            Text = text,
            Provider = Name,
            Confidence = string.IsNullOrWhiteSpace(text) ? 0.2f : 0.8f,
            Locale = turn.Locale ?? _options.WhisperLanguage,
            Metadata = new Dictionary<string, object?>
            {
                ["modelDirectory"] = model.Directory,
                ["pcmSamples"] = pcm.Length,
                ["engine"] = "sherpa-onnx-online-zipformer"
            }
        });
    }

    private string Recognize(float[] pcm, SherpaModelLocator.ModelPaths model)
    {
        lock (_recognizerSync)
        {
            var recognizer = GetOrCreateRecognizer(model);
            using var stream = recognizer.CreateStream();
            stream.AcceptWaveform(16000, pcm);
            stream.InputFinished();
            while (recognizer.IsReady(stream))
                recognizer.Decode(stream);

            return recognizer.GetResult(stream).Text?.Trim() ?? string.Empty;
        }
    }

    private OnlineRecognizer GetOrCreateRecognizer(SherpaModelLocator.ModelPaths model)
    {
        if (_recognizer is not null &&
            string.Equals(_recognizerDirectory, model.Directory, StringComparison.OrdinalIgnoreCase))
            return _recognizer;

        _recognizer?.Dispose();

        var config = new OnlineRecognizerConfig();
        config.FeatConfig.SampleRate = 16000;
        config.FeatConfig.FeatureDim = 80;
        config.ModelConfig.Tokens = model.Tokens;
        config.ModelConfig.Transducer.Encoder = model.Encoder;
        config.ModelConfig.Transducer.Decoder = model.Decoder;
        config.ModelConfig.Transducer.Joiner = model.Joiner;
        config.ModelConfig.NumThreads = _options.WhisperThreads > 0
            ? _options.WhisperThreads
            : Math.Max(1, Environment.ProcessorCount / 2);
        config.ModelConfig.Provider = "cpu";
        config.DecodingMethod = "greedy_search";
        config.EnableEndpoint = 0;
        if (!string.IsNullOrWhiteSpace(model.Hotwords))
        {
            config.HotwordsFile = model.Hotwords;
            config.HotwordsScore = 1.5f;
            config.DecodingMethod = "modified_beam_search";
            config.MaxActivePaths = 4;
        }

        _recognizer = new OnlineRecognizer(config);
        _recognizerDirectory = model.Directory;
        _logger.LogInformation("Initialized Sherpa streaming recognizer from {Directory}", model.Directory);
        return _recognizer;
    }

    private static IReadOnlyList<byte[]> ReadBufferedAudioFrames(TurnContext turn)
    {
        if (turn.Attributes.TryGetValue("bufferedAudioFrames", out var value) &&
            value is IReadOnlyList<byte[]> frames)
            return frames;

        return [];
    }
}
