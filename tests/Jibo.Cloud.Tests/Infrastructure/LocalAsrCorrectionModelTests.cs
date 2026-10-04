using Jibo.Cloud.Application.Services;
using Jibo.Cloud.Infrastructure.Audio;
using Jibo.Cloud.Infrastructure.Content;
using Jibo.Cloud.Infrastructure.Persistence;
using Jibo.Runtime.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace Jibo.Cloud.Tests.Infrastructure;

public sealed class LocalAsrCorrectionModelTests(ITestOutputHelper output)
{
    [LocalCorrectionFact]
    public async Task InstalledNeuralModel_CorrectsContext_AndRoutesCommand()
    {
        var options = new AsrCorrectionOptions { TimeoutMilliseconds = 150 };
        using var model = new LocalAsrCorrectionModel(options, NullLogger<LocalAsrCorrectionModel>.Instance);
        await model.StartAsync(CancellationToken.None);
        try
        {
            using var readyDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!model.IsReady) await Task.Delay(20, readyDeadline.Token);
            var service = new JiboInteractionService(
                new JiboExperienceContentCache(new InMemoryJiboExperienceContentRepository()),
                new FirstRandomizer(), new InMemoryPersonalMemoryStore(),
                asrCorrectionModel: model, asrCorrectionOptions: options);
            // Warm the normal command router separately from timed inference.
            Assert.Equal("robot_favorite_color", (await service.BuildDecisionAsync(new TurnContext
                { RawTranscript = "what is your favorite color" })).IntentName);
            Assert.True(AsrCorrectionAcceptance.IsConservativeEdit("what's your paper color", "what is your favorite color"));
            var grammar = await model.TryCorrectAsync("what your favorite color");
            Assert.NotNull(grammar);
            Assert.Equal("what is your favorite color", grammar.Text);
            foreach (var (heard, corrected, intent) in new[] {
                ("what's your paper color", "what is your favorite color", "robot_favorite_color"),
                ("twic", "twerk", "twerk"),
                ("twirk", "twerk", "twerk"),
                ("twick", "twerk", "twerk"),
                ("twelc", "twerk", "twerk"),
                ("make a peter", "make a pizza", "pizza"),
                ("make a peter sir", "make a pizza", "pizza"),
                ("tell me a store ree", "tell me a story", "robot_story"),
                ("tell me a storee", "tell me a story", "robot_story"),
                ("make a pit sir", "make a pizza", "pizza"),
                ("do a dense", "do a dance", "dance"),
                ("tell me a storey", "tell me a story", "robot_story"),
                ("my time is it", "what time is it", "time"),
                ("tell me a choke", "tell me a joke", "joke"),
                ("what is your paper food", "what is your favorite food", "robot_favorite_food") })
            {
                var result = await model.TryCorrectAsync(heard);
                Assert.NotNull(result);
                Assert.Equal(corrected, Normalize(result.Text));
                Assert.True(result.Confidence >= options.MinimumConfidence);
                var turn = new TurnContext { RawTranscript = heard, DeviceId = "model-integration-test" };
                var decision = await service.BuildDecisionAsync(turn);

                Assert.Equal(intent, decision.IntentName);
                Assert.Equal(heard, turn.RawTranscript);
                Assert.Equal(corrected, turn.Attributes[JiboInteractionService.ModelCorrectedTranscriptKey]);
                Assert.Equal("bert-mini-context-q8", turn.Attributes["stt:correctionModel"]);
            }
            foreach (var text in new[] { "work", "twice", "twirl", "truck", "Tim", "not", "twic tomorrow", "do not twic", "twic 2", "twick tomorrow", "do not twick", "twerk", "make a pencil", "make a pit sir tomorrow", "do not make a pit sir",
                "make my pit sir", "make two pit sir", "make a pizza", "do a dance",
                "what is your paper color printer", "what color is your paper",
                "what is my favorite color", "my name is Paper", "three purple clouds", "tell me a poem",
                "do not tell me a choke", "set timer for five minutes", "what is your favorite color" })
                Assert.Null(await model.TryCorrectAsync(text));

            foreach (var phrase in new[] { "what is your paper color", "twic", "twick", "twelc" })
            {
                var timings = new List<double>();
                for (var index = 0; index < 20; index++)
                {
                    var started = System.Diagnostics.Stopwatch.GetTimestamp();
                    Assert.NotNull(await model.TryCorrectAsync(phrase));
                    timings.Add(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                }
                timings.Sort();
                output.WriteLine($"Uncached neural correction '{phrase}': median={timings[10]:F2} ms, p95={timings[18]:F2} ms");
            }
        }
        finally { await model.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task DisabledOrUninstalledModel_ReturnsImmediately()
    {
        using var model = new LocalAsrCorrectionModel(new AsrCorrectionOptions
            { Directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")) },
            NullLogger<LocalAsrCorrectionModel>.Instance);
        await model.StartAsync(CancellationToken.None);
        Assert.Null(await model.TryCorrectAsync("tell me a jok"));
        await model.StopAsync(CancellationToken.None);
    }

    private static string Normalize(string text) => TranscriptTextNormalizer.NormalizeLooseText(text);

    private sealed class FirstRandomizer : IJiboRandomizer
    {
        public T Choose<T>(IReadOnlyList<T> items) => items[0];
    }
}

public sealed class LocalCorrectionFactAttribute : FactAttribute
{
    public LocalCorrectionFactAttribute()
    {
        var directory = LocalAsrCorrectionModel.ResolveDirectory(null);
        var python = Path.Combine(directory, "venv", OperatingSystem.IsWindows() ? "Scripts/python.exe" : "bin/python");
        if (!File.Exists(python) || !File.Exists(Path.Combine(directory, "model/onnx/model_quantized.onnx")))
            Skip = "Install the optional model with scripts/cloud/setup-asr-correction-model.py to run neural integration tests.";
    }
}
