using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Application.Services;
using Jibo.Cloud.Infrastructure.Content;
using Jibo.Cloud.Infrastructure.Persistence;
using Jibo.Runtime.Abstractions;

namespace Jibo.Cloud.Tests.Application;

// Original chitchat answers reach an unidentified speaker, and the mood shortcut doesn't shadow them.
public sealed class NativeChitchatReachTests
{
    [Theory]
    [InlineData("i want to kill myself", "need some help")]          // SRS: !speaker lines with no one recognized
    [InlineData("i drink too much whiskey", "need some help")]
    [InlineData("do you hate me", "course not")]
    [InlineData("do you get lonely", "Not really")]                  // was "All systems are go."
    [InlineData("are you mad at me", "never get mad at you")]
    [InlineData("you should be proud", "proud of")]                  // was "I can do that mood."
    [InlineData("what's your favorite ice cream", "mint chocolate chip")] // was a random favorite (macaroni)
    [InlineData("tell me your favorite color", "blue")]               // was "For that I don't have a favorite yet."
    public async Task OriginalChitchatAnswer(string transcript, string expected)
    {
        var decision = await CreateService().BuildDecisionAsync(new TurnContext
        {
            RawTranscript = transcript, NormalizedTranscript = transcript, DeviceId = "Ghost-Instance-Onion-Silk"
        });

        Assert.Contains(expected, decision.ReplyText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MoodMimWithNoLineForTheMood_KeepsTheMoodAnswer()
    {
        var decision = await CreateService().BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "are you pleased", NormalizedTranscript = "are you pleased", DeviceId = "Ghost-Instance-Onion-Silk"
        });

        Assert.Equal("emotion_query", decision.IntentName);
    }

    private static JiboInteractionService CreateService() => new(
        new JiboExperienceContentCache(new InMemoryJiboExperienceContentRepository()),
        new FirstItemRandomizer(), new InMemoryPersonalMemoryStore());

    private sealed class FirstItemRandomizer : IJiboRandomizer
    {
        public T Choose<T>(IReadOnlyList<T> items) => items[0];
    }
}
