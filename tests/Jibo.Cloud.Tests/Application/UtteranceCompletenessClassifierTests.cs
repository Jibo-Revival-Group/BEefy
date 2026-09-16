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
}
