using Jibo.Cloud.Infrastructure.Audio;

namespace Jibo.Cloud.Tests.Infrastructure;

public sealed class SttReplayHarnessTests
{
    [Fact]
    public async Task Replay_WarmsAudio_SeparatesAsrTiming_AndCountsWeightedErrors()
    {
        var calls = 0;
        var reports = await SttReplayHarness.ReplayAsync("greedy", new[]
        {
            new SttReplayHarness.Recording("short", "short.ogg", "hello", "chat"),
            new SttReplayHarness.Recording("long", "long.ogg", "what time is it", "time")
        }, (item, _) => { Interlocked.Increment(ref calls); return Task.FromResult(item.Name == "short" ? "goodbye" : "what time is it"); },
            (_, _) => Task.FromResult("time"), repetitions: 2, concurrency: 2);
        Assert.Equal(6, calls); // two warmups, four measured samples
        Assert.Equal(4, reports.SampleCount);
        Assert.Equal(0.2, reports.WeightedWer);
        Assert.Equal(2, reports.CommandErrors);
        Assert.True(reports.CommandsLabeled);
    }

    [Fact]
    public void Selection_RespectsLatencyAndCommandErrors_ThenBreaksWerTiesByLatency()
    {
        var baseline = Report("greedy", 0.3, 2, 100);
        Assert.Equal("beam2", SttReplayHarness.SelectProfile(new[] { baseline,
            Report("beam2", 0.2, 2, 200), Report("beam4", 0.2, 2, 250) }).Profile);
        Assert.Equal("greedy", SttReplayHarness.SelectProfile(new[] { baseline,
            Report("beam2", 0.1, 2, 301), Report("beam4", 0.1, 3, 200) }).Profile);
        Assert.Equal("greedy", SttReplayHarness.SelectProfile(new[] { baseline with { CommandsLabeled = false },
            Report("beam2", 0.1, 0, 100) }).Profile);
        Assert.Equal("greedy", SttReplayHarness.SelectProfile(new[] { baseline, Report("beam2", 0.3, 2, 50) }).Profile);
    }

    [Fact]
    public void Percentiles_UseNearestRank()
    {
        Assert.Equal(10, SttReplayHarness.Percentile(Enumerable.Range(1, 20).Select(i => (double)i), 0.5));
        Assert.Equal(19, SttReplayHarness.Percentile(Enumerable.Range(1, 20).Select(i => (double)i), 0.95));
    }

    private static SttReplayHarness.Report Report(string profile, double wer, int errors, double p95) =>
        new(profile, 100, wer, errors, true, 50, p95, []);
}
