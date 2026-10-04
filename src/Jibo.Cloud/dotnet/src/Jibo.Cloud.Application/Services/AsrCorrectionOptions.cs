namespace Jibo.Cloud.Application.Services;

public sealed class AsrCorrectionOptions
{
    public bool Enabled { get; set; } = true;
    public string? Directory { get; set; }
    public string? PythonPath { get; set; }
    public string? WorkerPath { get; set; }
    public int TimeoutMilliseconds { get; set; } = 150;
    public double MinimumConfidence { get; set; } = 0.8;
}
