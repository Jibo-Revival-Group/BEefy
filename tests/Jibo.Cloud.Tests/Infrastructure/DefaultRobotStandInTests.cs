using Jibo.Cloud.Infrastructure.Persistence;

namespace Jibo.Cloud.Tests.Infrastructure;

// Live bug: on the managed server every robot that never identified itself was given the default
// robot's identity, so unrelated households shared one device id and inherited each other's
// in-progress dialog state (personal report, household list, chit-chat) across reconnects.
public sealed class DefaultRobotStandInTests
{
    private static InMemoryCloudStateStore Store(bool standsIn) =>
        new(Path.Combine(Path.GetTempPath(), $"default-robot-{Guid.NewGuid():N}.json"))
        {
            DefaultRobotStandsInForUnidentified = standsIn
        };

    [Fact]
    public void Managed_UnidentifiedHubTokenAndUnknownTokens_GetNoDeviceIdentity()
    {
        var store = Store(standsIn: false);

        var hubToken = store.IssueHubToken(deviceId: null, useDefaultRobot: true);
        Assert.Null(store.FindSessionByToken(hubToken)!.DeviceId);

        var session = store.OpenSession("neo-hub-listen", null, "unknown-robot-token", "api.5x1.com", "/v1/listen");
        Assert.Null(session.DeviceId);

        // An identified robot keeps its own identity.
        Assert.Equal("5d8e3cbfd08e", store.FindSessionByToken(store.IssueHubToken("5d8e3cbfd08e"))!.DeviceId);
    }

    [Fact]
    public void Managed_TwoUnidentifiedRobots_DoNotInheritEachOthersDialogState()
    {
        var store = Store(standsIn: false);
        var first = store.OpenSession("neo-hub-listen", null, "robot-a-token", "api.5x1.com", "/v1/listen");
        first.Metadata["personalReportState"] = "awaiting_identity_name";
        first.Metadata["personalReportIdentityName"] = "Zane";
        store.CloseSession(first.SessionId);

        var second = store.OpenSession("neo-hub-listen", null, "robot-b-token", "api.5x1.com", "/v1/listen");

        Assert.False(second.Metadata.ContainsKey("personalReportState"));
        Assert.False(second.Metadata.ContainsKey("personalReportIdentityName"));
    }

    [Fact]
    public void SelfHosted_DefaultRobotStillStandsIn()
    {
        var store = Store(standsIn: true);
        var robot = store.GetRobot().DeviceId;

        Assert.Equal(robot, store.FindSessionByToken(store.IssueHubToken(deviceId: null, useDefaultRobot: true))!.DeviceId);
        Assert.Equal(robot, store.OpenSession("neo-hub-listen", null, "unknown-robot-token", "lan", "/v1/listen").DeviceId);
    }
}
