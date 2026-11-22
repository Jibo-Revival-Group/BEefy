namespace Jibo.Cloud.Infrastructure.Agent;

/// <summary>Operator-only, never populated from robot headers, sessions, or learned identity mappings.</summary>
public sealed class FreeAgentOptions
{
    public const string DefaultModel = "claude-sonnet-5-5";
    public bool Enabled { get; set; }
    // Read on every request, so the operator can switch Free's model (one line, e.g. claude-sonnet-5-5) or rotate
    // the key by editing these files: no restart, no deploy.
    public string ModelFile { get; set; } = "/var/lib/5x1/free-agent-model";
    public string KeyFile { get; set; } = "/var/lib/5x1/anthropic-key";

    public string CurrentModel()
    {
        try
        {
            var model = File.ReadAllText(ModelFile).Trim();
            if (System.Text.RegularExpressions.Regex.IsMatch(model, @"\A[A-Za-z0-9._:-]{1,100}\z")) return model;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return DefaultModel;
    }

    public string? ApiKey()
    {
        try { var key = File.ReadAllText(KeyFile).Trim(); return key.Length > 0 ? key : null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
