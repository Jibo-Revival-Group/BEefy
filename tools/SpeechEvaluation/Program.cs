using System.Diagnostics;
using System.Text.Json;
using Jibo.Cloud.Application.Services;
using Jibo.Cloud.Infrastructure.Audio;
using Jibo.Cloud.Infrastructure.Content;
using Jibo.Cloud.Infrastructure.Nlu;
using Jibo.Cloud.Infrastructure.Persistence;
using Jibo.Runtime.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
string? Argument(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}
if (args.Contains("--help") || args.Length == 0)
{
    Console.WriteLine("SpeechEvaluation --corpus recordings.json --model MODEL_DIRECTORY [--repetitions 5] [--concurrency 1] [--threads 0]");
    Console.WriteLine("SpeechEvaluation --nlu-corpus intents.json  (uses OPENJIBO_JEV_*; performs paid requests)");
    return;
}
try
{
    if (Argument("--nlu-corpus") is { } nluPath)
    {
        var options = JevNluOptions.Resolve(null);
        if (!options.Enabled || string.IsNullOrWhiteSpace(options.ApiKey))
            throw new ArgumentException("Configure OPENJIBO_JEV_ENABLED=true and OPENJIBO_JEV_API_KEY before live evaluation.");
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var classifier = new JevNluClassifier(http, options, NullLogger<JevNluClassifier>.Instance);
        var cases = JsonSerializer.Deserialize<NluCase[]>(await File.ReadAllTextAsync(nluPath), jsonOptions)
            ?? throw new ArgumentException("NLU corpus must be an array.");
        if (cases.Length == 0) throw new ArgumentException("NLU corpus must not be empty.");
        var results = new List<object>();
        var elapsed = new List<double>();
        var correct = 0;
        foreach (var item in cases)
        {
            var started = Stopwatch.GetTimestamp();
            var result = await classifier.ClassifyAsync(item.Text);
            var duration = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            elapsed.Add(duration);
            var matches = (result?.Intent ?? "unknown") == item.ExpectedIntent;
            if (matches) correct++;
            results.Add(new { item.Name, item.ExpectedIntent, result, matches, durationMs = duration });
        }
        Console.WriteLine(JsonSerializer.Serialize(new { cases = cases.Length, correct,
            accuracy = correct / (double)cases.Length, p50Ms = SttReplayHarness.Percentile(elapsed, 0.5),
            p95Ms = SttReplayHarness.Percentile(elapsed, 0.95), results }, jsonOptions));
        return;
    }
    var corpusPath = Path.GetFullPath(Argument("--corpus") ?? throw new ArgumentException("--corpus is required."));
    var model = Argument("--model") ?? throw new ArgumentException("--model is required; replay does not download models.");
    var repetitions = int.Parse(Argument("--repetitions") ?? "5");
    var concurrency = int.Parse(Argument("--concurrency") ?? "1");
    var threads = int.Parse(Argument("--threads") ?? "0");
    var recordings = JsonSerializer.Deserialize<SttReplayHarness.Recording[]>(await File.ReadAllTextAsync(corpusPath), jsonOptions)
        ?? throw new ArgumentException("Corpus must be an array.");
    if (recordings.Length == 0 || recordings.Any(r => string.IsNullOrWhiteSpace(r.Name) ||
        string.IsNullOrWhiteSpace(r.AudioFile) || string.IsNullOrWhiteSpace(r.Reference)) ||
        recordings.Select(r => r.Name).Distinct().Count() != recordings.Length)
        throw new ArgumentException("Each recording needs a unique name, audioFile and nonempty reference.");
    var frames = new Dictionary<string, IReadOnlyList<byte[]>>();
    foreach (var recording in recordings)
        frames[recording.Name] = new[] { await File.ReadAllBytesAsync(Path.Combine(Path.GetDirectoryName(corpusPath)!, recording.AudioFile)) };
    var content = new JiboExperienceContentCache(new InMemoryJiboExperienceContentRepository());
    var reports = new List<SttReplayHarness.Report>();
    foreach (var profile in new[] { (Name: "greedy", Method: "greedy_search", Paths: 4),
        (Name: "beam2", Method: "modified_beam_search", Paths: 2), (Name: "beam4", Method: "modified_beam_search", Paths: 4) })
    {
        var options = new BufferedAudioSttOptions { EnableStreamingSherpa = true, AutoDownloadSherpaModel = false,
            SherpaModelDirectory = model, SherpaDecodingMethod = profile.Method,
            SherpaMaxActivePaths = profile.Paths, SherpaThreads = threads };
        using var provider = new SherpaOnlineRecognizerProvider(options, new ListenEndpointingOptions(),
            new SherpaModelLocator(NullLogger<SherpaModelLocator>.Instance));
        if (!provider.TryResolveModel(out _)) throw new ArgumentException("Sherpa model files unavailable.");
        var strategy = new StreamingSherpaBufferedAudioSttStrategy(options, provider);
        reports.Add(await SttReplayHarness.ReplayAsync(profile.Name, recordings,
            async (recording, token) => (await strategy.TranscribeAsync(new TurnContext
            { Attributes = new Dictionary<string, object?> { ["bufferedAudioFrames"] = frames[recording.Name] } }, token)).Text,
            async (text, token) => (await new JiboInteractionService(content, new FirstRandomizer(),
                new InMemoryPersonalMemoryStore()).BuildDecisionCoreAsync(new TurnContext
            { RawTranscript = text, NormalizedTranscript = text, InputMode = TurnInputMode.DirectText }, token)).IntentName,
            repetitions, concurrency));
    }
    Console.WriteLine(JsonSerializer.Serialize(new { measurement = "Warm buffered ASR processing; excludes endpoint silence and NLU",
        concurrency, threads, recommendedProfile = SttReplayHarness.SelectProfile(reports).Profile,
        requiresLiveEndpointLatencyValidation = true, reports }, jsonOptions));
}
catch (Exception exception)
{
    // Avoid printing remote exception bodies or configuration secrets.
    Console.Error.WriteLine($"Speech evaluation failed ({exception.GetType().Name}). Check corpus, options and model files.");
    Environment.ExitCode = 1;
}

internal sealed record NluCase(string Name, string Text, string ExpectedIntent);
internal sealed class FirstRandomizer : IJiboRandomizer
{
    public T Choose<T>(IReadOnlyList<T> items) => items[0];
}
