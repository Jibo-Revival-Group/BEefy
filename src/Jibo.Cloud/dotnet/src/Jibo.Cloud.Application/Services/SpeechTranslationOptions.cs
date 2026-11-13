namespace Jibo.Cloud.Application.Services;

public sealed class SpeechTranslationOptions
{
    public bool Enabled { get; set; } = true;
    public string? Directory { get; set; }
    public string? PythonPath { get; set; }
    public string? WorkerPath { get; set; }
    public int TimeoutMilliseconds { get; set; } = 4000;
}
