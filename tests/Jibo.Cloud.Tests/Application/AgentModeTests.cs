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

    [Theory]
    [InlineData("tell me about AI mode")]
    [InlineData("I wish we could have a conversation")]
    [InlineData("let's chat about music")]
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
