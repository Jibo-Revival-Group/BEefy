using System.Diagnostics;

namespace Jibo.Cloud.Api.Hosting;

/// <summary>
/// Starts the vendored Phoenix parser, skills, history, and data services on
/// localhost. Failure leaves the API listening; turns fall back to the current dispatcher.
/// </summary>
internal sealed class PhoenixConversationHost(ILogger<PhoenixConversationHost> logger) : BackgroundService
{
    private readonly List<Process> _processes = new();

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _ = Task.Run(() => StartAll(stoppingToken), stoppingToken);
        return Task.CompletedTask;
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var process in _processes)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (Exception exception)
            {
                logger.LogDebug(exception, "Phoenix helper already stopped");
            }
        }

        return base.StopAsync(cancellationToken);
    }

    private void StartAll(CancellationToken stoppingToken)
    {
        var root = FindConversationRoot();
        if (root is null)
        {
            logger.LogWarning("Phoenix conversation packages were not found; Hey Jibo uses the current dispatcher");
            return;
        }

        if (!File.Exists(Path.Combine(root, "node_modules", "@phoenix", "common", "package.json")))
        {
            logger.LogWarning(
                "Phoenix packages are present but not installed. From {Root} run npm install. Hey Jibo uses the current dispatcher until then.",
                root);
            return;
        }

        var node = FindNode();
        if (node is null)
        {
            logger.LogWarning("node was not found on PATH; Hey Jibo uses the current dispatcher");
            return;
        }

        Start(node, root, "packages/nlu/src/index.js", "24701", stoppingToken);
        Start(node, root, "packages/skills/src/index.js", "24702", stoppingToken);
        Start(node, root, "packages/history/src/index.js", "24703", stoppingToken);
        Start(node, root, "packages/data/src/index.js", "24704", stoppingToken);
    }

    private void Start(string node, string root, string script, string port, CancellationToken stoppingToken)
    {
        if (stoppingToken.IsCancellationRequested)
            return;
        if (!File.Exists(Path.Combine(root, script)))
        {
            logger.LogWarning("Missing Phoenix script {Script}", script);
            return;
        }

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = node,
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };
        process.StartInfo.ArgumentList.Add(script);
        process.StartInfo.Environment["PORT"] = port;
        process.StartInfo.Environment["ETCO_server_port"] = port;
        process.StartInfo.Environment["PHOENIX_BIND_HOST"] = "127.0.0.1";
        process.StartInfo.Environment["ETCO_parser_layaEnabled"] = "false";
        process.StartInfo.Environment["ETCO_parser_llmEnabled"] = "false";
        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (!string.IsNullOrWhiteSpace(eventArgs.Data))
                logger.LogDebug("phoenix {Port}: {Line}", port, eventArgs.Data);
        };
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (!string.IsNullOrWhiteSpace(eventArgs.Data))
                logger.LogInformation("phoenix {Port}: {Line}", port, eventArgs.Data);
        };

        try
        {
            if (!process.Start())
                return;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _processes.Add(process);
            logger.LogInformation("Phoenix {Script} listening on 127.0.0.1:{Port}", script, port);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not start Phoenix {Script}", script);
        }
    }

    private static string? FindNode()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, OperatingSystem.IsWindows() ? "node.exe" : "node");
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    internal static string? FindConversationRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(Path.GetFullPath(start));
            while (directory is not null)
            {
                var conversation = Path.Combine(directory.FullName, "conversation");
                if (File.Exists(Path.Combine(conversation, "package.json")))
                    return conversation;
                directory = directory.Parent;
            }
        }

        return null;
    }
}
