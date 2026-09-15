using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace Jibo.Cloud.Application.Services;

/// <summary>
/// Signs and validates capability URLs for loop-member profile photos.
/// The robot fetches photos with bare axios (no auth headers), so the signature lives in the
/// query string while the content-hash path basename remains a stable robot-side cache key.
/// </summary>
public sealed class LoopMemberPhotoUrlSigner
{
    private static readonly TimeSpan DefaultLifetime = TimeSpan.FromHours(24);
    private readonly byte[] _signingKey;
    private readonly TimeProvider _timeProvider;

    public LoopMemberPhotoUrlSigner(IConfiguration configuration)
        : this(configuration, TimeProvider.System)
    {
    }

    public LoopMemberPhotoUrlSigner(IConfiguration configuration, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        var configuredSecret = configuration["OpenJibo:Portal:PhotoSigningKey"]
                               ?? configuration["OpenJibo:Portal:SessionSigningKey"]
                               ?? configuration["OpenJibo:Portal:StatusPassword"]
                               ?? Environment.GetEnvironmentVariable("OPENJIBO_PORTAL_PHOTO_SIGNING_KEY")
                               ?? Environment.GetEnvironmentVariable("OPENJIBO_PORTAL_SESSION_SIGNING_KEY")
                               ?? Environment.GetEnvironmentVariable("OPENJIBO_PORTAL_STATUS_PASSWORD")
                               ?? "openjibo-portal-session-development-fallback";

        _signingKey = SHA256.HashData(Encoding.UTF8.GetBytes(configuredSecret));
        _timeProvider = timeProvider;
    }

    public TimeSpan Lifetime { get; init; } = DefaultLifetime;

    public string BuildPath(string memberId, string contentHash) =>
        $"/media/loop-member-photo/{Uri.EscapeDataString(memberId.Trim())}/{contentHash.Trim().ToLowerInvariant()}.jpg";

    public string BuildSignedUrl(string absoluteOrigin, string memberId, string contentHash)
    {
        var path = BuildPath(memberId, contentHash);
        var expires = _timeProvider.GetUtcNow().Add(Lifetime).ToUnixTimeSeconds();
        var signature = Sign(memberId, contentHash, expires);
        var origin = absoluteOrigin.TrimEnd('/');
        return $"{origin}{path}?expires={expires}&signature={Uri.EscapeDataString(signature)}";
    }

    public bool TryValidate(string memberId, string contentHash, string? expiresRaw, string? signature)
    {
        if (string.IsNullOrWhiteSpace(memberId) ||
            string.IsNullOrWhiteSpace(contentHash) ||
            string.IsNullOrWhiteSpace(expiresRaw) ||
            string.IsNullOrWhiteSpace(signature))
            return false;

        if (!long.TryParse(expiresRaw, out var expiresUnix))
            return false;

        var now = _timeProvider.GetUtcNow().ToUnixTimeSeconds();
        if (expiresUnix < now)
            return false;

        // Reject absurdly far-future expirations (clock skew / forged).
        if (expiresUnix > now + (long)TimeSpan.FromDays(7).TotalSeconds)
            return false;

        var expected = Sign(memberId, contentHash.Trim().ToLowerInvariant(), expiresUnix);
        var provided = Uri.UnescapeDataString(signature.Trim());
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var providedBytes = Encoding.UTF8.GetBytes(provided);
        return CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes);
    }

    private string Sign(string memberId, string contentHash, long expiresUnix)
    {
        var payload = $"loop-member-photo.v1|{memberId.Trim()}|{contentHash.Trim().ToLowerInvariant()}|{expiresUnix}";
        using var hmac = new HMACSHA256(_signingKey);
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
