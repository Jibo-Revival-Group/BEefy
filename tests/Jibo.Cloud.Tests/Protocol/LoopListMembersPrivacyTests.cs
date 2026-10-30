using System.Text.Json;
using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Application.Services;
using Jibo.Cloud.Domain.Models;
using Jibo.Cloud.Infrastructure.Persistence;

namespace Jibo.Cloud.Tests.Protocol;

// Loop_*.ListMembers must only return the calling robot's own household.
public sealed class LoopListMembersPrivacyTests
{
    [Fact]
    public async Task ListMembers_DoesNotReturnAnotherRobotsHousehold()
    {
        var (service, victimLoopId) = CreateVictim();

        foreach (var body in new[] { "{}", JsonSerializer.Serialize(new { loopId = victimLoopId }) })
        {
            Assert.DoesNotContain("Privateson", (await service.DispatchAsync(ListMembers(body, "Attacker-Robot-Name-Two"))).BodyText);
            Assert.DoesNotContain("Privateson", (await service.DispatchAsync(ListMembers(body, null))).BodyText);
        }
    }

    [Fact]
    public async Task ListMembers_ReturnsTheCallingRobotsOwnHousehold()
    {
        var (service, victimLoopId) = CreateVictim();

        Assert.Contains("Privateson", (await service.DispatchAsync(ListMembers("{}", "Victim-Robot-Name-One"))).BodyText);
        Assert.Contains("Privateson", (await service.DispatchAsync(
            ListMembers(JsonSerializer.Serialize(new { loopId = victimLoopId }), "Victim-Robot-Name-One"))).BodyText);
    }

    private static (JiboCloudProtocolService, string) CreateVictim()
    {
        var store = new InMemoryCloudStateStore();
        var victimLoop = store.AddLoop(null, store.GetAccount().AccountId, "Victim-Robot-Name-One", "Victim-Robot-Name-One");
        store.SyncPeopleFromLoopUsers(victimLoop.LoopId, "Victim-Robot-Name-One",
            [new LoopUserSnapshot("looper-alice", "Alice", "Privateson", Type: "member")]);
        return (new JiboCloudProtocolService(store), victimLoop.LoopId);
    }

    private static ProtocolEnvelope ListMembers(string body, string? deviceId) => new()
    {
        Method = "POST",
        HostName = "api.5x1.com",
        ServicePrefix = "Loop_20160324",
        Operation = "ListMembers",
        DeviceId = deviceId,
        BodyText = body
    };
}
