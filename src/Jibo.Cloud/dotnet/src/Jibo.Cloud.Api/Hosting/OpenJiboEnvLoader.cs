namespace Jibo.Cloud.Api.Hosting;

internal static class OpenJiboEnvLoader
{
    public static void Load(string? startPath = null)
    {
        foreach (var envPath in ResolveEnvFileCandidates(startPath))
        {
            if (!File.Exists(envPath)) continue;

            try
            {
                LoadFile(envPath);
                return;
            }
            catch
            {
                // Ignore unreadable .env files and keep searching other candidates.
            }
        }
    }

    private static IEnumerable<string> ResolveEnvFileCandidates(string? startPath)
    {
        // Repo root is the directory that contains OpenJibo.slnx (this project is no longer nested).
        var repoRoot = FindOpenJiboRepoRoot(startPath ?? Directory.GetCurrentDirectory()) ??
                       FindOpenJiboRepoRoot(AppContext.BaseDirectory);
        if (!string.IsNullOrWhiteSpace(repoRoot))
            yield return Path.Combine(repoRoot, ".env");

        // Also accept a .env next to the current working directory when not running from the tree.
        var cwd = startPath ?? Directory.GetCurrentDirectory();
        if (!string.IsNullOrWhiteSpace(cwd))
        {
            var cwdEnv = Path.Combine(Path.GetFullPath(cwd), ".env");
            if (string.IsNullOrWhiteSpace(repoRoot) ||
                !string.Equals(cwdEnv, Path.Combine(repoRoot, ".env"), StringComparison.OrdinalIgnoreCase))
                yield return cwdEnv;
        }
    }

    private static void LoadFile(string envPath)
    {
        foreach (var line in File.ReadLines(envPath))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;

            var separatorIndex = trimmed.IndexOf('=');
            if (separatorIndex <= 0) continue;

            var key = trimmed[..separatorIndex].Trim();
            if (key.Length == 0) continue;

            var value = trimmed[(separatorIndex + 1)..].Trim();
            if (value.Length >= 2 &&
                ((value.StartsWith('"') && value.EndsWith('"')) ||
                 (value.StartsWith('\'') && value.EndsWith('\''))))
                value = value[1..^1];

            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key)))
                Environment.SetEnvironmentVariable(key, value);
        }
    }

    private static string? FindOpenJiboRepoRoot(string? startPath)
    {
        if (string.IsNullOrWhiteSpace(startPath)) return null;

        var directory = new DirectoryInfo(Path.GetFullPath(startPath));
        if (directory is { Exists: false, Parent: not null }) directory = directory.Parent;

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "OpenJibo.slnx")))
                return directory.FullName;

            directory = directory.Parent;
        }

        return null;
    }
}
