using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.UniFi;
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

    private List<(string TargetId, MonitoringTargetType TargetType, string? WanInterface)>? _ispTransitTargets;
    private DateTime _ispTransitTargetsCacheTime;
    private static readonly TimeSpan TargetCacheTtl = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How old a target's last probe reading may be and still be plotted as live. Half again the
    /// slowest poll interval a target can be given (60 s), so one missed cycle rides through and
    /// the next expires the reading rather than letting it stand in for a current one.
    /// </summary>
    public static readonly TimeSpan LiveReadingMaxAge = TimeSpan.FromSeconds(90);
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

    /// <summary>When this cache came to life; readers use it to say how warmed up its histories are.</summary>
    public DateTime StartedAt { get; } = DateTime.UtcNow;

    private readonly ConcurrentDictionary<string, DeviceLiveStats> _stats = new();

    // Last time SNMP data was seen for a device, keyed by normalized MAC. On an
    // agent-covered site the server never polls SNMP locally (the agent does), so the
    // collection agent's own last-polled tracker stays empty; the tunnel result sink
    // stamps this each time a device's SNMP batch arrives, and the SNMP Devices status
    // table reads it so agent-polled devices show as Polling rather than "not yet polled".
    private readonly ConcurrentDictionary<string, DateTime> _snmpSeenByMac = new();

    /// <summary>Records that SNMP data was just relayed for a device (agent-covered sites).</summary>
    public void RecordSnmpSeen(string deviceMac, DateTime timestamp)
    {
        if (string.IsNullOrEmpty(deviceMac)) return;
        _snmpSeenByMac[Normalize(deviceMac)] = timestamp;
    }

    /// <summary>The last time SNMP data was seen for a device, or null.</summary>
    public DateTime? GetSnmpLastSeen(string deviceMac)
    {
        if (string.IsNullOrEmpty(deviceMac)) return null;
        return _snmpSeenByMac.TryGetValue(Normalize(deviceMac), out var t) ? t : null;
    }

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
    private readonly ConcurrentDictionary<string, ConsoleWanRate> _consoleWanRates = new();
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
        PortLiveRate stored;
        if (downBps == 0 && upBps == 0
            && prior != null
            && (prior.DownBps > 0 || prior.UpBps > 0)
            && prior.ConsecutiveZeroPolls < 1)
        {
            _logger.LogTrace(
                "Port rate hold: {Mac}/{If} was {Down:F0}/{Up:F0} bps, holding through single zero poll",
                deviceMac, ifName, prior.DownBps, prior.UpBps);
            stored = prior with
            {
                LastUpdate = timestamp,
                ConsecutiveZeroPolls = prior.ConsecutiveZeroPolls + 1,
            };
        }
        else
        {
            stored = new PortLiveRate
            {
                DownBps = downBps,
                UpBps = upBps,
                LastUpdate = timestamp,
            };
        }
        _portRates[key] = stored;
        AppendRowRate(PortRowKey(deviceMac, ifName), stored.DownBps, stored.UpBps, timestamp);
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
    public async Task<List<(string TargetId, MonitoringTargetType TargetType, string? WanInterface)>> GetIspTransitTargetsAsync(
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
            .Select(t => new { t.TargetId, t.TargetType, t.WanInterface })
            .ToListAsync(ct);

        var result = targets.Select(t => (t.TargetId, t.TargetType, t.WanInterface)).ToList();
        lock (_targetCacheLock)
        {
            _ispTransitTargets = result;
            _ispTransitTargetsCacheTime = DateTime.UtcNow;
        }
        return result;
    }

    /// <summary>
    /// Combined ISP+Transit live latency as plotted by the WAN live chart: RTT and
    /// loss averaged per target type, then the mean of the two types. Loss is
    /// combined over the types that have loss samples, independent of RTT presence,
    /// mirroring the historic series (QueryMeanIspTransitLatencyAsync) - during a
    /// full outage RTT goes null while loss pegs at 100, and gating loss on RTT
    /// blanked the chart exactly when loss mattered most. Shared by the live-stats
    /// endpoint and the LAN flow map WAN globes so both always show the same number.
    /// </summary>
    /// <param name="wanInterface">
    /// Scope to one WAN's targets. Null keeps the site-wide mean, which is what every caller meant
    /// before there was more than one WAN to tell apart. An unstamped target belongs to the
    /// primary - the same rule every per-WAN reader uses - so a secondary WAN with no targets of
    /// its own returns nothing rather than borrowing the primary's numbers and presenting them as
    /// its own.
    /// </param>
    public async Task<(double? MeanRttMs, double? MeanLossPercent)> GetMeanIspTransitLiveAsync(
        CancellationToken ct = default,
        string? wanInterface = null,
        bool isPrimary = false)
    {
        var targets = await GetIspTransitTargetsAsync(ct);
        // No WAN named means the primary, not every WAN. A chart showing one WAN asks for it by
        // omitting the parameter, and skipping the filter entirely averaged in the other WANs'
        // targets - a speed test on a secondary WAN then appeared as a latency and loss spike on
        // the primary's chart, from readings that were never on its path. Unchanged on a
        // single-WAN site, where every target is the primary's already.
        var key = string.IsNullOrEmpty(wanInterface)
            ? GatewayWanHelper.DefaultWanKey
            : GatewayWanHelper.WanInterfaceKeyFromKey(wanInterface!);
        var primaryScope = isPrimary || string.IsNullOrEmpty(wanInterface);
        targets = targets.Where(t => MonitoringTarget.IsUnpinned(t.WanInterface)
            ? primaryScope
            : string.Equals(GatewayWanHelper.WanInterfaceKeyFromKey(t.WanInterface!), key,
                StringComparison.OrdinalIgnoreCase))
            .ToList();

        var ispRtts = new List<double>();
        var ispLosses = new List<double>();
        var transitRtts = new List<double>();
        var transitLosses = new List<double>();

        // A reading is only evidence while it is current. A target that stops reporting - which is
        // exactly what some failures look like, rather than a reported 100% loss - otherwise keeps
        // presenting its last good reading forever, and the card reads healthy through an outage.
        // Observed on a WAN whose ISP targets went quiet under a blackhole while its transit
        // targets kept reporting: transit showed the true 100% loss, the ISP rows showed the RTT
        // and 0% loss they had carried before it started.
        var stale = DateTime.UtcNow - LiveReadingMaxAge;

        foreach (var t in targets)
        {
            var st = GetTargetStats(t.TargetId);
            if (st == null || st.LastUpdate < stale) continue;

            if (t.TargetType == MonitoringTargetType.AccessIsp)
            {
                if (st.RttAvgMs != null) ispRtts.Add(st.RttAvgMs.Value);
                ispLosses.Add(st.LossPercent);
            }
            else
            {
                if (st.RttAvgMs != null) transitRtts.Add(st.RttAvgMs.Value);
                transitLosses.Add(st.LossPercent);
            }
        }

        var ispRtt = ispRtts.Count > 0 ? ispRtts.Average() : (double?)null;
        var ispLoss = ispLosses.Count > 0 ? ispLosses.Average() : (double?)null;
        var transitRtt = transitRtts.Count > 0 ? transitRtts.Average() : (double?)null;
        var transitLoss = transitLosses.Count > 0 ? transitLosses.Average() : (double?)null;

        double? meanRtt;
        if (ispRtt != null && transitRtt != null)
            meanRtt = (ispRtt.Value + transitRtt.Value) / 2;
        else meanRtt = ispRtt ?? transitRtt;

        // Null, never zero, when nothing fresh reported: no reading is not the same claim as no
        // loss, and the zero read as a healthy connection during an outage.
        double? meanLoss;
        if (ispLoss != null && transitLoss != null)
            meanLoss = (ispLoss.Value + transitLoss.Value) / 2;
        else meanLoss = ispLoss ?? transitLoss;

        return (meanRtt, meanLoss);
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
            // longer wipes the others. PON supplement fields (set by RecordSfpPon) are
            // always carried through - this DDM path never touches them.
            (_, prior) => prior with
            {
                RxPowerDbm = rxDbm ?? prior.RxPowerDbm,
                TxPowerDbm = txDbm ?? prior.TxPowerDbm,
                BiasMa = biasMa ?? prior.BiasMa,
                TemperatureC = tempC ?? prior.TemperatureC,
                VoltageV = voltageV ?? prior.VoltageV,
                LastUpdate = timestamp
            });
    }

    /// <summary>
    /// Record the latest PON-layer supplement (from an attached ONT provider) onto the
    /// module's live SFP entry, preserving the DDM readings. Absolute counters; the card
    /// shows them as-is. Skips the write when nothing usable came back so a failed
    /// supplement poll doesn't blank the prior good values.
    /// </summary>
    public void RecordSfpPon(string deviceMac, string portName, string? ponLinkStatus,
        long? bipErrors, long? fecErrors, long? hecUncorrected, bool? fecEnabled,
        long? gemRxDropped, DateTime timestamp)
    {
        if (string.IsNullOrEmpty(deviceMac) || string.IsNullOrEmpty(portName)) return;
        if (string.IsNullOrEmpty(ponLinkStatus) && !bipErrors.HasValue && !fecErrors.HasValue
            && !hecUncorrected.HasValue && !gemRxDropped.HasValue)
            return;

        var key = (Normalize(deviceMac), portName);
        _sfpStats.AddOrUpdate(
            key,
            _ => new SfpLiveStats
            {
                PonLinkStatus = ponLinkStatus,
                BipErrors = bipErrors,
                FecErrors = fecErrors,
                HecUncorrected = hecUncorrected,
                FecEnabled = fecEnabled,
                GemRxDropped = gemRxDropped,
                LastUpdate = timestamp
            },
            (_, prior) => prior with
            {
                PonLinkStatus = ponLinkStatus ?? prior.PonLinkStatus,
                BipErrors = bipErrors ?? prior.BipErrors,
                FecErrors = fecErrors ?? prior.FecErrors,
                HecUncorrected = hecUncorrected ?? prior.HecUncorrected,
                FecEnabled = fecEnabled ?? prior.FecEnabled,
                GemRxDropped = gemRxDropped ?? prior.GemRxDropped,
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
        // A caller carrying a prior reading forward passes the prior's zero-poll count with it;
        // every other caller leaves it at zero, as a fresh measurement should.
        var fresh = snapshot with
        {
            ClientMac = key,
            ApMac = Normalize(snapshot.ApMac),
        };
        var stored = _wifiClients.AddOrUpdate(key, fresh, (_, prior) =>
        {
            // One entry per client, so two access points claiming the same one race, and the later
            // write wins whether or not it is the live association. An access point can hold a
            // station long after the client left: idle separates them, and without this the maps
            // and Client Performance follow whichever poll landed last.
            if (KeepPriorApClaim(prior, fresh)) return prior;

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
                    Source = fresh.Source,
                    Hostname = fresh.Hostname ?? prior.Hostname,
                    LastUpdate = fresh.LastUpdate,
                    ConsecutiveZeroPolls = prior.ConsecutiveZeroPolls + 1,
                };
            }
            // Hostname is identity, not a reading. Sources that carry no name (the AP Agent knows
            // MACs only) must not blank the one a source that does carry it already established.
            return fresh.Hostname is null ? fresh with { Hostname = prior.Hostname } : fresh;
        });
        if (stored.TxThroughputBps != null || stored.RxThroughputBps != null)
            AppendRowRate(WifiRowKey(key), stored.TxThroughputBps ?? 0, stored.RxThroughputBps ?? 0, stored.LastUpdate);
    }

    /// <summary>
    /// How stale an incoming claim must be before a different access point's fresher one outranks
    /// it. Well past any normal poll gap, so two access points genuinely serving a client in turn
    /// hand over immediately and only an abandoned station is held back.
    /// </summary>
    private const long ContestedClaimIdleSeconds = 60;

    /// <summary>How long a held claim stays authoritative without being reasserted by its own poll.</summary>
    private static readonly TimeSpan ClaimFreshness = TimeSpan.FromSeconds(90);

    /// <summary>
    /// Whether an existing claim on a client outranks an incoming one from a different access
    /// point. Only when both report idle, the incoming one is itself stale, and the held one is
    /// both fresher and still being reasserted - so a client that really did leave still moves
    /// once its old access point stops answering.
    /// </summary>
    private static bool KeepPriorApClaim(WifiClientLiveSnapshot prior, WifiClientLiveSnapshot fresh)
    {
        if (string.Equals(prior.ApMac, fresh.ApMac, StringComparison.OrdinalIgnoreCase)) return false;
        if (prior.IdleSeconds is not { } priorIdle || fresh.IdleSeconds is not { } freshIdle) return false;
        if (freshIdle < ContestedClaimIdleSeconds || freshIdle <= priorIdle) return false;

        return DateTime.UtcNow - prior.LastUpdate <= ClaimFreshness;
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
        var stored = _wiredClients.AddOrUpdate(key, fresh, (_, prior) =>
        {
            var newTx = fresh.TxThroughputBps ?? 0;
            var newRx = fresh.RxThroughputBps ?? 0;
            if (newTx == 0 && newRx == 0 && ((prior.TxThroughputBps ?? 0) > 0 || (prior.RxThroughputBps ?? 0) > 0) && prior.ConsecutiveZeroPolls < 1)
                return prior with { TxThroughputBps = prior.TxThroughputBps, RxThroughputBps = prior.RxThroughputBps, LastUpdate = fresh.LastUpdate, ConsecutiveZeroPolls = prior.ConsecutiveZeroPolls + 1 };
            return fresh;
        });
        if (stored.TxThroughputBps != null || stored.RxThroughputBps != null)
            AppendRowRate(WiredRowKey(key), stored.TxThroughputBps ?? 0, stored.RxThroughputBps ?? 0, stored.LastUpdate);
    }

    public WiredClientLiveSnapshot? GetWiredClient(string clientMac)
    {
        if (string.IsNullOrEmpty(clientMac)) return null;
        return _wiredClients.TryGetValue(Normalize(clientMac), out var v) ? v : null;
    }

    // ---- Console WAN rates (the gateway's per-client view) ----

    /// <summary>
    /// Records the console's per-client rate. Kept apart from the client snapshots because it is a
    /// different measurement: the gateway's, so WAN only, for wired and Wi-Fi clients alike, and
    /// tens of seconds behind. Recorded for every client, including those an AP Agent serves.
    /// </summary>
    /// <summary>How much console rate history is kept per client, for the WAN-baseline read.</summary>
    public static readonly TimeSpan ConsoleRateHistoryFor = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<string, List<(DateTime At, double Down, double Up)>> _consoleRateHistory = new(StringComparer.OrdinalIgnoreCase);

    public void RecordConsoleWanRate(string clientMac, double downBps, double upBps, DateTime at)
    {
        if (string.IsNullOrEmpty(clientMac)) return;
        var key = Normalize(clientMac);
        var fresh = new ConsoleWanRate(Math.Max(0, downBps), Math.Max(0, upBps), at);
        var kept = _consoleWanRates.AddOrUpdate(key, fresh, (_, prior) =>
            // The console's -r fields read 0/0 for one sample between active ones, as the client
            // snapshots already allow for. One zero is held; two in a row are idle.
            fresh.DownBps == 0 && fresh.UpBps == 0 && (prior.DownBps > 0 || prior.UpBps > 0) && !prior.HeldZero
                ? prior with { At = at, HeldZero = true }
                : fresh);
        var history = _consoleRateHistory.GetOrAdd(key, _ => new());
        lock (history)
        {
            history.Add((at, kept.DownBps, kept.UpBps));
            history.RemoveAll(s => at - s.At > ConsoleRateHistoryFor);
        }
    }

    /// <summary>The console's recorded rates for a client over the kept history, oldest first.</summary>
    public IReadOnlyList<(DateTime At, double Down, double Up)> ConsoleRateHistory(string clientMac)
    {
        if (string.IsNullOrEmpty(clientMac) || !_consoleRateHistory.TryGetValue(Normalize(clientMac), out var history))
            return Array.Empty<(DateTime, double, double)>();
        lock (history) return history.ToArray();
    }

    /// <summary>The console's WAN rate for a client, or null when it has none newer than <paramref name="maxAge"/>.</summary>
    public ConsoleWanRate? GetConsoleWanRate(string clientMac, TimeSpan maxAge)
    {
        if (string.IsNullOrEmpty(clientMac)) return null;
        return _consoleWanRates.TryGetValue(Normalize(clientMac), out var v) && DateTime.UtcNow - v.At <= maxAge ? v : null;
    }

    // ---- Gateway conntrack: measured per-client WAN rates ----
    // THE per-client WAN cache everything else has been approximating: the on-gateway agent's
    // conntrack window deltas, recorded as rates by the tunnel sink. Source of truth from the
    // first batch - no arming period, no baseline dependency.

    /// <summary>A client's conntrack-measured WAN rate, computed from a window's byte deltas.</summary>
    public readonly record struct ClientWanRate(double DownBps, double UpBps, DateTime At);

    /// <summary>
    /// How old a conntrack rate may be and still cover a row. Three 5s windows, so one late or
    /// stretched sample pass rides through; past it the row falls back to the estimated split
    /// rather than showing a stale measurement as current.
    /// </summary>
    public static readonly TimeSpan ConntrackFreshness = TimeSpan.FromSeconds(20);

    /// <summary>The synthetic identity for WAN bytes conntrack saw but could not attribute
    /// (endpoints with no neighbor entry - VPN road warriors, rotated IPv6 privacy addresses).</summary>
    public const string ConntrackUnattributed = "unattributed";

    private readonly ConcurrentDictionary<string, ClientWanRate> _clientWanRates = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastConntrackBatchAt = DateTime.MinValue;

    /// <summary>Records a client's measured WAN rate from a conntrack window (all WANs summed).</summary>
    public void RecordClientWanRate(string clientMac, double downBps, double upBps, DateTime at)
    {
        if (string.IsNullOrEmpty(clientMac)) return;
        _clientWanRates[Normalize(clientMac)] = new ClientWanRate(Math.Max(0, downBps), Math.Max(0, upBps), at);
        _lastConntrackBatchAt = at;
    }

    /// <summary>Stamps a conntrack batch that carried no client samples (an idle WAN is still coverage).</summary>
    public void NoteConntrackBatch(DateTime at) => _lastConntrackBatchAt = at;

    /// <summary>A client's measured WAN rate, or null when none newer than <paramref name="maxAge"/>.
    /// A covered site's idle client has no entry (or a stale one): coverage says its WAN is zero,
    /// which is why callers pair this with <see cref="HasConntrackCoverage"/>.</summary>
    public ClientWanRate? GetClientWanRate(string clientMac, TimeSpan maxAge)
    {
        if (string.IsNullOrEmpty(clientMac)) return null;
        return _clientWanRates.TryGetValue(Normalize(clientMac), out var v) && DateTime.UtcNow - v.At <= maxAge ? v : null;
    }

    /// <summary>Whether the gateway agent's conntrack feed is currently flowing for this site.</summary>
    public bool HasConntrackCoverage(TimeSpan maxAge) => DateTime.UtcNow - _lastConntrackBatchAt <= maxAge;

    /// <summary>When the conntrack feed last reported, or null if it never has.</summary>
    public DateTime? LastConntrackBatchAt => _lastConntrackBatchAt == DateTime.MinValue ? null : _lastConntrackBatchAt;

    // Recent measured rates per Bandwidth Hogs row, appended where the sources land (Wi-Fi client
    // throughput, SNMP/port-table port rates, the wired-client fallback) so the baselines are
    // always warm - no page needs to be open, and no new polling: the data flows anyway.
    private readonly ConcurrentDictionary<string, List<(DateTime At, double Down, double Up)>> _rowRates = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// How much measured-rate history is kept per row. Deliberately wide: the Bandwidth Hogs
    /// baseline learns a device's background habit from it, and co-movement spans it so a WAN
    /// burst from half an hour ago stays excluded from that habit. The console history stays at
    /// 15 minutes on purpose - a recent ceiling against a wide floor errs toward not attributing.
    /// </summary>
    public static readonly TimeSpan RowRateHistoryFor = TimeSpan.FromMinutes(60);

    /// <summary>Sources write faster than a baseline needs; samples closer than this are dropped.</summary>
    private static readonly TimeSpan RowRateSampleSpacing = TimeSpan.FromSeconds(10);

    /// <summary>History key for a Wi-Fi client row.</summary>
    public static string WifiRowKey(string clientMac) => "wifi:" + Normalize(clientMac);

    /// <summary>History key for a switch-port row (a wired client's port, or a shared port's hub).</summary>
    public static string PortRowKey(string deviceMac, string ifName) => "port:" + Normalize(deviceMac) + "|" + ifName;

    /// <summary>History key for a wired client with no port rate (non-SNMP switch fallback).</summary>
    public static string WiredRowKey(string clientMac) => "wired:" + Normalize(clientMac);

    private void AppendRowRate(string key, double down, double up, DateTime at)
    {
        var list = _rowRates.GetOrAdd(key, _ => new());
        lock (list)
        {
            if (list.Count > 0 && at - list[^1].At < RowRateSampleSpacing) return;
            list.Add((at, down, up));
            list.RemoveAll(s => at - s.At > RowRateHistoryFor);
        }
    }

    /// <summary>A row's measured-rate samples over the kept history, oldest first.</summary>
    public IReadOnlyList<(DateTime At, double Down, double Up)> RowRateHistory(string key)
    {
        if (string.IsNullOrEmpty(key) || !_rowRates.TryGetValue(key, out var list))
            return Array.Empty<(DateTime, double, double)>();
        lock (list) return list.ToArray();
    }

    // ---- Bandwidth Hogs learned baselines, persisted so a restart starts armed ----

    /// <summary>A row's learned baseline local rate, and when it was last computed live.</summary>
    public readonly record struct RowBaseline(double DownBps, double UpBps, DateTime At);

    private readonly ConcurrentDictionary<string, RowBaseline> _rowBaselines = new(StringComparer.OrdinalIgnoreCase);
    private int _rowBaselinesLoadStarted;
    private DateTime _rowBaselinesPersistedAt = DateTime.UtcNow;
    private static readonly TimeSpan RowBaselinePersistEvery = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RowBaselineKeepFor = TimeSpan.FromHours(24);

    /// <summary>Records a row's live-computed baseline; the newest per key survives restarts.</summary>
    public void RecordRowBaseline(string key, double downBps, double upBps, DateTime at)
    {
        if (string.IsNullOrEmpty(key)) return;
        EnsureRowBaselinesLoaded();
        _rowBaselines[key] = new RowBaseline(Math.Max(0, downBps), Math.Max(0, upBps), at);
    }

    /// <summary>The learned baseline for a row, or null when none newer than <paramref name="maxAge"/>.</summary>
    public RowBaseline? GetRowBaseline(string key, TimeSpan maxAge)
    {
        if (string.IsNullOrEmpty(key)) return null;
        EnsureRowBaselinesLoaded();
        return _rowBaselines.TryGetValue(key, out var b) && DateTime.UtcNow - b.At <= maxAge ? b : null;
    }

    private void EnsureRowBaselinesLoaded()
    {
        if (Interlocked.Exchange(ref _rowBaselinesLoadStarted, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await using var db = await CreateSiteContextAsync(CancellationToken.None);
                var cutoff = DateTime.UtcNow - RowBaselineKeepFor;
                // TryAdd only: anything computed live since startup outranks the persisted copy.
                foreach (var row in await db.HogRowBaselines.AsNoTracking().ToListAsync())
                    if (row.UpdatedAt >= cutoff)
                        _rowBaselines.TryAdd(row.RowKey, new RowBaseline(row.DownBps, row.UpBps, row.UpdatedAt));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not load persisted Bandwidth Hogs baselines");
            }
        });
    }

    private async Task PersistRowBaselinesAsync()
    {
        try
        {
            var cutoff = DateTime.UtcNow - RowBaselineKeepFor;
            foreach (var stale in _rowBaselines.Where(kv => kv.Value.At < cutoff).Select(kv => kv.Key).ToList())
                _rowBaselines.TryRemove(stale, out _);
            var live = _rowBaselines.ToArray();
            await using var db = await CreateSiteContextAsync(CancellationToken.None);
            var stored = await db.HogRowBaselines.ToDictionaryAsync(r => r.RowKey);
            foreach (var (key, b) in live)
            {
                if (stored.TryGetValue(key, out var row))
                {
                    if (row.UpdatedAt >= b.At) continue;
                    row.DownBps = b.DownBps;
                    row.UpBps = b.UpBps;
                    row.UpdatedAt = b.At;
                }
                else
                {
                    db.HogRowBaselines.Add(new HogRowBaseline { RowKey = key, DownBps = b.DownBps, UpBps = b.UpBps, UpdatedAt = b.At });
                }
            }
            foreach (var row in stored.Values.Where(r => r.UpdatedAt < cutoff))
                db.HogRowBaselines.Remove(row);
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not persist Bandwidth Hogs baselines");
        }
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
        foreach (var kvp in _consoleWanRates)
        {
            if (kvp.Value.At < cutoff)
                _consoleWanRates.TryRemove(kvp.Key, out _);
        }
        foreach (var kvp in _rowRates)
        {
            bool stale;
            lock (kvp.Value) stale = kvp.Value.Count == 0 || kvp.Value[^1].At < cutoff;
            if (stale) _rowRates.TryRemove(kvp.Key, out _);
        }
        foreach (var kvp in _consoleRateHistory)
        {
            bool stale;
            lock (kvp.Value) stale = kvp.Value.Count == 0 || kvp.Value[^1].At < cutoff;
            if (stale) _consoleRateHistory.TryRemove(kvp.Key, out _);
        }
        if (DateTime.UtcNow - _rowBaselinesPersistedAt >= RowBaselinePersistEvery && !_rowBaselines.IsEmpty)
        {
            _rowBaselinesPersistedAt = DateTime.UtcNow;
            _ = Task.Run(PersistRowBaselinesAsync);
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
/// <summary>
/// Where a live client reading came from, fastest first. The AP Agent polls every 10 s and Client
/// Performance drives it to 500 ms; WiFiman runs at 1 Hz on sites with no agent; the console wifi
/// tier is the 30 s baseline every site has.
/// </summary>
public enum WifiClientSource
{
    Console,
    WiFiMan,
    ApAgent,
}

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

    /// <summary>
    /// Seconds since the access point reporting this last heard from the client. Null where the
    /// source does not carry it. Two access points can both claim a client - one of them holding a
    /// station that has physically left - and this is what tells them apart.
    /// </summary>
    public long? IdleSeconds { get; init; }

    public DateTime LastUpdate { get; init; }

    /// <summary>
    /// Which poller wrote this. Readers that must decide whether the cache beats their own copy of
    /// the same console data need it: age cannot separate the sources, since a console write is
    /// zero seconds old at the moment it lands.
    /// </summary>
    public WifiClientSource Source { get; init; } = WifiClientSource.Console;

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

    /// <summary>PON activation state, influx-encoded (e.g. "operation"), from an attached
    /// supplemental ONT provider. Null when the module has no PON supplement.</summary>
    public string? PonLinkStatus { get; init; }
    /// <summary>Absolute (cumulative) BIP error count from the PON supplement.</summary>
    public long? BipErrors { get; init; }
    /// <summary>Absolute (cumulative) uncorrectable FEC codewords from the PON supplement.</summary>
    public long? FecErrors { get; init; }
    /// <summary>Absolute (cumulative) uncorrectable HEC header errors - the always-on
    /// framing-layer error signal, meaningful even when payload FEC is disabled.</summary>
    public long? HecUncorrected { get; init; }
    /// <summary>Whether payload FEC is enabled per the OLT profile. Null = unknown. When
    /// false, the FEC counters stay 0 and HEC is the live error signal to show instead.</summary>
    public bool? FecEnabled { get; init; }
    /// <summary>Absolute (cumulative) dropped GEM frames from the PON supplement.</summary>
    public long? GemRxDropped { get; init; }
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
/// The console's per-client rate in the client's frame: <see cref="DownBps"/> is what it received.
/// The gateway's measurement, so WAN traffic only, and behind the moment by the console's own
/// reporting delay.
/// </summary>
public readonly record struct ConsoleWanRate(double DownBps, double UpBps, DateTime At)
{
    /// <summary>Internal: this reading is a prior one held through a single zero poll.</summary>
    public bool HeldZero { get; init; }
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
