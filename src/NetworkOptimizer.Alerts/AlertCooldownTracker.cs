using System.Collections.Concurrent;

namespace NetworkOptimizer.Alerts;

/// <summary>
/// In-memory cooldown tracker. Keyed by "{ruleId}:{deviceId}" to avoid DB round-trips.
/// </summary>
public class AlertCooldownTracker
{
    private readonly ConcurrentDictionary<string, DateTime> _lastFired = new();

    /// <summary>
    /// Check if the given key is currently in cooldown.
    /// </summary>
    public bool IsInCooldown(string key, int cooldownSeconds)
    {
        if (cooldownSeconds <= 0)
            return false;

        if (!_lastFired.TryGetValue(key, out var lastFired))
            return false;

        return (DateTime.UtcNow - lastFired).TotalSeconds < cooldownSeconds;
    }

    /// <summary>
    /// Record that an alert was fired for the given key.
    /// </summary>
    public void RecordFired(string key)
    {
        _lastFired[key] = DateTime.UtcNow;
    }

    /// <summary>
    /// Clear expired entries to prevent unbounded growth.
    /// </summary>
    public void Cleanup(TimeSpan maxAge)
    {
        var cutoff = DateTime.UtcNow - maxAge;
        foreach (var kvp in _lastFired)
        {
            if (kvp.Value < cutoff)
                _lastFired.TryRemove(kvp.Key, out _);
        }
    }
}
