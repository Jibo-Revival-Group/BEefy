using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Application.Services;
using Jibo.Cloud.Infrastructure.Content;
using Jibo.Cloud.Infrastructure.Persistence;
using Jibo.Runtime.Abstractions;

namespace Jibo.Cloud.Tests.Application;

public sealed class NluUtteranceVariantRoutingTests
{
    [Theory]
    [InlineData("so what time is it", "time")]
    [InlineData("well what time is it", "time")]
    [InlineData("um what time is it", "time")]
    [InlineData("oh what time is it", "time")]
    [InlineData("okay what time is it", "time")]
    [InlineData("actually what time is it", "time")]
    [InlineData("what time is it please", "time")]
    [InlineData("could you tell me the time", "time")]
    [InlineData("can you tell me the time please", "time")]
    [InlineData("whats the weather please", "weather")]
    [InlineData("what's the weather please", "weather")]
    [InlineData("whats the weather thanks", "weather")]
    [InlineData("i want to know the weather", "weather")]
    [InlineData("what is the date please", "date")]
    [InlineData("can we play word of the day please", "word_of_the_day")]
    [InlineData("can we play word of the day", "word_of_the_day")]
    [InlineData("could we play word of the day", "word_of_the_day")]
    [InlineData("shall we play word of the day please", "word_of_the_day")]
    public async Task BuildDecisionAsync_FillerAndPoliteVariants_RouteCorrectly(
        string transcript,
        string expectedIntent)
    {
        var decision = await CreateService().BuildDecisionAsync(new TurnContext
        {
            RawTranscript = transcript,
            NormalizedTranscript = transcript,
            DeviceId = "Ghost-Instance-Onion-Silk"
        });

        Assert.Equal(expectedIntent, decision.IntentName);
    }

    [Theory]
    [InlineData("can you dance", "robot_can_dance")]
    [InlineData("please dance", "dance")]
    [InlineData("tell me about dancing", "chat")]
    public async Task BuildDecisionAsync_ExistingAbilityVsCommandSplit_Preserved(
        string transcript,
        string expectedIntent)
    {
        var decision = await CreateService().BuildDecisionAsync(new TurnContext
        {
            RawTranscript = transcript,
            NormalizedTranscript = transcript,
            DeviceId = "Ghost-Instance-Onion-Silk"
        });

        Assert.Equal(expectedIntent, decision.IntentName);
    }

    [Fact]
    public async Task BuildDecisionAsync_CanWePlayWordOfTheDay_OnYesNoTurn_RoutesToWordOfTheDay()
    {
        var decision = await CreateService().BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "can we play word of the day please",
            NormalizedTranscript = "can we play word of the day please",
            DeviceId = "Ghost-Instance-Onion-Silk",
            Attributes = new Dictionary<string, object?>
            {
                ["listenHotphrase"] = false,
                ["listenRules"] = (string[])["shared/yes_no", "globals/gui_nav"],
                ["listenAsrHints"] = (string[])["$YESNO"]
            }
        });

        Assert.Equal("word_of_the_day", decision.IntentName);
    }

    [Fact]
    public async Task BuildDecisionAsync_CanWePlayWordOfTheDay_OnSkillOwnedListen_RoutesToWordOfTheDay()
    {
        var decision = await CreateService().BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "can we play word of the day please",
            NormalizedTranscript = "can we play word of the day please",
            DeviceId = "Ghost-Instance-Onion-Silk",
            Attributes = new Dictionary<string, object?>
            {
                ["listenHotphrase"] = false,
                ["listenRules"] = (string[])["exercise/want_to", "globals/gui_nav"]
            }
        });

        Assert.Equal("word_of_the_day", decision.IntentName);
    }

    [Fact]
    public async Task BuildDecisionAsync_StrongPromptEcho_OnYesNoTurn_StillSkillListen()
    {
        var decision = await CreateService().BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "do you want to take a picture",
            NormalizedTranscript = "do you want to take a picture",
            DeviceId = "Ghost-Instance-Onion-Silk",
            Attributes = new Dictionary<string, object?>
            {
                ["listenHotphrase"] = false,
                ["listenRules"] = (string[])["shared/yes_no", "globals/gui_nav"],
                ["listenAsrHints"] = (string[])["$YESNO"]
            }
        });

        Assert.Equal("skill_listen", decision.IntentName);
    }

    private static JiboInteractionService CreateService()
    {
        return new JiboInteractionService(
            new JiboExperienceContentCache(new InMemoryJiboExperienceContentRepository()),
            new FirstItemRandomizer(),
            new InMemoryPersonalMemoryStore());
    }

    private sealed class FirstItemRandomizer : IJiboRandomizer
    {
        public T Choose<T>(IReadOnlyList<T> items) => items[0];
    }
}
