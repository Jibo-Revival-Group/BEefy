namespace Jibo.Cloud.Domain.Models;

/// <summary>
/// The news topics one person turned off in BEacon's Personal Reports tab (every topic is on until then).
/// Scoped like calendar feeds (<see cref="MemberCalendarFeedRecord.RobotScope"/>).
/// </summary>
public sealed class NewsTopicsRecord
{
    public string Scope { get; init; } = string.Empty;
    public string MemberId { get; init; } = string.Empty;
    public string? MemberName { get; init; }
    public string[] DisabledTopics { get; init; } = [];
    public DateTimeOffset UpdatedUtc { get; init; } = DateTimeOffset.UtcNow;
}
