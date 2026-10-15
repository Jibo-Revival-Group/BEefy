using System.Text.Json;
using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Application.Services;
using Jibo.Cloud.Domain.Models;
using Jibo.Cloud.Infrastructure.Audio;
using Jibo.Cloud.Infrastructure.Content;
using Jibo.Cloud.Infrastructure.Persistence;
using Jibo.Cloud.Tests.Infrastructure;
using Jibo.Runtime.Abstractions;
using Moq;

namespace Jibo.Cloud.Tests.WebSockets;

public sealed class SyntheticTurnContextTests
{
    private const string Context = """{"runtime":{"location":{"city":"Pleasant Hill","lat":38.7875,"lng":-94.2694,"iso":"2026-10-15T10:00:00-05:00"},"perception":{"speaker":"looper-1"}},"skill":{"id":"@be/main-menu"}}""";

    private static WebSocketMessageEnvelope Envelope(string type, object data, string transId = "menu-turn") => new()
    {
        ConnectionId = "menu-connection", Token = "menu-token", Kind = "neo-hub-listen",
        HostName = "neo-hub.jibo.com", Path = "/v1/listen",
        Text = JsonSerializer.Serialize(new { type, transID = transId, data })
    };

    private static WebSocketMessageEnvelope Listen(string mode = "CLIENT_ASR", string transId = "menu-turn") =>
        Envelope("LISTEN", new { hotphrase = true, mode, rules = new[] { "globals/global_commands_launch" } }, transId);

    private static WebSocketMessageEnvelope RuntimeContext(string transId = "menu-turn") =>
        Envelope("CONTEXT", JsonSerializer.Deserialize<JsonElement>(Context), transId);

    private static JiboWebSocketService Service(InMemoryCloudStateStore store, IConversationBroker broker) =>
        new(store, new NullWebSocketTelemetrySink(), new WebSocketTurnFinalizationService(broker,
            new DefaultSttStrategySelector([new SyntheticBufferedAudioSttStrategy()]), new NullTurnTelemetrySink()));

    private static Mock<IConversationBroker> Broker(Action<TurnContext> received)
    {
        var broker = new Mock<IConversationBroker>();
        broker.Setup(b => b.HandleTurnAsync(It.IsAny<TurnContext>(), It.IsAny<CancellationToken>()))
            .Callback<TurnContext, CancellationToken>((turn, _) => received(turn))
            .ReturnsAsync(new ResponsePlan { Status = ResponseStatus.Succeeded, IntentName = "test" });
        return broker;
    }

