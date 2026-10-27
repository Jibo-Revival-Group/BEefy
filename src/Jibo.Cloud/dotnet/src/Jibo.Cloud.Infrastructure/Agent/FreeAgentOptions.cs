namespace Jibo.Cloud.Infrastructure.Agent;

/// <summary>Operator-only, never populated from robot headers, sessions, or learned identity mappings.</summary>
public sealed class FreeAgentOptions
{
    public const string Model = "gemma3:4b";
    public bool Enabled { get; set; }
}
