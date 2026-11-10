namespace Jibo.Cloud.Domain.Models;

/// <summary>
/// A geocoded commute address BEacon saved for a robot: the household's home (<see cref="HomeMemberId"/>),
/// one person's own home (<see cref="HomeOf"/>) or one person's job. Scoped like calendar feeds (<see cref="MemberCalendarFeedRecord.RobotScope"/>).
/// </summary>
public sealed class CommuteAddressRecord
{
    public const string HomeMemberId = "home";

    /// <summary>Key of one person's own home, used for their commute instead of the household's.</summary>
    public static string HomeOf(string memberId) => $"{HomeMemberId}:{memberId}";

    public static bool IsHome(string memberId) =>
        memberId == HomeMemberId || memberId.StartsWith(HomeMemberId + ":", StringComparison.Ordinal);

    public string Scope { get; init; } = string.Empty;
    public string MemberId { get; init; } = string.Empty;
    public string Address { get; init; } = string.Empty;
    public double Latitude { get; init; }
    public double Longitude { get; init; }
    public string? MemberName { get; init; }
    public string? TimeZoneId { get; init; }
    public DateTimeOffset UpdatedUtc { get; init; } = DateTimeOffset.UtcNow;
}