    [Theory]
    [InlineData("CLIENT_ASR", false)]
    [InlineData("CLIENT_ASR", true)]
    [InlineData("CLIENT_NLU", false)]
    [InlineData("CLIENT_NLU", true)]
    public async Task Weather_UsesRuntimeLocationWithEitherNativeMessageOrder(string mode, bool contextFirst)
    {
        var weather = new Mock<IWeatherReportProvider>();
        WeatherReportRequest? received = null;
        weather.Setup(w => w.GetReportAsync(It.IsAny<WeatherReportRequest>(), It.IsAny<CancellationToken>()))
            .Callback<WeatherReportRequest, CancellationToken>((request, _) => received = request)
            .ReturnsAsync(new WeatherReportSnapshot("Pleasant Hill", "clear", 61, 65, 54, "clear", false));
        var interaction = new JiboInteractionService(new JiboExperienceContentCache(new InMemoryJiboExperienceContentRepository()),
            new DefaultJiboRandomizer(), new InMemoryPersonalMemoryStore(), weather.Object);
        var service = Service(new InMemoryCloudStateStore(), new DemoConversationBroker(interaction));
        await service.HandleMessageAsync(Listen(mode));
        if (contextFirst) await service.HandleMessageAsync(RuntimeContext());
        object input = mode == "CLIENT_ASR" ? new { text = "tell me about the weather" } :
            new { intent = "requestWeatherPR", entities = new { }, rules = new[] { "globals/global_commands_launch" } };
        var replies = await service.HandleMessageAsync(Envelope(mode, input));
        if (!contextFirst)
        {
            Assert.Empty(replies);
            Assert.Null(received);
            replies = await service.HandleMessageAsync(RuntimeContext());
        }
        Assert.NotEmpty(replies);
        Assert.NotNull(received);
        Assert.Null(received.LocationQuery);
        Assert.Equal(38.7875, received.Latitude);
        Assert.Equal(-94.2694, received.Longitude);
        Assert.Equal("Pleasant Hill", received.PreferredLocationName);
        weather.Verify(w => w.GetReportAsync(It.IsAny<WeatherReportRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Empty(await service.HandleMessageAsync(RuntimeContext()));
    }

    [Theory]
    [InlineData("personal report")]
    [InlineData("tell me about the weather")]
    [InlineData("tell me about the news")]
    [InlineData("tell me about my calendar")]
    [InlineData("what does my commute look like")]
    [InlineData("tell me a joke")]
    [InlineData("do a dance")]
    [InlineData("surprise me")]
    public async Task MenuLaunches_ReceiveContextBeforeExecuting(string text)
    {
        TurnContext? received = null;
        var broker = Broker(turn => received = turn);
        var service = Service(new InMemoryCloudStateStore(), broker.Object);
        await service.HandleMessageAsync(Listen());
        Assert.Empty(await service.HandleMessageAsync(Envelope("CLIENT_ASR", new { text })));
        Assert.Null(received);
        await service.HandleMessageAsync(RuntimeContext());
        Assert.NotNull(received);
        Assert.Equal(text, received.RawTranscript);
        using var context = JsonDocument.Parse(Assert.IsType<string>(received.Attributes["context"]));
        Assert.Equal("Pleasant Hill", context.RootElement.GetProperty("runtime").GetProperty("location").GetProperty("city").GetString());
        Assert.Equal("looper-1", context.RootElement.GetProperty("runtime").GetProperty("perception").GetProperty("speaker").GetString());
        broker.Verify(b => b.HandleTurnAsync(It.IsAny<TurnContext>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task NewTransaction_DiscardsPendingClientInput()
    {
        var broker = Broker(_ => { });
        var service = Service(new InMemoryCloudStateStore(), broker.Object);
        await service.HandleMessageAsync(Listen());
        await service.HandleMessageAsync(Envelope("CLIENT_ASR", new { text = "tell me about the weather" }));
        await service.HandleMessageAsync(Listen(transId: "next-turn"));
        await service.HandleMessageAsync(RuntimeContext("next-turn"));
        broker.Verify(b => b.HandleTurnAsync(It.IsAny<TurnContext>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task MissingContext_UsesExistingListenTimeoutAndDiscardsPendingInput()
    {
        var broker = Broker(_ => { });
        var store = new InMemoryCloudStateStore();
        var service = Service(store, broker.Object);
        await service.HandleMessageAsync(Listen());
        await service.HandleMessageAsync(Envelope("CLIENT_ASR", new { text = "tell me about the weather" }));
        var session = store.FindSessionByToken("menu-token")!;
        session.TurnState.ListenOpenedUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
        Assert.NotEmpty(await service.HandleIdleAsync(session, Listen()));
        await service.HandleMessageAsync(RuntimeContext());
        broker.Verify(b => b.HandleTurnAsync(It.IsAny<TurnContext>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("CLIENT_ASR")]
    [InlineData("CLIENT_NLU")]
    public async Task LocalSkillInput_StillPassesThroughWithoutContext(string mode)
    {
        var broker = Broker(_ => { });
        var service = Service(new InMemoryCloudStateStore(), broker.Object);
        await service.HandleMessageAsync(Envelope("LISTEN", new { mode, rules = new[] { "clock/clock_menu" } }));
        object input = mode == "CLIENT_ASR" ? new { text = "what time is it" } :
            new { intent = "askForTime", entities = new { domain = "clock" }, rules = new[] { "clock/clock_menu" } };
        Assert.NotEmpty(await service.HandleMessageAsync(Envelope(mode, input)));
    }

    [Fact]
    public async Task StandaloneClientInput_StillExecutesWithoutListenOrContext()
    {
        var broker = Broker(_ => { });
        var service = Service(new InMemoryCloudStateStore(), broker.Object);
        await service.HandleMessageAsync(Envelope("CLIENT_ASR", new { text = "tell me about the weather" }));
        broker.Verify(b => b.HandleTurnAsync(It.IsAny<TurnContext>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
