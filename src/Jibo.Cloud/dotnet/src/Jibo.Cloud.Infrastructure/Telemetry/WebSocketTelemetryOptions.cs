namespace Jibo.Cloud.Infrastructure.Telemetry;

public sealed class WebSocketTelemetryOptions
{
    public bool Enabled { get; set; } = true;
    public bool ExportFixtures { get; set; } = true;
    public string DirectoryPath { get; set; } = "captures/websocket";
    /// <summary>Delete capture files older than this many days. 0 disables age pruning.</summary>
    public int RetentionDays { get; set; } = 7;
    /// <summary>Soft cap on capture directory size in bytes. 0 disables size pruning.</summary>
    public long MaxDirectoryBytes { get; set; } = 512L * 1024 * 1024;
}