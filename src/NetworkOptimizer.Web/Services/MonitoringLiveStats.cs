using System.Collections.Concurrent;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// In-memory cache of the most recently observed monitoring stats per device. Updated by
/// MonitoringCollectionAgent on each polling cycle; read by the dashboard to surface live
/// values on device cards without hitting InfluxDB on every UI refresh.
///
/// InfluxDB remains the historical source of truth — this is just a hot snapshot. There's
/// no recomputation path that could drift: the agent writes to InfluxDB and updates this
/// cache in the same code path.
/// </summary>
public class MonitoringLiveStats
{
    private readonly ConcurrentDictionary<string, DeviceLiveStats> _stats = new();

    /// <summary>Total bytes/sec across all monitored interfaces on this device, plus latency.</summary>
    public DeviceLiveStats? GetForDevice(string deviceMac)
    {
        if (string.IsNullOrEmpty(deviceMac)) return null;
        return _stats.TryGetValue(Normalize(deviceMac), out var v) ? v : null;
    }

    /// <summary>
    /// Apply a delta from the fast SNMP poll cycle. The agent calls this once per device
    /// per cycle with the summed rates across all interfaces just polled.
    /// </summary>
    public void RecordInterfaceAggregate(string deviceMac, double aggregateInBps, double aggregateOutBps, DateTime timestamp)
    {
        if (string.IsNullOrEmpty(deviceMac)) return;
        _stats.AddOrUpdate(Normalize(deviceMac),
            _ => new DeviceLiveStats
            {
                RateInBps = aggregateInBps,
                RateOutBps = aggregateOutBps,
                LastRateUpdate = timestamp
            },
            (_, existing) => existing with
            {
                RateInBps = aggregateInBps,
                RateOutBps = aggregateOutBps,
                LastRateUpdate = timestamp
            });
    }

    /// <summary>
    /// Apply the latest fabric latency probe result. The card uses this for the "ping ~3 ms"
    /// display; full-hour aggregates come from InfluxDB on the diagnostic view (5.8).
    /// </summary>
    public void RecordLatency(string deviceMac, double? rttAvgMs, double lossPercent, DateTime timestamp)
    {
        if (string.IsNullOrEmpty(deviceMac)) return;
        _stats.AddOrUpdate(Normalize(deviceMac),
            _ => new DeviceLiveStats
            {
                LatestRttMs = rttAvgMs,
                LatestLossPercent = lossPercent,
                LastLatencyUpdate = timestamp
            },
            (_, existing) => existing with
            {
                LatestRttMs = rttAvgMs,
                LatestLossPercent = lossPercent,
                LastLatencyUpdate = timestamp
            });
    }

    /// <summary>Drop stale entries — called periodically by the agent.</summary>
    public void Prune(TimeSpan maxAge)
    {
        var cutoff = DateTime.UtcNow - maxAge;
        foreach (var kvp in _stats)
        {
            var newest = kvp.Value.LastRateUpdate ?? kvp.Value.LastLatencyUpdate;
            if (newest != null && newest < cutoff)
                _stats.TryRemove(kvp.Key, out _);
        }
    }

    private static string Normalize(string mac) =>
        mac.ToLowerInvariant().Replace('-', ':');
}

public record DeviceLiveStats
{
    public double? RateInBps { get; init; }
    public double? RateOutBps { get; init; }
    public DateTime? LastRateUpdate { get; init; }

    public double? LatestRttMs { get; init; }
    public double LatestLossPercent { get; init; }
    public DateTime? LastLatencyUpdate { get; init; }

    /// <summary>True if any data has landed for this device, within the freshness window.</summary>
    public bool HasFreshData(TimeSpan maxAge)
    {
        var now = DateTime.UtcNow;
        return (LastRateUpdate.HasValue && (now - LastRateUpdate.Value) <= maxAge)
            || (LastLatencyUpdate.HasValue && (now - LastLatencyUpdate.Value) <= maxAge);
    }
}
