using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.Web.Services.Monitoring;

namespace NetworkOptimizer.Web.Services.LanFlowMap;

/// <summary>
/// Process-wide cache for the LAN flow map snapshot. Topology + AP placement +
/// WAN clouds are expensive to build (UniFi API call + Influx queries + force
/// directed layout); the live polling loop on the browser must NOT rebuild this
/// on every tick. Snapshot is built on demand and refreshed at TTL.
///
/// Lifetime: Singleton. The wrapped <see cref="LanFlowMapSnapshot"/> is mutable
/// in the live-rate dictionary across requests; reads/writes there are
/// guarded by <see cref="LiveRatesLock"/>.
/// </summary>
public class LanFlowMapCache
{
    /// <summary>How long a built snapshot is considered fresh before we rebuild.</summary>
    public TimeSpan TopologyRefreshInterval { get; set; } = TimeSpan.FromSeconds(30);

    private LanFlowMapSnapshot? _snapshot;
    private DateTime _snapshotAt;
    private readonly SemaphoreSlim _buildLock = new(1, 1);

    /// <summary>Object monitor protecting <see cref="LanFlowMapSnapshot.LiveRates"/>
    /// mutations from concurrent /live polls.</summary>
    public object LiveRatesLock { get; } = new object();

    public LanFlowMapSnapshot? Current => _snapshot;

    public LanFlowMapSnapshot? GetIfFresh()
    {
        if (_snapshot == null) return null;
        if (DateTime.UtcNow - _snapshotAt > TopologyRefreshInterval) return null;
        return _snapshot;
    }

    public async Task<LanFlowMapSnapshot> BuildOrGetAsync(
        Func<CancellationToken, Task<LanFlowMapSnapshot>> builder,
        CancellationToken ct = default)
    {
        var fresh = GetIfFresh();
        if (fresh != null) return fresh;

        await _buildLock.WaitAsync(ct);
        try
        {
            // Re-check after acquiring the lock - another request may have built it.
            fresh = GetIfFresh();
            if (fresh != null) return fresh;

            _snapshot = await builder(ct);
            _snapshotAt = DateTime.UtcNow;
            return _snapshot;
        }
        finally
        {
            _buildLock.Release();
        }
    }

    /// <summary>Hard-invalidate, e.g. when the user reconnects to a different controller.</summary>
    public void Invalidate()
    {
        _snapshot = null;
        _snapshotAt = DateTime.MinValue;
        HistoricData = null;
    }

    /// <summary>Cached InfluxDB results for historic playback, shared across scoped service instances.</summary>
    public HistoricDataCache? HistoricData { get; set; }

    /// <summary>Earliest interface_counters timestamp - the floor of the playback timeline.</summary>
    public DateTime? EarliestData { get; set; }

    /// <summary>When <see cref="EarliestData"/> was last queried.</summary>
    public DateTime EarliestDataAt { get; set; } = DateTime.MinValue;
}

public record HistoricDataCache(
    DateTime From,
    DateTime To,
    Dictionary<string, IReadOnlyList<MonitoringInfluxClient.InterfaceRatePoint>> RatesByDevice,
    IReadOnlyList<MonitoringInfluxClient.ClientThroughputPoint> WifiClients,
    IReadOnlyList<MonitoringInfluxClient.ClientThroughputPoint> WiredClients,
    Dictionary<string, IReadOnlyList<MonitoringInfluxClient.DeviceHealthPoint>> HealthByDevice,
    Dictionary<MonitoringTargetType, IReadOnlyList<MonitoringInfluxClient.LatencyPoint>> LatencyByTargetType);
