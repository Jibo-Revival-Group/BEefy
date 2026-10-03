using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Jibo.Cloud.Domain.Models;

namespace Jibo.Cloud.Application.Services;

public static class AwsRequestAccessKey
{
    public static string? Read(ProtocolEnvelope envelope)
    {
        var authorization = Aws3Signature.ReadAuthorization(envelope);
        var credential = ExtractCredential(authorization) ??
                         (envelope.QueryParameters.TryGetValue("X-Amz-Credential", out var queryCredential)
                             ? queryCredential
                             : null);
        if (envelope.Headers.TryGetValue("X-Amz-Credential", out var headerCredential))
            credential ??= headerCredential;

        var accessKeyId = credential?.Split('/', 2)[0] ?? ExtractAws3AccessKeyId(authorization);
        return string.IsNullOrWhiteSpace(accessKeyId) ? null : accessKeyId.Trim();
    }

    public static string Fingerprint(string accessKeyId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(accessKeyId))).ToLowerInvariant()[..16];

    private static string? ExtractCredential(string? authorization)
    {
        if (string.IsNullOrWhiteSpace(authorization)) return null;
        var match = Regex.Match(authorization, @"(?:^|[,\s])Credential=([^,\s]+)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string? ExtractAws3AccessKeyId(string? authorization)
    {
        if (string.IsNullOrWhiteSpace(authorization)) return null;
        var match = Regex.Match(authorization, @"(?:^|[,\s])AWSAccessKeyId=([^,\s]+)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }
}
