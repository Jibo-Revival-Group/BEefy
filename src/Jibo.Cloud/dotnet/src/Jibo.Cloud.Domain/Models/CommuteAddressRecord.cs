namespace Jibo.Cloud.Domain.Models;

/// <summary>
/// A geocoded commute address BEacon saved for a robot: the household's home (<see cref="HomeMemberId"/>)
/// or one person's job. Scoped like calendar feeds (<see cref="MemberCalendarFeedRecord.RobotScope"/>).
/// </summary>
public sealed class CommuteAddressRecord
{
    public const string HomeMemberId = "home";

    public string Scope { get; init; } = string.Empty;
    public string MemberId { get; init; } = string.Empty;
    public string Address { get; init; } = string.Empty;
    public double Latitude { get; init; }
    public double Longitude { get; init; }
    public string? MemberName { get; init; }
    public string? TimeZoneId { get; init; }
    public DateTimeOffset UpdatedUtc { get; init; } = DateTimeOffset.UtcNow;
}
