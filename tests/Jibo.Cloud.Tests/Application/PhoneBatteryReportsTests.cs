using Jibo.Cloud.Application.Services;

namespace Jibo.Cloud.Tests.Application;

public sealed class PhoneBatteryReportsTests
{
    // The store is shared by every test, so each one uses its own robot.
    private readonly string Robot = "robot:" + Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    private string? Line(string? user, int minutesAgo, int level, bool charging = false, string name = "Zane Vickers")
    {
        PhoneBatteryReports.Save(Robot, new PhoneBatteryReports.Reading("z1", name, level, charging, Now.AddMinutes(-minutesAgo)));
        return PhoneBatteryReports.ReportLine(Robot, user, Now);
    }

    [Fact]
    public void FreshReading_SaysItsLevel() => Assert.Equal("Your phone is at 73 percent.", Line("Zane", 1, 73));

    [Fact]
    public void OlderReading_SaysAsOf() => Assert.Equal("As of 23 minutes ago, your phone was at 73 percent.", Line("Zane", 23, 73));

    [Fact]
    public void LowAndUnplugged_SaysToCharge() =>
        Assert.Equal("As of 40 minutes ago, your phone was at 15 percent. You better charge it before you go!", Line("zane", 40, 15));

    [Fact]
    public void LowButCharging_NoNagging() => Assert.Equal("Your phone is at 15 percent and charging.", Line("Zane", 0, 15, charging: true));

    [Theory]
    [InlineData(121)] // not heard from in the last two hours
    public void StaleReading_IsLeftOut(int minutesAgo) => Assert.Null(Line("Zane", minutesAgo, 73));

    [Fact]
    public void SomeoneElsesPhone_IsLeftOut() => Assert.Null(Line("Zoe", 5, 73));

    [Fact]
    public void NoPhonePaired_IsLeftOut()
    {
        Assert.Null(PhoneBatteryReports.ReportLine(Robot, "Zane", Now));
        Assert.Null(PhoneBatteryReports.ReportLine(null, "Zane", Now));
    }
}
