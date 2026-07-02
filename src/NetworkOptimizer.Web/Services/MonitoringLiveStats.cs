using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.Web.Services.Monitoring;

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
    private readonly ILogger<MonitoringLiveStats> _logger;
    private readonly IDbContextFactory<NetworkOptimizerDbContext> _dbFactory;

    private List<(string TargetId, MonitoringTargetType TargetType)>? _ispTransitTargets;
    private DateTime _ispTransitTargetsCacheTime;
    private static readonly TimeSpan TargetCacheTtl = TimeSpan.FromSeconds(30);
    private readonly Lock _targetCacheLock = new();

    private readonly SiteDbContextFactory? _siteDbFactory;
    private readonly string? _siteSlug;

    /// <param name="siteSlug">
    /// Non-default site whose database backs the target lookups. Null/empty =
    /// the default site, reading from the main database as before.
    /// </param>
    public MonitoringLiveStats(ILogger<MonitoringLiveStats> logger,
        IDbContextFactory<NetworkOptimizerDbContext> dbFactory,
        SiteDbContextFactory? siteDbFactory = null,
        string? siteSlug = null)
    {
        _logger = logger;
        _dbFactory = dbFactory;
        _siteDbFactory = siteDbFactory;
        _siteSlug = string.IsNullOrEmpty(siteSlug) ? null : siteSlug;
    }

    /// <summary>Context for the database holding this instance's site data.</summary>
    private async Task<NetworkOptimizerDbContext> CreateSiteContextAsync(CancellationToken ct)
    {
        if (_siteSlug != null && _siteDbFactory != null)
            return _siteDbFactory.CreateForSite(_siteSlug, isDefault: false);
        return await _dbFactory.CreateDbContextAsync(ct);
    }

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
    /// Fabric ingress/egress sum across the device's port_table. Stored
    /// alongside the trunk-port rate so the 3D map's node-aggregate badge
    /// can show "what this switch is moving across all ports" without
    /// clobbering the direction-aware trunk rate that the trunk LINK
    /// renderer relies on.
    /// </summary>
    public void RecordFabricSum(string deviceMac, double ingressBps, double egressBps, DateTime timestamp)
    {
        if (string.IsNullOrEmpty(deviceMac)) return;
        _stats.AddOrUpdate(Normalize(deviceMac),
            _ => new DeviceLiveStats
            {
                FabricIngressBps = ingressBps,
                FabricEgressBps = egressBps,
                LastRateUpdate = timestamp
            },
            (_, existing) => existing with
            {
                FabricIngressBps = ingressBps,
                FabricEgressBps = egressBps,
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

    public void RecordHealth(string deviceMac, double? cpuPercent, double? memoryUsedPercent, double? temperatureC, long? uptimeSeconds, DateTime timestamp)
    {
        if (string.IsNullOrEmpty(deviceMac)) return;
        _stats.AddOrUpdate(Normalize(deviceMac),
            _ => new DeviceLiveStats
            {
                CpuPercent = cpuPercent,
                MemoryUsedPercent = memoryUsedPercent,
                TemperatureC = temperatureC,
                UptimeSeconds = uptimeSeconds,
                LastHealthUpdate = timestamp
            },
            (_, existing) => existing with
            {
                CpuPercent = cpuPercent ?? existing.CpuPercent,
                MemoryUsedPercent = memoryUsedPercent ?? existing.MemoryUsedPercent,
                TemperatureC = temperatureC ?? existing.TemperatureC,
                UptimeSeconds = uptimeSeconds ?? existing.UptimeSeconds,
                LastHealthUpdate = timestamp
            });
    }

    private readonly ConcurrentDictionary<(string DeviceMac, string PortName), SfpLiveStats> _sfpStats = new();
    private readonly ConcurrentDictionary<string, TargetLiveStats> _targetStats = new();
    private readonly ConcurrentDictionary<string, WifiClientLiveSnapshot> _wifiClients = new();
    private readonly ConcurrentDictionary<string, WiredClientLiveSnapshot> _wiredClients = new();
    // Per-port rate cache. Keyed by (deviceMac, ifName) so the SNMP fast tier
    // (clean 5s cadence) is the writer - the UniFi PortTable byte counters lag
    // ~30s server-side, so polling them every 5s yields a burst-then-zeros
    // pattern that would overwrite snapshot-seeded rates with stale zeros.
    // Direction: DownBps = port TX (data leaving this port toward the connected
    // leaf), UpBps = port RX (data arriving on this port from the leaf).
    private readonly ConcurrentDictionary<(string DeviceMac, string IfName), PortLiveRate> _portRates = new();

    public void RecordPortRate(string deviceMac, string ifName, double downBps, double upBps, DateTime timestamp)
    {
        if (string.IsNullOrEmpty(deviceMac) || string.IsNullOrEmpty(ifName)) return;
        var key = (Normalize(deviceMac), ifName);
        _portRates.TryGetValue(key, out var prior);
        if (downBps == 0 && upBps == 0
            && prior != null
            && (prior.DownBps > 0 || prior.UpBps > 0)
            && prior.ConsecutiveZeroPolls < 1)
        {
            _logger.LogTrace(
                "Port rate hold: {Mac}/{If} was {Down:F0}/{Up:F0} bps, holding through single zero poll",
                deviceMac, ifName, prior.DownBps, prior.UpBps);
            _portRates[key] = prior with
            {
                LastUpdate = timestamp,
                ConsecutiveZeroPolls = prior.ConsecutiveZeroPolls + 1,
            };
            return;
        }
        _portRates[key] = new PortLiveRate
        {
            DownBps = downBps,
            UpBps = upBps,
            LastUpdate = timestamp,
        };
    }

    public PortLiveRate? GetPortRate(string deviceMac, string ifName)
    {
        if (string.IsNullOrEmpty(deviceMac) || string.IsNullOrEmpty(ifName)) return null;
        return _portRates.TryGetValue((Normalize(deviceMac), ifName), out var v) ? v : null;
    }

    // Full per-port snapshot (status, speed, packets, errors, discards + rates) for
    // the Live View port stats table, letting live mode skip an InfluxDB round-trip.
    // Independent of _portRates above (which the 3D map leaf rates depend on) - this
    // is purely additive and read only by the port stats endpoint's live path.
    private readonly ConcurrentDictionary<(string DeviceMac, string IfName), MonitoringInfluxClient.PortStatsPoint> _portStats = new();

    public void RecordPortStats(MonitoringInfluxClient.PortStatsPoint point)
    {
        if (string.IsNullOrEmpty(point.DeviceMac) || string.IsNullOrEmpty(point.IfName)) return;
        var key = (Normalize(point.DeviceMac), point.IfName);
        // Carry forward any field the latest sample didn't carry (rates are only
        // computed when a delta is available), so a partial cycle never blanks a column.
        _portStats[key] = _portStats.TryGetValue(key, out var prior)
            ? new MonitoringInfluxClient.PortStatsPoint
            {
                DeviceMac = point.DeviceMac,
                IfName = point.IfName,
                PortId = string.IsNullOrEmpty(point.PortId) ? prior.PortId : point.PortId,
                OperStatus = point.OperStatus ?? prior.OperStatus,
                SpeedBps = point.SpeedBps ?? prior.SpeedBps,
                RateInBps = point.RateInBps ?? prior.RateInBps,
                RateOutBps = point.RateOutBps ?? prior.RateOutBps,
                BytesIn = point.BytesIn ?? prior.BytesIn,
                BytesOut = point.BytesOut ?? prior.BytesOut,
                UcastPktsIn = point.UcastPktsIn ?? prior.UcastPktsIn,
                UcastPktsOut = point.UcastPktsOut ?? prior.UcastPktsOut,
                McastPktsIn = point.McastPktsIn ?? prior.McastPktsIn,
                McastPktsOut = point.McastPktsOut ?? prior.McastPktsOut,
                BcastPktsIn = point.BcastPktsIn ?? prior.BcastPktsIn,
                BcastPktsOut = point.BcastPktsOut ?? prior.BcastPktsOut,
                ErrorsIn = point.ErrorsIn ?? prior.ErrorsIn,
                ErrorsOut = point.ErrorsOut ?? prior.ErrorsOut,
                DiscardsIn = point.DiscardsIn ?? prior.DiscardsIn,
                DiscardsOut = point.DiscardsOut ?? prior.DiscardsOut,
                Time = point.Time,
            }
            : point;
    }

    // Agent-resolved interface display labels (ifname -> friendly label) per device,
    // e.g. "gre1" -> "WAN3 - AT&T Wireless (5G)". Resolved live by the polling agent
    // from UniFi config so this can become persisted time series later; for now it is
    // an in-memory snapshot read by the port stats endpoint. Purely additive.
    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> _interfaceLabels = new();

    /// <summary>Replaces the resolved ifname→label map for a device.</summary>
    public void RecordInterfaceLabels(string deviceMac, IReadOnlyDictionary<string, string> labels)
    {
        if (string.IsNullOrEmpty(deviceMac) || labels == null) return;
        _interfaceLabels[Normalize(deviceMac)] = labels;
    }

    /// <summary>Resolved label for a device interface, or null when none is known.</summary>
    public string? GetInterfaceLabel(string deviceMac, string ifName)
    {
        if (string.IsNullOrEmpty(deviceMac) || string.IsNullOrEmpty(ifName)) return null;
        return _interfaceLabels.TryGetValue(Normalize(deviceMac), out var map)
            && map.TryGetValue(ifName, out var label) ? label : null;
    }

    /// <summary>The single wired client on a switch/gateway port (for the port stats table).</summary>
    public readonly record struct PortClient(string Mac, string Ip, string Name);

    // Wired client per (device mac, port number), for ports with exactly one client.
    // Refreshed by the WiFi/client tier; swapped atomically. Additive - nothing else
    // reads this, so it can't regress existing consumers.
    private volatile IReadOnlyDictionary<(string DeviceMac, int Port), PortClient> _portClients =
        new Dictionary<(string, int), PortClient>();

    /// <summary>Replaces the whole (device, port) → wired-client map.</summary>
    public void RecordPortClients(IReadOnlyDictionary<(string DeviceMac, int Port), PortClient> map)
    {
        if (map != null) _portClients = map;
    }

    /// <summary>The wired client on a device port, or null when none / ambiguous.</summary>
    public PortClient? GetPortClient(string deviceMac, int port)
    {
        if (string.IsNullOrEmpty(deviceMac)) return null;
        return _portClients.TryGetValue((Normalize(deviceMac), port), out var c) ? c : null;
    }

    /// <summary>Latest cached per-port snapshot, optionally filtered to specific device MACs.</summary>
    public IReadOnlyList<MonitoringInfluxClient.PortStatsPoint> GetPortStatsSnapshot(IReadOnlyCollection<string>? deviceMacs)
    {
        if (deviceMacs != null && deviceMacs.Count > 0)
        {
            var set = deviceMacs.Select(Normalize).ToHashSet();
            return _portStats.Values.Where(p => set.Contains(Normalize(p.DeviceMac))).ToList();
        }
        return _portStats.Values.ToList();
    }

    /// <summary>Latest probe result for a specific monitoring target ID.</summary>
    public TargetLiveStats? GetTargetStats(string targetId)
    {
        if (string.IsNullOrEmpty(targetId)) return null;
        return _targetStats.TryGetValue(targetId, out var v) ? v : null;
    }

    /// <summary>Record the latest probe for a target. Called by the agent's latency tier.</summary>
    public void RecordTargetProbe(string targetId, double? rttAvgMs, double lossPercent, bool success, DateTime timestamp)
    {
        if (string.IsNullOrEmpty(targetId)) return;
        _targetStats[targetId] = new TargetLiveStats
        {
            RttAvgMs = rttAvgMs,
            LossPercent = lossPercent,
            Success = success,
            LastUpdate = timestamp
        };
    }

    /// <summary>Cached list of enabled ISP+Transit monitoring targets. Refreshed every 30s.</summary>
    public async Task<List<(string TargetId, MonitoringTargetType TargetType)>> GetIspTransitTargetsAsync(
        CancellationToken ct = default)
    {
        lock (_targetCacheLock)
        {
            if (_ispTransitTargets != null && DateTime.UtcNow - _ispTransitTargetsCacheTime < TargetCacheTtl)
                return _ispTransitTargets;
        }

        await using var db = await CreateSiteContextAsync(ct);
        var targets = await db.MonitoringTargets.AsNoTracking()
            .Where(t => t.Enabled
                && (t.TargetType == MonitoringTargetType.AccessIsp
                    || t.TargetType == MonitoringTargetType.Transit)
                && (t.AsnNumber == null || !WellKnownAsns.NonTransitInfrastructure.Contains(t.AsnNumber.Value)))
            .Select(t => new { t.TargetId, t.TargetType })
            .ToListAsync(ct);

        var result = targets.Select(t => (t.TargetId, t.TargetType)).ToList();
        lock (_targetCacheLock)
        {
            _ispTransitTargets = result;
            _ispTransitTargetsCacheTime = DateTime.UtcNow;
        }
        return result;
    }

    /// <summary>Total fabric ingress/egress across all devices in the cache.
    /// Only devices with non-null fabric data contribute (APs are excluded
    /// because the collection agent never calls RecordFabricSum for them).</summary>
    public (double IngressBps, double EgressBps) GetTotalFabricLoad()
    {
        double totalIn = 0, totalOut = 0;
        foreach (var kvp in _stats)
        {
            if (kvp.Value.FabricIngressBps == null && kvp.Value.FabricEgressBps == null) continue;
            totalIn += kvp.Value.FabricIngressBps ?? 0;
            totalOut += kvp.Value.FabricEgressBps ?? 0;
        }
        return (totalIn, totalOut);
    }

    /// <summary>Latest SFP DDM snapshot for a given device port.</summary>
    public SfpLiveStats? GetSfpStats(string deviceMac, string portName)
    {
        if (string.IsNullOrEmpty(deviceMac) || string.IsNullOrEmpty(portName)) return null;
        return _sfpStats.TryGetValue((Normalize(deviceMac), portName), out var v) ? v : null;
    }

    /// <summary>All currently-known SFP readings — used by the dashboard SFP card.</summary>
    public IReadOnlyList<(string DeviceMac, string PortName, SfpLiveStats Stats)> AllSfp()
    {
        return _sfpStats
            .Select(kvp => (kvp.Key.DeviceMac, kvp.Key.PortName, kvp.Value))
            .ToList();
    }

    public void RecordSfp(string deviceMac, string portName, double? rxDbm, double? txDbm, double? biasMa, double? tempC, double? voltageV, DateTime timestamp)
    {
        if (string.IsNullOrEmpty(deviceMac) || string.IsNullOrEmpty(portName)) return;

        // If every DDM field came back null, the polling cycle gave us nothing usable -
        // skip the write entirely so we don't blank out the prior good values on the
        // card. UniFi will sometimes report sfp_found=true with all-null DDM values
        // during port renegotiation or transient SNMP failures.
        if (!rxDbm.HasValue && !txDbm.HasValue && !biasMa.HasValue && !tempC.HasValue && !voltageV.HasValue)
            return;

        var key = (Normalize(deviceMac), portName);
        _sfpStats.AddOrUpdate(
            key,
            _ => new SfpLiveStats
            {
                RxPowerDbm = rxDbm,
                TxPowerDbm = txDbm,
                BiasMa = biasMa,
                TemperatureC = tempC,
                VoltageV = voltageV,
                LastUpdate = timestamp
            },
            // Merge: each field keeps the new value when present, otherwise preserves
            // the prior value. One null reading on a single sensor (e.g. bias) no
            // longer wipes the others.
            (_, prior) => new SfpLiveStats
            {
                RxPowerDbm = rxDbm ?? prior.RxPowerDbm,
                TxPowerDbm = txDbm ?? prior.TxPowerDbm,
                BiasMa = biasMa ?? prior.BiasMa,
                TemperatureC = tempC ?? prior.TemperatureC,
                VoltageV = voltageV ?? prior.VoltageV,
                LastUpdate = timestamp
            });
    }

    // ---- WiFi clients (spec 5.2 client data collection) ----

    /// <summary>
    /// Record / refresh a live WiFi client snapshot. Called by the agent's WiFi tier
    /// on every stat/sta poll cycle. Snapshot is keyed by the client MAC so the same
    /// client roaming between APs replaces (rather than duplicates) its row.
    /// </summary>
    public void RecordWifiClient(WifiClientLiveSnapshot snapshot)
    {
        if (string.IsNullOrEmpty(snapshot.ClientMac)) return;
        var key = Normalize(snapshot.ClientMac);
        var fresh = snapshot with
        {
            ClientMac = key,
            ApMac = Normalize(snapshot.ApMac),
            ConsecutiveZeroPolls = 0,
        };
        _wifiClients.AddOrUpdate(key, fresh, (_, prior) =>
        {
            var newTx = fresh.TxThroughputBps ?? 0;
            var newRx = fresh.RxThroughputBps ?? 0;
            var priorTx = prior.TxThroughputBps ?? 0;
            var priorRx = prior.RxThroughputBps ?? 0;
            // UniFi's per-client stat poll often reports 0/0 throughput for one
            // sample between active samples even on a busy client. Hold the
            // prior non-zero rates through a single zero poll; two consecutive
            // zero polls accept the new value as genuinely idle.
            if (newTx == 0 && newRx == 0 && (priorTx > 0 || priorRx > 0) && prior.ConsecutiveZeroPolls < 1)
            {
                return prior with
                {
                    ApMac = fresh.ApMac,
                    Band = fresh.Band,
                    Channel = fresh.Channel,
                    ChannelWidth = fresh.ChannelWidth,
                    SignalDbm = fresh.SignalDbm,
                    NoiseDbm = fresh.NoiseDbm,
                    TxRateKbps = fresh.TxRateKbps,
                    RxRateKbps = fresh.RxRateKbps,
                    Satisfaction = fresh.Satisfaction,
                    Rssi = fresh.Rssi,
                    IsMlo = fresh.IsMlo,
                    Hostname = fresh.Hostname,
                    LastUpdate = fresh.LastUpdate,
                    ConsecutiveZeroPolls = prior.ConsecutiveZeroPolls + 1,
                };
            }
            return fresh;
        });
    }

    /// <summary>Latest snapshot for a specific client MAC, or null if unknown / stale.</summary>
    public WifiClientLiveSnapshot? GetWifiClient(string clientMac)
    {
        if (string.IsNullOrEmpty(clientMac)) return null;
        return _wifiClients.TryGetValue(Normalize(clientMac), out var v) ? v : null;
    }

    /// <summary>All WiFi clients currently connected to a given AP. Used by the 3D map
    /// to render client leaf nodes off their parent AP.</summary>
    public IReadOnlyList<WifiClientLiveSnapshot> GetWifiClientsForAp(string apMac)
    {
        if (string.IsNullOrEmpty(apMac)) return Array.Empty<WifiClientLiveSnapshot>();
        var normalized = Normalize(apMac);
        return _wifiClients.Values
            .Where(c => c.ApMac == normalized)
            .ToList();
    }

    /// <summary>Every currently-tracked WiFi client (across all APs).</summary>
    public IReadOnlyList<WifiClientLiveSnapshot> AllWifiClients() => _wifiClients.Values.ToList();

    // ---- Wired clients (fallback for non-SNMP switches) ----

    public void RecordWiredClient(WiredClientLiveSnapshot snapshot)
    {
        if (string.IsNullOrEmpty(snapshot.ClientMac)) return;
        var key = Normalize(snapshot.ClientMac);
        var fresh = snapshot with { ClientMac = key, ConsecutiveZeroPolls = 0 };
        _wiredClients.AddOrUpdate(key, fresh, (_, prior) =>
        {
            var newTx = fresh.TxThroughputBps ?? 0;
            var newRx = fresh.RxThroughputBps ?? 0;
            if (newTx == 0 && newRx == 0 && ((prior.TxThroughputBps ?? 0) > 0 || (prior.RxThroughputBps ?? 0) > 0) && prior.ConsecutiveZeroPolls < 1)
                return prior with { TxThroughputBps = prior.TxThroughputBps, RxThroughputBps = prior.RxThroughputBps, LastUpdate = fresh.LastUpdate, ConsecutiveZeroPolls = prior.ConsecutiveZeroPolls + 1 };
            return fresh;
        });
    }

    public WiredClientLiveSnapshot? GetWiredClient(string clientMac)
    {
        if (string.IsNullOrEmpty(clientMac)) return null;
        return _wiredClients.TryGetValue(Normalize(clientMac), out var v) ? v : null;
    }

    /// <summary>Drop stale entries — called periodically by the agent.</summary>
    public void Prune(TimeSpan maxAge)
    {
        var cutoff = DateTime.UtcNow - maxAge;
        // SFP polls on the slow tier (~5min). If the SFP cutoff matches the
        // poll interval, every Prune tick between polls races the SFP entry
        // off the cache and the UI flashes blank ("-") for a few seconds
        // until the next slow poll repopulates. Give SFP a generous window
        // (3x the regular cache) so it survives one missed/late slow tick.
        var sfpCutoff = DateTime.UtcNow - TimeSpan.FromTicks(maxAge.Ticks * 3);
        foreach (var kvp in _stats)
        {
            var newest = kvp.Value.LastRateUpdate ?? kvp.Value.LastLatencyUpdate;
            if (newest != null && newest < cutoff)
                _stats.TryRemove(kvp.Key, out _);
        }
        foreach (var kvp in _sfpStats)
        {
            if (kvp.Value.LastUpdate < sfpCutoff)
                _sfpStats.TryRemove(kvp.Key, out _);
        }
        foreach (var kvp in _targetStats)
        {
            if (kvp.Value.LastUpdate < cutoff)
                _targetStats.TryRemove(kvp.Key, out _);
        }
        foreach (var kvp in _wifiClients)
        {
            if (kvp.Value.LastUpdate < cutoff)
                _wifiClients.TryRemove(kvp.Key, out _);
        }
        foreach (var kvp in _portRates)
        {
            if (kvp.Value.LastUpdate < cutoff)
                _portRates.TryRemove(kvp.Key, out _);
        }
        foreach (var kvp in _portStats)
        {
            if (kvp.Value.Time < cutoff)
                _portStats.TryRemove(kvp.Key, out _);
        }
    }

    private static string Normalize(string mac) =>
        mac.ToLowerInvariant().Replace('-', ':');
}

/// <summary>
/// Most recent snapshot of a WiFi client's state. Fed by the agent's WiFi tier on
/// each stat/sta poll. Per spec 3.5, PHY tx/rx rate fields are CAPACITY (the
/// negotiated link rate, available even when the client is idle), while the
/// throughput fields are MEASURED traffic. The 3D map renders particle flow from
/// throughput and uses PHY rate as the "pipe width" / utilization denominator.
/// Don't conflate them.
/// </summary>
public record WifiClientLiveSnapshot
{
    public required string ClientMac { get; init; }
    public required string ApMac { get; init; }
    /// <summary>"2.4ghz" / "5ghz" / "6ghz".</summary>
    public required string Band { get; init; }
    public int? Channel { get; init; }
    public int? ChannelWidth { get; init; }
    public double? SignalDbm { get; init; }
    public double? NoiseDbm { get; init; }
    /// <summary>PHY TX rate (kbps) - capacity, not traffic.</summary>
    public long? TxRateKbps { get; init; }
    /// <summary>PHY RX rate (kbps) - capacity, not traffic.</summary>
    public long? RxRateKbps { get; init; }
    /// <summary>Measured AP->client throughput (bps), from tx_bytes-r when present
    /// else delta-derived from cumulative tx_bytes.</summary>
    public double? TxThroughputBps { get; init; }
    /// <summary>Measured client->AP throughput (bps).</summary>
    public double? RxThroughputBps { get; init; }
    public int? Satisfaction { get; init; }
    public int? Rssi { get; init; }
    public bool IsMlo { get; init; }
    public string? Hostname { get; init; }
    public DateTime LastUpdate { get; init; }

    /// <summary>Internal: tracks consecutive 0/0 throughput polls so a single
    /// transient zero between active samples doesn't blink the UI to silent.</summary>
    public int ConsecutiveZeroPolls { get; init; }
}

public record TargetLiveStats
{
    public double? RttAvgMs { get; init; }
    public double LossPercent { get; init; }
    public bool Success { get; init; }
    public DateTime LastUpdate { get; init; }
}

public record SfpLiveStats
{
    public double? RxPowerDbm { get; init; }
    public double? TxPowerDbm { get; init; }
    public double? BiasMa { get; init; }
    public double? TemperatureC { get; init; }
    public double? VoltageV { get; init; }
    public DateTime LastUpdate { get; init; }
}

public record PortLiveRate
{
    /// <summary>Downstream-toward-leaf direction rate (parent port TX delta) in bps.</summary>
    public double DownBps { get; init; }
    /// <summary>Upstream-from-leaf direction rate (parent port RX delta) in bps.</summary>
    public double UpBps { get; init; }
    public DateTime LastUpdate { get; init; }
    public int ConsecutiveZeroPolls { get; init; }
}

public record DeviceLiveStats
{
    public double? RateInBps { get; init; }
    public double? RateOutBps { get; init; }
    public DateTime? LastRateUpdate { get; init; }

    /// <summary>Fabric ingress/egress for switches - sum of every port_table
    /// RX/TX delta. Separate from Rate{In,Out}Bps which carries the trunk-
    /// port-only direction-aware rate that the trunk link's per-link
    /// renderer relies on.</summary>
    public double? FabricIngressBps { get; init; }
    public double? FabricEgressBps { get; init; }

    public double? LatestRttMs { get; init; }
    public double LatestLossPercent { get; init; }
    public DateTime? LastLatencyUpdate { get; init; }

    public double? CpuPercent { get; init; }
    public double? MemoryUsedPercent { get; init; }
    public double? TemperatureC { get; init; }
    public long? UptimeSeconds { get; init; }
    public DateTime? LastHealthUpdate { get; init; }

    /// <summary>True if any data has landed for this device, within the freshness window.</summary>
    public bool HasFreshData(TimeSpan maxAge)
    {
        var now = DateTime.UtcNow;
        return (LastRateUpdate.HasValue && (now - LastRateUpdate.Value) <= maxAge)
            || (LastLatencyUpdate.HasValue && (now - LastLatencyUpdate.Value) <= maxAge);
    }
}

/// <summary>
/// Throughput snapshot for a wired client, derived from UniFi client stats.
/// Used as a fallback when the parent switch lacks SNMP.
/// </summary>
public record WiredClientLiveSnapshot
{
    public required string ClientMac { get; init; }
    public double? TxThroughputBps { get; init; }
    public double? RxThroughputBps { get; init; }
    public DateTime LastUpdate { get; init; }
    public int ConsecutiveZeroPolls { get; init; }
}
