using System.Text.Json;
using Jibo.Cloud.Application.Services;
using Jibo.Cloud.Domain.Models;
using Jibo.Cloud.Infrastructure.Content;
using Jibo.Cloud.Infrastructure.Persistence;
using Jibo.Runtime.Abstractions;

namespace Jibo.Cloud.Tests.Application;

public sealed class MusicInteractionServiceTests
{
    [Theory]
    [InlineData("Hey Jibo, play Yes I'm A Mess", "Yes I'm A Mess", null)]
    [InlineData("play Yes I’m A Mess by AJR", "Yes I’m A Mess", "AJR")]
    [InlineData("Could you play the song Hello by Adele?", "Hello", "Adele")]
    public void Parser_PreservesSongAndArtist(string text, string title, string? artist)
    {
        Assert.True(MusicCommandParser.TryParse(text, out var request));
        Assert.Equal(title, request!.Query);
        Assert.Equal(artist, request.Artist);
    }

    [Theory]
    [InlineData("play the radio")]
    [InlineData("play circuit saver")]
    [InlineData("play JiboTube")]
    [InlineData("play a game")]
    [InlineData("sing me a song")]
    [InlineData("dance")]
    public void Parser_DoesNotCaptureExistingActivities(string text) => Assert.False(MusicCommandParser.TryParse(text, out _));

    [Theory]
    [InlineData("Hey Jibo, play Yes I'm A Mess", "Yes I'm A Mess", "")]
    [InlineData("play Yes I’m A Mess by AJR", "Yes I’m A Mess", "AJR")]
    public async Task Request_LaunchesBuiltInPlayer_WithEntitiesAndNoCloudSpeech(string text, string title, string artist)
    {
        var turn = Turn(text);
        var plan = await new DemoConversationBroker(Service()).HandleTurnAsync(turn);
        Assert.Equal("music_play", plan.IntentName);
        Assert.False(plan.FollowUp.KeepMicOpen);
        Assert.Equal("@be/music", Assert.Single(plan.Actions.OfType<InvokeNativeSkillAction>()).SkillName);
        var messages = ResponsePlanToSocketMessagesMapper.Map(plan, turn, new CloudSession(), false)
            .Select(message => JsonDocument.Parse(message.Text).RootElement.Clone()).ToArray();
        var listen = Assert.Single(messages, message => message.GetProperty("type").GetString() == "LISTEN");
        var data = listen.GetProperty("data");
        Assert.Equal("@be/music", data.GetProperty("match").GetProperty("skillID").GetString());
        Assert.Equal("playSong", data.GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal(title, data.GetProperty("nlu").GetProperty("entities").GetProperty("query").GetString());
        Assert.Equal(artist, data.GetProperty("nlu").GetProperty("entities").GetProperty("artist").GetString());
        Assert.DoesNotContain(messages, message => message.GetProperty("type").GetString() is "SKILL_ACTION" or "SKILL_REDIRECT");
    }

    [Fact]
    public async Task StockRobot_DoesNotLaunchMissingPlayer()
    {
        var decision = await Service().BuildDecisionAsync(Turn("play Hello by Adele", "1.9.2"));
        Assert.Equal("music_play", decision.IntentName);
        Assert.Null(decision.SkillName);
        Assert.Contains("BEam", decision.ReplyText);
    }

    [Theory]
    [InlineData("play the radio", "radio")]
    [InlineData("play circuit saver", "launchGame")]
    [InlineData("sing me a song", "robot_can_sing")]
    public async Task ExistingCommands_KeepTheirRoute(string text, string intent)
    {
        var decision = await Service().BuildDecisionAsync(Turn(text));
        Assert.Equal(intent, decision.IntentName);
    }

    private static TurnContext Turn(string text, string release = "BEam.1.0.0") => new()
    {
        RawTranscript = text, NormalizedTranscript = text, InputMode = TurnInputMode.DirectText,
        FirmwareVersion = release, DeviceId = "music-test", Attributes = new Dictionary<string, object?> { ["transID"] = "music-turn" }
    };
    private static JiboInteractionService Service() => new(
        new JiboExperienceContentCache(new InMemoryJiboExperienceContentRepository()),
        new FirstRandomizer(), new InMemoryPersonalMemoryStore());
    private sealed class FirstRandomizer : IJiboRandomizer
    {
        public T Choose<T>(IReadOnlyList<T> items) => items[0];
    }
}
