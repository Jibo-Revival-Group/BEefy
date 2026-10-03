using System.Net.WebSockets;
using System.Text.Json;
using Jibo.Cloud.Application.Services;
using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Domain.Models;
using Jibo.Cloud.Infrastructure.Content;
using Jibo.Cloud.Infrastructure.Persistence;
using Jibo.Runtime.Abstractions;

namespace Jibo.Cloud.Tests.Application;

public sealed class HomeAssistantEntityAwareTests
{
    [Fact]
    public async Task SendCommandAndWaitAsync_ReturnsResult_WhenCommandResultArrives()
    {
        var registry = new HomeAssistantConnectionRegistry();
        var socket = new CapturingWebSocket(registry);
        registry.RegisterPairedConnection("ha-instance-1", socket);

        var waitTask = registry.SendCommandAndWaitAsync(
            "ha-instance-1",
            "link-test-1",
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            "lights_off_named",
            new Dictionary<string, string> { ["targetName"] = "zanes" });

        var result = await waitTask;

        Assert.NotNull(result);
        Assert.True(result!.IsOk);
        Assert.Equal("Zane's Lamp", result.MatchedName);
    }

    [Fact]
    public async Task BuildDecisionAsync_NamedLight_SpeaksMatchedName_WhenHaReturnsOk()
    {
        var (service, _) = CreateServiceWithRespondingHa(
            status: "ok",
            matchedName: "Bedroom Lamp");

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "turn off bedrom light",
            NormalizedTranscript = "turn off bedrom light",
            DeviceId = "Ghost-Instance-Onion-Silk"
        });

        Assert.Equal("ha_lights_off", decision.IntentName);
        Assert.Equal("Okay, turning off Bedroom Lamp.", decision.ReplyText);
    }

    [Fact]
    public async Task BuildDecisionAsync_NamedLight_SpeaksNoLightNamed_WhenHaReturnsNotFound()
    {
        var (service, _) = CreateServiceWithRespondingHa(
            status: "not_found",
            heardName: "spaceship");

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "turn on spaceship light",
            NormalizedTranscript = "turn on spaceship light",
            DeviceId = "Ghost-Instance-Onion-Silk"
        });

        Assert.Equal("ha_lights_on", decision.IntentName);
        Assert.Equal("There is no light named spaceship.", decision.ReplyText);
    }

    [Fact]
    public async Task BuildDecisionAsync_Light_ExplainsMissingRobotPairing()
    {
        var (service, _) = CreateServiceWithRespondingHa(status: "error", message: "pairing_required");
        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "turn off the lights",
            NormalizedTranscript = "turn off the lights",
            DeviceId = "Ghost-Instance-Onion-Silk"
        });
        Assert.Equal("My Home Assistant pairing is missing on this robot. Please pair me using the Yes or No prompt.",
            decision.ReplyText);
    }

    [Fact]
    public async Task BuildDecisionAsync_Climate_AsksWhichThermostat_WhenNeedsClarification()
    {
        var (service, pendingStore) = CreateServiceWithRespondingHa(
            status: "needs_clarification",
            candidates:
            [
                new HomeAssistantCommandCandidate("climate.hall", "Hallway"),
                new HomeAssistantCommandCandidate("climate.office", "Office")
            ]);

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "set the temperature to 70",
            NormalizedTranscript = "set the temperature to 70",
            DeviceId = "Ghost-Instance-Onion-Silk"
        });

        Assert.Equal("ha_climate_set_temp", decision.IntentName);
        Assert.Equal("Which thermostat should I use, Hallway or Office?", decision.ReplyText);
        Assert.NotNull(pendingStore.TryGet("BOJW-1000-0017-0820-0020", "Ghost-Instance-Onion-Silk"));
    }

    [Fact]
    public async Task BuildDecisionAsync_ClimateClarify_AppliesChosenEntity()
    {
        var (service, pendingStore) = CreateServiceWithRespondingHa(
            status: "ok",
            matchedName: "Hallway",
            autoRespondAction: "climate_apply_entity");

        pendingStore.Set(
            "Ghost-Instance-Onion-Silk",
            new HomeAssistantPendingClimateStore.PendingClimateAction(
                "set_temperature",
                [
                    new HomeAssistantCommandCandidate("climate.hall", "Hallway"),
                    new HomeAssistantCommandCandidate("climate.office", "Office")
                ],
                Temperature: "70"));

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "hallway",
            NormalizedTranscript = "hallway",
            DeviceId = "Ghost-Instance-Onion-Silk"
        });

        Assert.Equal("ha_climate_clarify", decision.IntentName);
        Assert.Equal("Okay, setting the Hallway to 70 degrees.", decision.ReplyText);
        Assert.Null(pendingStore.TryGet("BOJW-1000-0017-0820-0020", "Ghost-Instance-Onion-Silk"));
    }

    [Fact]
    public async Task BuildDecisionAsync_ClimateGetTemp_SpeaksAmbientTemperature_WhenHaReturnsOk()
    {
        var (service, _) = CreateServiceWithRespondingHa(
            status: "ok",
            matchedName: "Living Room Thermostat",
            currentTemperature: 72m,
            unit: "°F");

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "what temperature is it in here",
            NormalizedTranscript = "what temperature is it in here",
            DeviceId = "Ghost-Instance-Onion-Silk"
        });

        Assert.Equal("ha_climate_get_temp", decision.IntentName);
        Assert.Equal("It's 72 degrees in here.", decision.ReplyText);
    }

    [Fact]
    public async Task BuildDecisionAsync_ClimateGetTemp_AsksWhichThermostat_WhenNeedsClarification()
    {
        var (service, pendingStore) = CreateServiceWithRespondingHa(
            status: "needs_clarification",
            candidates:
            [
                new HomeAssistantCommandCandidate("climate.hall", "Hallway"),
                new HomeAssistantCommandCandidate("climate.office", "Office")
            ]);

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "what temperature is it in here",
            NormalizedTranscript = "what temperature is it in here",
            DeviceId = "Ghost-Instance-Onion-Silk"
        });

        Assert.Equal("ha_climate_get_temp", decision.IntentName);
        Assert.Equal("Which thermostat should I use, Hallway or Office?", decision.ReplyText);
        var pending = pendingStore.TryGet("BOJW-1000-0017-0820-0020", "Ghost-Instance-Onion-Silk");
        Assert.NotNull(pending);
        Assert.Equal("get_temperature", pending!.Action);
    }

    [Fact]
    public async Task BuildDecisionAsync_ClimateClarify_SpeaksReading_WhenPendingGetTemperature()
    {
        var (service, pendingStore) = CreateServiceWithRespondingHa(
            status: "ok",
            matchedName: "Hallway",
            currentTemperature: 71m,
            unit: "°F",
            autoRespondAction: "climate_apply_entity");

        pendingStore.Set(
            "Ghost-Instance-Onion-Silk",
            new HomeAssistantPendingClimateStore.PendingClimateAction(
                "get_temperature",
                [
                    new HomeAssistantCommandCandidate("climate.hall", "Hallway"),
                    new HomeAssistantCommandCandidate("climate.office", "Office")
                ]));

        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "hallway",
            NormalizedTranscript = "hallway",
            DeviceId = "Ghost-Instance-Onion-Silk"
        });

        Assert.Equal("ha_climate_clarify", decision.IntentName);
        Assert.Equal("It's 71 degrees on the Hallway.", decision.ReplyText);
        Assert.Null(pendingStore.TryGet("BOJW-1000-0017-0820-0020", "Ghost-Instance-Onion-Silk"));
    }

    [Fact]
    public void EntityNameMatcher_PicksClosestCandidate()
    {
        var match = HomeAssistantEntityNameMatcher.FindClosest(
            "hallwey",
            [
                new HomeAssistantCommandCandidate("climate.hall", "Hallway"),
                new HomeAssistantCommandCandidate("climate.office", "Office")
            ]);

        Assert.NotNull(match);
        Assert.Equal("Hallway", match!.Name);
    }

    [Theory]
    [InlineData("turn off the lights", "ok", "Okay, turning off the lights.")]
    [InlineData("lights off", "ok", "Okay, turning off the lights.")]
    [InlineData("turn the lights off", "ok", "Okay, turning off the lights.")]
    [InlineData("kill the lights", "ok", "Okay, turning off the lights.")]
    [InlineData("lights on", "ok", "Okay, turning on the lights.")]
    [InlineData("turn on the lights", "ok", "Okay, turning on the lights.")]
    [InlineData("turn off the lights", "error", "I couldn't control the lights right now.")]
    [InlineData("turn off the lights", "not_found", "I couldn't find any lights in my Home Assistant Area.")]
    public async Task RoomLights_ReplyReflectsHaResult(string transcript, string status, string reply)
    {
        var (service, _) = CreateServiceWithRespondingHa(status);
        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            DeviceId = "Ghost-Instance-Onion-Silk",
            NormalizedTranscript = transcript,
            RawTranscript = transcript
        });
        Assert.Equal(reply, decision.ReplyText);
    }

    [Theory]
    [InlineData("error", "auth_failed", "Home Assistant rejected my command. Please check my pairing and the server clocks.")]
    [InlineData("not_found", "missing_area", "Assign me to an Area in Home Assistant so I can control my room's lights.")]
    [InlineData("silent", null, "Home Assistant didn't confirm the light command in time.")]
    public async Task RoomLights_ReportsSpecificFailures(string status, string? message, string reply)
    {
        var sends = 0;
        var (service, _) = CreateServiceWithRespondingHa(status, message: message,
            onCommand: _ => sends++);
        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            DeviceId = "Ghost-Instance-Onion-Silk",
            NormalizedTranscript = "turn off the lights"
        });
        Assert.Equal(reply, decision.ReplyText);
        Assert.Equal(1, sends);
    }

    [Theory]
    [InlineData("turn off the lights")]
    [InlineData("turn off zanes light")]
    public async Task CompletedLightTurn_DispatchesExactlyOnce(string transcript)
    {
        var sends = 0;
        WebSocketTurnFinalizationService? finalizer = null;
        InMemoryCloudStateStore? state = null;
        var (harness, _) = CreateServiceWithRespondingHa("ok", onCommand: _ => sends++,
            inspect: (interaction, command, store) =>
            {
                state = store;
                finalizer = new WebSocketTurnFinalizationService(
                    new DemoConversationBroker(interaction),
                    new DefaultSttStrategySelector([new SyntheticBufferedAudioSttStrategy()]),
                    new NullTurnTelemetrySink(),
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<WebSocketTurnFinalizationService>.Instance,
                    command, store);
            });
        using var relayScope = harness.Bind();
        var token = state!.IssueHubToken("Ghost-Instance-Onion-Silk");
        var session = state.OpenSession("neo-hub-listen", null, token, "neo-hub.jibo.com", "/listen");
        var text = JsonSerializer.Serialize(new { type = "TURN", transID = "light-turn",
            data = new { text = transcript } });
        var replies = await finalizer!.HandleTurnAsync(session, new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com", Path = "/listen", Kind = "neo-hub-listen",
            Token = token, Text = text
        }, "TURN");
        Assert.NotEmpty(replies);
        Assert.Equal(1, sends);
    }

    private static (RespondingHaService Service, HomeAssistantPendingClimateStore PendingStore)
        CreateServiceWithRespondingHa(
            string status,
            string? matchedName = null,
            string? heardName = null,
            IReadOnlyList<HomeAssistantCommandCandidate>? candidates = null,
            string? autoRespondAction = null,
            decimal? currentTemperature = null,
            string? unit = null,
            string? message = null,
            Action<JsonElement>? onCommand = null,
            Action<JiboInteractionService, HomeAssistantCommandService, InMemoryCloudStateStore>? inspect = null)
    {
        var snapshotStore = new EncryptedUserDataSnapshotStore(
            Path.Combine(Path.GetTempPath(), $"openjibo-ha-aware-{Guid.NewGuid():N}.json"),
            new UserDataEncryptionService());
        var integrationStore = new InMemoryUserIntegrationStore(snapshotStore);

        var cloudStateStore = new InMemoryCloudStateStore();
        cloudStateStore.UpdateRobot(new DeviceRegistration
        {
            DeviceId = "BOJW-1000-0017-0820-0020",
            RobotId = "Ghost-Instance-Onion-Silk",
            FriendlyName = "Test Robot"
        });

        var registry = new HomeAssistantConnectionRegistry();
        var socket = new CapturingWebSocket(
            registry,
            status,
            matchedName,
            heardName,
            candidates,
            autoRespondAction,
            currentTemperature,
            unit, message, onCommand);
        registry.RegisterPairedConnection("ha-instance-1", socket);

        var pendingStore = new HomeAssistantPendingClimateStore();
        var relay = new HomeAssistantRobotRelay();
        var commandService = new HomeAssistantCommandService(cloudStateStore, relay);
        var service = new JiboInteractionService(
            new JiboExperienceContentCache(new InMemoryJiboExperienceContentRepository()),
            new FirstItemRandomizer(),
            new InMemoryPersonalMemoryStore(),
            cloudStateStore: cloudStateStore,
            homeAssistantCommandService: commandService,
            homeAssistantPendingClimateStore: pendingStore);

        inspect?.Invoke(service, commandService, cloudStateStore);
        return (new RespondingHaService(service, relay, socket), pendingStore);
    }

    private sealed class RespondingHaService(
        JiboInteractionService service, HomeAssistantRobotRelay relay, CapturingWebSocket socket)
    {
        public IDisposable Bind() => AmbientTurnProgressPublisher.Begin(async (reply, cancellationToken) =>
        {
            using var doc = JsonDocument.Parse(reply.Text!);
            Assert.Equal("HA_COMMAND", doc.RootElement.GetProperty("type").GetString());
            var action = doc.RootElement.GetProperty("data");
            var bytes = JsonSerializer.SerializeToUtf8Bytes(action);
            await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken);
            if (socket.LastResult is not null && action.TryGetProperty("callbackToken", out var token))
                relay.TryComplete(token.GetString()!, HomeAssistantCommandResult.FromJson(socket.LastResult.Value));
        });

        public async Task<JiboInteractionDecision> BuildDecisionAsync(TurnContext turn)
        {
            using var scope = Bind();
            return await service.BuildDecisionAsync(turn);
        }
    }

    private sealed class CapturingWebSocket : WebSocket
    {
        public JsonElement? LastResult { get; private set; }
        private readonly HomeAssistantConnectionRegistry _registry;
        private readonly string _status;
        private readonly string? _matchedName;
        private readonly string? _heardName;
        private readonly IReadOnlyList<HomeAssistantCommandCandidate>? _candidates;
        private readonly string? _autoRespondAction;
        private readonly decimal? _currentTemperature;
        private readonly string? _unit;
        private readonly string? _message;
        private readonly Action<JsonElement>? _onCommand;

        public CapturingWebSocket(
            HomeAssistantConnectionRegistry registry,
            string status = "ok",
            string? matchedName = "Zane's Lamp",
            string? heardName = null,
            IReadOnlyList<HomeAssistantCommandCandidate>? candidates = null,
            string? autoRespondAction = null,
            decimal? currentTemperature = null,
            string? unit = null, string? message = null, Action<JsonElement>? onCommand = null)
        {
            _registry = registry;
            _status = status;
            _matchedName = matchedName;
            _heardName = heardName;
            _candidates = candidates;
            _autoRespondAction = autoRespondAction;
            _currentTemperature = currentTemperature;
            _unit = unit;
            _message = message;
            _onCommand = onCommand;
        }

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;

        public override void Abort()
        {
        }

        public override Task CloseAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public override Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public override void Dispose()
        {
        }

        public override Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken)
        {
            using var document = JsonDocument.Parse(buffer.Array!.AsMemory(buffer.Offset, buffer.Count));
            var root = document.RootElement;
            _onCommand?.Invoke(root);
            if (_status == "silent") return Task.CompletedTask;
            if (!root.TryGetProperty("requestId", out var requestIdElement))
                return Task.CompletedTask;

            var requestId = requestIdElement.GetString();
            if (string.IsNullOrWhiteSpace(requestId)) return Task.CompletedTask;

            var command = root.TryGetProperty("command", out var commandElement)
                ? commandElement.GetString()
                : null;
            if (!string.IsNullOrWhiteSpace(_autoRespondAction) &&
                !string.Equals(command, _autoRespondAction, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(command, "lights_off_named", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(command, "lights_on_named", StringComparison.OrdinalIgnoreCase) &&
                command is not null &&
                !command.StartsWith("climate_", StringComparison.OrdinalIgnoreCase))
                return Task.CompletedTask;

            var payload = new Dictionary<string, object?>
            {
                ["type"] = "command_result",
                ["requestId"] = requestId,
                ["status"] = _status
            };
            if (_message is not null) payload["message"] = _message;
            if (!string.IsNullOrWhiteSpace(_matchedName))
                payload["matchedName"] = _matchedName;
            if (!string.IsNullOrWhiteSpace(_heardName))
                payload["heardName"] = _heardName;
            if (_candidates is { Count: > 0 })
                payload["candidates"] = _candidates
                    .Select(candidate => new { entityId = candidate.EntityId, name = candidate.Name })
                    .ToArray();
            if (_currentTemperature is not null)
                payload["currentTemperature"] = _currentTemperature.Value;
            if (!string.IsNullOrWhiteSpace(_unit))
                payload["unit"] = _unit;

            var json = JsonSerializer.Serialize(payload);
            using var resultDoc = JsonDocument.Parse(json);
            LastResult = resultDoc.RootElement.Clone();
            _registry.TryCompleteCommandResult(LastResult.Value);
            return Task.CompletedTask;
        }
    }

    private sealed class FirstItemRandomizer : IJiboRandomizer
    {
        public T Choose<T>(IReadOnlyList<T> items) => items[0];
        public int Next(int minInclusive, int maxExclusive) => minInclusive;
    }
}
