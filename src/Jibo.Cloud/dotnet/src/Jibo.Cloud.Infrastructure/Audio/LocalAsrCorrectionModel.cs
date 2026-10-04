using System.Diagnostics;
using System.Text.Json;
using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Application.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jibo.Cloud.Infrastructure.Audio;

/// <summary>A supervised offline inference worker. Busy/unready workers never queue turns.</summary>
public sealed class LocalAsrCorrectionModel(
    AsrCorrectionOptions options,
    ILogger<LocalAsrCorrectionModel> logger) : BackgroundService, IAsrCorrectionModel
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
        var script = options.WorkerPath ?? Path.Combine(AppContext.BaseDirectory, "Audio", "AsrCorrection", "worker.py");
        var model = Path.Combine(root, "model");
        if (!File.Exists(python) || !File.Exists(script) ||
            !File.Exists(Path.Combine(model, "onnx", "model_quantized.onnx")))
        {
            logger.LogInformation("ASR correction model is not installed at {Directory}; normal routing remains active. Run setup-asr-correction-model.py to install it.", root);
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
                start.ArgumentList.Add("--model-directory");
                start.ArgumentList.Add(model);
                process = Process.Start(start) ?? throw new InvalidOperationException("Could not start ASR correction worker.");
                process.ErrorDataReceived += (_, args) =>
                {
                    if (args.Data is not null) logger.LogDebug("ASR correction worker: {Message}", args.Data);
                };
                process.BeginErrorReadLine();
                using var startup = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                startup.CancelAfter(TimeSpan.FromSeconds(15));
                var readyLine = await process.StandardOutput.ReadLineAsync(startup.Token);
                using var ready = JsonDocument.Parse(readyLine ?? "{}");
                if (!ready.RootElement.TryGetProperty("ready", out var flag) || !flag.GetBoolean())
                    throw new InvalidOperationException("ASR correction worker did not become ready.");
                _worker = process;
                logger.LogInformation("Local quantized contextual BERT ASR correction model is ready.");
                await process.WaitForExitAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "ASR correction worker unavailable; normal routing continues.");
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

    public async Task<AsrCorrection?> TryCorrectAsync(string transcript, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!options.Enabled || transcript.Length is 0 or > 256 || _worker is null ||
            !await _requestGate.WaitAsync(0, cancellationToken))
            return null;
        Process? process = null;
        try
        {
            process = _worker;
            if (process is null || process.HasExited) return null;
            var id = Guid.NewGuid().ToString("N");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(Math.Clamp(options.TimeoutMilliseconds, 1, 500));
            var request = JsonSerializer.Serialize(new { id, text = transcript, candidates = AsrCommandCatalog.Candidates,
                budgetMs = Math.Max(1, Math.Clamp(options.TimeoutMilliseconds, 1, 500) - 5) });
            await process.StandardInput.WriteLineAsync(request.AsMemory(), deadline.Token);
            await process.StandardInput.FlushAsync(deadline.Token);
            var line = await process.StandardOutput.ReadLineAsync(deadline.Token);
            using var response = JsonDocument.Parse(line ?? "{}");
            var root = response.RootElement;
            if (!root.TryGetProperty("id", out var responseId) || responseId.GetString() != id)
            {
                Kill(process); // Never reuse a pipe whose request/response alignment is unknown.
                return null;
            }
            if (!root.TryGetProperty("result", out var result) || result.ValueKind == JsonValueKind.Null)
                return null;
            var text = result.GetProperty("text").GetString();
            var confidence = result.GetProperty("confidence").GetDouble();
            return string.IsNullOrWhiteSpace(text) || text.Length > 256 || !double.IsFinite(confidence) ||
                   confidence < 0 || confidence > 1
                ? null : new AsrCorrection(text, confidence, "bert-mini-context-q8");
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
        catch (Exception ex)
        {
            Kill(process);
            logger.LogDebug(ex, "ASR correction request failed; normal routing continues.");
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
                    return Path.Combine(directory.FullName, "App_Data", "asr-correction");
        }
        return Path.Combine(AppContext.BaseDirectory, "App_Data", "asr-correction");
    }
}
