using System.Text.Json;
using Jibo.Cloud.Application.Services;
using Jibo.Cloud.Domain.Models;
using Jibo.Cloud.Infrastructure.Content;
using Jibo.Cloud.Infrastructure.Persistence;
using Jibo.Runtime.Abstractions;

namespace Jibo.Cloud.Tests.WebSockets;

public sealed class AgentModeSocketTests
{
    [Theory]
    [InlineData("let's chat")]
    [InlineData("let's have a conversation")]
    [InlineData("AI Mode")]
    public async Task LaunchThroughListenSocket_SelectsAgentSkill(string phrase)
    {
        var service = CreateService();
        await service.HandleMessageAsync(Envelope("""{"type":"LISTEN","transID":"agent-turn","data":{"hotphrase":true,"rules":["launch"]}}"""));
        var replies = await service.HandleMessageAsync(Envelope(JsonSerializer.Serialize(new
        {
            type = "CLIENT_ASR", transID = "agent-turn", data = new { text = phrase }
        })));
        var data = ListenData(replies);
        Assert.Equal("@be/agent", data.GetProperty("match").GetProperty("skillID").GetString());
    }

    [Theory]
    [InlineData("turn off the lights")]
    [InlineData("stop")]
    [InlineData("Hmm.")]
    [InlineData("what's your paper color")]
    public async Task SkillListenThroughSocket_PreservesFreeFormText(string phrase)
    {
        var service = CreateService();
        await service.HandleMessageAsync(Envelope("""{"type":"LISTEN","transID":"agent-turn","data":{"hotphrase":false,"rules":["agent/conversation"]}}"""));
        var replies = await service.HandleMessageAsync(Envelope(JsonSerializer.Serialize(new
        {
            type = "CLIENT_ASR", transID = "agent-turn", data = new { text = phrase }
        })));
        var data = ListenData(replies);
        Assert.Equal(phrase, data.GetProperty("asr").GetProperty("text").GetString());
        Assert.Equal("skill_listen", data.GetProperty("nlu").GetProperty("intent").GetString());
    }

    private static JsonElement ListenData(IReadOnlyList<WebSocketReply> replies)
    {
        var messages = replies.Select(reply => JsonDocument.Parse(reply.Text!).RootElement.Clone()).ToArray();
        Assert.DoesNotContain(messages, message => message.GetProperty("type").GetString() is "SKILL_ACTION" or "SKILL_REDIRECT");
        return Assert.Single(messages, message => message.GetProperty("type").GetString() == "LISTEN").GetProperty("data");
    }
    private static WebSocketMessageEnvelope Envelope(string text) => new()
    {
        HostName = "neo-hub.jibo.com", Path = "/listen", Kind = "neo-hub-listen", Token = "agent-socket-test", Text = text
    };
    private static JiboWebSocketService CreateService()
    {
        var broker = new DemoConversationBroker(new JiboInteractionService(
            new JiboExperienceContentCache(new InMemoryJiboExperienceContentRepository()),
            new FirstRandomizer(), new InMemoryPersonalMemoryStore()));
        return new JiboWebSocketService(new InMemoryCloudStateStore(), new NullWebSocketTelemetrySink(),
            new WebSocketTurnFinalizationService(broker, new DefaultSttStrategySelector([new SyntheticBufferedAudioSttStrategy()]), new NullTurnTelemetrySink()));
    }
    private sealed class FirstRandomizer : IJiboRandomizer
    {
        public T Choose<T>(IReadOnlyList<T> items) => items[0];
    }
}
