using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jibo.Cloud.Infrastructure.Audio;

/// <summary>
/// Loads the Zipformer OnlineRecognizer during process startup so the first listen
/// turn does not block the websocket receive loop on ONNX init.
/// </summary>
public sealed class SherpaRecognizerWarmupHostedService(
    BufferedAudioSttOptions sttOptions,
    SherpaOnlineRecognizerProvider recognizerProvider,
    ILogger<SherpaRecognizerWarmupHostedService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var resolved = BufferedAudioSttPathResolver.Resolve(sttOptions);
        if (!resolved.EnableStreamingSherpa)
            return Task.CompletedTask;

        return Task.Run(() =>
        {
            try
            {
                if (!recognizerProvider.TryResolveModel(out var model))
                {
                    logger.LogInformation("Sherpa warmup skipped — model not available yet.");
                    return;
                }

                cancellationToken.ThrowIfCancellationRequested();
                recognizerProvider.GetOrCreate(model);
                logger.LogInformation("Sherpa streaming recognizer warmed up from {Directory}", model.Directory);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // shutdown during warmup
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Sherpa recognizer warmup failed; first turn may be slower.");
            }
        }, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
