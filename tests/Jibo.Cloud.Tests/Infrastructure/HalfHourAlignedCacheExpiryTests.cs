using Jibo.Cloud.Infrastructure.Caching;

namespace Jibo.Cloud.Tests.Infrastructure;

public sealed class HalfHourAlignedCacheExpiryTests
{
    [Theory]
    [InlineData(14, 23, 0, 14, 30)]
    [InlineData(14, 34, 0, 15, 0)]
    [InlineData(14, 0, 0, 14, 30)]
    [InlineData(14, 29, 59, 14, 30)]
    [InlineData(14, 30, 0, 15, 0)]
    [InlineData(14, 59, 59, 15, 0)]
    [InlineData(23, 45, 0, 0, 0)] // next day 00:00
    public void GetExpiryUtc_AlignsToNextHalfHour(
        int hour,
        int minute,
        int second,
        int expectedHour,
        int expectedMinute)
    {
        var day = new DateTime(2026, 9, 15, 0, 0, 0, DateTimeKind.Utc);
        var now = new DateTimeOffset(day.Year, day.Month, day.Day, hour, minute, second, TimeSpan.Zero);
        var expiry = HalfHourAlignedCacheExpiry.GetExpiryUtc(now);

        var expectedDay = hour == 23 && expectedHour == 0 ? day.AddDays(1) : day;
        var expected = new DateTimeOffset(
            expectedDay.Year,
            expectedDay.Month,
            expectedDay.Day,
            expectedHour,
            expectedMinute,
            0,
            TimeSpan.Zero);

        Assert.Equal(expected, expiry);
        Assert.True(expiry > now);
    }

    [Fact]
    public void GetExpiryUtc_NormalizesNonUtcOffsets()
    {
        // 14:23 Eastern (-04:00) == 18:23 UTC → expires 18:30 UTC
        var local = new DateTimeOffset(2026, 9, 15, 14, 23, 0, TimeSpan.FromHours(-4));
        var expiry = HalfHourAlignedCacheExpiry.GetExpiryUtc(local);
        Assert.Equal(new DateTimeOffset(2026, 9, 15, 18, 30, 0, TimeSpan.Zero), expiry);
    }
}
