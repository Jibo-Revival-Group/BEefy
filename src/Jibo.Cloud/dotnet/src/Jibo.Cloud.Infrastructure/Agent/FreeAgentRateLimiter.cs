using System.Net;

namespace Jibo.Cloud.Infrastructure.Agent;

/// <summary>
/// Process-local quota for Free AI requests only. Share one instance across Free endpoints.
/// Do not call for Hermes, user-provided providers, native BEefy requests, or ASR.
/// The caller supplies an self-reported robot identity and a trusted connection IP;
/// this class neither authenticates callers nor interprets forwarded headers.
/// Robot IDs allow 50 admissions in 30 minutes; IPs allow 200 in one hour. Robot IDs use
/// ordinal identity; the caller must supply the same canonical authenticated ID.
/// IPv4-mapped IPv6 addresses share their IPv4 bucket; native IPv6 remains distinct.
/// Stale data is reclaimed on the next attempt, including unrelated stale keys.
/// Cardinality exhaustion denies new keys rather than evicting active quotas.
/// State is bounded by maxBucketsPerDimension buckets per dimension, each with at
/// most 200 timestamps. This is not a distributed or restart-persistent quota.
/// </summary>
public sealed class FreeAgentRateLimiter
{
    private readonly object gate = new();
    private readonly Dictionary<string, Queue<long>> ips = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<long>> robots = new(StringComparer.Ordinal);
    private readonly TimeProvider timeProvider;
    private static readonly TimeSpan RobotWindow = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan IpWindow = TimeSpan.FromHours(1);

    private readonly int maxBucketsPerDimension;

    internal (int Robots, int ClientIps) TrackedBucketCounts
    {
        get { lock (gate) return (robots.Count, ips.Count); }
    }

    public FreeAgentRateLimiter(TimeProvider? timeProvider = null, int maxBucketsPerDimension = 10_000)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBucketsPerDimension);
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.maxBucketsPerDimension = maxBucketsPerDimension;
    }

    /// <summary>
    /// Atomically consumes one permit in both buckets, or consumes neither.
    /// On denial, retryAfterSeconds is the ceiling of the wait until both quota
    /// and cardinality checks would admit, assuming no intervening admissions.
    /// It is zero on success. Missing robot identity/IP throws, never bypasses.
    /// </summary>
    public bool TryAcquire(string robotId, IPAddress clientIp, out int retryAfterSeconds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(robotId);
        ArgumentNullException.ThrowIfNull(clientIp);
        lock (gate)
            return TryAcquireLocked(robotId, clientIp, out retryAfterSeconds);
    }

    private bool TryAcquireLocked(string robotId, IPAddress clientIp, out int retryAfterSeconds)
    {
        var now = timeProvider.GetTimestamp();
        RemoveExpired(robots, now, RobotWindow);
        RemoveExpired(ips, now, IpWindow);
        var ipKey = (clientIp.IsIPv4MappedToIPv6 ? clientIp.MapToIPv4() : clientIp).ToString();
        var robotRetry = GetDelay(robots, robotId, now, RobotWindow, 50);
        var ipRetry = GetDelay(ips, ipKey, now, IpWindow, 200);
        retryAfterSeconds = (int)Math.Ceiling(Math.Max(robotRetry.TotalSeconds, ipRetry.TotalSeconds));
        if (retryAfterSeconds > 0)
            return false;
        if (!robots.TryGetValue(robotId, out var robotBucket))
            robots[robotId] = robotBucket = new Queue<long>();
        if (!ips.TryGetValue(ipKey, out var ipBucket))
            ips[ipKey] = ipBucket = new Queue<long>();
        robotBucket.Enqueue(now);
        ipBucket.Enqueue(now);
        return true;
    }

    private TimeSpan GetDelay(Dictionary<string, Queue<long>> buckets, string key, long now, TimeSpan window, int limit)
    {
        if (!buckets.TryGetValue(key, out var bucket))
            return buckets.Count >= maxBucketsPerDimension
                ? buckets.Values.Min(active => window - timeProvider.GetElapsedTime(active.Last(), now))
                : TimeSpan.Zero;
        return bucket.Count >= limit ? window - timeProvider.GetElapsedTime(bucket.Peek(), now) : TimeSpan.Zero;
    }

    private void RemoveExpired(Dictionary<string, Queue<long>> buckets, long now, TimeSpan window)
    {
        List<string>? expired = null;
        foreach (var (key, bucket) in buckets)
        {
            while (bucket.TryPeek(out var oldest) && timeProvider.GetElapsedTime(oldest, now) >= window)
                bucket.Dequeue();
            if (bucket.Count == 0)
                (expired ??= []).Add(key);
        }
        if (expired is not null)
            foreach (var key in expired) buckets.Remove(key);
    }
}
