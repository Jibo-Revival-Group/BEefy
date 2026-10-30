using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Application.Services;
using Jibo.Cloud.Infrastructure.Content;
using Jibo.Cloud.Infrastructure.Persistence;
using Jibo.Runtime.Abstractions;

namespace Jibo.Cloud.Tests.Application;

// Keywords match whole words: "hi"/"hey" inside "highest"/"which" and "sup" inside "super" are not greetings.
public sealed class WholeWordKeywordMatchingTests
{
    [Theory]
    [InlineData("what is the highest mountain in the world")]
    [InlineData("which planet is the biggest")]
    [InlineData("what do you think about cats")]
    [InlineData("what is the capital of chile")]
    [InlineData("tell me about the history of rome")]
    public async Task QuestionContainingHiSubstring_IsNotAGreeting(string transcript)
    {
        var decision = await CreateInteractionService().BuildDecisionAsync(Turn(transcript));
        Assert.NotEqual("hello", decision.IntentName);
    }

    [Theory]
    [InlineData("who won the super bowl")]
    [InlineData("what is a supernova")]
    [InlineData("what is the supreme court")]
    [InlineData("what's for supper")]
    public async Task QuestionContainingSup_IsNotWhatsUp(string transcript)
    {
        var decision = await CreateInteractionService().BuildDecisionAsync(Turn(transcript));
        Assert.NotEqual("whats_up", decision.IntentName);
    }

    [Theory]
    [InlineData("hi", "hello")]
    [InlineData("hey there", "hello")]
    [InlineData("sup", "whats_up")]
    [InlineData("what's up", "whats_up")]
    [InlineData("cancel timers", "timer_delete")]
    public async Task WholeWordKeywords_StillRoute(string transcript, string intent)
    {
        var decision = await CreateInteractionService().BuildDecisionAsync(Turn(transcript));
        Assert.Equal(intent, decision.IntentName);
    }

    private static TurnContext Turn(string transcript) => new()
    {
        RawTranscript = transcript, NormalizedTranscript = transcript, DeviceId = "Ghost-Instance-Onion-Silk"
    };

    private static JiboInteractionService CreateInteractionService() => new(
        new JiboExperienceContentCache(new InMemoryJiboExperienceContentRepository()),
        new FirstItemRandomizer(),
        new InMemoryPersonalMemoryStore());

    private sealed class FirstItemRandomizer : IJiboRandomizer
    {
        public T Choose<T>(IReadOnlyList<T> items) => items[0];
    }
}
