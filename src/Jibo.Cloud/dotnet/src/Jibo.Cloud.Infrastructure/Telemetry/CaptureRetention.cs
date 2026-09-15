namespace Jibo.Cloud.Infrastructure.Telemetry;

/// <summary>
/// Best-effort pruning for capture directories so telemetry cannot fill the disk.
/// </summary>
internal static class CaptureRetention
{
    private static readonly object SyncRoot = new();
    private static readonly Dictionary<string, DateTimeOffset> LastPruneUtc = new(StringComparer.OrdinalIgnoreCase);

    internal static void Enforce(string directory, int retentionDays, long maxDirectoryBytes)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return;

        if (retentionDays <= 0 && maxDirectoryBytes <= 0)
            return;

        lock (SyncRoot)
        {
            if (LastPruneUtc.TryGetValue(directory, out var last) &&
                DateTimeOffset.UtcNow - last < TimeSpan.FromMinutes(5))
                return;

            LastPruneUtc[directory] = DateTimeOffset.UtcNow;
        }

        try
        {
            var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Select(path => new FileInfo(path))
                .Where(info => info.Exists)
                .OrderBy(info => info.LastWriteTimeUtc)
                .ToList();

            if (retentionDays > 0)
            {
                var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
                foreach (var file in files.Where(info => info.LastWriteTimeUtc < cutoff).ToList())
                {
                    TryDelete(file);
                    files.Remove(file);
                }
            }

            if (maxDirectoryBytes > 0)
            {
                var totalBytes = files.Sum(info => info.Length);
                foreach (var file in files)
                {
                    if (totalBytes <= maxDirectoryBytes)
                        break;

                    totalBytes -= file.Length;
                    TryDelete(file);
                }
            }
        }
        catch
        {
            // Retention is best-effort; never block capture writes.
        }
    }

    private static void TryDelete(FileInfo file)
    {
        try
        {
            file.Delete();
        }
        catch
        {
            // Ignore locked or already-deleted files.
        }
    }
}
