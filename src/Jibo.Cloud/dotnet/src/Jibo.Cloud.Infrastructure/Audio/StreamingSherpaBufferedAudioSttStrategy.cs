using Jibo.Cloud.Application.Services;
using Jibo.Runtime.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jibo.Cloud.Infrastructure.Audio;

/// <summary>
/// In-process streaming Zipformer STT via sherpa-onnx + Concentus Opus decode.
/// Preferred over whisper.cpp when <see cref="BufferedAudioSttOptions.EnableStreamingSherpa"/> is true.
/// </summary>
public sealed class StreamingSherpaBufferedAudioSttStrategy : ISttStrategy
{
    private readonly BufferedAudioSttOptions _options;
    private readonly SherpaOnlineRecognizerProvider _recognizerProvider;
    private readonly ILogger<StreamingSherpaBufferedAudioSttStrategy> _logger;

    public StreamingSherpaBufferedAudioSttStrategy(
        BufferedAudioSttOptions options,
        SherpaOnlineRecognizerProvider recognizerProvider,
        ILogger<StreamingSherpaBufferedAudioSttStrategy>? logger = null)
    {
        _options = BufferedAudioSttPathResolver.Resolve(options);
        _recognizerProvider = recognizerProvider;
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
            return _recognizerProvider.TryResolveModel(out _);
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

        if (!_recognizerProvider.TryResolveModel(out var model))
            throw new InvalidOperationException("Sherpa streaming model files were not found.");

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
        // Batch finalize shares the endpoint-enabled recognizer; InputFinished drains the stream.
        var recognizer = _recognizerProvider.GetOrCreate(model);
        return _recognizerProvider.WithRecognizerLock(() =>
        {
            using var stream = recognizer.CreateStream();
            stream.AcceptWaveform(16000, pcm);
            stream.InputFinished();
            while (recognizer.IsReady(stream))
                recognizer.Decode(stream);

            return recognizer.GetResult(stream).Text?.Trim() ?? string.Empty;
        });
    }

    private static IReadOnlyList<byte[]> ReadBufferedAudioFrames(TurnContext turn)
    {
        if (turn.Attributes.TryGetValue("bufferedAudioFrames", out var value) &&
            value is IReadOnlyList<byte[]> frames)
            return frames;

        return [];
    }
}
