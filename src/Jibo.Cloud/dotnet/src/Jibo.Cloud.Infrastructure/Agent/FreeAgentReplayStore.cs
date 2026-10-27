namespace Jibo.Cloud.Infrastructure.Agent;

/// <summary>Bounded process-local tombstones, never evicts a live replay record.</summary>
public sealed class FreeAgentReplayStore(TimeProvider clock, int capacity = 10000)
{
    private readonly object gate = new();
    private readonly Dictionary<(string Principal, string Request), long> entries = new();
    public bool TryReserve(string principal, string requestId, out bool duplicate)
    {
        lock (gate)
        {
            var now = clock.GetTimestamp();
            foreach (var key in entries.Where(p => clock.GetElapsedTime(p.Value, now) >= TimeSpan.FromMinutes(2)).Select(p => p.Key).ToArray()) entries.Remove(key);
            duplicate = entries.ContainsKey((principal, requestId));
            if (duplicate || entries.Count >= capacity) return false;
            entries.Add((principal, requestId), now);
            return true;
        }
    }
}
