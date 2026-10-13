using Microsoft.Extensions.Hosting;

namespace Jibo.Cloud.Infrastructure.Audio;

public sealed class SherpaIdleReleaseHostedService(BeefyErOptions options,
    SherpaOnlineRecognizerProvider provider) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled || !options.LowMemoryMode) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
            provider.TryReleaseIdle(options.LocalModelIdleTimeout);
    }
}
