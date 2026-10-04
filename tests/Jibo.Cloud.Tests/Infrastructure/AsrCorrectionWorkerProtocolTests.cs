using Jibo.Cloud.Application.Services;
using Jibo.Cloud.Infrastructure.Audio;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jibo.Cloud.Tests.Infrastructure;

public sealed class AsrCorrectionWorkerProtocolTests
{
    [PythonWorkerFact]
    public async Task BusyWorkerDoesNotQueue_AndTimeoutRestartsWithoutStaleResponses()
    {
        var (model, directory) = CreateWorker();
        using (model)
        {
            await model.StartAsync(CancellationToken.None);
            try
            {
                await WaitUntil(() => model.IsReady);
                var slow = model.TryCorrectAsync("slow");
                Assert.Null(await model.TryCorrectAsync("second"));
                Assert.Null(await slow);
                await WaitUntil(() => !model.IsReady);
                await WaitUntil(() => model.IsReady);
                var result = await model.TryCorrectAsync("second");
                Assert.NotNull(result);
                Assert.Equal("second", result.Text);
            }
            finally
            {
                await model.StopAsync(CancellationToken.None);
                Directory.Delete(directory, true);
            }
        }
    }

    [PythonWorkerFact]
    public async Task CallerCancellation_KillsInFlightWorkerAndPropagates()
    {
        var (model, directory) = CreateWorker();
        using (model)
        {
            await model.StartAsync(CancellationToken.None);
            try
            {
                await WaitUntil(() => model.IsReady);
                using var cancellation = new CancellationTokenSource(10);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => model.TryCorrectAsync("slow", cancellation.Token));
            }
            finally
            {
                await model.StopAsync(CancellationToken.None);
                Directory.Delete(directory, true);
            }
        }
    }

    [PythonWorkerFact]
    public async Task ResponseForDifferentRequest_IsDiscarded()
    {
        var (model, directory) = CreateWorker();
        using (model)
        {
            await model.StartAsync(CancellationToken.None);
            try
            {
                await WaitUntil(() => model.IsReady);
                Assert.Null(await model.TryCorrectAsync("wrong-id"));
            }
            finally
            {
                await model.StopAsync(CancellationToken.None);
                Directory.Delete(directory, true);
            }
        }
    }

    private static (LocalAsrCorrectionModel Model, string Directory) CreateWorker()
    {
        var directory = Path.Combine(Path.GetTempPath(), "asr-worker-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "model", "onnx"));
        File.WriteAllText(Path.Combine(directory, "model", "onnx", "model_quantized.onnx"), "fake-model");
        var script = Path.Combine(directory, "worker.py");
        File.WriteAllText(script, """
            import json, sys, time
            print(json.dumps({"ready": True}), flush=True)
            for line in sys.stdin:
                request = json.loads(line)
                if request["text"] == "slow": time.sleep(1)
                identifier = "wrong" if request["text"] == "wrong-id" else request["id"]
                print(json.dumps({"id": identifier, "result": {"text": request["text"], "confidence": 0.99}}), flush=True)
            """);
        var options = new AsrCorrectionOptions
        {
            Directory = directory,
            PythonPath = PythonWorkerFactAttribute.FindPython(),
            WorkerPath = script,
            TimeoutMilliseconds = 60
        };
        return (new LocalAsrCorrectionModel(options, NullLogger<LocalAsrCorrectionModel>.Instance), directory);
    }

    private static async Task WaitUntil(Func<bool> predicate)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!predicate()) await Task.Delay(10, deadline.Token);
    }
}

public sealed class PythonWorkerFactAttribute : FactAttribute
{
    public PythonWorkerFactAttribute()
    {
        if (FindPython() is null) Skip = "Python is required for local worker protocol tests.";
    }

    internal static string? FindPython() =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
        .Select(directory => Path.Combine(directory, OperatingSystem.IsWindows() ? "python.exe" : "python3"))
        .FirstOrDefault(File.Exists);
}
