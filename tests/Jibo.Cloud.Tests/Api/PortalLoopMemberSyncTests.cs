using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Domain.Models;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Jibo.Cloud.Tests.Api;

/// <summary>
/// BEacon owns household CRUD. BEefy still mirrors robot-reported people via
/// SyncPeopleFromLoopUsers for calendars, greetings, and the identity graph.
/// </summary>
public sealed class PortalLoopMemberSyncTests
{
    [Fact]
    public void RobotRosterSync_UpsertsPeopleAndLoopMembersFromLoopUsers()
    {
        using var factory = CreateFactory();
        var store = factory.Services.GetRequiredService<ICloudStateStore>();

        var loop = store.AddLoop(null, null, "Ghost-Instance-Onion-Silk", "BOJW-1000-0017-0820-0020");
        var count = store.SyncPeopleFromLoopUsers(
            loop.LoopId,
            "Ghost-Instance-Onion-Silk",
            [
                new LoopUserSnapshot("looper-zane", "Zane", "Tester", Type: "owner"),
                new LoopUserSnapshot("looper-jon", "Jon", "Tester", Type: "member")
            ]);

        Assert.Equal(2, count);
        Assert.Contains(
            store.GetLoopMembers(loop.LoopId),
            member => member.Id.Equals("looper-zane", StringComparison.OrdinalIgnoreCase) &&
                      member.FirstName == "Zane");
        Assert.Contains(
            store.GetPeople(loop.LoopId),
            person => person.PersonId.Equals("looper-jon", StringComparison.OrdinalIgnoreCase) &&
                      person.DisplayName.Contains("Jon", StringComparison.Ordinal));
    }

    [Fact]
    public void RobotRosterSync_UpdatesNamesWhenRobotReportsRename()
    {
        using var factory = CreateFactory();
        var store = factory.Services.GetRequiredService<ICloudStateStore>();

        var loop = store.AddLoop(null, null, "Ghost-Instance-Onion-Silk", "BOJW-1000-0017-0820-0020");
        store.SyncPeopleFromLoopUsers(
            loop.LoopId,
            "Ghost-Instance-Onion-Silk",
            [new LoopUserSnapshot("looper-zane", "Zane", "Tester", Type: "owner")]);

        store.SyncPeopleFromLoopUsers(
            loop.LoopId,
            "Ghost-Instance-Onion-Silk",
            [new LoopUserSnapshot("looper-zane", "Alexander", "Tester", Type: "owner")]);

        var member = store.GetLoopMembers(loop.LoopId)
            .First(item => item.Id.Equals("looper-zane", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("Alexander", member.FirstName);
    }

    [Fact]
    public void RobotRosterSync_DropsPeopleAbsentFromLatestRoster()
    {
        using var factory = CreateFactory();
        var store = factory.Services.GetRequiredService<ICloudStateStore>();

        var loop = store.AddLoop(null, null, "Ghost-Instance-Onion-Silk", "BOJW-1000-0017-0820-0020");
        store.SyncPeopleFromLoopUsers(
            loop.LoopId,
            "Ghost-Instance-Onion-Silk",
            [
                new LoopUserSnapshot("looper-zane", "Zane", "Tester", Type: "owner"),
                new LoopUserSnapshot("looper-jon", "Jon", "Tester", Type: "member")
            ]);

        store.SyncPeopleFromLoopUsers(
            loop.LoopId,
            "Ghost-Instance-Onion-Silk",
            [new LoopUserSnapshot("looper-zane", "Zane", "Tester", Type: "owner")]);

        Assert.DoesNotContain(
            store.GetPeople(),
            person => person.PersonId.Equals("looper-jon", StringComparison.OrdinalIgnoreCase) &&
                      person.RobotId.Equals("Ghost-Instance-Onion-Silk", StringComparison.OrdinalIgnoreCase));
    }

    private static WebApplicationFactory<Program> CreateFactory()
    {
        var root = Path.Combine(Path.GetTempPath(), $"openjibo-portal-loop-sync-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("OpenJibo:Telemetry:DirectoryPath", Path.Combine(root, "websocket"));
                builder.UseSetting("OpenJibo:ProtocolTelemetry:DirectoryPath", Path.Combine(root, "http"));
                builder.UseSetting("OpenJibo:TurnTelemetry:DirectoryPath", Path.Combine(root, "turn"));
                builder.UseSetting("OpenJibo:Logging:DirectoryPath", Path.Combine(root, "logs"));
                builder.UseSetting(
                    "OpenJibo:UserIntegrations:PersistencePath",
                    Path.Combine(root, "user-integrations.json"));
                builder.UseSetting("OpenJibo:State:Backend", "File");
                builder.UseSetting("OpenJibo:PersonalMemory:Backend", "File");
                builder.UseSetting("OpenJibo:State:PersistencePath", Path.Combine(root, "cloud-state.json"));
                builder.UseSetting(
                    "OpenJibo:PersonalMemory:PersistencePath",
                    Path.Combine(root, "personal-memory.json"));
                builder.UseSetting("OpenJibo:Stt:EnableLocalWhisperCpp", "false");
                builder.UseSetting("OpenJibo:Stt:EnableWhisperServer", "false");
            });
    }
}
