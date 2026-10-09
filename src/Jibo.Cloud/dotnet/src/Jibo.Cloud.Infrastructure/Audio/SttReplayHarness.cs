using System.Diagnostics;
using Jibo.Cloud.Application.Services;

namespace Jibo.Cloud.Infrastructure.Audio;

/// <summary>Recorded Ogg/Opus replay, with ASR timing independent of intent routing.</summary>
public static class SttReplayHarness
{
    public sealed record Recording(string Name, string AudioFile, string Reference, string? ExpectedIntent);
    public sealed record Sample(string Name, string Reference, string Hypothesis, string? ExpectedIntent,
        string? Intent, double AsrMilliseconds, double Wer);
    public sealed record Report(string Profile, int SampleCount, double WeightedWer, int CommandErrors,
        bool CommandsLabeled, double P50Milliseconds, double P95Milliseconds, IReadOnlyList<Sample> Samples);

    public static async Task<Report> ReplayAsync(string profile, IReadOnlyList<Recording> recordings,
        Func<Recording, CancellationToken, Task<string>> transcribe,
        Func<string, CancellationToken, Task<string>> classify,
        int repetitions = 5, int concurrency = 1, CancellationToken cancellationToken = default)
    {
        if (recordings.Count == 0 || repetitions < 1 || concurrency < 1)
            throw new ArgumentException("Replay requires recordings and positive repetitions/concurrency.");
        // Warm each recording once; do not count initialization or decoder warmup.
        foreach (var recording in recordings)
            await transcribe(recording, cancellationToken);
        using var slots = new SemaphoreSlim(concurrency);
        var tasks = Enumerable.Range(0, repetitions).SelectMany(_ => recordings).Select(recording => Task.Run(async () =>
        {
            await slots.WaitAsync(cancellationToken);
            try
            {
                var started = Stopwatch.GetTimestamp();
                var hypothesis = await transcribe(recording, cancellationToken);
                var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                // Intent evaluation is deliberately outside ASR timing.
                var intent = await classify(hypothesis, cancellationToken);
                return new Sample(recording.Name, recording.Reference, hypothesis, recording.ExpectedIntent,
                    intent, elapsed, SttWerHarness.ComputeWer(recording.Reference, hypothesis));
            }
            finally { slots.Release(); }
        }, cancellationToken)).ToArray();
        var samples = await Task.WhenAll(tasks);
        var wer = SttWerHarness.Evaluate(samples.Select(s => (s.Name, s.Reference, s.Hypothesis)));
        var words = samples.Sum(s => SttWerHarness.CountWords(s.Reference));
        var errors = wer.Cases.Sum(c => c.Substitutions + c.Deletions + c.Insertions);
        var timings = samples.Select(s => s.AsrMilliseconds).ToArray();
        return new Report(profile, samples.Length, words == 0 ? wer.AverageWer : errors / (double)words,
            samples.Count(s => s.ExpectedIntent is not null && s.Intent != s.ExpectedIntent),
            recordings.All(r => !string.IsNullOrWhiteSpace(r.ExpectedIntent)),
            Percentile(timings, 0.5), Percentile(timings, 0.95), samples);
    }

    public static Report SelectProfile(IReadOnlyList<Report> reports, double extraLatencyMs = 200)
    {
        var baseline = reports.Single(r => r.Profile == "greedy");
        if (!baseline.CommandsLabeled) return baseline;
        return reports.Where(r => r.CommandsLabeled && r.SampleCount == baseline.SampleCount &&
                r.P95Milliseconds <= baseline.P95Milliseconds + extraLatencyMs &&
                r.CommandErrors <= baseline.CommandErrors && r.WeightedWer < baseline.WeightedWer)
            .OrderBy(r => r.WeightedWer).ThenBy(r => r.P95Milliseconds).FirstOrDefault() ?? baseline;
    }

    public static double Percentile(IEnumerable<double> values, double fraction)
    {
        var sorted = values.Order().ToArray();
        if (sorted.Length == 0 || !double.IsFinite(fraction) || fraction is < 0 or > 1)
            throw new ArgumentException("Percentile requires samples and a fraction from 0 to 1.");
        return sorted[Math.Max(0, (int)Math.Ceiling(fraction * sorted.Length) - 1)];
    }
}
