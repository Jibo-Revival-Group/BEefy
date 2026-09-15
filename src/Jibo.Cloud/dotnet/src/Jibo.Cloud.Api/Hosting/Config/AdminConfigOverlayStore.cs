using System.Text.Json;
using System.Text.Json.Nodes;

namespace Jibo.Cloud.Api.Hosting.Config;

/// <summary>
/// Reads and writes the admin config overlay JSON file. Overlay values are loaded
/// last at startup so they win over appsettings without mutating tracked files.
/// </summary>
internal sealed class AdminConfigOverlayStore
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true
    };

    private readonly object _syncRoot = new();
    private readonly string _overlayPath;

    public AdminConfigOverlayStore(IConfiguration configuration, IHostEnvironment environment)
    {
        var configured = configuration["OpenJibo:AdminConfig:OverlayPath"];
        _overlayPath = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(environment.ContentRootPath, "App_Data", "admin-config-overlay.json")
            : Path.IsPathRooted(configured)
                ? configured
                : Path.GetFullPath(configured, environment.ContentRootPath);
    }

    public string OverlayPath => _overlayPath;

    public IReadOnlyDictionary<string, string?> ReadFlatValues()
    {
        lock (_syncRoot)
        {
            if (!File.Exists(_overlayPath))
                return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

            try
            {
                using var stream = File.OpenRead(_overlayPath);
                var root = JsonNode.Parse(stream) as JsonObject;
                if (root is null)
                    return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

                var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                Flatten(root, "", values);
                return values;
            }
            catch
            {
                return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    public void Upsert(IReadOnlyDictionary<string, string?> updates)
    {
        lock (_syncRoot)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_overlayPath)!);
            var root = LoadRootUnlocked();

            foreach (var (key, value) in updates)
            {
                if (string.IsNullOrWhiteSpace(key))
                    continue;

                if (value is null)
                    RemovePath(root, key.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                else
                    SetPath(root, key.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), value);
            }

            var json = root.ToJsonString(WriteOptions);
            var tempPath = _overlayPath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _overlayPath, overwrite: true);
        }
    }

    public void Clear()
    {
        lock (_syncRoot)
        {
            if (File.Exists(_overlayPath))
                File.Delete(_overlayPath);
        }
    }

    private JsonObject LoadRootUnlocked()
    {
        if (!File.Exists(_overlayPath))
            return new JsonObject();

        try
        {
            using var stream = File.OpenRead(_overlayPath);
            return JsonNode.Parse(stream) as JsonObject ?? new JsonObject();
        }
        catch
        {
            return new JsonObject();
        }
    }

    private static void Flatten(JsonObject node, string prefix, IDictionary<string, string?> values)
    {
        foreach (var property in node)
        {
            var key = string.IsNullOrEmpty(prefix) ? property.Key : $"{prefix}:{property.Key}";
            switch (property.Value)
            {
                case JsonObject child:
                    Flatten(child, key, values);
                    break;
                case JsonValue value:
                    values[key] = value.ToString();
                    break;
                case null:
                    values[key] = null;
                    break;
            }
        }
    }

    private static void SetPath(JsonObject root, string[] segments, string value)
    {
        if (segments.Length == 0)
            return;

        var current = root;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (current[segments[i]] is not JsonObject child)
            {
                child = new JsonObject();
                current[segments[i]] = child;
            }

            current = child;
        }

        current[segments[^1]] = JsonValue.Create(value);
    }

    private static void RemovePath(JsonObject root, string[] segments)
    {
        if (segments.Length == 0)
            return;

        var stack = new Stack<(JsonObject Parent, string Key)>();
        var current = root;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (current[segments[i]] is not JsonObject child)
                return;

            stack.Push((current, segments[i]));
            current = child;
        }

        current.Remove(segments[^1]);

        while (stack.Count > 0)
        {
            var (parent, key) = stack.Pop();
            if (parent[key] is JsonObject { Count: 0 })
                parent.Remove(key);
            else
                break;
        }
    }
}
