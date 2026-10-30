namespace Jibo.Cloud.Domain.Models;

public sealed class MemberCalendarFeedRecord
{
    public string FeedId { get; init; } = Guid.NewGuid().ToString("N");
    public string LoopId { get; init; } = "openjibo-default-loop";
    public string MemberId { get; init; } = string.Empty;
    public string IcalUrl { get; init; } = string.Empty;
    public bool IsEnabled { get; init; } = true;
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastSuccessUtc { get; init; }
    public string? LastError { get; init; }
    /// <summary>The person's name as BEacon shows it, so the report can pick the speaker's feed.</summary>
    public string? MemberName { get; init; }
    /// <summary>The robot's home time zone (IANA id), so "today" and event times are the household's.</summary>
    public string? TimeZoneId { get; init; }

    /// <summary>
    /// Feeds are stored per robot (its friendly id, the deviceId its speech turns carry) in managed
    /// hosting, so one household never sees or changes another's calendars.
    /// </summary>
    public static string RobotScope(string deviceId) => "robot:" + deviceId.Trim();
}
