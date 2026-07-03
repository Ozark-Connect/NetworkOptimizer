using System.Collections.Concurrent;

namespace NetworkOptimizer.Monitoring;

/// <summary>
/// Failure counting with temporary exclusion for SNMP polling. We only poll
/// devices UniFi reports as SNMP-enabled, so repeated failures likely mean a
/// transient outage (firmware upgrade, reboot) rather than "device doesn't
/// speak SNMP". A short exclusion avoids hammering unresponsive devices while
/// covering a typical ~3 min firmware upgrade cycle. Shared by the server's
/// per-site collection loops and the on-site agent's SNMP runner so the gating
/// behavior can't drift between the two paths.
/// </summary>
public sealed class SnmpFailureTracker
{
    /// <summary>Consecutive failures before a device is excluded.</summary>
    public const int DefaultFailureThreshold = 5;

    /// <summary>How long an excluded device stays out of the poll rotation.</summary>
    public static readonly TimeSpan DefaultExclusionDuration = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, int> _failures = new();
    private readonly ConcurrentDictionary<string, DateTime> _excluded = new();
    private readonly int _threshold;
    private readonly TimeSpan _duration;

    public SnmpFailureTracker(int failureThreshold = DefaultFailureThreshold, TimeSpan? exclusionDuration = null)
    {
        _threshold = failureThreshold;
        _duration = exclusionDuration ?? DefaultExclusionDuration;
    }

    /// <summary>The exclusion window applied when the failure threshold is hit.</summary>
    public TimeSpan ExclusionDuration => _duration;

    /// <summary>
    /// Records a failed poll. Returns true when this failure crossed the
    /// threshold and started an exclusion window (callers log that transition).
    /// </summary>
    public bool NoteFailure(string key)
    {
        if (string.IsNullOrEmpty(key)) return false;
        if (IsExcluded(key, out _)) return false;

        var count = _failures.AddOrUpdate(key, 1, (_, prev) => prev + 1);
        if (count < _threshold) return false;

        return _excluded.TryAdd(key, DateTime.UtcNow);
    }

    /// <summary>Records a successful poll, resetting the failure counter.</summary>
    public void NoteSuccess(string key)
    {
        if (string.IsNullOrEmpty(key)) return;
        _failures.TryRemove(key, out _);
    }

    /// <summary>
    /// Whether the key is currently excluded. An expired exclusion is removed
    /// (with its failure count) and reported through <paramref name="justExpired"/>
    /// so the caller can log that polling resumes.
    /// </summary>
    public bool IsExcluded(string key, out bool justExpired)
    {
        justExpired = false;
        if (string.IsNullOrEmpty(key)) return false;
        if (!_excluded.TryGetValue(key, out var excludedAt)) return false;
        if (DateTime.UtcNow - excludedAt < _duration) return true;

        _excluded.TryRemove(key, out _);
        _failures.TryRemove(key, out _);
        justExpired = true;
        return false;
    }

    /// <summary>
    /// Read-only check of whether a key is currently excluded, without the
    /// expiry side effect <see cref="IsExcluded"/> performs. Safe to call from
    /// UI threads assembling status views.
    /// </summary>
    public bool PeekExcluded(string key, out DateTime excludedAt)
    {
        if (_excluded.TryGetValue(key, out excludedAt))
            return DateTime.UtcNow - excludedAt < _duration;
        excludedAt = default;
        return false;
    }

    /// <summary>Current consecutive-failure count for a key (0 when none).</summary>
    public int GetFailureCount(string key) =>
        _failures.TryGetValue(key, out var count) ? count : 0;

    /// <summary>Snapshot of the currently excluded keys.</summary>
    public IReadOnlyCollection<string> ExcludedKeys => _excluded.Keys.ToList();
}
