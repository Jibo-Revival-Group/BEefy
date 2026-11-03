using System.Text.Json;
using Jibo.Cloud.Application.Services;
using Jibo.Cloud.Domain.Models;
using Jibo.Cloud.Infrastructure.Content;
using Jibo.Cloud.Infrastructure.Persistence;
using Jibo.Runtime.Abstractions;

namespace Jibo.Cloud.Tests.Application;

public sealed class AgentModeTests
{
    [Theory]
    [InlineData("Hey Jibo, let's chat")]
    [InlineData("Let’s have a conversation.")]
    [InlineData("AI Mode")]
    [InlineData("agent mode please")]
    [InlineData("A I mode")]
    public async Task Launch_RoutesToRobotWithoutCloudSpeech(string text)
    {
        var turn = Turn(text);
        var plan = await new DemoConversationBroker(Service()).HandleTurnAsync(turn);
        Assert.Equal("agent_mode", plan.IntentName);
        Assert.False(plan.FollowUp.KeepMicOpen);
        Assert.Equal("@be/agent", Assert.Single(plan.Actions.OfType<InvokeNativeSkillAction>()).SkillName);
        var messages = Messages(plan, turn);
        var listen = Assert.Single(messages, message => message.GetProperty("type").GetString() == "LISTEN");
        Assert.Equal("@be/agent", listen.GetProperty("data").GetProperty("match").GetProperty("skillID").GetString());
        Assert.DoesNotContain(messages, message => message.GetProperty("type").GetString() is "SKILL_ACTION" or "SKILL_REDIRECT");
    }

    [Theory]
    [InlineData("How is my phone's battery?")]
    [InlineData("hey jibo what's my phone battery at")]
    [InlineData("how much battery does my iPhone have")]
    [InlineData("is my phone charged")]
    [InlineData("what's my phone at")]
    [InlineData("hey jibo how's my phone doing")]
    [InlineData("how is my phone")]
    public async Task PhoneBattery_RoutesToRobotSkill(string text)
    {
        var turn = Turn(text);
        var plan = await new DemoConversationBroker(Service()).HandleTurnAsync(turn);
        Assert.Equal("phone_battery", plan.IntentName);
        Assert.Equal("@be/phone-battery", Assert.Single(plan.Actions.OfType<InvokeNativeSkillAction>()).SkillName);
        var listen = Assert.Single(Messages(plan, turn), message => message.GetProperty("type").GetString() == "LISTEN");
        Assert.Equal("@be/phone-battery", listen.GetProperty("data").GetProperty("match").GetProperty("skillID").GetString());
    }

    [Theory]
    [InlineData("how is your battery")]
    [InlineData("call my phone")]
    [InlineData("what's the battery level")]
    [InlineData("what's my phone number")]
    [InlineData("how is my phone call going to work")]
    public void PhoneBattery_LeavesOtherQuestionsAlone(string text) => Assert.False(PhoneBatteryRouting.IsQuestion(text));

    [Theory]
    [InlineData("play Hello by Adele")]
    [InlineData("turn off the lights")]
    [InlineData("what time is it")]
    [InlineData("stop")]
    [InlineData("let's chat")]
    [InlineData("Call me Alex, and tell me a story.")]
    public async Task Conversation_ReturnsOriginalTranscriptWithoutExecutingCommands(string text)
    {
        var turn = Turn(text, true);
        var plan = await new DemoConversationBroker(Service()).HandleTurnAsync(turn);
        Assert.Equal("skill_listen", plan.IntentName);
        Assert.False(plan.FollowUp.KeepMicOpen);
        Assert.Empty(plan.Actions.OfType<InvokeNativeSkillAction>());
        Assert.All(plan.Actions.OfType<SpeakAction>(), action => Assert.Empty(action.Text));
        var messages = Messages(plan, turn);
        var data = Assert.Single(messages, message => message.GetProperty("type").GetString() == "LISTEN").GetProperty("data");
        Assert.Equal(text, data.GetProperty("asr").GetProperty("text").GetString());
        Assert.Equal("skill_listen", data.GetProperty("nlu").GetProperty("intent").GetString());
        Assert.DoesNotContain(messages, message => message.GetProperty("type").GetString() is "SKILL_ACTION" or "SKILL_REDIRECT");
    }

    // Misheard "let's chat" variants seen in production transcripts.
    [Theory]
    [InlineData("less chat")]
    [InlineData("list chat")]
    [InlineData("let chat")]
    [InlineData("let's cha")]
    [InlineData("lis chap")]
    [InlineData("less chap")]
    [InlineData("less cha")]
    [InlineData("let's chant")]
    [InlineData("let's have a chat")]
    [InlineData("hey jibo let's chat please")]
    [InlineData("Let's Chat")]
    public void Launch_AcceptsMisheardLetsChat(string text) => Assert.True(AgentModeRouting.IsLaunch(text));

    [Theory]
    [InlineData("tell me about AI mode")]
    [InlineData("I wish we could have a conversation")]
    [InlineData("let's chat about music")]
    [InlineData("less is more")]
    [InlineData("list chat history")]
    [InlineData("chat")]
    [InlineData("let's go")]
    [InlineData("pass chat")]
    public void Launch_DoesNotCaptureUnrelatedSentences(string text) => Assert.False(AgentModeRouting.IsLaunch(text));

    private static JsonElement[] Messages(ResponsePlan plan, TurnContext turn) =>
        ResponsePlanToSocketMessagesMapper.Map(plan, turn, new CloudSession(), true)
            .Select(reply => JsonDocument.Parse(reply.Text).RootElement.Clone()).ToArray();
    private static TurnContext Turn(string text, bool agent = false) => new()
    {
        RawTranscript = text, NormalizedTranscript = text, InputMode = TurnInputMode.DirectText,
        DeviceId = "agent-test", Attributes = new Dictionary<string, object?>
        {
            ["transID"] = "agent-turn", ["messageType"] = "CLIENT_ASR",
            ["listenRules"] = agent ? new[] { AgentModeRouting.ConversationRule } : new[] { "launch" }
        }
    };
    private static JiboInteractionService Service() => new(
        new JiboExperienceContentCache(new InMemoryJiboExperienceContentRepository()),
        new FirstRandomizer(), new InMemoryPersonalMemoryStore());
    private sealed class FirstRandomizer : IJiboRandomizer
    {
        public T Choose<T>(IReadOnlyList<T> items) => items[0];
    }
}
