using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Jibo.Cloud.Domain.Models;

namespace Jibo.Cloud.Application.Services;

/// <summary>
/// AWS signature version 3 as implemented by <c>@jibo/jibo-server-client</c> Signers.V3.
/// The string to sign is SHA-256'd, then that digest is HMAC-SHA256'd with the secret.
/// </summary>
public static class Aws3Signature
{
    public static string BuildAuthorization(
        string accessKeyId,
        string secretAccessKey,
        string method,
        IReadOnlyDictionary<string, string> headers,
        string body)
    {
        var signed = SignedHeaderLines(headers);
        var signedNames = string.Join(';', signed.Select(static line => line.Split(':', 2)[0]));
        var signature = Compute(secretAccessKey, method, signed, body);
        return "AWS3 " +
               "AWSAccessKeyId=" + accessKeyId + "," +
               "Algorithm=HmacSHA256," +
               "SignedHeaders=" + signedNames + "," +
               "Signature=" + signature;
    }

    public static bool Verify(ProtocolEnvelope envelope, string secretAccessKey)
    {
        var authorization = ReadAuthorization(envelope);
        var presented = ExtractParameter(authorization, "Signature");
        if (string.IsNullOrWhiteSpace(presented) || string.IsNullOrWhiteSpace(secretAccessKey))
            return false;

        var expected = Compute(secretAccessKey, envelope.Method, SignedHeaderLines(envelope.Headers), envelope.BodyText ?? string.Empty);
        var presentedBytes = Encoding.UTF8.GetBytes(presented);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        return presentedBytes.Length == expectedBytes.Length &&
               CryptographicOperations.FixedTimeEquals(presentedBytes, expectedBytes);
    }

    public static bool HasPresentedCredential(ProtocolEnvelope envelope)
    {
        var authorization = ReadAuthorization(envelope);
        return !string.IsNullOrWhiteSpace(ExtractParameter(authorization, "AWSAccessKeyId")) &&
               !string.IsNullOrWhiteSpace(ExtractParameter(authorization, "Signature"));
    }

    public static string? ReadAuthorization(ProtocolEnvelope envelope)
    {
        envelope.Headers.TryGetValue("Authorization", out var authorization);
        if (!string.IsNullOrWhiteSpace(authorization) &&
            authorization.Contains("AWSAccessKeyId=", StringComparison.OrdinalIgnoreCase))
            return authorization;

        if (envelope.Headers.TryGetValue("X-Amzn-Authorization", out var amzn) &&
            !string.IsNullOrWhiteSpace(amzn))
            return amzn;

        return authorization;
    }

    private static string Compute(
        string secretAccessKey,
        string method,
        IReadOnlyList<string> signedHeaderLines,
        string body)
    {
        var canonical = signedHeaderLines.Count == 0
            ? "\n"
            : string.Join('\n', signedHeaderLines) + "\n";
        var stringToSign = string.Join('\n', new[]
        {
            string.IsNullOrWhiteSpace(method) ? "POST" : method.ToUpperInvariant(),
            "/",
            "",
            canonical,
            body ?? string.Empty
        });
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(stringToSign));
        var hmac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secretAccessKey), digest);
        return Convert.ToBase64String(hmac);
    }

    private static IReadOnlyList<string> SignedHeaderLines(IEnumerable<KeyValuePair<string, string>> headers)
    {
        var lines = new List<string>();
        foreach (var header in headers)
        {
            if (!IsSignedHeader(header.Key)) continue;
            lines.Add(header.Key.Trim().ToLowerInvariant() + ":" + header.Value.Trim());
        }

        lines.Sort(StringComparer.Ordinal);
        return lines;
    }

    private static bool IsSignedHeader(string name)
    {
        if (name.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Content-Encoding", StringComparison.OrdinalIgnoreCase))
            return true;

        // The V3 signer adds X-Amzn-Authorization after computing the signature.
        return name.StartsWith("X-Amz", StringComparison.OrdinalIgnoreCase) &&
               !name.Equals("X-Amzn-Authorization", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ExtractParameter(string? authorization, string parameter)
    {
        if (string.IsNullOrWhiteSpace(authorization)) return null;
        var match = Regex.Match(authorization, $@"(?:^|[,\s]){Regex.Escape(parameter)}=([^,\s]+)",
            RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }
}
