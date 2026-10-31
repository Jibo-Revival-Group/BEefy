using Jibo.Cloud.Application.Services;
using Jibo.Cloud.Infrastructure.Content;
using Jibo.Cloud.Infrastructure.Persistence;
using Jibo.Runtime.Abstractions;

namespace Jibo.Cloud.Tests.Application;

// Requests seen in production that used to end in "I didn't understand" / "I can't find anything".
public sealed class HelpIntentInteractionServiceTests
{
    [Theory]
    [InlineData("what server are you connected to", "api.5x1.com", "I'm connected to api dot five ex one dot com, running BEefy cloud version")]
    [InlineData("hey jibo which server are you on", "api.5x1.com:443", "I'm connected to api dot five ex one dot com, running")]
    [InlineData("what server are you connected to oliver", "self.example.org", "I'm connected to self dot example dot org, running")]
    [InlineData("what server are you using", null, "I'm connected to my cloud server, running")]
    public async Task ServerQuestion_NamesTheConnectedHost(string transcript, string? host, string expected)
    {
        var decision = await Service().BuildDecisionAsync(new TurnContext { RawTranscript = transcript, NormalizedTranscript = transcript, HostName = host });
        Assert.Equal("server_info", decision.IntentName);
        Assert.StartsWith(expected, decision.ReplyText);
    }

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
        Assert.Contains("Add a person", decision.ReplyText);
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
