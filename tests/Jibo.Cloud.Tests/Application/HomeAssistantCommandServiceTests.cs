using System.Text.Json;
using Jibo.Cloud.Application.Services;
using Jibo.Cloud.Infrastructure.Persistence;
using Jibo.Runtime.Abstractions;

namespace Jibo.Cloud.Tests.Application;

public sealed class HomeAssistantCommandServiceTests
{
    [Theory]
    [InlineData("turn off the lights", "ha_lights_off", "lights_off_current_room", null)]
    [InlineData("turn on the lights", "ha_lights_on", "lights_on_current_room", null)]
    [InlineData("turn off zanes light", "ha_lights_off", "lights_off_named", "zanes")]
    public async Task Lights_RelayToRequestingRobotWithoutCloudPairingOrLocalMarker(
        string transcript, string intent, string command, string? target)
    {
        var relay = new HomeAssistantRobotRelay();
        var service = new HomeAssistantCommandService(new InMemoryCloudStateStore(), relay);
        var sends = 0;
        using var scope = AmbientTurnProgressPublisher.Begin((reply, _) =>
        {
            sends++;
            using var doc = JsonDocument.Parse(reply.Text!);
            Assert.Equal("HA_COMMAND", doc.RootElement.GetProperty("type").GetString());
            var action = doc.RootElement.GetProperty("data");
            Assert.Equal(command, action.GetProperty("command").GetString());
            if (target is not null) Assert.Equal(target, action.GetProperty("targetName").GetString());
            relay.TryComplete(action.GetProperty("callbackToken").GetString()!,
                new HomeAssistantCommandResult(action.GetProperty("requestId").GetString()!, "ok"));
            return Task.CompletedTask;
        });
        var result = await service.DispatchLightCommandAsync(new TurnContext
        {
            DeviceId = "unregistered-robot", NormalizedTranscript = transcript
        }, intent, true);
        Assert.True(result!.IsOk);
        Assert.Equal(1, sends);
    }

    [Theory]
    [InlineData("it's hot in here", "ha_climate_cool_down", "climate_cool_down_current_room")]
    [InlineData("set the bedroom thermostat to 72", "ha_climate_set_temp", "climate_set_temperature_named")]
    public async Task Climate_UsesRobotPairingWithoutCloudRecord(string transcript, string intent, string command)
    {
        var relay = new HomeAssistantRobotRelay();
        var service = new HomeAssistantCommandService(new InMemoryCloudStateStore(), relay);
        using var scope = AmbientTurnProgressPublisher.Begin((reply, _) =>
        {
            using var doc = JsonDocument.Parse(reply.Text!);
            Assert.Equal("HA_COMMAND", doc.RootElement.GetProperty("type").GetString());
            var action = doc.RootElement.GetProperty("data");
            Assert.Equal(command, action.GetProperty("command").GetString());
            relay.TryComplete(action.GetProperty("callbackToken").GetString()!,
                new HomeAssistantCommandResult(action.GetProperty("requestId").GetString()!, "ok"));
            return Task.CompletedTask;
        });
        Assert.True((await service.DispatchClimateCommandAsync(
            new TurnContext { NormalizedTranscript = transcript }, intent, true))!.IsOk);
    }

    [Fact]
    public async Task MissingRequestingRobotTransport_ReturnsUnreachable()
    {
        var service = new HomeAssistantCommandService(new InMemoryCloudStateStore(), new HomeAssistantRobotRelay());
        Assert.Null(await service.DispatchLightCommandAsync(
            new TurnContext { NormalizedTranscript = "turn off the lights" }, "ha_lights_off", true));
    }
}
