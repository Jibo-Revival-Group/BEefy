using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace Jibo.Cloud.Application.Services;

/// <summary>
/// Opaque UGC key round-trip. The encrypted key is stored and handed back so a
/// gallery client is not stuck waiting on a key that never arrives.
/// </summary>
public sealed class UgcKeyRelay
{
    private readonly string _path;
    private readonly Lock _gate = new();
    private Store _store;

    public UgcKeyRelay(IConfiguration? configuration)
    {
        var root = RobotDataRoot.Resolve(configuration);
        _path = Path.Combine(root, "ugc-keys.json");
        _store = Load();
    }

    public bool ShouldCreate(string loopId)
    {
        lock (_gate)
        {
            return !_store.Backups.ContainsKey(loopId) &&
                   !_store.Requests.Values.Any(request =>
                       request.LoopId.Equals(loopId, StringComparison.Ordinal) &&
                       !string.IsNullOrEmpty(request.EncryptedKey));
        }
    }

    public object CreateRequest(string loopId, string publicKey)
    {
        lock (_gate)
        {
            var id = Guid.NewGuid().ToString("N");
            _store.Requests[id] = new Request
            {
                Id = id,
                LoopId = loopId,
                PublicKey = publicKey
            };
            Save();
            return View(_store.Requests[id]);
        }
    }

    public object? GetRequest(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        lock (_gate)
        {
            return _store.Requests.TryGetValue(id, out var request) ? View(request) : null;
        }
    }

    public object? Share(string? id, string? encryptedKey)
    {
        if (string.IsNullOrWhiteSpace(id) || encryptedKey is null) return null;
        lock (_gate)
        {
            if (!_store.Requests.TryGetValue(id, out var request))
            {
                request = new Request { Id = id, LoopId = string.Empty };
                _store.Requests[id] = request;
            }

            request.EncryptedKey = encryptedKey;
            Save();
            return View(request);
        }
    }

    public object[] Incoming(string loopId)
    {
        lock (_gate)
        {
            return _store.Requests.Values
                .Where(request => request.LoopId.Equals(loopId, StringComparison.Ordinal) &&
                                  string.IsNullOrEmpty(request.EncryptedKey))
                .Select(View)
                .ToArray();
        }
    }

    public object Backup(string loopId, string encryptedKey)
    {
        lock (_gate)
        {
            _store.Backups[loopId] = encryptedKey;
            Save();
            return new { loopId, encryptedKey };
        }
    }

    public object? Restore(string loopId)
    {
        lock (_gate)
        {
            return _store.Backups.TryGetValue(loopId, out var encryptedKey)
                ? new { loopId, encryptedKey }
                : null;
        }
    }

    private static object View(Request request) => new
    {
        id = request.Id,
        loopId = request.LoopId,
        publicKey = request.PublicKey,
        encryptedKey = string.IsNullOrEmpty(request.EncryptedKey) ? null : request.EncryptedKey
    };

    private Store Load()
    {
        try
        {
            if (!File.Exists(_path)) return new Store();
            return JsonSerializer.Deserialize<Store>(File.ReadAllText(_path)) ?? new Store();
        }
        catch (Exception)
        {
            return new Store();
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".partial";
            File.WriteAllText(temp, JsonSerializer.Serialize(_store));
            File.Move(temp, _path, true);
        }
        catch (Exception)
        {
            // A missing key file must not fail the request that already holds the value in memory.
        }
    }

    private sealed class Store
    {
        public Dictionary<string, Request> Requests { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> Backups { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class Request
    {
        public string Id { get; set; } = string.Empty;
        public string LoopId { get; set; } = string.Empty;
        public string PublicKey { get; set; } = string.Empty;
        public string EncryptedKey { get; set; } = string.Empty;
    }
}
