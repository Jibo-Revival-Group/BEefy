using Jibo.Cloud.Infrastructure.Audio;

namespace Jibo.Cloud.Tests.Infrastructure;

public sealed class SttWerHarnessTests
{
    [Fact]
    public void ComputeWer_IdenticalTranscripts_IsZero()
    {
        Assert.Equal(0, SttWerHarness.ComputeWer("hey jibo what time is it", "hey jibo what time is it"));
    }

    [Fact]
    public void ComputeWer_Substitution_IsCounted()
    {
        var wer = SttWerHarness.ComputeWer("hey jibo play music", "hey jibo play news");
        Assert.Equal(0.25, wer, 3);
    }

    [Fact]
    public void EvaluateFromJson_ProducesAverage()
    {
        var summary = SttWerHarness.EvaluateFromJson(
            """
            [
              {"name":"exact","reference":"hello jibo","hypothesis":"hello jibo"},
              {"name":"sub","reference":"good morning","hypothesis":"good evening"}
            ]
            """);

        Assert.Equal(2, summary.CaseCount);
        Assert.Equal(0.25, summary.AverageWer, 3);
        Assert.Contains("Average WER", SttWerHarness.FormatMarkdown(summary));
    }
}
