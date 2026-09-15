using Microsoft.Extensions.Logging;

namespace Jibo.Cloud.Infrastructure.Audio;

/// <summary>
/// Locates or downloads the streaming Zipformer model used by sherpa-onnx.
/// </summary>
public sealed class SherpaModelLocator(ILogger<SherpaModelLocator> logger)
{
    internal const string DefaultModelFolderName = "sherpa-onnx-streaming-zipformer-en-2023-06-26";

    private const string ModelArchiveUrl =
        "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-streaming-zipformer-en-2023-06-26.tar.bz2";

    private static readonly object DownloadSync = new();

    public sealed record ModelPaths(
        string Directory,
        string Encoder,
        string Decoder,
        string Joiner,
        string Tokens,
        string? Hotwords);

    public ModelPaths? Resolve(BufferedAudioSttOptions options)
    {
        foreach (var candidate in EnumerateCandidateDirectories(options))
        {
            var resolved = TryResolve(candidate);
            if (resolved is not null)
                return resolved;
        }

        if (!options.AutoDownloadSherpaModel)
            return null;

        var downloadRoot = ResolveDefaultModelRoot();
        Directory.CreateDirectory(downloadRoot);
        var targetDirectory = Path.Combine(downloadRoot, DefaultModelFolderName);

        lock (DownloadSync)
        {
            var existing = TryResolve(targetDirectory);
            if (existing is not null)
                return existing;

            try
            {
                logger.LogInformation("Downloading Sherpa streaming Zipformer model to {Directory}", targetDirectory);
                DownloadAndExtract(targetDirectory);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to download Sherpa streaming model.");
                return null;
            }

            return TryResolve(targetDirectory);
        }
    }

    private static IEnumerable<string> EnumerateCandidateDirectories(BufferedAudioSttOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.SherpaModelDirectory))
            yield return options.SherpaModelDirectory;

        yield return Path.Combine(ResolveDefaultModelRoot(), DefaultModelFolderName);

        var upstream = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "..",
            "JiboExperiments", "OpenJibo", "artifacts", "streaming-asr", DefaultModelFolderName));
        if (Directory.Exists(upstream))
            yield return upstream;

        var sibling = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "JiboExperiments", "OpenJibo", "artifacts", "streaming-asr", DefaultModelFolderName));
        if (Directory.Exists(sibling))
            yield return sibling;
    }

    private static string ResolveDefaultModelRoot()
    {
        var repoRoot = FindRepoRoot(Directory.GetCurrentDirectory()) ??
                       FindRepoRoot(AppContext.BaseDirectory);
        if (!string.IsNullOrWhiteSpace(repoRoot))
            return Path.Combine(repoRoot, "App_Data", "sherpa-models");

        return Path.Combine(AppContext.BaseDirectory, "App_Data", "sherpa-models");
    }

    private static string? FindRepoRoot(string? startPath)
    {
        if (string.IsNullOrWhiteSpace(startPath)) return null;
        var directory = new DirectoryInfo(Path.GetFullPath(startPath));
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "OpenJibo.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        return null;
    }

    private static ModelPaths? TryResolve(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return null;

        var encoder = FirstExisting(directory,
            "encoder-epoch-99-avg-1-chunk-16-left-128.int8.onnx",
            "encoder-epoch-99-avg-1-chunk-16-left-128.onnx");
        var decoder = FirstExisting(directory,
            "decoder-epoch-99-avg-1-chunk-16-left-128.int8.onnx",
            "decoder-epoch-99-avg-1-chunk-16-left-128.onnx");
        var joiner = FirstExisting(directory,
            "joiner-epoch-99-avg-1-chunk-16-left-128.int8.onnx",
            "joiner-epoch-99-avg-1-chunk-16-left-128.onnx");
        var tokens = Path.Combine(directory, "tokens.txt");
        if (encoder is null || decoder is null || joiner is null || !File.Exists(tokens))
            return null;

        var hotwords = Path.Combine(directory, "openjibo-hotwords.txt");
        // Hotwords path is retained for diagnostics but not applied — BPE models reject
        // word-level phrases like "Jibo" (see SherpaOnlineRecognizerProvider).
        return new ModelPaths(directory, encoder, decoder, joiner, tokens,
            File.Exists(hotwords) ? hotwords : null);
    }

    private static string? FirstExisting(string directory, params string[] names)
    {
        foreach (var name in names)
        {
            var path = Path.Combine(directory, name);
            if (File.Exists(path))
                return path;
        }

        return null;
    }

    private static void DownloadAndExtract(string targetDirectory)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetDirectory)!);
        var archivePath = targetDirectory + ".tar.bz2";
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
        using (var response = http.GetAsync(ModelArchiveUrl).GetAwaiter().GetResult())
        {
            response.EnsureSuccessStatusCode();
            using var archiveStream = File.Create(archivePath);
            response.Content.CopyToAsync(archiveStream).GetAwaiter().GetResult();
        }

        var extractRoot = targetDirectory + ".extract";
        if (Directory.Exists(extractRoot))
            Directory.Delete(extractRoot, true);
        Directory.CreateDirectory(extractRoot);

        // Use system tar which handles .tar.bz2 portably on Linux CI and the VM.
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "tar",
            Arguments = $"-xjf \"{archivePath}\" -C \"{extractRoot}\"",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        using (var process = System.Diagnostics.Process.Start(startInfo) ??
                             throw new InvalidOperationException("Failed to start tar."))
        {
            process.WaitForExit();
            if (process.ExitCode != 0)
                throw new InvalidOperationException(process.StandardError.ReadToEnd());
        }

        var extractedModel = Directory.EnumerateDirectories(extractRoot)
            .FirstOrDefault(path => Path.GetFileName(path)
                .Equals(DefaultModelFolderName, StringComparison.OrdinalIgnoreCase))
            ?? extractRoot;

        if (Directory.Exists(targetDirectory))
            Directory.Delete(targetDirectory, true);
        Directory.Move(extractedModel, targetDirectory);

        var hotwordsPath = Path.Combine(targetDirectory, "openjibo-hotwords.txt");
        if (!File.Exists(hotwordsPath))
            File.WriteAllText(hotwordsPath, "Jibo\nhey Jibo\nhi Jibo\nokay Jibo\n");

        try { File.Delete(archivePath); } catch { /* best-effort */ }
        try { Directory.Delete(extractRoot, true); } catch { /* best-effort */ }
    }
}
