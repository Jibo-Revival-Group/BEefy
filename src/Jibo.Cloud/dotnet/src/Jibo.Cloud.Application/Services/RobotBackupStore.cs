using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace Jibo.Cloud.Application.Services;

/// <summary>
/// Phoenix-shaped robot backup blobs. Backup.New hands back a PUT URL, the PUT
/// stores the bytes, and Backup.List returns a GET URL for the newest blob.
/// A missing directory is an empty store.
/// </summary>
public sealed partial class RobotBackupStore
{
    private const long Magick = 9999999999999;
    private readonly string _root;
    private readonly byte[] _secret;

    public RobotBackupStore(IConfiguration? configuration)
    {
        _root = Path.Combine(RobotDataRoot.Resolve(configuration), "backups");
        try
        {
            Directory.CreateDirectory(_root);
            var secretPath = Path.Combine(_root, ".bearer");
            if (!File.Exists(secretPath))
                File.WriteAllBytes(secretPath, RandomNumberGenerator.GetBytes(32));
            _secret = File.ReadAllBytes(secretPath);
        }
        catch (Exception)
        {
            _secret = RandomNumberGenerator.GetBytes(32);
        }
    }

    public string CreateUploadUrl(string loopId, string publicBase)
    {
        var key = (Magick - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).ToString(CultureInfo.InvariantCulture);
        var expires = DateTimeOffset.UtcNow.AddHours(24).ToUnixTimeMilliseconds();
        return SignedUrl(publicBase, "PUT", loopId, key, expires);
    }

    public object[] List(string loopId, string publicBase)
    {
        var newest = FindNewest(loopId);
        if (newest is null) return [];

        var expires = DateTimeOffset.UtcNow.AddHours(24).ToUnixTimeMilliseconds();
        return
        [
            new
            {
                modified = newest.Modified,
                etag = newest.Etag,
                size = newest.Size,
                location = new
                {
                    expires = expires.ToString(CultureInfo.InvariantCulture),
                    url = SignedUrl(publicBase, "GET", loopId, newest.Key, expires)
                }
            }
        ];
    }

