namespace Jibo.Cloud.Domain.Models;

public sealed class OobeSetupRecord
{
    public string Token { get; init; } = string.Empty;
    public string? UserId { get; init; }
    public string? DeviceId { get; init; }
    public string? LoopId { get; init; }
    public string TargetMode { get; init; } = "open-jibo";
    public string? TargetHost { get; init; }
    public string? RollbackSnapshotId { get; init; }
    public bool Complete { get; init; }
    public DateTimeOffset ExpiresUtc { get; init; }
}
