using Jibo.Cloud.Application.Services;

namespace Jibo.Cloud.Tests.Application;

public sealed class UtteranceCompletenessClassifierTests
{
    [Theory]
    [InlineData("what time is it")]
    [InlineData("hey jibo what time is it")]
    [InlineData("turn on the lights")]
    [InlineData("stop")]
    [InlineData("yes")]
    [InlineData("how old are you")]
    [InlineData("play word of the day")]
    public void Classify_CompleteUtterances(string transcript)
    {
        var result = UtteranceCompletenessClassifier.Classify(transcript);
        Assert.True(result.IsComplete, $"{transcript} => {result.Reason}");
        Assert.Equal("complete", result.Reason);
    }

    // Prod: "what can you do" waited ~20 s for more speech because it ends in "do".
    [Theory]
    [InlineData("what can you do")]
    [InlineData("hey jibo what can you do")]
    [InlineData("what can you do what can you do")]
    [InlineData("what are you do")]
    [InlineData("what's your favorite thing to do")]
    [InlineData("what should i do")]
    [InlineData("how do you do")]
    [InlineData("what does it do")]
    public void Classify_QuestionEndingInDo_IsComplete(string transcript) =>
        Assert.True(UtteranceCompletenessClassifier.Classify(transcript).IsComplete, transcript);

    [Theory]
    [InlineData("can you do")]
    [InlineData("there you go how do")]
    [InlineData("what do")]
    [InlineData("i want to do")]
    public void Classify_UnfinishedDo_KeepsWaiting(string transcript) =>
        Assert.False(UtteranceCompletenessClassifier.Classify(transcript).IsComplete, transcript);

    [Theory]
    [InlineData("who am i")]
    [InlineData("hey jibo who am i")]
    [InlineData("what is my name")]
    [InlineData("what's my name")]
    [InlineData("do you know me")]
    [InlineData("who is this")]
    [InlineData("can you recognize me")]
    public void Classify_KnownCompleteIdentityQuestions(string transcript)
    {
        var result = UtteranceCompletenessClassifier.Classify(transcript);
        Assert.True(result.IsComplete, $"{transcript} => {result.Reason}");
        Assert.Equal("known_complete_identity_question", result.Reason);
    }

    [Theory]
    [InlineData("turn on the", "dangling_function_word")]
    [InlineData("hey jibo turn on the", "dangling_function_word")]
    [InlineData("what is your", "dangling_function_word")]
    [InlineData("how old are", "dangling_function_word")]
    [InlineData("my favorite is", "dangling_function_word")]
    [InlineData("who", "dangling_function_word")]
    [InlineData("who am", "dangling_function_word")]
    [InlineData("and i", "dangling_function_word")]
    [InlineData("hey jibo", "wake_word_only")]
    [InlineData("", "empty_partial")]
    public void Classify_IncompleteUtterances(string transcript, string expectedReason)
    {
        var result = UtteranceCompletenessClassifier.Classify(transcript);
        Assert.False(result.IsComplete, $"{transcript} should be incomplete");
        Assert.Equal(expectedReason, result.Reason);
    }

    [Fact]
    public void Classify_ConstrainedYesNo_UnmatchedIsIncomplete()
    {
        var result = UtteranceCompletenessClassifier.Classify(
            "maybe tomorrow",
            ["shared/yes_no"]);
        Assert.False(result.IsComplete);
        Assert.Equal("constrained_yes_no_unmatched", result.Reason);
    }

    [Fact]
    public void Classify_ConstrainedYesNo_MatchedIsComplete()
    {
        var result = UtteranceCompletenessClassifier.Classify(
            "yes please",
            ["shared/yes_no"]);
        Assert.True(result.IsComplete);
    }

    [Theory]
    [InlineData("what's my phone's battery at")]
    [InlineData("hey jibo what's your battery at")]
    [InlineData("what's my phone at")]
    public void BatteryAtQuestion_IsComplete(string transcript) =>
        Assert.True(UtteranceCompletenessClassifier.Classify(transcript, ["launch"]).IsComplete);
}