    public BackupBlobResult Store(string? loopId, string? key, string? expiresText, string? signature, byte[] body)
    {
        if (!Authorize("PUT", loopId, key, expiresText, signature, out var expires))
            return BackupBlobResult.Forbidden();
        if (body.Length > 1_000_000_000)
            return BackupBlobResult.TooLarge();

        try
        {
            var dir = LoopDirectory(loopId!);
            Directory.CreateDirectory(dir);
            var etag = $"\"{Convert.ToHexString(MD5.HashData(body)).ToLowerInvariant()}\"";
            var binPath = Path.Combine(dir, key + ".bin");
            var metaPath = Path.Combine(dir, key + ".json");
            var temp = binPath + ".partial";
            File.WriteAllBytes(temp, body);
            File.Move(temp, binPath, true);
            File.WriteAllText(metaPath, JsonSerializer.Serialize(new BackupMeta
            {
                Key = key!,
                Etag = etag,
                Size = body.Length,
                Modified = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)
            }));
            return BackupBlobResult.Stored(etag);
        }
        catch (Exception)
        {
            return BackupBlobResult.StoreFailed();
        }
    }

    public BackupBlobResult Open(string? loopId, string? key, string? expiresText, string? signature)
    {
        if (!Authorize("GET", loopId, key, expiresText, signature, out _))
            return BackupBlobResult.Forbidden();

        try
        {
            var path = Path.Combine(LoopDirectory(loopId!), key + ".bin");
            if (!File.Exists(path)) return BackupBlobResult.Missing();
            return BackupBlobResult.Bytes(File.ReadAllBytes(path));
        }
        catch (Exception)
        {
            return BackupBlobResult.Missing();
        }
    }

    private BackupMeta? FindNewest(string loopId)
    {
        try
        {
            var dir = LoopDirectory(loopId);
            if (!Directory.Exists(dir)) return null;
            BackupMeta? newest = null;
            foreach (var metaPath in Directory.EnumerateFiles(dir, "*.json"))
            {
                var meta = JsonSerializer.Deserialize<BackupMeta>(File.ReadAllText(metaPath));
                if (meta is null || string.IsNullOrWhiteSpace(meta.Key)) continue;
                if (!File.Exists(Path.Combine(dir, meta.Key + ".bin"))) continue;
                if (newest is null || string.CompareOrdinal(meta.Modified, newest.Modified) > 0)
                    newest = meta;
            }

            return newest;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private bool Authorize(string method, string? loopId, string? key, string? expiresText, string? signature,
        out long expires)
    {
        expires = 0;
        if (string.IsNullOrWhiteSpace(loopId) || string.IsNullOrWhiteSpace(key) ||
            string.IsNullOrWhiteSpace(expiresText) || string.IsNullOrWhiteSpace(signature))
            return false;
        if (!long.TryParse(expiresText, NumberStyles.None, CultureInfo.InvariantCulture, out expires))
            return false;
        if (expires <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) return false;
        if (!SafeToken().IsMatch(key)) return false;

        var expected = Sign(method, loopId, key, expires);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var suppliedBytes = Encoding.UTF8.GetBytes(signature);
        return expectedBytes.Length == suppliedBytes.Length &&
               CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
    }

    private string SignedUrl(string publicBase, string method, string loopId, string key, long expires)
    {
        var signature = Sign(method, loopId, key, expires);
        return $"{publicBase.TrimEnd('/')}/backup/blob?loopId={Uri.EscapeDataString(loopId)}" +
               $"&key={Uri.EscapeDataString(key)}&expires={expires.ToString(CultureInfo.InvariantCulture)}" +
               $"&signature={signature}";
    }

    private string Sign(string method, string loopId, string key, long expires)
    {
        var payload = $"beefy-backup-v1\n{method}\n{loopId}\n{key}\n{expires.ToString(CultureInfo.InvariantCulture)}";
        return Convert.ToHexString(HMACSHA256.HashData(_secret, Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    private string LoopDirectory(string loopId)
    {
        var name = SafeToken().IsMatch(loopId)
            ? loopId
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(loopId))).ToLowerInvariant();
        return Path.Combine(_root, name);
    }

    [GeneratedRegex("^[A-Za-z0-9_-]+$")]
    private static partial Regex SafeToken();

    private sealed class BackupMeta
    {
        public string Key { get; set; } = string.Empty;
        public string Etag { get; set; } = string.Empty;
        public long Size { get; set; }
        public string Modified { get; set; } = string.Empty;
    }
}

public sealed class BackupBlobResult
{
    public int StatusCode { get; init; }
    public string? Etag { get; init; }
    public byte[]? Body { get; init; }
    public string Message { get; init; } = string.Empty;

    public static BackupBlobResult Stored(string etag) => new() { StatusCode = 200, Etag = etag };
    public static BackupBlobResult Bytes(byte[] body) => new() { StatusCode = 200, Body = body };
    public static BackupBlobResult Forbidden() => new() { StatusCode = 403, Message = "forbidden" };
    public static BackupBlobResult Missing() => new() { StatusCode = 404, Message = "no such backup" };
    public static BackupBlobResult TooLarge() => new() { StatusCode = 413, Message = "payload too large" };
    public static BackupBlobResult StoreFailed() => new() { StatusCode = 500, Message = "store failed" };
}

internal static class RobotDataRoot
{
    internal static string Resolve(IConfiguration? configuration)
    {
        var persistence = configuration?["OpenJibo:State:PersistencePath"];
        if (!string.IsNullOrWhiteSpace(persistence))
        {
            var directory = Path.GetDirectoryName(persistence);
            if (!string.IsNullOrWhiteSpace(directory))
                return directory;
        }

        return Path.Combine(Path.GetTempPath(), "beefy-robot-data");
    }
}
