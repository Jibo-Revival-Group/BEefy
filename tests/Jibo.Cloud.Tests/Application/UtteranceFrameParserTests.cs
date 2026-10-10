using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Application.Services;
using Jibo.Cloud.Infrastructure.Content;
using Jibo.Cloud.Infrastructure.Persistence;
using Jibo.Runtime.Abstractions;

namespace Jibo.Cloud.Tests.Application;

public sealed class UtteranceFrameParserTests
{
    [Theory]
    [InlineData("buts you favor color", "robot_favorite_color")]
    [InlineData("buts you flavor colour", "robot_favorite_color")]
    [InlineData("buts your favour collar", "robot_favorite_color")]
    [InlineData("butts you favorite color", "robot_favorite_color")]
    [InlineData("wats your favor to color", "robot_favorite_color")]
    [InlineData("what's your favourite colour", "robot_favorite_color")]
    [InlineData("what's your favorit color", "robot_favorite_color")]
    [InlineData("buts you favourit colour", "robot_favorite_color")]
    [InlineData("what is your favorite color", "robot_favorite_color")]
    [InlineData("who is your favourite singer", "robot_favorite_singer")]
    [InlineData("do you have a least favorite color", "robot_least_favorite_color")]
    [InlineData("what color do you like least", "robot_least_favorite_color")]
    [InlineData("what colour do you dislike", "robot_least_favorite_color")]
    [InlineData("what joke do you like best", "robot_favorite_joke")]
    [InlineData("what's your favorite word", "robot_favorite_word")]
    [InlineData("what's your favorite word of the day", "robot_favorite_word")]
    [InlineData("what's your favorite ice cream flavor", "robot_favorite_ice_cream_flavor")]
    [InlineData("what kind of music do you like", "robot_favorite_music")]
    [InlineData("what is your favorite kind of music", "robot_favorite_music_genre")]
    [InlineData("what was your favorite color", "robot_favorite_color")]
    [InlineData("what was your favourite super bowl commercial", "robot_favorite_super_bowl_commercial")]
    [InlineData("what's your favorite banned", "robot_favorite_various_styles_band")]
    [InlineData("what banned do you like", "robot_favorite_various_styles_band")]
    [InlineData("what's your least favorite banned", "robot_least_favorite_band")]
    public void TryParsePreference_ReadsSlotsThroughAsrConfusion(string transcript, string expectedIntent)
    {
        Assert.Equal(expectedIntent, UtteranceFrameParser.TryParsePreference(transcript));
    }

    [Theory]
    [InlineData("what's your flavor")]
    [InlineData("what's my favorite color")]
    [InlineData("what flavor are you")]
    [InlineData("play word of the day")]
    public void TryParsePreference_LeavesNonPreferenceUtterances(string transcript)
    {
        Assert.Null(UtteranceFrameParser.TryParsePreference(transcript));
    }

    [Fact]
    public void TryParsePastPreference_UnknownSubject_UsesDidYouHaveFavorite()
    {
        Assert.Equal(
            "robot_did_you_have_a_favorite",
            UtteranceFrameParser.TryParsePastPreference("what was your favorite"));
        Assert.Equal(
            "robot_did_you_have_a_favorite",
            UtteranceFrameParser.TryParsePastPreference("what was your favorite zucchini"));
    }

    [Theory]
    [InlineData("play word of the day")]
    [InlineData("play ward of the day")]
    [InlineData("play work of the day")]
    [InlineData("play work of a day")]
    [InlineData("work the day")]
    [InlineData("work a day")]
    [InlineData("word of a day")]
    [InlineData("word of the date")]
    [InlineData("start word of da day")]
    [InlineData("can we play word of the day please")]
    public void TryParseWordOfTheDay_AcceptsConfusedLaunches(string transcript)
    {
        Assert.Equal("word_of_the_day", UtteranceFrameParser.TryParseWordOfTheDay(transcript));
    }

    [Theory]
    [InlineData("what's your favorite word")]
    [InlineData("what's the date")]
    [InlineData("what's your flavor")]
    public void TryParseWordOfTheDay_DoesNotStealOtherFrames(string transcript)
    {
        Assert.Null(UtteranceFrameParser.TryParseWordOfTheDay(transcript));
    }

    [Theory]
    [InlineData("buts you favor color", "robot_favorite_color")]
    [InlineData("what's your favorit color", "robot_favorite_color")]
    [InlineData("play ward of the day", "word_of_the_day")]
    [InlineData("play work of a day", "word_of_the_day")]
    [InlineData("what's your favorite banned", "robot_favorite_various_styles_band")]
    [InlineData("what's your favorite word", "robot_favorite_word")]
    [InlineData("do you like blue", "robot_favorite_color")]
    [InlineData("what's your flavor", "robot_flavor")]
    [InlineData("can we play word of the day please", "word_of_the_day")]
    public async Task BuildDecisionAsync_FrameParserRoutesMisheardAndExistingUtterances(
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
