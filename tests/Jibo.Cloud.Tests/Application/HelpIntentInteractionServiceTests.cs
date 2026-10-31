using Jibo.Cloud.Application.Services;
using Jibo.Cloud.Infrastructure.Content;
using Jibo.Cloud.Infrastructure.Persistence;
using Jibo.Runtime.Abstractions;

namespace Jibo.Cloud.Tests.Application;

// Requests seen in production that used to end in "I didn't understand" / "I can't find anything".
public sealed class HelpIntentInteractionServiceTests
{
    [Theory]
    [InlineData("what server are you connected to")]
    [InlineData("hey jibo which server are you on")]
    [InlineData("what server are you connected to oliver")]
    [InlineData("what server are you using")]
    public async Task ServerQuestion_SaysFiveExOne(string transcript)
    {
        var decision = await Service().BuildDecisionAsync(new TurnContext { RawTranscript = transcript, NormalizedTranscript = transcript, HostName = "api.5x1.com" });
        Assert.Equal("server_info", decision.IntentName);
        Assert.Equal("I'm connected to five ex one.", decision.ReplyText);
    }

    // Ending in "on"/"to" must not make the turn wait for more speech.
    [Theory]
    [InlineData("what server are you on")]
    [InlineData("hey jibo which server are you connected to")]
    public void ServerQuestion_IsACompleteUtterance(string transcript) =>
        Assert.True(UtteranceCompletenessClassifier.Classify(transcript, ["launch"]).IsComplete);

    [Theory]
    [InlineData("i wanna add a new person")]
    [InlineData("let's add another person to your memory")]
    [InlineData("how do i add a family member to you")]
    [InlineData("how do i add a new user to the loop")]
    [InlineData("i wanna add somebody to the family")]
    [InlineData("how do i add some one to the lo")]
    public async Task AddPersonRequests_PointToBeacon(string transcript)
    {
        var decision = await Service().BuildDecisionAsync(new TurnContext { RawTranscript = transcript, NormalizedTranscript = transcript });
        Assert.Equal("add_person_help", decision.IntentName);
        Assert.Contains("portal dot five ex one dot com", decision.ReplyText);
        Assert.Contains("manage Jibo on beacon", decision.ReplyText);
    }

    [Theory]
    [InlineData("add milk to my shopping list")]
    [InlineData("what server is the best for minecraft")]
    public async Task UnrelatedRequests_AreNotCaptured(string transcript)
    {
        var decision = await Service().BuildDecisionAsync(new TurnContext { RawTranscript = transcript, NormalizedTranscript = transcript });
        Assert.NotEqual("add_person_help", decision.IntentName);
        Assert.NotEqual("server_info", decision.IntentName);
    }

    private static JiboInteractionService Service() => new(
        new JiboExperienceContentCache(new InMemoryJiboExperienceContentRepository()),
        new FirstRandomizer(),
        new InMemoryPersonalMemoryStore());

    private sealed class FirstRandomizer : IJiboRandomizer
    {
        public T Choose<T>(IReadOnlyList<T> items) => items[0];
    }
}
