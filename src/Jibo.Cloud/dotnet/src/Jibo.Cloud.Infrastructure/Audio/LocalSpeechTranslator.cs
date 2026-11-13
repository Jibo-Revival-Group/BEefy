using System.Diagnostics;
using System.Text.Json;
using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Application.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jibo.Cloud.Infrastructure.Audio;

/// <summary>Supervised offline worker that turns English speech text into another language in Jibo's voice
/// (OPUS-MT translation + phoneme markup). Same supervision as <see cref="LocalAsrCorrectionModel"/>.</summary>
public sealed class LocalSpeechTranslator(
    SpeechTranslationOptions options,
    ILogger<LocalSpeechTranslator> logger) : BackgroundService, ISpeechTranslator
{
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private volatile Process? _worker;
    public bool IsReady
    {
        get
        {
            try { return _worker is { HasExited: false }; }
            catch (InvalidOperationException) { return false; }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled) return;
        var root = ResolveDirectory(options.Directory);
        var python = options.PythonPath ?? Path.Combine(root, "venv",
            OperatingSystem.IsWindows() ? "Scripts/python.exe" : "bin/python");
        var script = options.WorkerPath ?? Path.Combine(AppContext.BaseDirectory, "Audio", "SpeechTranslation", "worker.py");
        var weights = Path.Combine(root, "en-es", "model.bin");
        if (!File.Exists(python) || !File.Exists(script) || !File.Exists(weights))
        {
            logger.LogInformation("Speech translation is not installed at {Directory}; Jibo speaks English only. " +
                "Run scripts/cloud/setup-speech-translation.py to install it.", root);
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            Process? process = null;
            try
            {
                var start = new ProcessStartInfo(python)
                {
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                start.ArgumentList.Add("-u");
                start.ArgumentList.Add(script);
                start.ArgumentList.Add("--directory");
                start.ArgumentList.Add(root);
                process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the speech translation worker.");
                process.ErrorDataReceived += (_, args) =>
                {
                    if (args.Data is not null) logger.LogDebug("Speech translation worker: {Message}", args.Data);
                };
                process.BeginErrorReadLine();
                using var startup = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                startup.CancelAfter(TimeSpan.FromSeconds(15));
                var readyLine = await process.StandardOutput.ReadLineAsync(startup.Token);
                using var ready = JsonDocument.Parse(readyLine ?? "{}");
                if (!ready.RootElement.TryGetProperty("ready", out var flag) || !flag.GetBoolean())
                    throw new InvalidOperationException("Speech translation worker did not become ready.");
                _worker = process;
                logger.LogInformation("Speech translation worker is ready.");
                await process.WaitForExitAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Speech translation worker unavailable; Jibo speaks English.");
            }
            finally
            {
                if (ReferenceEquals(_worker, process)) _worker = null;
                Kill(process);
                // Wait for an in-flight pipe reader before disposing its Process.
                await _requestGate.WaitAsync(CancellationToken.None);
                try { process?.Dispose(); }
                finally { _requestGate.Release(); }
            }
            try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    public async Task<TranslatedSpeech?> TryTranslateAsync(string language, string text, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!options.Enabled || text.Length is 0 or > 2000 || _worker is null) return null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Math.Clamp(options.TimeoutMilliseconds, 100, 30000));
        // Requests wait their turn (one robot may be mid-sentence), but only until the deadline.
        try { await _requestGate.WaitAsync(deadline.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
        Process? process = null;
        try
        {
            process = _worker;
            if (process is null || process.HasExited) return null;
            var id = Guid.NewGuid().ToString("N");
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { id, language, text }).AsMemory(), deadline.Token);
            await process.StandardInput.FlushAsync(deadline.Token);
            var line = await process.StandardOutput.ReadLineAsync(deadline.Token);
            using var response = JsonDocument.Parse(line ?? "{}");
            var root = response.RootElement;
            if (!root.TryGetProperty("id", out var responseId) || responseId.GetString() != id)
            {
                Kill(process); // Never reuse a pipe whose request/response alignment is unknown.
                return null;
            }
            if (!root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object) return null;
            var parts = result.GetProperty("parts").EnumerateArray().Select(static part => part.GetString() ?? "")
                .Where(static part => part.Length is > 0 and <= 1000).ToArray();
            return parts.Length == 0 ? null : new TranslatedSpeech(result.GetProperty("translation").GetString() ?? "", parts);
        }
        catch (OperationCanceledException)
        {
            Kill(process); // The answer to this request may still arrive; the pipe can no longer be trusted.
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
        catch (Exception ex)
        {
            Kill(process);
            logger.LogDebug(ex, "Speech translation request failed; Jibo speaks English.");
            return null;
        }
        finally { _requestGate.Release(); }
    }

    private static void Kill(Process? process)
    {
        try { if (process is { HasExited: false }) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    internal static string ResolveDirectory(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "OpenJibo.slnx")))
                    return Path.Combine(directory.FullName, "App_Data", "speech-translation");
        }
        return Path.Combine(AppContext.BaseDirectory, "App_Data", "speech-translation");
    }
}
