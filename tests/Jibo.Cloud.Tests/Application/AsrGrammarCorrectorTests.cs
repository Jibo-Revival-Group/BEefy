using Jibo.Cloud.Application.Services;
using Xunit.Abstractions;

namespace Jibo.Cloud.Tests.Application;

public sealed class AsrGrammarCorrectorTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("what's your favorate holiday", "whats your favorite holiday", "robot_favorite_holiday")]
    [InlineData("what's your flavor collar", "whats your favorite color", "robot_favorite_color")]
    [InlineData("what's your favorite banned", "whats your favorite band", "robot_favorite_various_styles_band")]
    public void Correct_RepairsKnownQuestionFrames(string heard, string corrected, string intent)
    {
        Assert.Equal(corrected, AsrGrammarCorrector.Correct(heard));
        Assert.Equal(intent, UtteranceFrameParser.TryParsePreference(heard));
        Assert.Equal(corrected, AsrGrammarCorrector.Correct(corrected));
    }

    [Theory]
    [InlineData("what's your paper color")]
    [InlineData("what's your favorite color")]
    [InlineData("what is your favourite holiday")]
    [InlineData("what's your paper color printer")]
    [InlineData("what color is your paper")]
    [InlineData("what's your paper size")]
    [InlineData("what's your paper")]
    [InlineData("what's your flavor")]
    [InlineData("what's my paper color")]
    [InlineData("my paper color is blue")]
    [InlineData("turn off the paper color lamp")]
    [InlineData("set a timer for fourteen minutes")]
    [InlineData("my name is Paper")]
    [InlineData("he asked what's your paper color")]
    [InlineData("what's your paper color and why")]
    [InlineData("what's your favorite ice cream flavor")]
    [InlineData("what's your least favorite word")]
    [InlineData("")]
    public void Correct_PreservesOtherUtterances(string transcript)
    {
        Assert.Equal(transcript, AsrGrammarCorrector.Correct(transcript));
    }

    [Fact]
    public void Correct_BoundsLongInput()
    {
        var transcript = "what's your paper color " + new string('a', 5000);
        Assert.Equal(transcript, AsrGrammarCorrector.Correct(transcript));
    }

    [Fact]
    public void Correct_ReportsPerTurnProcessingTime()
    {
        string[] utterances = ["what's your paper color", "what time is it", "what's your favorite color", "what's your paper color printer"];
        for (var i = 0; i < 100; i++)
            foreach (var utterance in utterances) AsrGrammarCorrector.Correct(utterance);
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        for (var i = 0; i < 10000; i++)
            foreach (var utterance in utterances) AsrGrammarCorrector.Correct(utterance);
        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        output.WriteLine($"Grammar correction mean per transcript: {elapsed / 40000:F4} ms (40000 calls)");
    }
}
