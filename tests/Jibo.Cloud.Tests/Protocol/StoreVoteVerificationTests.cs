using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Application.Services;
using Jibo.Cloud.Domain.Models;
using Jibo.Cloud.Infrastructure.Persistence;

namespace Jibo.Cloud.Tests.Protocol;

// BEaker asks BEefy before it counts a 5x1 Store star from BEacon: only robots that really talk to 5x1 may star.
public sealed class StoreVoteVerificationTests
{
    [Fact]
    public async Task RobotWithTurnsOverTime_IsVerified()
    {
        var history = new History();
        for (var day = 1; day <= 6; day++) history.Add("Real-Robot", DateTimeOffset.UtcNow.AddDays(-day));
        var result = await Service(history).DispatchAsync(Vote("Real-Robot"));
        Assert.Equal(200, result.StatusCode);
        Assert.Contains("\"verified\":true", result.BodyText);
        Assert.Contains("Real-Robot", result.BodyText);
    }

    [Theory]
    [InlineData("Made-Up-Robot")]   // no turns at all
    [InlineData("Brand-New-Robot")] // plenty of turns, but all in the last day
    [InlineData("Quiet-Robot")]     // old, but only two turns
    [InlineData("")]                // no robot named
    public async Task OtherRobots_AreRefused(string robot)
    {
        var history = new History();
        for (var i = 0; i < 20; i++) history.Add("Brand-New-Robot", DateTimeOffset.UtcNow.AddHours(-i));
        history.Add("Quiet-Robot", DateTimeOffset.UtcNow.AddDays(-10));
        history.Add("Quiet-Robot", DateTimeOffset.UtcNow.AddDays(-9));
        var result = await Service(history).DispatchAsync(Vote(robot));
        Assert.Equal(403, result.StatusCode);
        Assert.Contains("Unable to star", result.BodyText);
    }

    [Fact]
    public async Task WithoutHistory_NobodyIsVerified()
    {
        var result = await new JiboCloudProtocolService(new InMemoryCloudStateStore()).DispatchAsync(Vote("Real-Robot"));
        Assert.Equal(403, result.StatusCode);
    }

    private static JiboCloudProtocolService Service(History history) =>
        new(new InMemoryCloudStateStore(), conversationHistory: history);

    private static ProtocolEnvelope Vote(string robot) => new()
    {
        Method = "POST",
        HostName = "api.jibo.com",
        ServicePrefix = "Loop_20160324",
        Operation = "StoreVote",
        DeviceId = robot,
        BodyText = "{\"repo\":\"zane/astra\",\"starred\":true}"
    };

    private sealed class History : IConversationHistory
    {
        private readonly List<ConversationHistoryRecord> _records = [];

        public void Add(string robot, DateTimeOffset at) =>
            _records.Add(new ConversationHistoryRecord(Guid.NewGuid().ToString(), at, robot, "s", "chat", "chat", null, null, null));

        public void Record(ConversationHistoryRecord record) => _records.Add(record);

        public ConversationHistoryRecord? Latest(string robotId, string? skillId = null) =>
            _records.Where(r => r.RobotId == robotId).MaxBy(r => r.Timestamp);

        public int Count(string robotId, string? skillId = null, DateTimeOffset? since = null) =>
            _records.Count(r => r.RobotId == robotId && (since is null || r.Timestamp >= since));
    }
}
