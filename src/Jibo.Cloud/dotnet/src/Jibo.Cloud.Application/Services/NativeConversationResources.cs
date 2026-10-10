using System.IO.Compression;
using System.Text.Json;

namespace Jibo.Cloud.Application.Services;

/// <summary>Version-pinned text data; no executable JavaScript or runtime checkout dependency.</summary>
public sealed class NativeConversationResources
{
    public static NativeConversationResources Instance { get; } = new();
    public string Revision { get; }
    public IReadOnlyDictionary<string, string> Files { get; }

    private NativeConversationResources()
    {
        using var stream = typeof(NativeConversationResources).Assembly
            .GetManifestResourceStream("Jibo.Cloud.Application.native-conversation.json.gz")
            ?? throw new InvalidOperationException("Native conversation resources are missing.");
        using var gzip = new GZipStream(stream, CompressionMode.Decompress);
        using var json = JsonDocument.Parse(gzip);
        Revision = json.RootElement.GetProperty("revision").GetString()!;
        Files = json.RootElement.GetProperty("files").EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal);
    }
}
