using System.Net;
using Jibo.Cloud.Infrastructure.Agent;

namespace Jibo.Cloud.Tests.Infrastructure;

public sealed class FreeAgentRateLimiterTests
{
    private static readonly IPAddress Ip = IPAddress.Parse("192.0.2.1");

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref ticks);
        public void Advance(TimeSpan elapsed) => Interlocked.Add(ref ticks, elapsed.Ticks);
    }

    [Fact]
    public void RollingWindowExpiresAtExactBoundaryAndRoundsRetryUp()
    {
        var clock = new ManualTimeProvider();
        var limiter = new FreeAgentRateLimiter(clock);
        Assert.True(limiter.TryAcquire("a", Ip, out _));
        clock.Advance(TimeSpan.FromMinutes(15));
        for (var i = 0; i < 49; i++) Assert.True(limiter.TryAcquire("a", Ip, out _));
        clock.Advance(TimeSpan.FromMinutes(15) - TimeSpan.FromTicks(1));
        Assert.False(limiter.TryAcquire("a", Ip, out var retry));
        Assert.Equal(1, retry);
        clock.Advance(TimeSpan.FromTicks(1));
        Assert.True(limiter.TryAcquire("a", Ip, out retry));
        Assert.Equal(0, retry);
        Assert.False(limiter.TryAcquire("a", Ip, out retry));
        Assert.Equal(900, retry);
        clock.Advance(TimeSpan.FromMinutes(15));
        for (var i = 0; i < 49; i++) Assert.True(limiter.TryAcquire("a", Ip, out _));
        Assert.False(limiter.TryAcquire("a", Ip, out retry));
        Assert.Equal(900, retry);
    }

    [Fact]
    public void IpQuotaIsTwoHundredAcrossRobotsAndExpiresAtOneHour()
    {
        var clock = new ManualTimeProvider(); var limiter = new FreeAgentRateLimiter(clock);
        for (var i = 0; i < 200; i++) Assert.True(limiter.TryAcquire($"robot-{i}", Ip, out _));
        Assert.False(limiter.TryAcquire("new", IPAddress.Parse("::ffff:192.0.2.1"), out var retry)); Assert.Equal(3600, retry);
        clock.Advance(TimeSpan.FromHours(1) - TimeSpan.FromTicks(1));
        Assert.False(limiter.TryAcquire("new", Ip, out retry)); Assert.Equal(1, retry);
        clock.Advance(TimeSpan.FromTicks(1)); Assert.True(limiter.TryAcquire("new", Ip, out _));
    }

    [Fact]
    public void RobotQuotaIsSharedAcrossIps()
    {
        var limiter = new FreeAgentRateLimiter(new ManualTimeProvider());
        for (var i = 0; i < 50; i++) Assert.True(limiter.TryAcquire("robot", IPAddress.Parse($"192.0.2.{i + 1}"), out _));
        Assert.False(limiter.TryAcquire("robot", IPAddress.Parse("198.51.100.1"), out var retry)); Assert.Equal(1800, retry);
        Assert.Equal((1, 50), limiter.TrackedBucketCounts);
    }

    [Fact]
    public void ConcurrentIpAdmissionsAreBounded()
    {
        var limiter = new FreeAgentRateLimiter(new ManualTimeProvider()); int count = 0;
        Parallel.For(0, 1000, i => { if (limiter.TryAcquire($"robot-{i}", Ip, out _)) Interlocked.Increment(ref count); });
        Assert.Equal(200, count);
    }

    [Fact]
    public void CardinalityFailsClosedUntilBothDimensionsExpire()
    {
        var clock = new ManualTimeProvider(); var limiter = new FreeAgentRateLimiter(clock, 1);
        Assert.True(limiter.TryAcquire("a", Ip, out _));
        Assert.False(limiter.TryAcquire("b", IPAddress.Parse("192.0.2.2"), out var retry)); Assert.Equal(3600, retry);
        clock.Advance(TimeSpan.FromHours(1)); Assert.True(limiter.TryAcquire("b", IPAddress.Parse("192.0.2.2"), out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingRobotIdentityIsRejected(string? robot)
    {
        var limiter = new FreeAgentRateLimiter();
        Assert.ThrowsAny<ArgumentException>(() => limiter.TryAcquire(robot!, Ip, out _));
        Assert.Equal((0, 0), limiter.TrackedBucketCounts);
    }

    [Fact]
    public void MissingTrustedConnectionIpIsRejectedWithoutChargingRobot()
    {
        var limiter = new FreeAgentRateLimiter();
        Assert.Throws<ArgumentNullException>(() => limiter.TryAcquire("a", null!, out _));
        Assert.Equal((0, 0), limiter.TrackedBucketCounts);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonpositiveCardinalityBoundIsRejected(int capacity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FreeAgentRateLimiter(maxBucketsPerDimension: capacity));
    }

    [Fact]
    public void DefaultQuotaAdmitsFiftyThenDeniesFiftyFirst()
    {
        var limiter = new FreeAgentRateLimiter(new ManualTimeProvider());
        for (var i = 0; i < 50; i++)
            Assert.True(limiter.TryAcquire("robot-a", Ip, out _));
        Assert.False(limiter.TryAcquire("robot-a", Ip, out var retry));
        Assert.Equal(1800, retry);
    }

    [Fact]
    public void FirstRequestIsAdmittedWithoutRetryDelay()
    {
        var limiter = new FreeAgentRateLimiter();
        Assert.True(limiter.TryAcquire("robot-a", Ip, out var retry));
        Assert.Equal(0, retry);
    }
}
