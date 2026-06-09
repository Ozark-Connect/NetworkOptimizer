using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.UniFi.Models;
using NetworkOptimizer.Web.Services.Monitoring;
using NetworkOptimizer.WiFi.Models;

namespace NetworkOptimizer.Web.Services.LanFlowMap;

/// <summary>
/// Single source of truth feeding the 3D LAN flow map (spec 5.7). Assembles the
/// topology graph, projects AP placement coordinates (from our ApMapService, not
/// UniFi), pre-resolves direction mapping per spec 5.7.1, and surfaces live + historic
/// rate data the JS layer can paint without rederiving anything.
/// </summary>
public class LanFlowMapService
{
    private readonly IUniFiClientProvider _connection;
    private readonly INetworkPathAnalyzer _pathAnalyzer;
    private readonly MonitoringLiveStats _liveStats;
    private readonly MonitoringInfluxClient _influx;
    private readonly MonitoringPathView _pathView;
    private readonly ApMapService _apMap;
    private readonly LanFlowMapCache _cache;
    private readonly IDbContextFactory<NetworkOptimizerDbContext> _dbFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<LanFlowMapService> _logger;

    public LanFlowMapService(
        IUniFiClientProvider connection,
        INetworkPathAnalyzer pathAnalyzer,
        MonitoringLiveStats liveStats,
        MonitoringInfluxClient influx,
        MonitoringPathView pathView,
        ApMapService apMap,
        LanFlowMapCache cache,
        IDbContextFactory<NetworkOptimizerDbContext> dbFactory,
        ILoggerFactory loggerFactory,
        ILogger<LanFlowMapService> logger)
    {
        _connection = connection;
        _pathAnalyzer = pathAnalyzer;
        _liveStats = liveStats;
        _influx = influx;
        _pathView = pathView;
        _apMap = apMap;
        _cache = cache;
        _dbFactory = dbFactory;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    /// <summary>
    /// Returns a fresh snapshot or the cached one if still inside the TTL. Browsers
    /// call this on map mount; the /live endpoint never rebuilds, it only refreshes
    /// rates on top of the cached snapshot.
    /// </summary>
    public Task<LanFlowMapSnapshot> BuildSnapshotAsync(CancellationToken ct = default)
        => _cache.BuildOrGetAsync(BuildSnapshotInternalAsync, ct);

    /// <summary>Force the next snapshot read to rebuild (e.g. on controller reconnect).</summary>
    public void InvalidateCache() => _cache.Invalidate();

    private async Task<LanFlowMapSnapshot> BuildSnapshotInternalAsync(CancellationToken ct)
    {
        var snapshot = new LanFlowMapSnapshot { GeneratedAt = DateTime.UtcNow };

        if (!_connection.IsConnected || _connection.Client == null)
        {
            return snapshot;
        }

        var discovery = new UniFiDiscovery(_connection.Client, _loggerFactory.CreateLogger<UniFiDiscovery>());
        var topology = await discovery.DiscoverTopologyAsync(ct);

        var markers = await _apMap.GetApMapMarkersAsync();

        // Load non-AP device placements (switches, gateways) from the same table.
        using var db = await _dbFactory.CreateDbContextAsync();
        var allLocations = await db.ApLocations.ToListAsync(ct);
        var apMacs = new HashSet<string>(
            markers.Select(m => m.Mac.ToLowerInvariant()),
            StringComparer.OrdinalIgnoreCase);
        var deviceLocations = allLocations
            .Where(l => !apMacs.Contains(l.ApMac.ToLowerInvariant()))
            .ToList();

        var anchors = ProjectAnchors(markers, deviceLocations,
            out var centerLat, out var centerLng, out var lngScale);
        snapshot.Bounds = ComputeBounds(anchors, centerLat, centerLng, lngScale);
        snapshot.Buildings = await BuildBuildingsAsync(centerLat, centerLng, lngScale, ct);
        snapshot.MaterialColors = new Dictionary<string, string>(
            WiFi.Data.MaterialAttenuation.MaterialColors, StringComparer.OrdinalIgnoreCase);
        CompactBuildingFloors(snapshot.Buildings, anchors);

        var nameMaps = await LoadInterfaceNameMaps(ct);

        // Raw device list with PortTable for direct UniFi-side port speed/name lookups.
        // Used as the immediate-fallback path for wired client link speed when the
        // SNMP slow tier hasn't populated the name map yet, and for surfacing the
        // switch port label in the wired client node tooltip.
        List<NetworkOptimizer.UniFi.Models.UniFiDeviceResponse> rawDevices;
        try
        {
            rawDevices = (await _connection.Client!.GetDevicesAsync(ct))?.ToList()
                         ?? new List<NetworkOptimizer.UniFi.Models.UniFiDeviceResponse>();
        }
        catch
        {
            rawDevices = new List<NetworkOptimizer.UniFi.Models.UniFiDeviceResponse>();
        }
        var rawByMac = rawDevices
            .Where(d => !string.IsNullOrEmpty(d.Mac))
            .ToDictionary(d => NormalizeMac(d.Mac), d => d, StringComparer.OrdinalIgnoreCase);

        // Mount type lookup so AP nodes carry their mount position for 3D vertical offset
        var mountTypes = markers
            .Where(m => !string.IsNullOrEmpty(m.MountType))
            .ToDictionary(m => NormalizeMac(m.Mac), m => m.MountType, StringComparer.OrdinalIgnoreCase);

        BuildInfrastructureGraph(topology, anchors, snapshot, nameMaps);

        foreach (var node in snapshot.Nodes)
        {
            if (node.Kind == LanNodeKind.AccessPoint && node.Mac != null
                && mountTypes.TryGetValue(node.Mac, out var mt))
            {
                node.MountType = mt;
            }
        }

        BuildClientLeaves(topology, anchors, snapshot, nameMaps, rawByMac);
        GroupMultiClientPorts(snapshot);
        await BuildWanAndClouds(topology, snapshot, ct);

        // WAN interface names for InfluxDB rate queries. Include both physical
        // and uplink names so PPPoE (ppp*) is covered when the physical port
        // has no active counters.
        var wans = await _pathView.GetWansAsync(ct);
        snapshot.WanIfNames = wans
            .SelectMany(w => new[] { w.PhysicalIfName, w.UplinkIfName })
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .Distinct()
            .ToList();

        var portRates = await SeedPortRatesAsync(snapshot, ct);
        SeedLiveRates(snapshot, portRates);

        snapshot.SpeedTests = await BuildSpeedTestOverlayAsync(
            since: snapshot.GeneratedAt - TimeSpan.FromDays(30),
            until: snapshot.GeneratedAt,
            limitPerKind: 3,
            ct: ct);

        return snapshot;
    }

    /// <summary>
    /// Polling endpoint. Refreshes link rates + per-device aggregates + cloud RTT
    /// from in-memory sources (<see cref="MonitoringLiveStats"/>, <see cref="MonitoringPathView.GetWansAsync"/>).
    /// Does NOT rebuild the snapshot topology - that happens on its own TTL inside the cache.
    /// </summary>
    public async Task<LanFlowMapLiveUpdate> GetLiveUpdateAsync(CancellationToken ct = default)
    {
        var update = new LanFlowMapLiveUpdate { AsOf = DateTime.UtcNow };
        if (!_connection.IsConnected) return update;

        // Read the cached snapshot or trigger its first build. Subsequent live ticks
        // will short-circuit on the freshness check inside the cache.
        var snapshot = await BuildSnapshotAsync(ct);

        // Fresh WAN rates per WAN link (the agent's per-port rate cache feeds WanSummary,
        // so this is cheap).
        var wans = await _pathView.GetWansAsync(ct);
        var wanByInterface = wans.ToDictionary(w => w.WanInterface, StringComparer.OrdinalIgnoreCase);

        foreach (var link in snapshot.Links)
        {
            // Pull fresh rates depending on link kind.
            LinkLiveRates? rates = null;
            if (link.Kind == LanLinkKind.Wan)
            {
                // WAN ID format: "wan-link-{wanInterface}". Recover the interface name.
                var wanIface = link.Id.StartsWith("wan-link-", StringComparison.Ordinal)
                    ? link.Id.Substring("wan-link-".Length)
                    : null;
                if (wanIface != null && wanByInterface.TryGetValue(wanIface, out var wan))
                {
                    // Per the empirical convention shared with the rest of the
                    // post-process (see the AP badge / trunk-link work):
                    // LiveRateInBps is uploads, LiveRateOutBps is downloads.
                    // The WAN link is oriented cloud (From) -> gateway (To),
                    // so DownstreamBps = downloads (cloud -> gateway) and
                    // UpstreamBps = uploads (gateway -> cloud).
                    rates = new LinkLiveRates
                    {
                        DownstreamBps = wan.LiveRateOutBps ?? 0,
                        UpstreamBps = wan.LiveRateInBps ?? 0,
                        AsOf = update.AsOf,
                    };
                }
            }
            else if (link.Kind == LanLinkKind.WifiClient)
            {
                var clientMac = ExtractWifiClientMacFromLinkId(link.Id);
                if (!string.IsNullOrEmpty(clientMac))
                {
                    var snap = _liveStats.GetWifiClient(clientMac);
                    if (snap != null)
                    {
                        rates = new LinkLiveRates
                        {
                            DownstreamBps = snap.TxThroughputBps ?? 0,
                            UpstreamBps = snap.RxThroughputBps ?? 0,
                            AsOf = snap.LastUpdate,
                        };
                    }
                }
            }
            else if (link.Kind == LanLinkKind.Uplink || link.Kind == LanLinkKind.MeshBackhaul)
            {
                // Primary: parent's trunk port via PortKey (same SNMP-fed path
                // that wired client links use, 5 s cadence).
                if (!string.IsNullOrEmpty(link.PortKey))
                {
                    var (parentMac, pIfName) = ParsePortKey(link.PortKey);
                    var portRate = _liveStats.GetPortRate(parentMac, pIfName);
                    if (portRate != null)
                    {
                        rates = new LinkLiveRates
                        {
                            DownstreamBps = portRate.DownBps,
                            UpstreamBps = portRate.UpBps,
                            AsOf = portRate.LastUpdate,
                        };
                    }
                }
                // Fallback: child's own uplink port.
                if (rates == null)
                {
                    var childDev = ExtractDeviceMacFromUplinkId(link.Id);
                    if (!string.IsNullOrEmpty(childDev))
                    {
                        var childNode = snapshot.Nodes.FirstOrDefault(n =>
                            string.Equals(n.Mac, childDev, StringComparison.OrdinalIgnoreCase));
                        if (childNode?.UplinkIfName != null)
                        {
                            var portRate = _liveStats.GetPortRate(childDev, childNode.UplinkIfName);
                            if (portRate != null)
                            {
                                rates = new LinkLiveRates
                                {
                                    DownstreamBps = portRate.UpBps,
                                    UpstreamBps = portRate.DownBps,
                                    AsOf = portRate.LastUpdate,
                                };
                            }
                        }
                    }
                }
                // Last resort: device-level aggregate (covers APs whose radio
                // interfaces don't map to per-port SNMP counters).
                if (rates == null)
                {
                    var childDev = ExtractDeviceMacFromUplinkId(link.Id);
                    if (!string.IsNullOrEmpty(childDev))
                    {
                        var stats = _liveStats.GetForDevice(childDev);
                        if (stats != null && stats.LastRateUpdate.HasValue)
                        {
                            rates = new LinkLiveRates
                            {
                                DownstreamBps = stats.RateOutBps ?? 0,
                                UpstreamBps = stats.RateInBps ?? 0,
                                AsOf = stats.LastRateUpdate.Value,
                            };
                        }
                    }
                }
            }
            else if (link.Kind == LanLinkKind.WiredClient)
            {
                // Primary: parent switch port via SNMP (PortKey).
                if (!string.IsNullOrEmpty(link.PortKey))
                {
                    var (parentMac, ifName) = ParsePortKey(link.PortKey);
                    if (!string.IsNullOrEmpty(parentMac) && !string.IsNullOrEmpty(ifName))
                    {
                        var portRate = _liveStats.GetPortRate(parentMac, ifName);
                        if (portRate != null)
                        {
                            rates = new LinkLiveRates
                            {
                                DownstreamBps = portRate.DownBps,
                                UpstreamBps = portRate.UpBps,
                                AsOf = portRate.LastUpdate,
                            };
                        }
                    }
                }
                // Fallback: UniFi client stats (for switches without SNMP).
                // TX from the client's perspective = upload = upstream on the link.
                if (rates == null)
                {
                    var clientMac = ExtractWiredClientMacFromLinkId(link.Id);
                    if (!string.IsNullOrEmpty(clientMac))
                    {
                        var wc = _liveStats.GetWiredClient(clientMac);
                        if (wc != null)
                        {
                            rates = new LinkLiveRates
                            {
                                DownstreamBps = wc.TxThroughputBps ?? 0,
                                UpstreamBps = wc.RxThroughputBps ?? 0,
                                AsOf = wc.LastUpdate,
                            };
                        }
                    }
                }
            }
            // Transit cloud-to-cloud edges don't have SNMP data; they keep snapshot rates.

            if (rates != null) update.LinkRates[link.Id] = rates;
        }

        // Per-device aggregate badges from the in-memory live stats.
        foreach (var node in snapshot.Nodes)
        {
            if (string.IsNullOrEmpty(node.Mac)) continue;
            var dev = _liveStats.GetForDevice(node.Mac);
            if (dev == null) continue;

            // For SNMP-free switches the only aggregate we have is the parent
            // switch's port rate (RateIn/RateOut), and parent-port direction
            // doesn't always map cleanly to the child's fabric ingress/egress
            // (LAGs, multiple uplinks, port_table direction quirks). Switches
            // WITH SNMP write FabricIngress/Egress directly from sum(rx)/sum(tx)
            // and don't hit this fallback. Show magnitude on both axes so the
            // floating label says "this much is moving, direction unknown"
            // instead of confidently flipping ingress and egress. The trunk
            // LINK rate keeps its direction-aware values - those are read from
            // dev.RateInBps/RateOutBps before they reach this clamp.
            var aggIn = dev.RateInBps;
            var aggOut = dev.RateOutBps;
            if (node.Kind == LanNodeKind.Switch
                && !dev.FabricIngressBps.HasValue
                && !dev.FabricEgressBps.HasValue
                && aggIn.HasValue && aggOut.HasValue)
            {
                var mag = Math.Max(aggIn.Value, aggOut.Value);
                aggIn = mag;
                aggOut = mag;
            }

            update.NodeBadges[node.Id] = new NodeLiveBadge
            {
                AggregateInBps = aggIn,
                AggregateOutBps = aggOut,
                FabricIngressBps = dev.FabricIngressBps,
                FabricEgressBps = dev.FabricEgressBps,
                Online = node.Online,
                CpuPercent = dev.CpuPercent,
                MemoryUsedPercent = dev.MemoryUsedPercent,
                TemperatureC = dev.TemperatureC,
                UptimeSeconds = dev.UptimeSeconds,
            };
        }

        // Cloud RTT: pick the lowest RTT across all access hop targets for this
        // WAN so the globe shows the nearest ISP infrastructure latency, not a
        // deeper transit hop that happens to be last in the wizard ordering.
        foreach (var cloud in snapshot.Clouds)
        {
            double? rtt = cloud.RttAvgMs;
            double? loss = cloud.LossPercent;
            bool success = rtt.HasValue;

            if (cloud.RttTargetIds.Count > 0)
            {
                double? bestRtt = null;
                double? bestLoss = null;
                foreach (var targetId in cloud.RttTargetIds)
                {
                    var live = _liveStats.GetTargetStats(targetId);
                    if (live?.RttAvgMs != null && (bestRtt == null || live.RttAvgMs.Value < bestRtt.Value))
                    {
                        bestRtt = live.RttAvgMs;
                        bestLoss = live.LossPercent;
                    }
                }
                if (bestRtt.HasValue)
                {
                    rtt = bestRtt;
                    loss = bestLoss;
                    success = true;
                }
            }

            update.CloudStats[cloud.Id] = new CloudLiveStats
            {
                RttAvgMs = rtt,
                LossPercent = loss,
                Success = success,
            };
        }

        return update;
    }

    /// <summary>
    /// Historic snapshot for the timeline scrubber. Queries InfluxDB at the requested
    /// instant +/- a small window matching the fast-tier interval (5 s).
    /// </summary>
    public async Task<LanFlowMapHistoricUpdate> GetHistoricUpdateAsync(DateTime at, CancellationToken ct = default)
    {
        var update = new LanFlowMapHistoricUpdate { At = at };
        if (!_connection.IsConnected) return update;

        var snapshot = await BuildSnapshotAsync(ct);

        var gwNode = snapshot.Nodes.FirstOrDefault(n => n.Kind == LanNodeKind.Gateway);
        var gwMac = gwNode?.Mac;

        // Build WAN interface → ifname candidates for per-WAN rate queries.
        var wanIfNameMap = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var wans = await _pathView.GetWansAsync(ct);
            foreach (var w in wans)
            {
                var candidates = new[] { w.PhysicalIfName, w.UplinkIfName }
                    .Where(n => !string.IsNullOrEmpty(n))
                    .Select(n => n!)
                    .Distinct()
                    .ToArray();
                if (candidates.Length > 0)
                    wanIfNameMap[w.WanInterface] = candidates;
            }
        }
        catch { }

        // Reuse cached InfluxDB results when the requested time falls within
        // the previously fetched window. Fetches 5 min ahead so forward
        // playback goes ~4 min before needing another round-trip.
        var cached = _cache.HistoricData;
        if (cached == null || at < cached.From || at > cached.To - TimeSpan.FromSeconds(30))
        {
            cached = await FetchHistoricDataAsync(at, snapshot, gwMac, ct);
            _cache.HistoricData = cached;
        }

        var ratesByDevice = cached.RatesByDevice;
        var from = cached.From;
        var to = cached.To;

        // Resolve closest client throughput points from cached data.
        var wifiClientRates = new Dictionary<string, MonitoringInfluxClient.ClientThroughputPoint>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in cached.WifiClients)
        {
            if (string.IsNullOrEmpty(p.ClientMac)) continue;
            if (!wifiClientRates.TryGetValue(p.ClientMac, out var existing)
                || Math.Abs((p.Time - at).TotalMilliseconds) < Math.Abs((existing.Time - at).TotalMilliseconds))
                wifiClientRates[p.ClientMac] = p;
        }
        var wiredClientRates = new Dictionary<string, MonitoringInfluxClient.ClientThroughputPoint>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in cached.WiredClients)
        {
            if (string.IsNullOrEmpty(p.ClientMac)) continue;
            if (!wiredClientRates.TryGetValue(p.ClientMac, out var existing)
                || Math.Abs((p.Time - at).TotalMilliseconds) < Math.Abs((existing.Time - at).TotalMilliseconds))
                wiredClientRates[p.ClientMac] = p;
        }

        // Resolve each link, mirroring the live endpoint's kind-aware dispatch.
        foreach (var link in snapshot.Links)
        {
            try
            {
                if (link.Kind == LanLinkKind.Wan)
                {
                    // Per-WAN: extract interface name from link ID, look up
                    // the physical ifname, then find matching rate points.
                    var wanIface = link.Id.StartsWith("wan-link-", StringComparison.Ordinal)
                        ? link.Id.Substring("wan-link-".Length) : null;
                    if (wanIface != null
                        && wanIfNameMap.TryGetValue(wanIface, out var rateIfs)
                        && !string.IsNullOrEmpty(gwMac)
                        && ratesByDevice.TryGetValue(gwMac, out var gwRates))
                    {
                        MonitoringInfluxClient.InterfaceRatePoint? closest = null;
                        foreach (var rateIf in rateIfs)
                        {
                            closest = gwRates
                                .Where(p => string.Equals(p.IfName, rateIf, StringComparison.OrdinalIgnoreCase))
                                .OrderBy(p => Math.Abs((p.Time - at).TotalMilliseconds))
                                .FirstOrDefault();
                            if (closest != null) break;
                        }
                        if (closest != null)
                        {
                            // rate_in_bps = downloads, rate_out_bps = uploads
                            update.LinkRates[link.Id] = MapPortToLinkRates(link,
                                closest.RateInBps ?? 0, closest.RateOutBps ?? 0, closest.Time);
                        }
                    }
                }
                else if (link.Kind == LanLinkKind.Uplink || link.Kind == LanLinkKind.MeshBackhaul)
                {
                    MonitoringInfluxClient.InterfaceRatePoint? resolved = null;
                    bool fromChildSide = false;

                    // Primary: parent's trunk port via PortKey.
                    if (!string.IsNullOrEmpty(link.PortKey))
                    {
                        var (pMac, pIf) = ParsePortKey(link.PortKey);
                        if (ratesByDevice.TryGetValue(pMac, out var pPts))
                        {
                            resolved = pPts
                                .Where(p => string.Equals(p.IfName, pIf, StringComparison.OrdinalIgnoreCase))
                                .OrderBy(p => Math.Abs((p.Time - at).TotalMilliseconds))
                                .FirstOrDefault();
                        }
                    }

                    // Fallback: child device's own interface. Covers mesh APs
                    // (vwiresta) and switches whose parent (e.g., a mesh AP)
                    // doesn't expose SNMP port data. The live code does the
                    // same at ComputePortRate(dev.Mac, dev.Uplink.PortIdx).
                    if (resolved == null)
                    {
                        var childMac = ExtractDeviceMacFromUplinkId(link.Id);
                        if (!string.IsNullOrEmpty(childMac) && ratesByDevice.TryGetValue(childMac, out var cPts))
                        {
                            if (link.Kind == LanLinkKind.MeshBackhaul)
                            {
                                resolved = cPts
                                    .Where(p => p.IfName.StartsWith("vwiresta", StringComparison.OrdinalIgnoreCase)
                                        && !p.IfName.Contains('.'))
                                    .OrderBy(p => Math.Abs((p.Time - at).TotalMilliseconds))
                                    .FirstOrDefault();
                            }
                            else
                            {
                                // Wired switch fallback: find the child's uplink port.
                                // On switches SNMP ifDescr is "Port N" and the uplink
                                // is the highest-rate port. Use the same closest-time
                                // point from the child's interface set; the direction
                                // swaps because we're reading from the other end.
                                var childNode = snapshot.Nodes.FirstOrDefault(n =>
                                    string.Equals(n.Mac, childMac, StringComparison.OrdinalIgnoreCase));
                                if (childNode?.UplinkIfName != null)
                                {
                                    resolved = cPts
                                        .Where(p => string.Equals(p.IfName, childNode.UplinkIfName, StringComparison.OrdinalIgnoreCase))
                                        .OrderBy(p => Math.Abs((p.Time - at).TotalMilliseconds))
                                        .FirstOrDefault();
                                    fromChildSide = true;
                                }
                            }
                        }
                    }

                    if (resolved != null)
                    {
                        if (link.Kind == LanLinkKind.MeshBackhaul && !fromChildSide)
                        {
                            // vwiresta rateIn = downloads, rateOut = uploads
                            update.LinkRates[link.Id] = new LinkLiveRates
                            {
                                DownstreamBps = resolved.RateInBps ?? 0,
                                UpstreamBps = resolved.RateOutBps ?? 0,
                                AsOf = resolved.Time,
                            };
                        }
                        else if (fromChildSide)
                        {
                            // Reading from child side: directions swap vs parent side.
                            // Child port RX = bytes arriving from parent = downstream.
                            // Child port TX = bytes leaving toward parent = upstream.
                            update.LinkRates[link.Id] = new LinkLiveRates
                            {
                                DownstreamBps = resolved.RateInBps ?? 0,
                                UpstreamBps = resolved.RateOutBps ?? 0,
                                AsOf = resolved.Time,
                            };
                        }
                        else
                        {
                            update.LinkRates[link.Id] = MapPortToLinkRates(link, resolved.RateInBps ?? 0, resolved.RateOutBps ?? 0, resolved.Time);
                        }
                    }
                }
                else if (link.Kind == LanLinkKind.WiredClient)
                {
                    // Primary: SNMP port rate via PortKey
                    LinkLiveRates? rates = null;
                    if (!string.IsNullOrEmpty(link.PortKey))
                    {
                        var (deviceMac, ifName) = ParsePortKey(link.PortKey);
                        if (ratesByDevice.TryGetValue(deviceMac, out var pts))
                        {
                            var closest = pts
                                .Where(p => string.Equals(p.IfName, ifName, StringComparison.OrdinalIgnoreCase))
                                .OrderBy(p => Math.Abs((p.Time - at).TotalMilliseconds))
                                .FirstOrDefault();
                            if (closest != null)
                                rates = MapPortToLinkRates(link, closest.RateInBps ?? 0, closest.RateOutBps ?? 0, closest.Time);
                        }
                    }
                    // Fallback: wired_client from batch pre-fetch
                    if (rates == null)
                    {
                        var clientMac = ExtractWiredClientMacFromLinkId(link.Id);
                        if (!string.IsNullOrEmpty(clientMac) && wiredClientRates.TryGetValue(clientMac, out var wp))
                        {
                            rates = new LinkLiveRates
                            {
                                DownstreamBps = wp.TxThroughputBps ?? 0,
                                UpstreamBps = wp.RxThroughputBps ?? 0,
                                AsOf = wp.Time,
                            };
                        }
                    }
                    if (rates != null) update.LinkRates[link.Id] = rates;
                }
                else if (link.Kind == LanLinkKind.WifiClient)
                {
                    var clientMac = ExtractWifiClientMacFromLinkId(link.Id);
                    if (!string.IsNullOrEmpty(clientMac) && wifiClientRates.TryGetValue(clientMac, out var wp))
                    {
                        update.LinkRates[link.Id] = new LinkLiveRates
                        {
                            DownstreamBps = wp.TxThroughputBps ?? 0,
                            UpstreamBps = wp.RxThroughputBps ?? 0,
                            AsOf = wp.Time,
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Historic rate failed for link {Id}", link.Id);
            }
        }

        // Node badges: device health + fabric/aggregate rates at the historic
        // instant. Matches the live endpoint's badge population logic:
        //   - Switches/gateways: fabricIngressBps = sum(port Rx), fabricEgressBps = sum(port Tx)
        //   - APs: aggregateInBps/OutBps from the uplink link rate (already computed above)
        // Without fabric rates the JS falls back to summing adjacent links,
        // which double-counts flows traversing the device.
        foreach (var node in snapshot.Nodes)
        {
            if (string.IsNullOrEmpty(node.Mac)) continue;
            var mac = node.Mac;
            try
            {
                var healthPt = cached.HealthByDevice.TryGetValue(mac, out var healthPts)
                    ? healthPts.OrderBy(p => Math.Abs((p.Time - at).TotalMilliseconds)).FirstOrDefault()
                    : null;

                double? fabIn = null, fabOut = null;
                if ((node.Kind == LanNodeKind.Switch || node.Kind == LanNodeKind.Gateway)
                    && ratesByDevice.TryGetValue(mac, out var rates))
                {
                    var isGw = node.Kind == LanNodeKind.Gateway;
                    var filtered = isGw
                        ? rates.Where(p => System.Text.RegularExpressions.Regex.IsMatch(p.IfName, @"^eth\d+$"))
                        : rates;
                    var closestRates = filtered
                        .GroupBy(p => p.Time)
                        .OrderBy(g => Math.Abs((g.Key - at).TotalMilliseconds))
                        .FirstOrDefault();
                    if (closestRates != null)
                    {
                        double sumRx = 0, sumTx = 0;
                        foreach (var r in closestRates)
                        {
                            sumRx += r.RateInBps ?? 0;
                            sumTx += r.RateOutBps ?? 0;
                        }
                        fabIn = sumRx;
                        fabOut = sumTx;
                    }
                }

                // For APs, pull aggregate from the uplink link rate we already computed.
                double? aggIn = null, aggOut = null;
                if (node.Kind == LanNodeKind.AccessPoint)
                {
                    var uplinkId = $"uplink-{mac}";
                    if (update.LinkRates.TryGetValue(uplinkId, out var uplinkRate))
                    {
                        aggIn = uplinkRate.UpstreamBps;
                        aggOut = uplinkRate.DownstreamBps;
                    }
                }

                if (healthPt == null && fabIn == null && aggIn == null) continue;

                update.NodeBadges[node.Id] = new NodeLiveBadge
                {
                    Online = node.Online,
                    CpuPercent = healthPt?.CpuPercent,
                    MemoryUsedPercent = healthPt?.MemoryUsedPercent,
                    TemperatureC = healthPt?.TemperatureC,
                    UptimeSeconds = healthPt?.UptimeSeconds,
                    FabricIngressBps = fabIn,
                    FabricEgressBps = fabOut,
                    AggregateInBps = aggIn,
                    AggregateOutBps = aggOut,
                };
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Historic badge failed for {Mac}", mac);
            }
        }

        // Cloud latency: map cloud kind to monitoring target type and query.
        foreach (var cloud in snapshot.Clouds)
        {
            try
            {
                var targetType = cloud.Kind switch
                {
                    LanCloudKind.AccessIsp => MonitoringTargetType.AccessIsp,
                    LanCloudKind.Transit => MonitoringTargetType.Transit,
                    _ => (MonitoringTargetType?)null
                };
                if (targetType == null) continue;
                MonitoringInfluxClient.LatencyPoint? best = null;
                if (cached.LatencyByTargetType.TryGetValue(targetType.Value, out var latPts))
                {
                    best = latPts
                        .OrderBy(p => Math.Abs((p.Time - at).TotalMilliseconds))
                        .FirstOrDefault();
                }
                if (best == null) continue;
                update.CloudStats[cloud.Id] = new CloudLiveStats
                {
                    RttAvgMs = best.RttAvgMs,
                    LossPercent = best.LossPercent,
                    Success = best.RttAvgMs.HasValue,
                };
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Historic latency failed for cloud {Id}", cloud.Id);
            }
        }

        update.SpeedTests = await BuildSpeedTestOverlayAsync(from, to, limitPerKind: 5, ct: ct);
        return update;
    }

    // ---------------------------------------------------------------------------------
    // Internal: AP placement -> local Cartesian
    // ---------------------------------------------------------------------------------

    private const double EarthRadiusMetres = 6_371_000.0;
    private const double FloorHeightMetres = 2.9;

    private static (double x, double y) ProjectLatLng(
        double lat, double lng, double centerLat, double centerLng, double lngScale)
    {
        double x = (lng - centerLng) * Math.PI / 180.0 * lngScale * EarthRadiusMetres;
        double y = (lat - centerLat) * Math.PI / 180.0 * EarthRadiusMetres;
        return (x, y);
    }

    private static Dictionary<string, LanPlacement> ProjectAnchors(
        IReadOnlyList<Web.Models.ApMapMarker> markers,
        IReadOnlyList<Storage.Models.ApLocation> deviceLocations,
        out double centerLat, out double centerLng, out double lngScale)
    {
        centerLat = 0;
        centerLng = 0;
        lngScale = 1;

        var anchors = new Dictionary<string, LanPlacement>();
        var withCoords = markers
            .Where(m => m.Latitude.HasValue && m.Longitude.HasValue)
            .ToList();
        if (withCoords.Count == 0 && deviceLocations.Count == 0) return anchors;

        // Centroid is computed from AP markers only so that repositioning a
        // switch/gateway doesn't shift the AP reference frame.
        if (withCoords.Count > 0)
        {
            centerLat = withCoords.Average(m => m.Latitude!.Value);
            centerLng = withCoords.Average(m => m.Longitude!.Value);
        }
        else
        {
            centerLat = deviceLocations.Average(d => d.Latitude);
            centerLng = deviceLocations.Average(d => d.Longitude);
        }

        lngScale = Math.Cos(centerLat * Math.PI / 180.0);

        foreach (var m in withCoords)
        {
            var (x, y) = ProjectLatLng(m.Latitude!.Value, m.Longitude!.Value, centerLat, centerLng, lngScale);
            anchors[NormalizeMac(m.Mac)] = new LanPlacement
            {
                X = x,
                Y = y,
                Z = (m.Floor ?? 1) * FloorHeightMetres,
                Source = LanPlacementSource.Anchor,
            };
        }

        foreach (var d in deviceLocations)
        {
            var mac = NormalizeMac(d.ApMac);
            if (anchors.ContainsKey(mac)) continue;
            var (x, y) = ProjectLatLng(d.Latitude, d.Longitude, centerLat, centerLng, lngScale);
            anchors[mac] = new LanPlacement
            {
                X = x,
                Y = y,
                Z = (d.Floor ?? 1) * FloorHeightMetres,
                Source = LanPlacementSource.Anchor,
            };
        }

        return anchors;
    }

    private static LanFlowMapBounds ComputeBounds(
        Dictionary<string, LanPlacement> anchors,
        double centerLat, double centerLng, double lngScale)
    {
        var bounds = new LanFlowMapBounds
        {
            AnchorCount = anchors.Count,
        };
        if (anchors.Count == 0)
        {
            bounds.Radius = 1.0;
            return bounds;
        }
        double maxR = 0;
        foreach (var p in anchors.Values)
        {
            var r = Math.Sqrt(p.X * p.X + p.Y * p.Y + p.Z * p.Z);
            if (r > maxR) maxR = r;
        }
        bounds.Radius = Math.Max(maxR, 1.0);
        bounds.CenterLat = centerLat;
        bounds.CenterLng = centerLng;
        bounds.LngScale = lngScale;
        return bounds;
    }

    private async Task<List<LanBuilding>> BuildBuildingsAsync(
        double centerLat, double centerLng, double lngScale, CancellationToken ct)
    {
        var result = new List<LanBuilding>();
        try
        {
            using var db = await _dbFactory.CreateDbContextAsync(ct);
            var buildings = await db.Buildings.Include(b => b.Floors).ToListAsync(ct);

            var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

            foreach (var building in buildings)
            {
                var lanBuilding = new LanBuilding
                {
                    Id = building.Id,
                    Name = building.Name,
                };

                foreach (var floor in building.Floors)
                {
                    if (string.IsNullOrWhiteSpace(floor.WallsJson) || floor.WallsJson == "[]")
                        continue;

                    List<PropagationWall>? walls;
                    try
                    {
                        walls = JsonSerializer.Deserialize<List<PropagationWall>>(floor.WallsJson, jsonOptions);
                    }
                    catch
                    {
                        continue;
                    }
                    if (walls == null || walls.Count == 0) continue;

                    var (swX, swY) = ProjectLatLng(floor.SwLatitude, floor.SwLongitude, centerLat, centerLng, lngScale);
                    var (neX, neY) = ProjectLatLng(floor.NeLatitude, floor.NeLongitude, centerLat, centerLng, lngScale);

                    var lanFloor = new LanBuildingFloor
                    {
                        FloorNumber = floor.FloorNumber,
                        FloorMaterial = floor.FloorMaterial ?? "floor_wood",
                        SwX = swX,
                        SwY = swY,
                        NeX = neX,
                        NeY = neY,
                        Z = floor.FloorNumber * FloorHeightMetres,
                    };

                    foreach (var wall in walls)
                    {
                        if (wall.Points.Count < 2) continue;
                        var lanWall = new LanWall
                        {
                            Material = wall.Material,
                            Materials = wall.Materials?.Select(m => (string?)m).ToList(),
                        };
                        foreach (var pt in wall.Points)
                        {
                            var (px, py) = ProjectLatLng(pt.Lat, pt.Lng, centerLat, centerLng, lngScale);
                            lanWall.Points.Add(new LanWallPoint { X = px, Y = py });
                        }
                        lanFloor.Walls.Add(lanWall);
                    }

                    lanBuilding.Floors.Add(lanFloor);
                }

                if (lanBuilding.Floors.Count > 0)
                    result.Add(lanBuilding);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to load buildings for 3D map");
        }
        return result;
    }

    private static void CompactBuildingFloors(
        List<LanBuilding> buildings, Dictionary<string, LanPlacement> anchors)
    {
        foreach (var building in buildings)
        {
            var floorNums = building.Floors.Select(f => f.FloorNumber).OrderBy(n => n).ToList();
            if (floorNums.Count < 2) continue;

            bool hasGap = false;
            for (int i = 1; i < floorNums.Count; i++)
            {
                if (floorNums[i] - floorNums[i - 1] > 1) { hasGap = true; break; }
            }
            if (!hasGap) continue;

            // Anchor from the top floor and compact downward so upper floors stay
            // level with the same floor in other buildings.
            var zMap = new Dictionary<int, double>();
            int topFloor = floorNums[^1];
            for (int i = 0; i < floorNums.Count; i++)
            {
                int distFromTop = floorNums.Count - 1 - i;
                zMap[floorNums[i]] = (topFloor - distFromTop) * FloorHeightMetres;
            }

            foreach (var floor in building.Floors)
            {
                if (zMap.TryGetValue(floor.FloorNumber, out var newZ))
                    floor.Z = newZ;
            }

            // Adjust devices whose position falls inside this building's footprint
            double minX = building.Floors.Min(f => Math.Min(f.SwX, f.NeX));
            double maxX = building.Floors.Max(f => Math.Max(f.SwX, f.NeX));
            double minY = building.Floors.Min(f => Math.Min(f.SwY, f.NeY));
            double maxY = building.Floors.Max(f => Math.Max(f.SwY, f.NeY));

            foreach (var anchor in anchors.Values)
            {
                if (anchor.X < minX || anchor.X > maxX || anchor.Y < minY || anchor.Y > maxY)
                    continue;
                int deviceFloor = (int)Math.Round(anchor.Z / FloorHeightMetres);
                if (zMap.TryGetValue(deviceFloor, out var newDevZ))
                    anchor.Z = newDevZ;
            }
        }
    }

    // ---------------------------------------------------------------------------------
    // Internal: topology -> nodes + links
    // ---------------------------------------------------------------------------------

    private void BuildInfrastructureGraph(
        NetworkTopology topology,
        Dictionary<string, LanPlacement> anchors,
        LanFlowMapSnapshot snapshot,
        Dictionary<(string mac, int port), InterfaceNameMap> nameMaps)
    {
        // First pass: emit nodes for every device.
        foreach (var d in topology.Devices)
        {
            var mac = NormalizeMac(d.Mac);
            anchors.TryGetValue(mac, out var anchor);
            var kind = MapDeviceKind(d);
            var node = new LanNode
            {
                Id = "dev-" + mac,
                Kind = kind,
                Mac = mac,
                Ip = string.IsNullOrEmpty(d.DisplayIpAddress) ? null : d.DisplayIpAddress,
                Name = string.IsNullOrEmpty(d.Name) ? d.FriendlyModelName : d.Name,
                Model = d.FriendlyModelName,
                Placement = anchor,
                Online = d.State == 1,
            };
            if (string.Equals(d.UplinkType, "wireless", StringComparison.OrdinalIgnoreCase))
            {
                node.PhyTxKbps = d.UplinkTxRateKbps > 0 ? d.UplinkTxRateKbps : null;
                node.PhyRxKbps = d.UplinkRxRateKbps > 0 ? d.UplinkRxRateKbps : null;
                node.Band = NormalizeBand(d.UplinkRadioBand);
            }
            snapshot.Nodes.Add(node);
        }

        // Switches and gateway inherit interpolated placement from the centroid of any
        // anchored descendants. Spec 3.4: switches are interpolated and marked.
        InterpolateInteriorPlacements(snapshot, topology);

        // Second pass: uplink edges. Build them as (child -> parent), so on the wire the
        // FromNodeId is the leaf side and the data flowing toward it (DownstreamBps) is
        // gateway -> device per spec 5.7.1.
        foreach (var d in topology.Devices)
        {
            var mac = NormalizeMac(d.Mac);
            if (string.IsNullOrEmpty(d.UplinkMac)) continue;
            var parentMac = NormalizeMac(d.UplinkMac);
            if (mac == parentMac) continue;

            var isWirelessBackhaul = string.Equals(d.UplinkType, "wireless", StringComparison.OrdinalIgnoreCase);
            var link = new LanLink
            {
                Id = $"uplink-{mac}",
                FromNodeId = "dev-" + parentMac,
                ToNodeId = "dev-" + mac,
                Kind = isWirelessBackhaul ? LanLinkKind.MeshBackhaul : LanLinkKind.Uplink,
                CapacityBps = ResolveUplinkCapacityBps(d),
                Band = isWirelessBackhaul ? NormalizeBand(d.UplinkRadioBand) : null,
            };

            // For wired uplinks, the parent switch port carries the throughput we want.
            // Resolve ifName via UniFi port number -> InterfaceNameMap (3.7 chain).
            if (!isWirelessBackhaul && d.UplinkPort.HasValue && d.UplinkPort.Value > 0)
            {
                if (nameMaps.TryGetValue((parentMac, d.UplinkPort.Value), out var nameMap))
                {
                    link.PortKey = PortKey(parentMac, nameMap.IfName);
                }
            }

            snapshot.Links.Add(link);

            // Stash the child's own uplink port ifName on its node. The historic
            // endpoint uses this as a fallback when the parent doesn't expose
            // SNMP data (e.g., switch plugged into a mesh AP's Ethernet port).
            if (d.LocalUplinkPort.HasValue && d.LocalUplinkPort.Value > 0)
            {
                var childNode = snapshot.Nodes.FirstOrDefault(n => n.Id == "dev-" + mac);
                if (childNode != null && nameMaps.TryGetValue((mac, d.LocalUplinkPort.Value), out var localMap))
                {
                    childNode.UplinkIfName = localMap.IfName;
                }
            }
        }
    }

    private void BuildClientLeaves(
        NetworkTopology topology,
        Dictionary<string, LanPlacement> anchors,
        LanFlowMapSnapshot snapshot,
        Dictionary<(string mac, int port), InterfaceNameMap> nameMaps,
        Dictionary<string, NetworkOptimizer.UniFi.Models.UniFiDeviceResponse> rawByMac)
    {
        foreach (var c in topology.Clients)
        {
            var clientMac = NormalizeMac(c.Mac);
            if (string.IsNullOrEmpty(clientMac)) continue;
            if (string.IsNullOrEmpty(c.ConnectedToDeviceMac)) continue;
            var parentMac = NormalizeMac(c.ConnectedToDeviceMac);

            anchors.TryGetValue(clientMac, out var anchor);
            var nodeId = "cli-" + clientMac;
            var node = new LanNode
            {
                Id = nodeId,
                Kind = c.IsWired ? LanNodeKind.WiredClient : LanNodeKind.WifiClient,
                Mac = clientMac,
                Ip = string.IsNullOrEmpty(c.IpAddress) ? null : c.IpAddress,
                Name = ResolveClientLabel(c),
                ParentId = "dev-" + parentMac,
                Placement = anchor,
                Network = c.Network,
                IsGuest = c.IsGuest,
                Ssid = c.Essid,
            };
            if (!c.IsWired)
            {
                node.Band = NormalizeBand(c.Radio);
                node.SignalDbm = c.SignalStrength ?? c.Rssi;
                node.PhyTxKbps = c.TxRate > 0 ? c.TxRate : null;
                node.PhyRxKbps = c.RxRate > 0 ? c.RxRate : null;
            }

            var link = new LanLink
            {
                Id = $"cli-link-{clientMac}",
                FromNodeId = "dev-" + parentMac,
                ToNodeId = nodeId,
                Kind = c.IsWired ? LanLinkKind.WiredClient : LanLinkKind.WifiClient,
                Band = c.IsWired ? null : NormalizeBand(c.Radio),
            };

            if (c.IsWired && c.SwitchPort.HasValue)
            {
                // Primary: SNMP-derived InterfaceNameMap. Gives us ifName for the
                // SNMP-keyed _portRateLatest path + speed from sysSpeed.
                if (nameMaps.TryGetValue((parentMac, c.SwitchPort.Value), out var nameMap))
                {
                    link.PortKey = PortKey(parentMac, nameMap.IfName);
                    if (nameMap.SpeedMbps.HasValue && nameMap.SpeedMbps.Value > 0)
                    {
                        link.CapacityBps = (long)nameMap.SpeedMbps.Value * 1_000_000L;
                        node.WiredLinkSpeedMbps = nameMap.SpeedMbps.Value;
                    }
                    if (!string.IsNullOrEmpty(nameMap.FriendlyName))
                        node.SwitchPortName = nameMap.FriendlyName;
                }

                // Fallback: direct UniFi PortTable lookup. Runs whenever the name map
                // didn't give us speed or port name (slow tier hasn't seen this switch
                // yet, or device doesn't speak SNMP). UniFi reports negotiated Speed +
                // user-defined port Name on every device fetch - no SNMP dependency.
                if (rawByMac.TryGetValue(parentMac, out var parentDev) && parentDev.PortTable != null)
                {
                    var port = parentDev.PortTable.FirstOrDefault(p => p.PortIdx == c.SwitchPort.Value);
                    if (port != null)
                    {
                        if (!node.WiredLinkSpeedMbps.HasValue && port.Speed > 0)
                            node.WiredLinkSpeedMbps = port.Speed;
                        if (!link.CapacityBps.HasValue && port.Speed > 0)
                            link.CapacityBps = (long)port.Speed * 1_000_000L;
                        if (string.IsNullOrEmpty(node.SwitchPortName) && !string.IsNullOrEmpty(port.Name))
                            node.SwitchPortName = port.Name;
                    }
                }
            }
            else if (!c.IsWired)
            {
                // PHY rate (kbps) acts as the WiFi link capacity (spec 3.5 - PHY is capacity).
                long maxPhyKbps = Math.Max(c.TxRate, c.RxRate);
                if (maxPhyKbps > 0) link.CapacityBps = maxPhyKbps * 1_000L;
            }

            snapshot.Nodes.Add(node);
            snapshot.Links.Add(link);
        }
    }

    /// <summary>
    /// Detect wired clients that share a single physical switch port (e.g. a
    /// server with many VLAN sub-interfaces, each with its own MAC) and roll
    /// them up under a synthetic VirtualHub node. Without grouping the map
    /// fans out one fat parent-port link into N identical-looking leaves with
    /// the same throughput, which clutters the view and double-renders the
    /// port rate. With grouping, the parent's port link terminates at the
    /// hub (carrying the real port rate) and the members hang off the hub
    /// as zero-rate logical leaves.
    /// </summary>
    private void GroupMultiClientPorts(LanFlowMapSnapshot snapshot)
    {
        var leafLinkByNodeId = snapshot.Links
            .Where(l => l.Kind == LanLinkKind.WiredClient && !string.IsNullOrEmpty(l.PortKey))
            .ToDictionary(l => l.ToNodeId);

        // Group wired clients by (parentNodeId, PortKey). Only PortKey-tagged
        // leaves can be grouped - without a PortKey we don't know which
        // physical port the client sits on.
        var groups = snapshot.Nodes
            .Where(n => n.Kind == LanNodeKind.WiredClient
                && leafLinkByNodeId.ContainsKey(n.Id))
            .Select(n => (Node: n, Link: leafLinkByNodeId[n.Id]))
            .GroupBy(x => (Parent: x.Link.FromNodeId, PortKey: x.Link.PortKey!))
            .Where(g => g.Count() > 1)
            .ToList();

        foreach (var grp in groups)
        {
            var parentId = grp.Key.Parent;
            var portKey = grp.Key.PortKey;
            var members = grp.ToList();
            var representativeLink = members[0].Link;

            // Hub node sits where the port would otherwise terminate. Mac
            // is left null - the hub is synthetic, not a real device.
            var hubId = $"hub-{parentId}-{portKey}";
            var portName = members.Select(m => m.Node.SwitchPortName).FirstOrDefault(s => !string.IsNullOrEmpty(s));
            var hubNode = new LanNode
            {
                Id = hubId,
                Kind = LanNodeKind.VirtualHub,
                Name = string.IsNullOrEmpty(portName)
                    ? $"{members.Count} interfaces"
                    : $"{portName} ({members.Count})",
                ParentId = parentId,
                SwitchPortName = portName,
                WiredLinkSpeedMbps = representativeLink.CapacityBps.HasValue
                    ? (int)(representativeLink.CapacityBps.Value / 1_000_000L)
                    : (int?)null,
            };
            snapshot.Nodes.Add(hubNode);

            // Parent switch -> hub link. Takes over the PortKey + capacity so
            // the live tick reads the port rate here, not on each member.
            snapshot.Links.Add(new LanLink
            {
                Id = $"hub-link-{hubId}",
                FromNodeId = parentId,
                ToNodeId = hubId,
                Kind = LanLinkKind.WiredClient,
                PortKey = portKey,
                CapacityBps = representativeLink.CapacityBps,
            });

            // Reparent each member: leaf link now goes hub -> client, with
            // no PortKey or capacity (it's a synthetic split of the shared
            // physical port, no measurable per-MAC rate).
            foreach (var (node, leafLink) in members)
            {
                leafLink.FromNodeId = hubId;
                leafLink.PortKey = null;
                leafLink.CapacityBps = null;
                node.ParentId = hubId;
            }
        }
    }

    private async Task BuildWanAndClouds(
        NetworkTopology topology,
        LanFlowMapSnapshot snapshot,
        CancellationToken ct)
    {
        // Spec 5.7: each WAN renders as a real link off the gateway directly to the
        // access-ISP cloud. There is no intermediate WAN node - the WAN IS the link.
        // Only the primary WAN surfaces the transit-cloud chain past the access cloud.
        var wans = await _pathView.GetWansAsync(ct);
        if (wans.Count == 0) return;

        // Mark the primary WAN at snapshot level for the JS layer's speed-test fallback.
        var primary = wans.FirstOrDefault(w => w.IsPrimary) ?? wans[0];
        snapshot.PrimaryWanInterface = primary.WanInterface;

        foreach (var wan in wans)
        {
            var gwId = !string.IsNullOrEmpty(wan.GatewayMac)
                ? "dev-" + NormalizeMac(wan.GatewayMac)
                : null;
            if (string.IsNullOrEmpty(gwId)) continue;

            UpstreamPathSnapshot? upstream = null;
            try { upstream = await _pathView.GetUpstreamPathAsync(wan.WanInterface, ct); }
            catch (Exception ex) { _logger.LogDebug(ex, "Upstream path fetch failed for {Wan}", wan.WanInterface); }
            if (upstream == null) continue;

            var accessCloud = new LanCloud
            {
                Id = $"cloud-access-{wan.WanInterface}",
                Kind = LanCloudKind.AccessIsp,
                Name = upstream.Access.AsnName ?? wan.FriendlyName ?? "Access ISP",
                Asn = upstream.Access.AsnNumber,
                AsnName = upstream.Access.AsnName,
                Order = 0,
                WanInterface = wan.WanInterface,
                AccessTechnology = upstream.Access.AccessTechnology,
                L2NeighborOui = upstream.Access.L2NeighborOui,
                IsCgnat = upstream.Access.IsCgnat,
                // TODO: secondary WAN discovery - currently only the primary WAN
                // runs upstream tracing, so secondary WANs always have 0 hops.
                // Suppress the "discovery pending" state for them until multi-WAN
                // tracing is implemented.
                IsDiscoveryPending = wan.IsPrimary && upstream.Access.Hops.Count == 0,
                Tier = wan.IsPrimary && upstream.Access.Hops.Count == 0 ? LanCloudTier.Unresolved : LanCloudTier.Solid,
            };
            // Collect all access hop target IDs so the live tick can pick the
            // lowest RTT across all of them (closest ISP infrastructure).
            accessCloud.RttTargetIds = upstream.Access.Hops
                .Where(h => !string.IsNullOrEmpty(h.TargetId))
                .Select(h => h.TargetId)
                .ToList();
            // Seed the initial RTT from the lowest-latency hop with live data.
            var bestLive = upstream.Access.Hops
                .Where(h => h.Live != null && h.Live.Success && h.Live.RttAvgMs.HasValue)
                .OrderBy(h => h.Live!.RttAvgMs!.Value)
                .FirstOrDefault();
            if (bestLive?.Live != null)
            {
                accessCloud.RttAvgMs = bestLive.Live.RttAvgMs;
                accessCloud.LossPercent = bestLive.Live.LossPercent;
            }
            // ISP expected speeds from UniFi WAN provider capabilities (cached in topology)
            var wanNet = topology.Networks.FirstOrDefault(n =>
                n.IsWan && n.WanNetworkgroup != null
                && n.WanNetworkgroup.Equals(wan.WanInterface, StringComparison.OrdinalIgnoreCase));
            if (wanNet?.WanDownloadMbps > 0)
                accessCloud.IspDownloadMbps = wanNet.WanDownloadMbps;
            if (wanNet?.WanUploadMbps > 0)
                accessCloud.IspUploadMbps = wanNet.WanUploadMbps;

            snapshot.Clouds.Add(accessCloud);

            // WAN link: gateway -> access cloud directly. Capacity from WanSummary,
            // PortKey for live SNMP rate seeding from the gateway's WAN port.
            var wanLink = new LanLink
            {
                Id = $"wan-link-{wan.WanInterface}",
                // Orient WAN like every other infra link: From = upstream end
                // (the ISP cloud), To = downstream end (the gateway). The JS
                // particle layer maps the From->To direction to the blue
                // downstream stream, so this makes blue downloads flow cloud
                // -> gateway and green uploads flow gateway -> cloud, matching
                // the rest of the topology.
                FromNodeId = accessCloud.Id,
                ToNodeId = gwId,
                Kind = LanLinkKind.Wan,
                CapacityBps = wan.LinkSpeedMbps.HasValue ? (long)wan.LinkSpeedMbps.Value * 1_000_000L : null,
            };
            if (!string.IsNullOrEmpty(wan.GatewayPortName))
            {
                wanLink.PortKey = PortKey(wan.GatewayMac!, wan.GatewayPortName);
            }
            snapshot.Links.Add(wanLink);

            // Seed live rates from WanSummary. On a WAN port the polled device IS the
            // gateway, so the direction convention flips relative to internal uplinks:
            //   RateIn  on gateway's WAN port = bytes from internet to gateway = downstream.
            //   RateOut on gateway's WAN port = bytes from gateway to internet = upstream.
            if (wan.LiveRateInBps.HasValue || wan.LiveRateOutBps.HasValue)
            {
                snapshot.LiveRates[wanLink.Id] = new LinkLiveRates
                {
                    DownstreamBps = wan.LiveRateOutBps ?? 0,
                    UpstreamBps = wan.LiveRateInBps ?? 0,
                    AsOf = DateTime.UtcNow,
                };
            }

            if (!upstream.IsPrimary) continue;

            // Transit + path-end clouds disabled for now. The visualization
            // wasn't conveying anything meaningful (clouds clustering even
            // with the fan layout, no per-trace chain info to draw real
            // adjacency). Keeping only the access cloud per WAN until the
            // map-driven trace loop / live graph design is settled. The
            // underlying monitoring targets are still committed by the
            // wizard and probed by the agent - just not rendered.
            //
            // int order = 1;
            // foreach (var t in upstream.Transits)
            // {
            //     var cloud = new LanCloud
            //     {
            //         Id = $"cloud-transit-{wan.WanInterface}-{t.AsnNumber}",
            //         Kind = LanCloudKind.Transit,
            //         Asn = t.AsnNumber,
            //         AsnName = t.AsnName,
            //         Name = t.AsnName,
            //         Order = order++,
            //         WanInterface = wan.WanInterface,
            //         Tier = t.Method switch
            //         {
            //             DiscoveryMethod.PathProxy => LanCloudTier.PathProxy,
            //             DiscoveryMethod.DirectRouter => LanCloudTier.Solid,
            //             _ => LanCloudTier.Unresolved,
            //         },
            //     };
            //     if (t.Live != null && t.Live.Success)
            //     {
            //         cloud.RttAvgMs = t.Live.RttAvgMs;
            //         cloud.LossPercent = t.Live.LossPercent;
            //     }
            //     snapshot.Clouds.Add(cloud);
            //     snapshot.Links.Add(new LanLink
            //     {
            //         Id = $"transit-link-{accessCloud.Id}-{cloud.Id}",
            //         FromNodeId = accessCloud.Id,
            //         ToNodeId = cloud.Id,
            //         Kind = LanLinkKind.Transit,
            //     });
            // }
        }
    }

    private static void InterpolateInteriorPlacements(LanFlowMapSnapshot snapshot, NetworkTopology topology)
    {
        // For devices with no anchor, position at centroid of any anchored devices that
        // are uplinked through them (transitive). This makes switches sit "in the middle"
        // of the APs they serve, and the gateway sit centrally. Spec 3.4 marks these as
        // interpolated.

        var byMac = snapshot.Nodes
            .Where(n => !string.IsNullOrEmpty(n.Mac))
            .ToDictionary(n => n.Mac!, n => n);

        var childrenOf = new Dictionary<string, List<string>>();
        foreach (var d in topology.Devices)
        {
            if (string.IsNullOrEmpty(d.UplinkMac)) continue;
            var p = NormalizeMac(d.UplinkMac);
            var c = NormalizeMac(d.Mac);
            if (!childrenOf.TryGetValue(p, out var list))
            {
                list = new List<string>();
                childrenOf[p] = list;
            }
            list.Add(c);
        }

        IEnumerable<LanPlacement> Descendants(string mac, HashSet<string> seen)
        {
            if (!seen.Add(mac)) yield break;
            if (byMac.TryGetValue(mac, out var node) && node.Placement?.Source == LanPlacementSource.Anchor)
            {
                yield return node.Placement;
            }
            if (childrenOf.TryGetValue(mac, out var kids))
            {
                foreach (var k in kids)
                {
                    foreach (var p in Descendants(k, seen)) yield return p;
                }
            }
        }

        foreach (var node in snapshot.Nodes)
        {
            if (string.IsNullOrEmpty(node.Mac) || node.Placement != null) continue;
            var seen = new HashSet<string>();
            var anchored = Descendants(node.Mac, seen).ToList();
            if (anchored.Count == 0) continue;
            node.Placement = new LanPlacement
            {
                X = anchored.Average(p => p.X),
                Y = anchored.Average(p => p.Y),
                Z = anchored.Average(p => p.Z) - FloorHeightMetres,  // sit slightly "below" the APs in 3D
                Source = LanPlacementSource.Interpolated,
            };
        }
    }

    // ---------------------------------------------------------------------------------
    // Internal: live rates
    // ---------------------------------------------------------------------------------

    private async Task<Dictionary<string, (double inBps, double outBps, DateTime ts)>> SeedPortRatesAsync(
        LanFlowMapSnapshot snapshot,
        CancellationToken ct)
    {
        var result = new Dictionary<string, (double, double, DateTime)>(StringComparer.OrdinalIgnoreCase);
        if (!_influx.IsConfigured) return result;

        var byDevice = snapshot.Links
            .Where(l => !string.IsNullOrEmpty(l.PortKey))
            .GroupBy(l => ParsePortKey(l.PortKey!).Mac)
            .Where(g => !string.IsNullOrEmpty(g.Key));

        var until = DateTime.UtcNow;
        var from = until - TimeSpan.FromSeconds(20);

        foreach (var grp in byDevice)
        {
            try
            {
                using var queryCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                queryCts.CancelAfter(TimeSpan.FromSeconds(5));
                var pts = await _influx.QueryInterfaceRatesAsync(grp.Key, from, until, null, queryCts.Token);
                foreach (var per in pts.GroupBy(p => p.IfName, StringComparer.OrdinalIgnoreCase))
                {
                    var latest = per.OrderByDescending(p => p.Time).First();
                    var key = PortKey(grp.Key, per.Key);
                    result[key] = (latest.RateInBps ?? 0, latest.RateOutBps ?? 0, latest.Time);
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _logger.LogDebug("Per-port rate seed timed out for {Device}, skipping remaining", grp.Key);
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Per-port rate seed failed for {Device}", grp.Key);
            }
        }

        return result;
    }

    private void SeedLiveRates(
        LanFlowMapSnapshot snapshot,
        Dictionary<string, (double inBps, double outBps, DateTime ts)> portRates)
    {
        var now = DateTime.UtcNow;
        foreach (var link in snapshot.Links)
        {
            LinkLiveRates? rates = null;

            if (!string.IsNullOrEmpty(link.PortKey) && portRates.TryGetValue(link.PortKey, out var portRate))
            {
                rates = MapPortToLinkRates(link, portRate.inBps, portRate.outBps, portRate.ts);
            }
            else if (link.Kind == LanLinkKind.WifiClient)
            {
                // WiFi client - look up via the new live-stats interface.
                var clientMac = ExtractWifiClientMacFromLinkId(link.Id);
                if (!string.IsNullOrEmpty(clientMac))
                {
                    var snap = _liveStats.GetWifiClient(clientMac);
                    if (snap != null)
                    {
                        rates = new LinkLiveRates
                        {
                            // Spec 5.7.1: AP TX (to client) = downstream blue.
                            //             AP RX (from client) = upstream green.
                            DownstreamBps = snap.TxThroughputBps ?? 0,
                            UpstreamBps = snap.RxThroughputBps ?? 0,
                            AsOf = snap.LastUpdate,
                        };
                    }
                }
            }
            else if (link.Kind == LanLinkKind.MeshBackhaul)
            {
                // Mesh backhaul throughput piggy-backs on the device aggregate from the
                // collection agent (spec 5.6 puts the AP rate on the parent switch port,
                // but for a wireless-uplinked AP we don't have that — fall back to the
                // child device's aggregate).
                var dev = ExtractDeviceMacFromUplinkId(link.Id);
                if (!string.IsNullOrEmpty(dev))
                {
                    var stats = _liveStats.GetForDevice(dev);
                    if (stats != null && stats.LastRateUpdate.HasValue)
                    {
                        rates = new LinkLiveRates
                        {
                            DownstreamBps = stats.RateInBps ?? 0,
                            UpstreamBps = stats.RateOutBps ?? 0,
                            AsOf = stats.LastRateUpdate.Value,
                        };
                    }
                }
            }

            if (rates != null) snapshot.LiveRates[link.Id] = rates;
        }
    }

    /// <summary>
    /// Resolve direction on a wired link given an SNMP rate reading. The mapping depends
    /// on which side of the link is being polled:
    ///   - Internal links (Uplink / WiredClient / MeshBackhaul): polled port is on the
    ///     UPSTREAM device (the switch). bytes_out leaving the switch port = toward leaf
    ///     = DownstreamBps. bytes_in entering = away from leaf = UpstreamBps.
    ///   - WAN links: polled port is on the GATEWAY (the downstream side from internet's
    ///     perspective, but the upstream side of the LAN's view of the WAN). bytes_in to
    ///     gateway = from internet = downstream. bytes_out from gateway = to internet =
    ///     upstream. This flips the in/out mapping.
    ///   - Transit links (cloud-to-cloud): not polled via SNMP, no rates.
    /// </summary>
    private static LinkLiveRates MapPortToLinkRates(LanLink link, double rateInBps, double rateOutBps, DateTime ts)
    {
        // WAN link: bytes_in on the gateway's WAN port comes FROM the internet,
        // i.e. travels toward the LAN = downstream blue (gateway-direction relative to
        // the link's far end is the access ISP cloud; "leaves" of the LAN tree are the
        // gateway and the rest of the LAN's devices, not the cloud).
        if (link.Kind == LanLinkKind.Wan)
        {
            return new LinkLiveRates
            {
                DownstreamBps = rateInBps,
                UpstreamBps = rateOutBps,
                AsOf = ts,
            };
        }
        return new LinkLiveRates
        {
            DownstreamBps = rateOutBps,
            UpstreamBps = rateInBps,
            AsOf = ts,
        };
    }

    // ---------------------------------------------------------------------------------
    // Internal: speed test overlay
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// Direction-resolved speed test list, ready for the JS overlay layer to paint.
    /// </summary>
    public async Task<List<SpeedTestOverlayItem>> BuildSpeedTestOverlayAsync(
        DateTime since,
        DateTime until,
        int limitPerKind = 5,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        // Group by WAN so secondary WANs aren't crowded out by frequent
        // primary WAN tests. Take limitPerKind per group.
        var raw = await db.Iperf3Results
            .AsNoTracking()
            .Where(r => r.Success && r.TestTime >= since && r.TestTime <= until)
            .OrderByDescending(r => r.TestTime)
            .ToListAsync(ct);
        raw = raw
            .GroupBy(r => r.WanNetworkGroup ?? "")
            .SelectMany(g => g.Take(limitPerKind))
            .ToList();

        var result = new List<SpeedTestOverlayItem>();
        foreach (var r in raw)
        {
            var item = new SpeedTestOverlayItem
            {
                Id = r.Id,
                // SQLite/EF Core returns DateTime with Kind=Unspecified, which JSON
                // serializes without a Z suffix; the browser then treats it as
                // local time and the WAN pill's "Last test: ... · 2h ago" age math
                // comes out as future-dated ("just now"). Tag it as Utc so the
                // client parses it correctly.
                TestTime = DateTime.SpecifyKind(r.TestTime, DateTimeKind.Utc),
                TestType = IsWanDirection(r.Direction) ? "wan" : "lan",
                WanNetworkGroup = r.WanNetworkGroup,
                DownloadMbps = r.DownloadMbps,
                UploadMbps = r.UploadMbps,
            };
            var hops = r.PathAnalysis?.Path?.Hops ?? new List<NetworkHop>();
            foreach (var h in hops)
            {
                item.Hops.Add(MapHopDirection(h));
            }
            result.Add(item);
        }
        return result;
    }

    /// <summary>
    /// CLAUDE.md "Speed Test Directional Concepts" mapping, pre-resolved server-side
    /// so the JS layer never has to remember which property maps to which direction.
    /// </summary>
    private static SpeedTestHop MapHopDirection(NetworkHop hop)
    {
        double ingressBps = (double)hop.IngressSpeedMbps * 1_000_000.0;
        double egressBps = (double)hop.EgressSpeedMbps * 1_000_000.0;

        // Wireless hop:  IngressSpeedMbps = To Device,     EgressSpeedMbps = From Device.
        // WAN/VPN hop:   IngressSpeedMbps = From Device,   EgressSpeedMbps = To Device.
        // Wired hop:     symmetric.
        bool isWireless = hop.IsWirelessIngress || hop.IsWirelessEgress;
        bool isWan = hop.Type == HopType.Wan || hop.Type == HopType.Vpn
            || hop.Type == HopType.Teleport || hop.Type == HopType.Tailscale;

        double? fromDevice;
        double? toDevice;
        if (isWireless)
        {
            fromDevice = egressBps > 0 ? egressBps : null;
            toDevice = ingressBps > 0 ? ingressBps : null;
        }
        else if (isWan)
        {
            fromDevice = ingressBps > 0 ? ingressBps : null;
            toDevice = egressBps > 0 ? egressBps : null;
        }
        else
        {
            // Wired link: both are nominally the same speed.
            double sym = Math.Max(ingressBps, egressBps);
            fromDevice = sym > 0 ? sym : null;
            toDevice = sym > 0 ? sym : null;
        }

        return new SpeedTestHop
        {
            DeviceMac = NormalizeMac(hop.DeviceMac),
            HopType = hop.Type.ToString(),
            FromDeviceBps = fromDevice,
            ToDeviceBps = toDevice,
        };
    }

    // ---------------------------------------------------------------------------------
    // Internal helpers
    // ---------------------------------------------------------------------------------

    private async Task<Dictionary<(string mac, int port), InterfaceNameMap>> LoadInterfaceNameMaps(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var maps = await db.InterfaceNameMaps.AsNoTracking().ToListAsync(ct);
        var dict = new Dictionary<(string, int), InterfaceNameMap>();
        foreach (var m in maps)
        {
            if (!m.PortNumber.HasValue) continue;
            dict[(NormalizeMac(m.DeviceMac), m.PortNumber.Value)] = m;
        }
        return dict;
    }

    private static LanNodeKind MapDeviceKind(DiscoveredDevice d) => d.Type switch
    {
        DeviceType.Gateway => LanNodeKind.Gateway,
        DeviceType.Switch => LanNodeKind.Switch,
        DeviceType.AccessPoint => LanNodeKind.AccessPoint,
        _ => LanNodeKind.Switch,
    };

    private static long? ResolveUplinkCapacityBps(DiscoveredDevice d)
    {
        if (d.UplinkSpeedMbps > 0) return (long)d.UplinkSpeedMbps * 1_000_000L;
        return null;
    }

    private static string NormalizeMac(string? mac) =>
        string.IsNullOrEmpty(mac) ? string.Empty : mac.ToLowerInvariant().Replace("-", ":");

    /// <summary>
    /// Client label fallback chain that matches what the audit / port-security
    /// analyzers use: user-set Name > device-reported Hostname > MAC. Keeps the 3D
    /// map labels consistent with the rest of the UI.
    /// </summary>
    private static string ResolveClientLabel(NetworkOptimizer.UniFi.DiscoveredClient c)
    {
        if (!string.IsNullOrWhiteSpace(c.Name)) return c.Name;
        if (!string.IsNullOrWhiteSpace(c.Hostname)) return c.Hostname;
        return string.IsNullOrEmpty(c.Mac) ? "unknown" : c.Mac;
    }

    private static string PortKey(string deviceMac, string ifName) =>
        deviceMac.ToLowerInvariant() + "|" + ifName;

    private static (string Mac, string IfName) ParsePortKey(string key)
    {
        var idx = key.IndexOf('|');
        if (idx <= 0) return (string.Empty, string.Empty);
        return (key.Substring(0, idx), key.Substring(idx + 1));
    }

    private static string? NormalizeBand(string? radio) => radio switch
    {
        "ng" or "2.4ghz" or "2.4 GHz" or "2.4" => "2.4",
        "na" or "5ghz" or "5 GHz" or "5" => "5",
        "6e" or "6ghz" or "6 GHz" or "6" => "6",
        _ => null,
    };

    private async Task<HistoricDataCache> FetchHistoricDataAsync(
        DateTime at, LanFlowMapSnapshot snapshot, string? gwMac, CancellationToken ct)
    {
        var from = at - TimeSpan.FromSeconds(90);
        var to = at + TimeSpan.FromMinutes(5);

        var deviceMacs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(gwMac)) deviceMacs.Add(gwMac);
        foreach (var link in snapshot.Links)
        {
            if (link.Kind == LanLinkKind.Uplink || link.Kind == LanLinkKind.MeshBackhaul)
            {
                var mac = ExtractDeviceMacFromUplinkId(link.Id);
                if (!string.IsNullOrEmpty(mac)) deviceMacs.Add(mac);
            }
            else if (!string.IsNullOrEmpty(link.PortKey))
            {
                var (mac, _) = ParsePortKey(link.PortKey);
                if (!string.IsNullOrEmpty(mac)) deviceMacs.Add(mac);
            }
        }

        var ratesByDevice = new Dictionary<string, IReadOnlyList<MonitoringInfluxClient.InterfaceRatePoint>>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var mac in deviceMacs)
        {
            try
            {
                ratesByDevice[mac] = await _influx.QueryInterfaceRatesRawAsync(mac, from, to, ct);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Historic rate fetch failed for device {Mac}", mac);
            }
        }

        IReadOnlyList<MonitoringInfluxClient.ClientThroughputPoint> wifi = Array.Empty<MonitoringInfluxClient.ClientThroughputPoint>();
        IReadOnlyList<MonitoringInfluxClient.ClientThroughputPoint> wired = Array.Empty<MonitoringInfluxClient.ClientThroughputPoint>();
        try { wifi = await _influx.QueryAllClientThroughputAsync("wifi_client", from, to, ct); }
        catch (Exception ex) { _logger.LogDebug(ex, "Historic WiFi client batch query failed"); }
        try { wired = await _influx.QueryAllClientThroughputAsync("wired_client", from, to, ct); }
        catch (Exception ex) { _logger.LogDebug(ex, "Historic wired client batch query failed"); }

        var healthByDevice = new Dictionary<string, IReadOnlyList<MonitoringInfluxClient.DeviceHealthPoint>>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var node in snapshot.Nodes)
        {
            if (string.IsNullOrEmpty(node.Mac)) continue;
            try
            {
                healthByDevice[node.Mac] = await _influx.QueryDeviceHealthRawAsync(node.Mac, from, to, ct);
            }
            catch (Exception ex) { _logger.LogDebug(ex, "Historic health fetch failed for {Mac}", node.Mac); }
        }

        var latencyByType = new Dictionary<MonitoringTargetType, IReadOnlyList<MonitoringInfluxClient.LatencyPoint>>();
        foreach (var targetType in new[] { MonitoringTargetType.AccessIsp, MonitoringTargetType.Transit })
        {
            try
            {
                latencyByType[targetType] = await _influx.QueryLatencyByTargetTypeRawAsync(targetType, from, to, ct);
            }
            catch (Exception ex) { _logger.LogDebug(ex, "Historic latency fetch failed for {Type}", targetType); }
        }

        return new HistoricDataCache(from, to, ratesByDevice, wifi, wired, healthByDevice, latencyByType);
    }

    private async Task<LinkLiveRates?> QueryClientThroughputAsync(
        string measurement, string clientMac, DateTime at, DateTime from, DateTime to, CancellationToken ct)
    {
        try
        {
            var result = await _influx.QueryClientThroughputAsync(measurement, clientMac, from, to, ct);
            var closest = result
                .OrderBy(p => Math.Abs((p.Time - at).TotalMilliseconds))
                .FirstOrDefault();
            if (closest == null) return null;
            // Tx = switch/AP→client = downstream, Rx = client→switch/AP = upstream
            return new LinkLiveRates
            {
                DownstreamBps = closest.TxThroughputBps ?? 0,
                UpstreamBps = closest.RxThroughputBps ?? 0,
                AsOf = closest.Time,
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Historic client throughput query failed for {Mac}", clientMac);
            return null;
        }
    }

    private static string? ExtractWifiClientMacFromLinkId(string linkId)
    {
        const string prefix = "cli-link-";
        return linkId.StartsWith(prefix, StringComparison.Ordinal)
            ? linkId.Substring(prefix.Length)
            : null;
    }

    private static string? ExtractWiredClientMacFromLinkId(string linkId)
        => ExtractWifiClientMacFromLinkId(linkId);

    private static string? ExtractDeviceMacFromUplinkId(string linkId)
    {
        const string prefix = "uplink-";
        return linkId.StartsWith(prefix, StringComparison.Ordinal)
            ? linkId.Substring(prefix.Length)
            : null;
    }


    private static bool IsWanDirection(SpeedTestDirection dir) => dir switch
    {
        SpeedTestDirection.CloudflareWan or SpeedTestDirection.CloudflareWanGateway
            or SpeedTestDirection.UwnWan or SpeedTestDirection.UwnWanGateway
            or SpeedTestDirection.OpenSpeedTestWan => true,
        _ => false,
    };
}
