using System.Collections.Concurrent;
using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Domain.Models;

namespace Jibo.Cloud.Infrastructure.Persistence;

/// <summary>
/// Separates compact, restart-safe authentication records from bounded live dialog sessions.
/// Active sessions are never part of the durable cloud-state snapshot.
/// </summary>
internal sealed class BoundedCloudSessionRegistry(int maximumActiveSessions = 256, int maximumDurableTokens = 256,
    ITransportMetrics? transportMetrics = null)
{
    private readonly Lock _mutationGate = new();
    private readonly ConcurrentDictionary<string, CloudSession> _active = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CloudSession> _durableTokens = new(StringComparer.OrdinalIgnoreCase);
    // Robots open a new listen socket per turn; keep the last closed session briefly so a
    // cloud-owned follow-up (personal report yes/no, name) can inherit its dialog state.
    private readonly ConcurrentDictionary<string, (CloudSession Session, DateTimeOffset ClosedUtc)> _recentlyClosed =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan RecentlyClosedLifetime = TimeSpan.FromMinutes(2);
    private readonly int _maximumActiveSessions = Math.Max(1, maximumActiveSessions);
    private readonly int _maximumDurableTokens = Math.Max(1, maximumDurableTokens);
    private readonly ITransportMetrics _transportMetrics = transportMetrics ?? NullTransportMetrics.Instance;

    public IReadOnlyCollection<CloudSession> Values
    {
        get
        {
            lock (_mutationGate)
            {
                RemoveExpiredDurableTokensCore();
                return SnapshotValues();
            }
        }
    }

    public IReadOnlyCollection<string> Keys
    {
        get
        {
            lock (_mutationGate)
            {
                RemoveExpiredDurableTokensCore();
                return _active.Keys.Concat(_durableTokens.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            }
        }
    }

    public IReadOnlyCollection<CloudSession> DurableTokenValues
    {
        get
        {
            lock (_mutationGate)
            {
                RemoveExpiredDurableTokensCore();
                return _durableTokens.Values.ToArray();
            }
        }
    }

    public TResult ExecuteExclusive<TResult>(Func<IReadOnlyCollection<CloudSession>, TResult> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_mutationGate)
        {
            RemoveExpiredDurableTokensCore();
            return action(SnapshotValues());
        }
    }

    private IReadOnlyCollection<CloudSession> SnapshotValues()
    {
        var active = _active.ToArray();
        var activeKeys = active.Select(pair => pair.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return active.Select(pair => pair.Value)
            .Concat(_durableTokens.Where(pair => !activeKeys.Contains(pair.Key)).Select(pair => pair.Value))
            .ToArray();
    }

    public void RegisterDurableToken(string token, CloudSession session)
    {
        lock (_mutationGate)
        {
            _durableTokens[token] = session;
            var overflow = _durableTokens.Count - _maximumDurableTokens;
            if (overflow <= 0) return;
            foreach (var staleToken in _durableTokens
                         .OrderBy(pair => pair.Value.CreatedUtc)
                         .Take(overflow)
                         .Select(pair => pair.Key)
                         .ToArray())
                _durableTokens.TryRemove(staleToken, out _);
        }
    }

    public void RegisterActive(string token, CloudSession session)
    {
        lock (_mutationGate)
        {
            if (_active.TryAdd(token, session))
                _transportMetrics.ActiveSessionsChanged(1);
            else
                _active[token] = session;
            var overflow = _active.Count - _maximumActiveSessions;
            if (overflow <= 0) return;

            foreach (var staleToken in _active
                         .OrderBy(pair => pair.Value.LastSeenUtc)
                         .ThenBy(pair => pair.Value.CreatedUtc)
                         .Take(overflow)
                         .Select(pair => pair.Key)
                         .ToArray())
                if (_active.TryRemove(staleToken, out _)) _transportMetrics.ActiveSessionsChanged(-1);
        }
    }

    public CloudSession? Find(string token)
    {
        lock (_mutationGate)
            return _active.GetValueOrDefault(token) ?? FindDurableCore(token);
    }

    public CloudSession? FindActive(string token)
    {
        lock (_mutationGate)
            return _active.GetValueOrDefault(token);
    }

    public CloudSession? FindDurable(string token)
    {
        lock (_mutationGate)
            return FindDurableCore(token);
    }

    private CloudSession? FindDurableCore(string token)
    {
        var durable = _durableTokens.GetValueOrDefault(token);
        if (durable?.ExpiresUtc is null || durable.ExpiresUtc > DateTimeOffset.UtcNow) return durable;
        _durableTokens.TryRemove(token, out _);
        return null;
    }

    public bool RemoveBySessionId(string sessionId)
    {
        lock (_mutationGate)
        {
            var match = _active.FirstOrDefault(pair =>
                pair.Value.SessionId.Equals(sessionId, StringComparison.OrdinalIgnoreCase));
            if (match.Key is null || !_active.TryRemove(match.Key, out _)) return false;
            _transportMetrics.ActiveSessionsChanged(-1);
            var now = DateTimeOffset.UtcNow;
            foreach (var stale in _recentlyClosed.Where(pair => now - pair.Value.ClosedUtc > RecentlyClosedLifetime))
                _recentlyClosed.TryRemove(stale.Key, out _);
            if (!string.IsNullOrWhiteSpace(match.Value.DeviceId))
                _recentlyClosed[match.Value.DeviceId.Trim()] = (match.Value, now);
            return true;
        }
    }

    public CloudSession? FindRecentlyClosed(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId) ||
            !_recentlyClosed.TryGetValue(deviceId.Trim(), out var entry)) return null;
        return DateTimeOffset.UtcNow - entry.ClosedUtc <= RecentlyClosedLifetime ? entry.Session : null;
    }

    // The newest live or just-closed dialog session for a device. Durable token records are
    // auth-only and never carry dialog state, so they must not shadow a just-closed session
    // (a robot can hold hundreds of issued tokens).
    public CloudSession? FindDialogDonor(string? deviceId, string excludeSessionId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return null;
        lock (_mutationGate)
        {
            var closed = FindRecentlyClosed(deviceId);
            return _active.Values
                .Concat(closed is null ? Array.Empty<CloudSession>() : new[] { closed })
                .Where(candidate =>
                    !string.Equals(candidate.SessionId, excludeSessionId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(candidate.DeviceId, deviceId.Trim(), StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(candidate => candidate.LastSeenUtc)
                .ThenByDescending(candidate => candidate.CreatedUtc)
                .FirstOrDefault();
        }
    }

    public bool TryRemove(string token, out CloudSession? session)
    {
        lock (_mutationGate)
        {
            var removedActive = _active.TryRemove(token, out var active);
            var removedDurable = _durableTokens.TryRemove(token, out var durable);
            session = active ?? durable;
            if (removedActive) _transportMetrics.ActiveSessionsChanged(-1);
            return removedActive || removedDurable;
        }
    }

    public int RemoveDurableForDevice(string deviceId, string kind)
    {
        lock (_mutationGate)
        {
            var removed = 0;
            foreach (var pair in _durableTokens.Where(pair =>
                         string.Equals(pair.Value.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase) &&
                         string.Equals(pair.Value.Kind, kind, StringComparison.OrdinalIgnoreCase)).ToArray())
                if (_durableTokens.TryRemove(pair.Key, out _)) removed++;
            return removed;
        }
    }

    private void RemoveExpiredDurableTokensCore()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in _durableTokens.Where(pair => pair.Value.ExpiresUtc <= now).ToArray())
            _durableTokens.TryRemove(pair.Key, out _);
    }

    public void Clear()
    {
        lock (_mutationGate)
        {
            var activeCount = _active.Count;
            _active.Clear();
            _durableTokens.Clear();
            if (activeCount > 0) _transportMetrics.ActiveSessionsChanged(-activeCount);
        }
    }
}
