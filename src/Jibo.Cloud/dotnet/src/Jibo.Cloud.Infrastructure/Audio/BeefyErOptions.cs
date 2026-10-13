using System.Security.Cryptography;
using System.Text;

namespace Jibo.Cloud.Infrastructure.Audio;

public sealed class BeefyErOptions
{
    public bool Enabled { get; set; }
    public string? SharedSecret { get; set; }
    public int SendQueueCapacity { get; set; } = 128;
    public int MaxConcurrentTurns { get; set; } = 1;
    public TimeSpan FinalTimeout { get; set; } = TimeSpan.FromSeconds(3);
    public TimeSpan HeartbeatTimeout { get; set; } = TimeSpan.FromSeconds(15);

    public void Validate()
    {
        if (Enabled && (string.IsNullOrWhiteSpace(SharedSecret) || SharedSecret.Length < 32))
            throw new ArgumentException("BEefyER requires a separate shared secret of at least 32 characters.");
        if (SendQueueCapacity < 4 || MaxConcurrentTurns < 1 || FinalTimeout <= TimeSpan.Zero ||
            HeartbeatTimeout < TimeSpan.FromSeconds(2))
            throw new ArgumentException("Invalid BEefyER capacity or timeout settings.");
    }

    public bool Authenticate(string? authorization)
    {
        if (!Enabled || string.IsNullOrEmpty(SharedSecret) || authorization is null ||
            !authorization.StartsWith("Bearer ", StringComparison.Ordinal)) return false;
        // Hash both inputs so the comparison length cannot disclose the configured secret length.
        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(SharedSecret)),
            SHA256.HashData(Encoding.UTF8.GetBytes(authorization[7..])));
    }
}
