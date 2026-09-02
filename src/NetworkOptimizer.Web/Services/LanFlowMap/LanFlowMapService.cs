using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Core.Helpers;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;
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
    // Scoped, per-site console connection (NOT the default-pinned IUniFiClientProvider
    // singleton) so the map's device/topology source is the current site's console,
    // not the main site's. UniFiConnectionService implements IUniFiClientProvider.
    private readonly UniFiConnectionService _connection;
    private readonly ClientDashboardService _dashboard;
    private readonly MonitoringLiveStats _liveStats;
    private readonly ApAgent.ApAgentTelemetryRegistry _apAgentTelemetry;
    private readonly MonitoringInfluxClient _influx;
    private readonly MonitoringPathView _pathView;
    private readonly ApMapService _apMap;
    private readonly LanFlowMapCache _cache;
    private readonly IDbContextFactory<NetworkOptimizerDbContext> _dbFactory;
    private readonly SiteDbContextFactory _siteDbFactory;
    private readonly SiteContextService _siteContext;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<LanFlowMapService> _logger;
    private readonly NetworkOptimizer.Core.Interfaces.IAgentClientPresenceSource _agentPresence;

    public LanFlowMapService(
        UniFiConnectionService connection,
        MonitoringLiveStats liveStats,
        ApAgent.ApAgentTelemetryRegistry apAgentTelemetry,
        MonitoringInfluxClient influx,
        MonitoringPathView pathView,
        ApMapService apMap,
        LanFlowMapCache cache,
        IDbContextFactory<NetworkOptimizerDbContext> dbFactory,
        SiteDbContextFactory siteDbFactory,
        SiteContextService siteContext,
        NetworkOptimizer.Core.Interfaces.IAgentClientPresenceSource agentPresence,
        ClientDashboardService dashboard,
        ILoggerFactory loggerFactory,
        ILogger<LanFlowMapService> logger)
    {
        _dashboard = dashboard;
        _connection = connection;
        _liveStats = liveStats;
        _apAgentTelemetry = apAgentTelemetry;
        _agentPresence = agentPresence;
        _influx = influx;
        _pathView = pathView;
        _apMap = apMap;
        _cache = cache;
        _dbFactory = dbFactory;
        _siteDbFactory = siteDbFactory;
        _siteContext = siteContext;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    /// <summary>
    /// Context for the current site's database. Placements, monitored SFPs, and
    /// interface name maps are per-site rows; reading them through the main-DB
    /// factory painted the main site's map objects onto secondary sites.
    /// </summary>
    private NetworkOptimizerDbContext CreateSiteDb() =>
        _siteDbFactory.CreateForSite(_siteContext.Slug, _siteContext.IsDefault);

    /// <summary>
    /// Returns a fresh snapshot or the cached one if still inside the TTL. Browsers
    /// call this on map mount; the /live endpoint never rebuilds, it only refreshes
    /// rates on top of the cached snapshot.
    /// </summary>
    public Task<LanFlowMapSnapshot> BuildSnapshotAsync(CancellationToken ct = default)
    {
        // An agent-observed association change marks the cached topology stale about ten seconds
        // later, so the map converges on the Console's fresh roster without waiting out the TTL.
        var nudge = _apAgentTelemetry.GetFor(_siteContext.Slug).RosterNudge;
        if (_cache.Current is { } current && nudge.ShouldRefresh(current.GeneratedAt, DateTime.UtcNow))
            _cache.MarkStale();

        return _cache.BuildOrGetAsync(BuildSnapshotInternalAsync, ct);
    }

    /// <summary>Force the next snapshot read to rebuild (e.g. on controller reconnect).</summary>
    public void InvalidateCache() => _cache.Invalidate();

    private async Task<LanFlowMapSnapshot> BuildSnapshotInternalAsync(CancellationToken ct)
    {
        var snapshot = new LanFlowMapSnapshot { GeneratedAt = DateTime.UtcNow };

        if (!_connection.IsConnected || _connection.Client == null)
        {
            return snapshot;
        }

        var discovery = new UniFiDiscovery(_connection.Client, _loggerFactory.CreateLogger<UniFiDiscovery>(), _agentPresence);
        var topology = await discovery.DiscoverTopologyAsync(ct);

        var markers = await _apMap.GetApMapMarkersAsync();

        // Load non-AP device placements (switches, gateways) from the same table.
        using var db = CreateSiteDb();
        var allLocations = await db.ApLocations.ToListAsync(ct);
        var apMacs = new HashSet<string>(
            markers.Select(m => m.Mac.ToLowerInvariant()),
            StringComparer.OrdinalIgnoreCase);
        var deviceLocations = allLocations
            .Where(l => !apMacs.Contains(l.ApMac.ToLowerInvariant()))
            .ToList();
        // Precise heights come straight off the ApLocations rows; the marker DTO
        // doesn't carry HeightM, so ProjectAnchors looks it up by MAC for APs.
        var heightByMac = allLocations
            .Where(l => l.HeightM.HasValue)
            .ToDictionary(l => NormalizeMac(l.ApMac), l => l.HeightM!.Value);

        var anchors = ProjectAnchors(markers, deviceLocations, heightByMac,
            out var centerLat, out var centerLng, out var lngScale);
        // AP anchors define the reference frame (centroid, scene radius, and the
        // set eligible for outlier pruning). Non-AP device placements are
        // deliberate user drags: they ride within the frame but must never
        // perturb the scale or be pruned, or a device placed toward a building
        // could shift or fall back to scatter on the next load. Keyed to match
        // the anchor dictionary (NormalizeMac).
        var apAnchorMacs = new HashSet<string>(
            markers.Where(m => m.Latitude.HasValue && m.Longitude.HasValue)
                   .Select(m => NormalizeMac(m.Mac)));
        snapshot.AnchorsByMac = anchors;
        var droppedAnchors = PruneAnchorOutliers(anchors, apAnchorMacs);
        if (droppedAnchors.Count > 0)
        {
            _logger.LogDebug(
                "LAN map: dropped {Count} outlier anchor(s) far outside the cluster (bad/stale placement): {Macs}",
                droppedAnchors.Count, string.Join(", ", droppedAnchors));
        }
        snapshot.Bounds = ComputeBounds(anchors, apAnchorMacs, centerLat, centerLng, lngScale);
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

        // The console nests the second unit of a Building Bridge pair inside the first, so it is
        // on no device list - yet it is the device the far building's switch uplinks to. Without
        // it the pair's wireless link has one end and everything behind it draws isolated.
        var bridgePeers = UniFiDiscovery.BuildingBridgePeers(rawDevices);
        if (bridgePeers.Count > 0)
        {
            var listed = new HashSet<string>(topology.Devices.Select(x => NormalizeMac(x.Mac)), StringComparer.OrdinalIgnoreCase);
            rawDevices.AddRange(bridgePeers);
            foreach (var peer in bridgePeers)
            {
                if (listed.Add(NormalizeMac(peer.Mac)))
                    topology.Devices.Add(UniFiDiscovery.MapBuildingBridgePeer(peer));
            }
            _logger.LogDebug("LAN map [{Site}]: {Count} Building Bridge peer unit(s) added from peer_ubb",
                _siteContext.Slug, bridgePeers.Count);
        }

        var rawByMac = rawDevices
            .Where(d => !string.IsNullOrEmpty(d.Mac))
            .ToDictionary(d => NormalizeMac(d.Mac), d => d, StringComparer.OrdinalIgnoreCase);

        // Names for clients that are not connected right now, so the timeline can label a client it
        // rebuilds from telemetry. v2 clients/history is the endpoint the UniFi UI itself uses for
        // its client list: it includes offline devices and carries display_name, which is the auto
        // name ("Vendor, Inc. a1:b2") the console shows for a client the user never renamed. That
        // name exists nowhere else - rest/user leaves name empty for those and its hostname is the
        // DHCP name ("iPhone"), and clients/active is connected-only. rest/user is still applied
        // afterwards so a user-set alias wins. Advisory: a failure means such a leaf shows its MAC.
        try
        {
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var history = await _connection.Client!.GetClientHistoryAsync(ClientNameLookbackHours, ct);
            var fromHistory = 0;
            foreach (var c in history)
            {
                var label = !string.IsNullOrWhiteSpace(c.DisplayName) ? c.DisplayName
                    : !string.IsNullOrWhiteSpace(c.Name) ? c.Name
                    : !string.IsNullOrWhiteSpace(c.Hostname) ? c.Hostname
                    : null;
                if (!string.IsNullOrEmpty(c.Mac) && label != null)
                {
                    names[NormalizeMac(c.Mac)] = label;
                    fromHistory++;
                }
            }

            var fromUserRecords = 0;
            foreach (var c in await _connection.Client!.GetAllKnownClientsAsync(ct))
            {
                // Only a real alias overrides the console's own label - not the DHCP hostname,
                // which is how "iPhone" would beat "Apple, Inc. a1:b2".
                if (!string.IsNullOrEmpty(c.Mac) && !string.IsNullOrWhiteSpace(c.Name))
                {
                    names[NormalizeMac(c.Mac)] = c.Name;
                    fromUserRecords++;
                }
            }

            snapshot.RecentClientNames = names;
            _logger.LogDebug(
                "LAN map [{Site}]: {Count} client name(s) for historic playback ({History} from client history, {Alias} user aliases)",
                _siteContext.Slug, names.Count, fromHistory, fromUserRecords);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "LAN map: client name lookup failed");
        }

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
        GroupMultiClientPorts(snapshot, await WanBytesByMacAsync(ct));
        await BuildWanAndClouds(topology, snapshot, ct);

        // WAN interface names for InfluxDB rate queries, one per WAN (ppp* tunnel
        // for PPPoE, physical port otherwise). Including both names would
        // double-count, since the physical port keeps counting under PPPoE.
        var wans = await _pathView.GetWansAsync(ct);
        snapshot.WanIfNames = wans
            .Select(w => NetworkUtilities.PreferredWanCounterInterface(w.PhysicalIfName, w.UplinkIfName))
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
    /// How far back to ask the console for client names. 90 days so a client that left months ago
    /// still reads as itself when the timeline reaches that far. Paid once per snapshot build, not
    /// per historic request, so the cost is bounded by the snapshot cache rather than by playback.
    /// </summary>
    private const int ClientNameLookbackHours = 2160;

    /// <summary>
    /// Tolerance for deriving historic online state from telemetry proximity. There is
    /// no stored online/state field, so a device counts as online at the scrub instant
    /// when a device_health point falls within this window of it. device_health is written
    /// ~every 30 s while a device is reachable, so this allows for one dropped sample plus
    /// jitter without flapping an online device to offline.
    /// </summary>
    private const double HistoricOnlineWindowSeconds = 60;

    /// <summary>
    /// Tolerance for deciding a client was connected at the scrub instant. Wider than the device
    /// window on purpose: a client pass can be skipped when the console is slow to answer, and
    /// leaving a client on the map one sample too long is a smaller error than blinking one out
    /// that was really there.
    /// </summary>
    /// <summary>
    /// How long after its last point a client is still drawn as present. Three times the write
    /// cadence: enough to ride out one missed write (an agent restart, a slow poll) without a
    /// connected client blinking out, and no more. It was three minutes only because points were
    /// traffic-driven and a quiet client left real gaps; presence is written every window now.
    /// </summary>
    private static readonly TimeSpan ClientPresenceTolerance = TimeSpan.FromSeconds(90);

    /// <summary>
    /// Instants within this many seconds of now are the "live edge". The historic cache
    /// fetches a window 5 min AHEAD of its fetch instant, but that ahead portion is empty
    /// when fetched (the data isn't written yet), and an agent-run speed test can delay
    /// writes ~2 min. Reusing the cached copy there froze the maps (all-offline / stuck
    /// rates) while Port Stats - which queries fresh per instant - stayed accurate. So near
    /// the live edge we bypass the cache and query fresh, matching Port Stats. Sized to the
    /// worst observed agent-test write lag; older instants still use the cache.
    /// </summary>
    private const double HistoricLiveEdgeSettleSeconds = 150;

    /// <summary>Gateway / switch / AP - the fabric devices the map renders an explicit
    /// online/offline appearance for (clients track association, not device state).</summary>
    private static bool IsInfraKind(LanNodeKind kind) =>
        kind is LanNodeKind.Gateway or LanNodeKind.Switch or LanNodeKind.AccessPoint;

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
        update.SnapshotGeneratedAt = snapshot.GeneratedAt;

        ApplyLiveClientStats(snapshot, update);
        AddLiveOnlyClients(snapshot, update);
        MarkDepartedClients(snapshot, update);

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
                            // Every aggregate writer stores uploads in RateInBps and downloads in
                            // RateOutBps (LanFabricAggregator, the fast tier's vwiresta swap), so
                            // downloads = link downstream. Do not "correct" this to a raw-ifIn
                            // reading - the raw counters are swapped before they land here.
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
                // UDB (single-port bridge) leaf: the bridged client's own wired counters are
                // always zero, so source the rate from the bridge's device aggregate (the same
                // value shown on the UDB's wireless-uplink link, since it's a single flow).
                // BridgeParentMac is set only for DeviceBridge parents, so switch/AP clients
                // never take this path and keep their existing client-counter behavior.
                if (rates == null && !string.IsNullOrEmpty(link.BridgeParentMac))
                {
                    var bridge = _liveStats.GetForDevice(link.BridgeParentMac);
                    if (bridge != null && bridge.LastRateUpdate.HasValue)
                    {
                        rates = new LinkLiveRates
                        {
                            DownstreamBps = bridge.RateOutBps ?? 0,
                            UpstreamBps = bridge.RateInBps ?? 0,
                            AsOf = bridge.LastRateUpdate.Value,
                        };
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

            // Offline infra: emit a bare offline badge (no rates) so the map dims the
            // device and zeros its links. node.Online is the UniFi device State from the
            // latest snapshot rebuild. We do this before reading live stats on purpose:
            // GetForDevice keeps the last sample until the next prune, so an offline
            // device can still have a stale entry - reading it would paint a dead device
            // with phantom throughput.
            if (IsInfraKind(node.Kind) && !node.Online)
            {
                update.NodeBadges[node.Id] = new NodeLiveBadge { Online = false };
                continue;
            }

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
        // WAN globe LOSS is different: it shows the combined ISP+Transit mean the
        // WAN live chart plots, so the globe and the chart's Loss series always
        // agree - the lowest-RTT hop's own loss missed drops on the other hops.
        double? wanChartLoss = null;
        try { wanChartLoss = (await _liveStats.GetMeanIspTransitLiveAsync(ct)).MeanLossPercent; }
        catch { }
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
                if (cloud.Kind == LanCloudKind.AccessIsp && wanChartLoss != null)
                    loss = wanChartLoss;
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
    /// Earliest instant historic playback can reach - the first interface_counters point
    /// in the primary bucket. It only moves forward slowly (retention trimming), so a
    /// non-null answer is cached for an hour; null (no data yet) retries after 5 minutes
    /// so a fresh monitoring setup picks up its timeline floor quickly.
    /// </summary>
    public async Task<DateTime?> GetHistoryStartAsync(CancellationToken ct = default)
    {
        var ttl = _cache.EarliestData == null ? TimeSpan.FromMinutes(5) : TimeSpan.FromHours(1);
        if (DateTime.UtcNow - _cache.EarliestDataAt < ttl)
            return _cache.EarliestData;
        try
        {
            // Keep a known-good floor on transient failures (Influx restart,
            // auth blip) - a null answer must not clobber the cached value.
            var earliest = await _influx.QueryEarliestInterfaceDataAsync(ct);
            if (earliest != null) _cache.EarliestData = earliest;
            _cache.EarliestDataAt = DateTime.UtcNow;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Earliest-data query failed; keeping cached timeline floor");
            _cache.EarliestDataAt = DateTime.UtcNow;
        }
        return _cache.EarliestData;
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

        // Reuse cached InfluxDB results when the requested time falls within the
        // previously fetched window. Fetches 5 min ahead so forward playback goes ~4 min
        // before another round-trip. But never reuse for the live edge: the ahead portion
        // was empty when fetched (data not written yet), so serving it there froze the maps
        // while fresh queries (Port Stats) stayed accurate. Refetch when `at` is within the
        // settle window of now so the live edge always reads freshly-written data.
        var atLiveEdge = at > DateTime.UtcNow - TimeSpan.FromSeconds(HistoricLiveEdgeSettleSeconds);
        var cached = _cache.HistoricData;
        if (cached == null || at < cached.From || at > cached.To - TimeSpan.FromSeconds(30) || atLiveEdge)
        {
            cached = await FetchHistoricDataAsync(at, snapshot, gwMac, ct);
            // Only promote a fetch to the reusable singleton when it is NOT a live-edge
            // fetch. A live-edge window's near-present portion is still-arriving/incomplete;
            // storing it poisons the cache so instants that later age past the settle window
            // fall inside that stale window and keep reading empty (the "test didn't show for
            // minutes, then appeared once the window rolled" symptom). Live-edge fetches are
            // used for this response only and always re-fetched fresh next time.
            if (!atLiveEdge)
            {
                // The window runs 5 min ahead of `at`, which for a recent instant reaches past the
                // settle line: that stretch was fetched before its points were written, and serving
                // it later showed a roam's first half and never its second. Keep only what had
                // settled at fetch time; anything beyond misses the cache and reads fresh. The 30 s
                // is the reuse test's own margin, so the miss lands on the settle line itself and
                // playback just behind the live edge is not refetching every tick.
                var usableTo = DateTime.UtcNow - TimeSpan.FromSeconds(HistoricLiveEdgeSettleSeconds - 30);
                _cache.HistoricData = cached.To > usableTo ? cached with { To = usableTo } : cached;
            }
        }

        var ratesByDevice = cached.RatesByDevice;
        var from = cached.From;
        var to = cached.To;

        // Resolve closest client throughput points from cached data.
        var wifiClientRates = new Dictionary<string, MonitoringInfluxClient.ClientThroughputPoint>(StringComparer.OrdinalIgnoreCase);
        // How a client is connected is written once per write window, while its throughput is
        // written every time it is measured - so the point nearest an instant usually carries a
        // rate and nothing else. Tracked separately and merged below, or a client would lose its
        // band and signal at most instants and only regain them on a window boundary.
        var wifiClientConnection = new Dictionary<string, MonitoringInfluxClient.ClientThroughputPoint>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in cached.WifiClients)
        {
            if (string.IsNullOrEmpty(p.ClientMac)) continue;
            if (!wifiClientRates.TryGetValue(p.ClientMac, out var existing)
                || Math.Abs((p.Time - at).TotalMilliseconds) < Math.Abs((existing.Time - at).TotalMilliseconds))
                wifiClientRates[p.ClientMac] = p;

            // PHY rate is the discriminator: it is written only on a full point. Band is a tag on
            // every point and signal rides on the thin ones too, so neither can tell them apart.
            if (p.TxRateKbps == null && p.RxRateKbps == null) continue;
            if (!wifiClientConnection.TryGetValue(p.ClientMac, out var describedBy)
                || Math.Abs((p.Time - at).TotalMilliseconds) < Math.Abs((describedBy.Time - at).TotalMilliseconds))
                wifiClientConnection[p.ClientMac] = p;
        }
        foreach (var (mac, described) in wifiClientConnection)
        {
            // Copy rather than mutate: these points are cached and re-read for every other instant
            // in the window.
            var nearest = wifiClientRates[mac];
            if (nearest.TxRateKbps != null || nearest.RxRateKbps != null) continue;
            wifiClientRates[mac] = nearest with
            {
                SignalDbm = described.SignalDbm,
                Band = described.Band,
                TxRateKbps = described.TxRateKbps,
                RxRateKbps = described.RxRateKbps,
            };
        }
        // Points whose MAC is an infrastructure node are the fast tier's mesh backhaul PHY
        // readings, recorded per mesh child under its base MAC. They feed the DEVICE node's
        // scrub stats below and must not read as clients (presence, measured sets, or
        // rebuilt historic-only leaves).
        var deviceNodeMacs = snapshot.Nodes
            .Where(n => n.Id.StartsWith("dev-", StringComparison.Ordinal) && !string.IsNullOrEmpty(n.Mac))
            .Select(n => NormalizeMac(n.Mac))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var meshDevRates = new Dictionary<string, MonitoringInfluxClient.ClientThroughputPoint>(StringComparer.OrdinalIgnoreCase);
        foreach (var mac in wifiClientRates.Keys.Where(deviceNodeMacs.Contains).ToList())
        {
            meshDevRates[mac] = wifiClientRates[mac];
            wifiClientRates.Remove(mac);
        }

        var wiredClientRates = new Dictionary<string, MonitoringInfluxClient.ClientThroughputPoint>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in cached.WiredClients)
        {
            if (string.IsNullOrEmpty(p.ClientMac)) continue;
            if (!wiredClientRates.TryGetValue(p.ClientMac, out var existing)
                || Math.Abs((p.Time - at).TotalMilliseconds) < Math.Abs((existing.Time - at).TotalMilliseconds))
                wiredClientRates[p.ClientMac] = p;
        }

        // Every client the window can speak to at all, before narrowing to this instant. Clients
        // outside it write no telemetry, so their absence proves nothing and playback leaves them be.
        foreach (var p in cached.WifiClients)
        {
            if (!string.IsNullOrEmpty(p.ClientMac) && !deviceNodeMacs.Contains(NormalizeMac(p.ClientMac)))
                update.MeasuredClientIds.Add("cli-" + NormalizeMac(p.ClientMac));
        }
        foreach (var p in cached.WiredClients)
        {
            if (!string.IsNullOrEmpty(p.ClientMac))
                update.MeasuredClientIds.Add("cli-" + NormalizeMac(p.ClientMac));
        }
        // The window is minutes wide; a client gone longer than that has no point in it. One the
        // collector writes every pass is measured whether or not the window caught it.
        foreach (var node in snapshot.Nodes)
        {
            if (node.WritesTelemetry) update.MeasuredClientIds.Add(node.Id);
        }

        // Who was connected at this instant, wired and wireless alike. A point far from `at` is
        // somewhere else in the cached window and says nothing about now, so it does not count.
        foreach (var (mac, p) in wifiClientRates)
        {
            if ((p.Time - at).Duration() <= ClientPresenceTolerance)
                update.PresentClientIds.Add("cli-" + NormalizeMac(mac));
        }
        foreach (var (mac, p) in wiredClientRates)
        {
            if ((p.Time - at).Duration() <= ClientPresenceTolerance)
                update.PresentClientIds.Add("cli-" + NormalizeMac(mac));
        }

        // WiFi client connection stats at the scrub instant (band/signal/PHY rate). Keyed
        // by client node id ("cli-{mac}") so the maps can override the snapshot-frozen
        // values. The band tag is "2.4ghz"/"5ghz"/"6ghz" - normalize to match snapshot.
        foreach (var (mac, p) in wifiClientRates)
        {
            var band = NormalizeBand(p.Band);
            var apNodeId = string.IsNullOrEmpty(p.ApMac) ? null : "dev-" + NormalizeMac(p.ApMac);
            if (band == null && p.SignalDbm == null && p.TxRateKbps == null
                && p.RxRateKbps == null && apNodeId == null)
                continue;
            update.ClientStats["cli-" + mac] = new NodeClientStats
            {
                Band = band,
                SignalDbm = p.SignalDbm,
                PhyTxKbps = p.TxRateKbps,
                PhyRxKbps = p.RxRateKbps,
                ApNodeId = apNodeId,
            };
        }

        // Mesh backhaul PHY at the scrub instant, keyed by the device node so the maps'
        // Link speed rows follow the playhead like a client's connection stats do.
        foreach (var (mac, p) in meshDevRates)
        {
            update.ClientStats["dev-" + NormalizeMac(mac)] = new NodeClientStats
            {
                Band = NormalizeBand(p.Band),
                SignalDbm = p.SignalDbm,
                PhyTxKbps = p.TxRateKbps,
                PhyRxKbps = p.RxRateKbps,
            };
        }

        AddHistoricOnlyClients(snapshot, update, wifiClientRates, wiredClientRates);

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
                            resolved = ClosestPortPoint(pPts, pIf, at);
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
                                // One vwiresta series per MLO link; the backhaul is their sum at
                                // the scrub instant (nearest point per series). A classic backhaul
                                // has one series, so this is the old single read for it.
                                var meshPts = cPts
                                    .Where(p => p.IfName.StartsWith("vwiresta", StringComparison.OrdinalIgnoreCase)
                                        && !p.IfName.Contains('.'))
                                    .GroupBy(p => p.IfName, StringComparer.OrdinalIgnoreCase)
                                    .Select(g => g.OrderBy(p => Math.Abs((p.Time - at).TotalMilliseconds)).First())
                                    .ToList();
                                if (meshPts.Count == 1)
                                {
                                    resolved = meshPts[0];
                                }
                                else if (meshPts.Count > 1)
                                {
                                    resolved = new MonitoringInfluxClient.InterfaceRatePoint
                                    {
                                        Time = meshPts.OrderBy(p => Math.Abs((p.Time - at).TotalMilliseconds)).First().Time,
                                        IfName = "vwiresta",
                                        RateInBps = meshPts.Sum(p => p.RateInBps ?? 0),
                                        RateOutBps = meshPts.Sum(p => p.RateOutBps ?? 0),
                                    };
                                }
                                // UDB and UBB: no vwiresta interface. Their bridged flow is persisted
                                // under a synthetic "bridge-downlink" series (BridgeInterfaceRecorder),
                                // stored in the same rateIn = downstream convention, so it maps
                                // through the block below.
                                resolved ??= cPts
                                    .Where(p => string.Equals(p.IfName, BridgeInterfaceRecorder.DownlinkIfName, StringComparison.OrdinalIgnoreCase))
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
                                    resolved = ClosestPortPoint(cPts, childNode.UplinkIfName, at);
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
                            var closest = ClosestPortPoint(pts, ifName, at);
                            if (closest != null)
                                rates = MapPortToLinkRates(link, closest.RateInBps ?? 0, closest.RateOutBps ?? 0, closest.Time);
                        }
                    }
                    // UDB bridged leaf: the client's own wired counters are always zero, so source
                    // the rate from the bridge's persisted downlink series (mirrors the live path's
                    // BridgeParentMac substitution). Set only for DeviceBridge parents, so switch/AP
                    // clients keep the wired_client fallback below untouched.
                    if (rates == null && !string.IsNullOrEmpty(link.BridgeParentMac)
                        && ratesByDevice.TryGetValue(link.BridgeParentMac, out var bpts))
                    {
                        var closest = bpts
                            .Where(p => string.Equals(p.IfName, BridgeInterfaceRecorder.DownlinkIfName, StringComparison.OrdinalIgnoreCase))
                            .OrderBy(p => Math.Abs((p.Time - at).TotalMilliseconds))
                            .FirstOrDefault();
                        if (closest != null)
                            rates = new LinkLiveRates
                            {
                                DownstreamBps = closest.RateInBps ?? 0,
                                UpstreamBps = closest.RateOutBps ?? 0,
                                AsOf = closest.Time,
                            };
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
                        // Exclude the synthetic UDB bridge-downlink series: it's a single directional
                        // flow, not a switch fabric, so summing it as ingress/egress makes a bridge
                        // look asymmetric. Dropping it leaves the bridge with no fabric badge, so it
                        // falls back to adjacent-link summing (symmetric) - matching the live badge,
                        // which has no fabric series for a UDB either.
                        : rates.Where(p => !string.Equals(p.IfName, BridgeInterfaceRecorder.DownlinkIfName, StringComparison.OrdinalIgnoreCase));
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

                // No explicit online/state is stored in the time series, so derive
                // historic liveness from telemetry proximity: device_health is written
                // roughly every 30 s only while a device is reachable, so a health point
                // close to the scrub instant means the device was online then. For
                // infrastructure we always emit a badge (offline ones get Online=false
                // and no rates) so playback reflects the real state at T instead of
                // freezing the current snapshot's live online state across the timeline.
                var isInfra = IsInfraKind(node.Kind);
                var onlineAtT = healthPt != null
                    && Math.Abs((healthPt.Time - at).TotalSeconds) <= HistoricOnlineWindowSeconds;

                // A second liveness signal at the same proximity: interface rates are only
                // recorded while a device is reachable and poll on a separate cadence from
                // device_health, so a single health sample dropped to poll jitter must not
                // dark a device that plainly had interface telemetry at T (the visible
                // symptom was particle streams freezing mid-playback, then restoring).
                // Bounded by the same window, so a genuine outage - no health AND no rate
                // telemetry near T - still reads offline.
                var rateNearT = ratesByDevice.TryGetValue(mac, out var rateHist) && rateHist.Count > 0
                    && rateHist.Min(p => Math.Abs((p.Time - at).TotalSeconds)) <= HistoricOnlineWindowSeconds;

                bool infraOnline = false;
                if (isInfra)
                {
                    // device_health is the authoritative liveness signal, but only when we
                    // actually collect it for this device. If we never recorded health for
                    // it (SNMP-only / third-party gear), don't force it dark across the
                    // whole timeline: fall back to rate telemetry, then to the current
                    // snapshot state. Devices we DO monitor get accurate per-instant state.
                    var hasHealthHistory =
                        cached.HealthByDevice.TryGetValue(mac, out var hh) && hh.Count > 0;
                    if (hasHealthHistory)
                        infraOnline = onlineAtT || rateNearT;
                    else
                        infraOnline = (fabIn != null || aggIn != null) || node.Online;

                    if (!infraOnline)
                    {
                        update.NodeBadges[node.Id] = new NodeLiveBadge { Online = false };
                        continue;
                    }
                }
                else if (healthPt == null && fabIn == null && aggIn == null)
                {
                    continue;
                }

                update.NodeBadges[node.Id] = new NodeLiveBadge
                {
                    Online = isInfra ? infraOnline : node.Online,
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
                // Only clouds with monitoring targets actually attributed to them get
                // latency. The historic query buckets by target TYPE (not WAN), so without
                // this gate every AccessIsp globe - including secondary WANs that have no
                // targets (only the primary is traced today) - would be painted with the
                // primary's RTT during playback, and the live path (which keys off
                // RttTargetIds) wouldn't, leaving the stale value stuck on resume. Mirror
                // the live path's per-cloud gating until multi-WAN upstream tracing exists.
                if (cloud.RttTargetIds.Count == 0) continue;

                var targetType = cloud.Kind switch
                {
                    LanCloudKind.AccessIsp => MonitoringTargetType.AccessIsp,
                    LanCloudKind.Transit => MonitoringTargetType.Transit,
                    _ => (MonitoringTargetType?)null
                };
                if (targetType == null) continue;
                // This cloud's own targets first, lowest RTT winning, exactly as the live path
                // chooses. The type bucket is the fallback for a target with no stored series.
                MonitoringInfluxClient.LatencyPoint? best = null;
                foreach (var targetId in cloud.RttTargetIds)
                {
                    if (!cached.LatencyByTargetId.TryGetValue(targetId, out var pts) || pts.Count == 0)
                        continue;
                    var nearest = pts
                        .Where(p => p.RttAvgMs.HasValue)
                        .OrderBy(p => Math.Abs((p.Time - at).TotalMilliseconds))
                        .FirstOrDefault();
                    if (nearest?.RttAvgMs == null) continue;
                    if (best?.RttAvgMs == null || nearest.RttAvgMs < best.RttAvgMs) best = nearest;
                }
                if (best == null && cached.LatencyByTargetType.TryGetValue(targetType.Value, out var latPts))
                {
                    best = latPts
                        .OrderBy(p => Math.Abs((p.Time - at).TotalMilliseconds))
                        .FirstOrDefault();
                }
                if (best == null) continue;
                // WAN globe loss mirrors the WAN chart's Loss series (combined
                // ISP+Transit mean) at the same instant, matching the live path.
                double? loss = best.LossPercent;
                if (cloud.Kind == LanCloudKind.AccessIsp && cached.MeanIspTransit.Count > 0)
                {
                    loss = cached.MeanIspTransit
                        .OrderBy(p => Math.Abs((p.Time - at).TotalMilliseconds))
                        .First().LossPercent;
                }
                update.CloudStats[cloud.Id] = new CloudLiveStats
                {
                    RttAvgMs = best.RttAvgMs,
                    LossPercent = loss,
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
        IReadOnlyDictionary<string, double> heightByMac,
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
            var mac = NormalizeMac(m.Mac);
            var (x, y) = ProjectLatLng(m.Latitude!.Value, m.Longitude!.Value, centerLat, centerLng, lngScale);
            anchors[mac] = new LanPlacement
            {
                X = x,
                Y = y,
                Z = (m.Floor ?? 1) * FloorHeightMetres,
                Source = LanPlacementSource.Anchor,
                HeightM = heightByMac.TryGetValue(mac, out var hm) ? hm : null,
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
                HeightM = d.HeightM,
            };
        }

        return anchors;
    }

    // A single anchor placed wildly far from the rest (bad geocode, a mis-drag, or a
    // stale placement from a since-relocated device) inflates the scene radius and
    // collapses the whole map: the shared node/building scale is driven by the
    // farthest anchor, so one outlier 1+ km out shrinks real buildings to a speck and
    // skews the camera centroid. Drop anchors that sit BOTH far beyond the cluster
    // (> OutlierMedianFactor x the median anchor distance) AND past an absolute sanity
    // range. Requiring both conditions means legitimately spread-out layouts
    // (multi-building / campus) are never touched - only a lone runaway spike is. A
    // dropped node loses its pin and simply floats with the force layout instead.
    private const double OutlierMedianFactor = 6.0;
    private const double OutlierAbsoluteMetres = 200.0;

    private static List<string> PruneAnchorOutliers(
        Dictionary<string, LanPlacement> anchors, IReadOnlySet<string> apAnchorMacs)
    {
        var removed = new List<string>();
        // Only AP anchors are eligible: they define the frame, and a bad AP
        // geocode is what this guards against. User-placed device anchors
        // (switches, cameras) are deliberate drags - pruning one would silently
        // scatter it far from its building, so they're exempt entirely.
        var apAnchors = anchors.Where(kv => apAnchorMacs.Contains(kv.Key)).ToList();
        // Need enough anchors for a median to be meaningful; with 1-2 we can't tell
        // which one is the outlier, so leave them alone.
        if (apAnchors.Count < 3) return removed;

        var distances = apAnchors.ToDictionary(
            kv => kv.Key,
            kv => Math.Sqrt(kv.Value.X * kv.Value.X + kv.Value.Y * kv.Value.Y));

        var sorted = distances.Values.OrderBy(d => d).ToList();
        var median = sorted[sorted.Count / 2];
        var threshold = Math.Max(OutlierMedianFactor * median, OutlierAbsoluteMetres);

        foreach (var mac in distances.Where(kv => kv.Value > threshold).Select(kv => kv.Key).ToList())
        {
            anchors.Remove(mac);
            removed.Add(mac);
        }
        return removed;
    }

    private static LanFlowMapBounds ComputeBounds(
        Dictionary<string, LanPlacement> anchors,
        IReadOnlySet<string> apAnchorMacs,
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
        // Radius (and thus the global scale) is defined by AP anchors only,
        // mirroring the AP-only centroid. If a non-AP device set the radius,
        // placing it would change the scale on the next load and shift every
        // already-placed device. Fall back to all anchors when no APs exist.
        var radiusAnchors = anchors.Where(kv => apAnchorMacs.Contains(kv.Key))
            .Select(kv => kv.Value).ToList();
        if (radiusAnchors.Count == 0) radiusAnchors = anchors.Values.ToList();
        double maxR = 0;
        foreach (var p in radiusAnchors)
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
            using var db = CreateSiteDb();
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
        // A mesh child whose own uplink UniFi did not report still has a parent that named it.
        // Without this the device gets no edge at all: gone from the 2D map, isolated on the 3D one.
        // Built before the node pass because MLO node rates read it too.
        var meshParentByChild = NetworkOptimizer.UniFi.UniFiDiscovery.BuildMeshParentByChild(topology.Devices);

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
                Online = UniFiDeviceStateMap.IsOnline(d.State),
            };
            if (string.Equals(d.UplinkType, "wireless", StringComparison.OrdinalIgnoreCase))
            {
                node.PhyTxKbps = d.UplinkTxRateKbps > 0 ? d.UplinkTxRateKbps : null;
                node.PhyRxKbps = d.UplinkRxRateKbps > 0 ? d.UplinkRxRateKbps : null;
                node.Band = NormalizeBand(d.UplinkRadioBand);
                node.IsMloMesh = d.UplinkIsMlo;
                // The child's uplink rates already sum its MLO links. The parent's claim is the
                // same aggregate from the other end (its perspective, so inverted) and only ever
                // raises what the child reported: a floor for a child that reports no links.
                if (meshParentByChild.TryGetValue(mac, out var mloClaim)
                    && mloClaim.IsMlo && !mloClaim.Contradicts(d.UplinkMac))
                {
                    node.IsMloMesh = true;
                    if (mloClaim.RxRateKbps > 0) node.PhyTxKbps = Math.Max(node.PhyTxKbps ?? 0, mloClaim.RxRateKbps);
                    if (mloClaim.TxRateKbps > 0) node.PhyRxKbps = Math.Max(node.PhyRxKbps ?? 0, mloClaim.TxRateKbps);
                }
            }
            snapshot.Nodes.Add(node);
        }

        // Switches and gateway inherit interpolated placement from the centroid of any
        // anchored descendants. Spec 3.4: switches are interpolated and marked.
        InterpolateInteriorPlacements(snapshot, topology);

        // Second pass: uplink edges. Build them as (child -> parent), so on the wire the
        // FromNodeId is the leaf side and the data flowing toward it (DownstreamBps) is
        // gateway -> device per spec 5.7.1.
        // An uplink is only useful if it names a device that is actually on the map. UniFi has been
        // seen reporting a stale one after a reboot - present, so nothing looked wrong, but naming
        // something no node exists for. The edge then hangs off nothing and the client drops it,
        // which is indistinguishable from having no uplink at all: the device draws isolated.
        var deviceMacs = new HashSet<string>(topology.Devices.Select(x => NormalizeMac(x.Mac)));

        foreach (var d in topology.Devices)
        {
            var mac = NormalizeMac(d.Mac);
            var uplinkMac = NormalizeMac(d.UplinkMac);
            var fromDownlinkTable = false;

            // A parent naming this device in its downlink_table outranks the device's own uplink
            // field ONLY when the two disagree. The field can be stale or plain wrong after a
            // reboot - it has been seen naming a switch that actually hangs off the AP - and
            // pointing a child at something downstream of itself closes a loop the layout cannot
            // place, so the device ends up with no position at all: isolated on 3D, absent from
            // 2D. When child and parent agree, the child's own report (capacity, band, uplink
            // port) is authoritative and this path is inert.
            if (meshParentByChild.TryGetValue(mac, out var claim) && claim.Contradicts(uplinkMac))
            {
                _logger.LogDebug(
                    "[LanFlowMap] {Mac} reports its uplink as {Reported}, but {Parent} claims it as a mesh child; using the parent",
                    mac, string.IsNullOrEmpty(uplinkMac) ? "none" : uplinkMac, claim.ParentMac);
                uplinkMac = claim.ParentMac;
                fromDownlinkTable = true;
            }
            if (string.IsNullOrEmpty(uplinkMac)) continue;
            var parentMac = NormalizeMac(uplinkMac);
            if (mac == parentMac) continue;
            if (!deviceMacs.Contains(parentMac))
            {
                _logger.LogDebug(
                    "[LanFlowMap] {Mac} uplinks to {Parent}, which is not a device on this site; no edge drawn",
                    mac, parentMac);
                continue;
            }

            // A downlink_table entry is a wireless backhaul by definition; UplinkType came from
            // the half that was missing, so it cannot be consulted for these.
            var isWirelessBackhaul = fromDownlinkTable
                || string.Equals(d.UplinkType, "wireless", StringComparison.OrdinalIgnoreCase);
            var link = new LanLink
            {
                Id = $"uplink-{mac}",
                FromNodeId = "dev-" + parentMac,
                ToNodeId = "dev-" + mac,
                Kind = isWirelessBackhaul ? LanLinkKind.MeshBackhaul : LanLinkKind.Uplink,
                // Negotiated speed and band belong to the link the child described. When that is
                // not this link, they are not ours to show - a wired 1 Gbps read off a stale
                // uplink is how a mesh backhaul ends up labelled 1 Gbps.
                CapacityBps = fromDownlinkTable ? null : ResolveUplinkCapacityBps(d),
                Band = isWirelessBackhaul && !fromDownlinkTable ? NormalizeBand(d.UplinkRadioBand) : null,
            };
            if (isWirelessBackhaul)
            {
                // Mesh PHY is asymmetric, and which field is which depends on who reported it.
                // The child's own fields are the child's perspective: its RX caps traffic toward
                // it (downstream), its TX caps traffic toward the parent (upstream). A claim from
                // the parent is the opposite way round - the parent transmitting IS the child
                // receiving - and it also describes a different link from the one the child's
                // stale uplink fields refer to, so the two are never mixed.
                if (fromDownlinkTable)
                {
                    if (claim.TxRateKbps > 0) link.CapacityDownBps = claim.TxRateKbps * 1_000L;
                    if (claim.RxRateKbps > 0) link.CapacityUpBps = claim.RxRateKbps * 1_000L;
                    link.IsMloMesh = claim.IsMlo;
                }
                else
                {
                    if (d.UplinkRxRateKbps > 0) link.CapacityDownBps = d.UplinkRxRateKbps * 1_000L;
                    if (d.UplinkTxRateKbps > 0) link.CapacityUpBps = d.UplinkTxRateKbps * 1_000L;
                    link.IsMloMesh = d.UplinkIsMlo;
                    // The child's rates already sum its MLO links; the parent's claim is the same
                    // aggregate from the other end and only ever raises them (a floor for a child
                    // that reports no links). claim holds the agreeing parent claim here - a
                    // contradicting one took the fromDownlinkTable branch above. CapacityBps
                    // drives the pipes' saturation ramp, so it takes the aggregate too or MLO
                    // throughput reads oversaturated.
                    if (claim.IsMlo)
                    {
                        link.IsMloMesh = true;
                        if (claim.TxRateKbps > 0) link.CapacityDownBps = Math.Max(link.CapacityDownBps ?? 0, claim.TxRateKbps * 1_000L);
                        if (claim.RxRateKbps > 0) link.CapacityUpBps = Math.Max(link.CapacityUpBps ?? 0, claim.RxRateKbps * 1_000L);
                        var aggPeak = Math.Max(claim.TxRateKbps, claim.RxRateKbps) * 1_000L;
                        if (aggPeak > 0) link.CapacityBps = Math.Max(link.CapacityBps ?? 0, aggPeak);
                    }
                }
            }

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
            //
            // Not when the parent came from its downlink table: the port the child names is the
            // one on the link it was wrong about, and here it faces DOWN - the switch hangs off
            // the AP. Readers of this invert it, because reading an uplink at the child's end
            // reverses direction, so pointing them at a downstream port reports the mesh link
            // backwards. Leaving it unset drops them to the device aggregate, which is the right
            // source for an AP whose backhaul is a radio anyway.
            if (!fromDownlinkTable && d.LocalUplinkPort.HasValue && d.LocalUplinkPort.Value > 0)
            {
                var childNode = snapshot.Nodes.FirstOrDefault(n => n.Id == "dev-" + mac);
                if (childNode != null && nameMaps.TryGetValue((mac, d.LocalUplinkPort.Value), out var localMap))
                {
                    childNode.UplinkIfName = localMap.IfName;
                }
            }
        }
    }

    /// <summary>
    /// Adds leaves for clients that were connected at the scrub instant but are absent from the
    /// live snapshot.
    ///
    /// The snapshot's client list is UniFi's currently-connected set, so a client that has since
    /// disconnected has no node - and the historic pass only decorates nodes that exist. Its
    /// telemetry is already in hand either way, so the leaf is rebuilt from the point itself:
    /// <c>device_mac</c> is the AP for wifi_client and the switch for wired_client, giving the
    /// parent it was actually on at that instant rather than a last-known guess, and the wired
    /// points carry the port and the client name too.
    ///
    /// Only ever called for a historic instant, and only for MACs with no live node, so live mode
    /// and every client already on the map are untouched. A client whose parent device is itself
    /// missing from the topology is skipped rather than parented to a guess.
    /// </summary>
    private void AddHistoricOnlyClients(
        LanFlowMapSnapshot snapshot,
        LanFlowMapHistoricUpdate update,
        Dictionary<string, MonitoringInfluxClient.ClientThroughputPoint> wifiClientRates,
        Dictionary<string, MonitoringInfluxClient.ClientThroughputPoint> wiredClientRates)
    {
        var liveNodeIds = new HashSet<string>(snapshot.Nodes.Select(n => n.Id), StringComparer.OrdinalIgnoreCase);
        // Infrastructure nodes are exactly the "dev-{mac}" ids, which is what a telemetry
        // device_mac resolves to - matching on the id form avoids tracking the kind list.
        var deviceNodeIds = new HashSet<string>(
            snapshot.Nodes.Where(n => n.Id.StartsWith("dev-", StringComparison.OrdinalIgnoreCase))
                          .Select(n => n.Id),
            StringComparer.OrdinalIgnoreCase);

        void Add(string mac, MonitoringInfluxClient.ClientThroughputPoint p, bool wired)
        {
            var clientMac = NormalizeMac(mac);
            var nodeId = "cli-" + clientMac;
            if (string.IsNullOrEmpty(clientMac) || liveNodeIds.Contains(nodeId)) return;

            // Same MAC can appear in both point sets across a wired/wireless switch; first wins.
            if (update.AddedClientNodes.Any(n => string.Equals(n.Id, nodeId, StringComparison.OrdinalIgnoreCase)))
                return;

            if (string.IsNullOrEmpty(p.ApMac)) return;
            var parentId = "dev-" + NormalizeMac(p.ApMac);
            if (!deviceNodeIds.Contains(parentId)) return;

            var band = wired ? null : NormalizeBand(p.Band);
            var node = new LanNode
            {
                Id = nodeId,
                Kind = wired ? LanNodeKind.WiredClient : LanNodeKind.WifiClient,
                Mac = clientMac,
                Name = !string.IsNullOrWhiteSpace(p.ClientName)
                    ? p.ClientName
                    : (snapshot.RecentClientNames.TryGetValue(clientMac, out var known) ? known : clientMac),
                ParentId = parentId,
                Band = band,
                SignalDbm = wired ? null : p.SignalDbm,
                PhyTxKbps = wired ? null : p.TxRateKbps,
                PhyRxKbps = wired ? null : p.RxRateKbps,
                SwitchPortName = wired && p.Port is > 0 ? $"Port {p.Port}" : null,
                // A client the user has placed keeps that position even at instants it was offline;
                // rebuilding it without one dropped it back to the force layout mid-playback.
                Placement = snapshot.AnchorsByMac.GetValueOrDefault(clientMac),
            };

            update.AddedClientNodes.Add(node);
            var linkId = $"cli-link-{clientMac}";
            update.AddedClientLinks.Add(new LanLink
            {
                Id = linkId,
                FromNodeId = parentId,
                ToNodeId = nodeId,
                Kind = wired ? LanLinkKind.WiredClient : LanLinkKind.WifiClient,
                Band = band,
            });

            // The rate pass above walks the snapshot's links, and this one is not in it - so
            // without this a client rebuilt for an instant drew a dead line while its throughput
            // sat right here in the same telemetry point.
            update.LinkRates[linkId] = new LinkLiveRates
            {
                DownstreamBps = p.TxThroughputBps ?? 0,
                UpstreamBps = p.RxThroughputBps ?? 0,
                AsOf = p.Time,
            };
        }

        foreach (var (mac, p) in wiredClientRates) Add(mac, p, wired: true);
        foreach (var (mac, p) in wifiClientRates) Add(mac, p, wired: false);

        if (update.AddedClientNodes.Count > 0)
        {
            // Nameless count is the actionable number: it is exactly what renders as a raw MAC,
            // and it says whether the shortfall is the name sources or this site's client records.
            var nameless = update.AddedClientNodes.Count(
                n => string.Equals(n.Name, n.Mac, StringComparison.OrdinalIgnoreCase));
            _logger.LogDebug(
                "LAN map [{Site}]: {Count} client(s) present at {At:u} but not connected now - rebuilt from telemetry, {Nameless} without a name",
                _siteContext.Slug, update.AddedClientNodes.Count, update.At, nameless);
        }
    }

    /// <summary>
    /// How old a cached client reading may be before the console's own value is preferred. Keeps
    /// the overlay strictly an improvement: past this it is no fresher than what it would replace.
    /// </summary>
    private static readonly TimeSpan LiveClientMaxAge = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How recently the cache must have been refreshed for a client to be ADDED back to the map.
    /// Bounds how long a client that left can be resurrected: the agent drops it from its own
    /// table within seconds, after which nothing refreshes its entry.
    /// </summary>
    private static readonly TimeSpan LiveClientAddMaxAge = TimeSpan.FromSeconds(15);

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

            // The live cache is fed far faster than the console client list: every 500 ms while
            // Client Performance is watching a client, every 10 s from an AP Agent otherwise,
            // against the console's 30 s. Prefer it so a roam and a signal change reach the map at
            // that rate. Bounded by age so it can only ever be fresher than what it replaces.
            var live = c.IsWired ? null : _liveStats.GetWifiClient(clientMac);
            // A Console-sourced entry is the same wifi tier data this build already holds, only on
            // an independent clock, so preferring it is as often staler as fresher.
            if (live is { Source: WifiClientSource.Console }) live = null;
            if (live != null && DateTime.UtcNow - live.LastUpdate > LiveClientMaxAge) live = null;

            // Deliberately NOT re-parenting from the cache here. The snapshot is built from one
            // console topology and is internally consistent; pointing a client at an access point
            // this build did not draw leaves its node and link referencing a parent that does not
            // exist, and the renderers drop it - a returning client vanishes rather than pops in.
            // Fast roam re-attach belongs on the live tick (ApplyLiveClientStats), which validates
            // the candidate against the nodes actually in the snapshot.

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
                node.Band = NormalizeBand(live?.Band) ?? NormalizeBand(c.Radio);
                // Every associated Wi-Fi client is written each pass - the writer skips one with no band.
                node.WritesTelemetry = node.Band != null;
                node.SignalDbm = live?.SignalDbm is { } dbm
                    ? (int)Math.Round(dbm)
                    : c.SignalStrength ?? c.Rssi;
                node.PhyTxKbps = live?.TxRateKbps > 0 ? live.TxRateKbps : (c.TxRate > 0 ? c.TxRate : null);
                node.PhyRxKbps = live?.RxRateKbps > 0 ? live.RxRateKbps : (c.RxRate > 0 ? c.RxRate : null);
            }

            var link = new LanLink
            {
                Id = $"cli-link-{clientMac}",
                FromNodeId = "dev-" + parentMac,
                ToNodeId = nodeId,
                Kind = c.IsWired ? LanLinkKind.WiredClient : LanLinkKind.WifiClient,
                Band = c.IsWired ? null : (NormalizeBand(live?.Band) ?? NormalizeBand(c.Radio)),
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

                    // A UDB (single-port bridge) never reports non-zero wired byte counters for the
                    // client behind it, so tag this leaf to source its live rate from the bridge's
                    // device aggregate instead. Only DeviceBridge parents are tagged - switch/AP
                    // clients keep their existing (working) client-counter path untouched.
                    if (parentDev.DeviceType == DeviceType.DeviceBridge)
                        link.BridgeParentMac = parentMac;
                }
                // The wired writer's gate: a ported client is written every pass, a bridged one never.
                node.WritesTelemetry = string.IsNullOrEmpty(link.BridgeParentMac);
            }
            else if (!c.IsWired)
            {
                // PHY rate (kbps) acts as the WiFi link capacity (spec 3.5 - PHY is capacity).
                long maxPhyKbps = Math.Max(c.TxRate, c.RxRate);
                if (maxPhyKbps > 0) link.CapacityBps = maxPhyKbps * 1_000L;
                // Directional PHY: AP TX rate limits traffic toward the client
                // (downstream), RX rate limits traffic from the client (upstream).
                if (c.TxRate > 0) link.CapacityDownBps = c.TxRate * 1_000L;
                if (c.RxRate > 0) link.CapacityUpBps = c.RxRate * 1_000L;
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
    /// <summary>
    /// Each connected client's WAN bytes over the last day, from the site-wide DPI report IF a
    /// reader has it cached. Never fetched from here: this runs inside the topology rebuild that
    /// every live tick waits on, and a console can take seconds over a day of DPI. Decides which
    /// interface a shared port's hub stands in for; empty means the lowest IP stands in instead.
    /// </summary>
    private Task<Dictionary<string, long>> WanBytesByMacAsync(CancellationToken ct)
    {
        var bytes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var now = DateTime.UtcNow;
            var traffic = _dashboard.PeekSiteTraffic(now - TimeSpan.FromHours(24), now);
            foreach (var c in traffic?.ClientUsageByApp ?? new())
            {
                var mac = NormalizeMac(c.Client?.Mac ?? "");
                if (mac.Length == 0) continue;
                long total = 0;
                foreach (var u in c.UsageByApp) total += u.BytesReceived + u.BytesTransmitted;
                bytes[mac] = total;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "WAN bytes by client unavailable; shared ports link to their first interface");
        }
        return Task.FromResult(bytes);
    }

    private void GroupMultiClientPorts(LanFlowMapSnapshot snapshot, Dictionary<string, long> wanBytesByMac)
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
            // The hub stands in for the interface behind it with the most WAN traffic lately, so a
            // click on the port lands on the client someone most likely means; with no traffic to
            // go on, the lowest IP, which is usually the host's own interface rather than a
            // sub-interface. A wrong guess is one pick away in Client Performance's own selector.
            var representative = members
                .Where(m => !string.IsNullOrEmpty(m.Node.Ip) && !string.IsNullOrEmpty(m.Node.Mac))
                .OrderByDescending(m => wanBytesByMac.TryGetValue(m.Node.Mac!, out var b) ? b : 0)
                .ThenBy(m => NetworkUtilities.IpSortKey(m.Node.Ip!))
                .FirstOrDefault();
            var hubNode = new LanNode
            {
                Id = hubId,
                Kind = LanNodeKind.VirtualHub,
                Name = string.IsNullOrEmpty(portName)
                    ? $"{members.Count} interfaces"
                    : $"{portName} ({members.Count})",
                Ip = representative.Node?.Ip,
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

        // Only WANs that pass the activity gate (up, or still holding an IP) render a
        // globe; base the "tag with WAN number" decision on that visible count so a lone
        // globe isn't labelled "(WAN1)" even when other WANs are configured but hidden.
        var shownWanCount = wans.Count(w => w.IsActive);

        foreach (var wan in wans)
        {
            var gwId = !string.IsNullOrEmpty(wan.GatewayMac)
                ? "dev-" + NormalizeMac(wan.GatewayMac)
                : null;
            if (string.IsNullOrEmpty(gwId)) continue;

            // Render WAN globes by activity, read from the gateway's wanN device JSON
            // (up + ip; networkconf can't distinguish an unused WAN). Active (up, has
            // IP) renders normally; a half-state (up without an IP, or down still
            // holding an IP) renders greyed like a discovery-pending cloud; an
            // effectively-unused WAN (down, no IP) is not shown at all.
            var hasIp = !string.IsNullOrEmpty(wan.IpAddress);
            if (!wan.IsActive) continue;
            var inactiveGrey = wan.Up != hasIp;

            UpstreamPathSnapshot? upstream = null;
            try { upstream = await _pathView.GetUpstreamPathAsync(wan.WanInterface, ct); }
            catch (Exception ex) { _logger.LogDebug(ex, "Upstream path fetch failed for {Wan}", wan.WanInterface); }
            if (upstream == null) continue;

            // Discovery counts as complete if EITHER access ISP hops or transit hops
            // were resolved - some access networks expose no pingable ICMP target, so
            // the path is proven via transit alone. (Only the primary WAN is traced.)
            var discoveryPending = wan.IsPrimary
                && upstream.Access.Hops.Count == 0
                && upstream.Transits.Count == 0;

            var accessCloud = new LanCloud
            {
                Id = $"cloud-access-{wan.WanInterface}",
                Kind = LanCloudKind.AccessIsp,
                Name = FormatWanGlobeName(upstream.Access.AsnName, wan.FriendlyName, wan.WanInterface, shownWanCount > 1),
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
                IsDiscoveryPending = discoveryPending,
                Tier = inactiveGrey || discoveryPending
                    ? LanCloudTier.Unresolved
                    : LanCloudTier.Solid,
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

            // WAN link: gateway -> access cloud directly. PortKey for live SNMP
            // rate seeding from the gateway's WAN port. The pipe diameter sizes
            // from the ISP-provisioned plan (larger of down/up) - the port PHY
            // is only the fallback when no plan speeds are configured.
            var ispDownBps = wanNet?.WanDownloadMbps > 0 ? (long)wanNet.WanDownloadMbps! * 1_000_000L : (long?)null;
            var ispUpBps = wanNet?.WanUploadMbps > 0 ? (long)wanNet.WanUploadMbps! * 1_000_000L : (long?)null;
            var ispMaxBps = ispDownBps.HasValue || ispUpBps.HasValue
                ? Math.Max(ispDownBps ?? 0, ispUpBps ?? 0)
                : (long?)null;
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
                CapacityBps = ispMaxBps ?? (wan.LinkSpeedMbps.HasValue ? (long)wan.LinkSpeedMbps.Value * 1_000_000L : null),
                CapacityDownBps = ispDownBps,
                CapacityUpBps = ispUpBps,
            };
            if (!string.IsNullOrEmpty(wan.GatewayPortName))
            {
                wanLink.PortKey = PortKey(wan.GatewayMac!, wan.GatewayPortName);
            }
            snapshot.Links.Add(wanLink);

            // Seed live rates from WanSummary. MonitoringPathView convention:
            //   LiveRateInBps  = WAN port TX = uploads   = upstream.
            //   LiveRateOutBps = WAN port RX = downloads = downstream.
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

    /// <summary>
    /// Builds the access-cloud display name shown on the flow-map globe. The discovered
    /// ISP (ASN) or a genuinely custom WAN/port name leads, with the WAN number trailing
    /// as a qualifier ("Acme Fiber (WAN2)"); on a single-WAN gateway the number is
    /// dropped as redundant. When there is no real name, a default port label is kept but
    /// led by the WAN number ("WAN2 (SFP+ 2)"), while a generic name ("Internet 2") is
    /// dropped to a bare WAN number. The WAN number suffix also disambiguates two WANs
    /// that resolve to the same ISP. Reuses the shared WAN-display/placeholder helpers.
    /// </summary>
    private static string FormatWanGlobeName(string? asnName, string? friendlyName, string wanKey, bool multiWan)
    {
        var wanNum = DisplayFormatters.NormalizeWanDisplay(GatewayWanHelper.WanNetworkGroupFromKey(wanKey));
        var name = !string.IsNullOrWhiteSpace(asnName)
            ? asnName.Trim()
            : (!string.IsNullOrWhiteSpace(friendlyName) && !IsPlaceholderWanName(friendlyName))
                ? friendlyName!.Trim()
                : null;
        if (name != null)
            return multiWan ? $"{name} ({wanNum})" : name;
        if (!string.IsNullOrWhiteSpace(friendlyName) && InterfaceLabelResolver.IsDefaultPortName(friendlyName))
            return $"{wanNum} ({friendlyName!.Trim()})";
        return wanNum;
    }

    /// <summary>True for names that aren't a real user identity: default port placeholders
    /// ("SFP+ 2") and generic WAN names ("WAN2", "Internet 2").</summary>
    private static bool IsPlaceholderWanName(string name)
        => GatewayWanHelper.IsPlaceholderWanName(name);

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
                        // Aggregate convention: RateInBps = uploads, RateOutBps = downloads
                        // (see the live-tick reader). This seed read the fields reversed since
                        // the original 3D map; the live tick replaced it within seconds, which
                        // is why it never showed. Keep both mappings identical.
                        rates = new LinkLiveRates
                        {
                            DownstreamBps = stats.RateOutBps ?? 0,
                            UpstreamBps = stats.RateInBps ?? 0,
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
        await using var db = CreateSiteDb();

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

    /// <summary>
    /// The point a port key names, nearest <paramref name="at"/>. Matches the raw port_id tag as
    /// well as if_name: a switch can hold two InterfaceNameMap rows for one port - the user's
    /// alias and the raw "0/N" - and which one a PortKey carries is decided by whichever row the
    /// map loaded last, while Influx keys the series on the alias. Matching either end makes the
    /// lookup indifferent to that. Live is unaffected: it reads the console's port stats by index.
    /// </summary>
    private static MonitoringInfluxClient.InterfaceRatePoint? ClosestPortPoint(
        IEnumerable<MonitoringInfluxClient.InterfaceRatePoint> points, string? ifName, DateTime at)
    {
        if (string.IsNullOrEmpty(ifName)) return null;
        return points
            .Where(p => string.Equals(p.IfName, ifName, StringComparison.OrdinalIgnoreCase)
                     || string.Equals(p.PortId, ifName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => Math.Abs((p.Time - at).TotalMilliseconds))
            .FirstOrDefault();
    }

    private async Task<Dictionary<(string mac, int port), InterfaceNameMap>> LoadInterfaceNameMaps(CancellationToken ct)
    {
        await using var db = CreateSiteDb();
        var maps = await db.InterfaceNameMaps.AsNoTracking().ToListAsync(ct);
        var dict = new Dictionary<(string, int), InterfaceNameMap>();
        foreach (var m in maps)
        {
            if (!m.PortNumber.HasValue) continue;
            // A port can hold several rows - the label, and the raw name a failed alias walk once
            // wrote. The active collector refreshes its row every metadata pass, so the freshest
            // one is the name the series are being written under now.
            var key = (NormalizeMac(m.DeviceMac), m.PortNumber.Value);
            if (!dict.TryGetValue(key, out var current) || m.LastUpdated > current.LastUpdated)
                dict[key] = m;
        }
        return dict;
    }

    private static LanNodeKind MapDeviceKind(DiscoveredDevice d) => d.Type switch
    {
        DeviceType.Gateway => LanNodeKind.Gateway,
        DeviceType.Switch => LanNodeKind.Switch,
        DeviceType.SmartPower => LanNodeKind.Switch,
        DeviceType.AccessPoint => LanNodeKind.AccessPoint,
        // A bridge unit (UDB, or either half of a UBB pair) is a one-port switch to the map.
        DeviceType.DeviceBridge or DeviceType.BuildingBridge => LanNodeKind.Switch,
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
    /// Client label fallback chain matching Wi-Fi Optimizer - Client Stats: UniFi's
    /// system-selected DisplayName (v2 active-clients) > user-set Name > device-reported
    /// Hostname > MAC. The DisplayName step keeps name-less clients from rendering as a
    /// raw MAC on the 2D/3D maps when the console has a friendly/fingerprint name for them.
    /// </summary>
    private static string ResolveClientLabel(NetworkOptimizer.UniFi.DiscoveredClient c)
    {
        if (!string.IsNullOrWhiteSpace(c.DisplayName)) return c.DisplayName;
        if (!string.IsNullOrWhiteSpace(c.Name)) return c.Name;
        // Bridged UniFi ecosystem devices (Protect cameras, UNAS) carry no user Name/DisplayName
        // but expose a friendly ucore name; prefer it over the auto-generated hostname.
        if (!string.IsNullOrWhiteSpace(c.UcoreName)) return c.UcoreName;
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

    /// <summary>
    /// Emits client leaves the live cache holds but the cached snapshot does not, so a client that
    /// reconnects appears without waiting for the next topology rebuild. The cache learns of an
    /// association from the AP Agent within a poll, where the console client list the snapshot is
    /// built from can take considerably longer.
    ///
    /// Console-sourced entries are skipped: those came from the same client list the snapshot was
    /// built from, so anything they could add is already either in it or about to be.
    /// </summary>
    private void AddLiveOnlyClients(LanFlowMapSnapshot snapshot, LanFlowMapLiveUpdate update)
    {
        var now = DateTime.UtcNow;
        var collector = _apAgentTelemetry.GetFor(_siteContext.Slug);
        var nodeIds = new HashSet<string>(snapshot.Nodes.Select(n => n.Id), StringComparer.OrdinalIgnoreCase);

        foreach (var live in _liveStats.AllWifiClients())
        {
            if (live.Source == WifiClientSource.Console) continue;
            // Tighter than the overlay's window on purpose. Overlaying RF onto a client the
            // snapshot still lists is harmless when slightly stale; ADDING one back after the
            // snapshot dropped it resurrects a client that left. A still-associated client is
            // refreshed every agent poll, so anything staler than that is one that went away.
            if (now - live.LastUpdate > LiveClientAddMaxAge) continue;

            var clientMac = NormalizeMac(live.ClientMac);
            var nodeId = "cli-" + clientMac;
            if (string.IsNullOrEmpty(clientMac) || nodeIds.Contains(nodeId)) continue;

            // Never accelerate a client the agent says has gone: MarkDepartedClients is removing
            // this same id on this same tick, and the age window above cannot tell a departure
            // from a quiet moment. The verdict can.
            if (collector.PresenceFor(live.ApMac, clientMac) == AgentClientPresence.Absent) continue;

            // A client whose access point this build did not draw is skipped rather than parented
            // to a guess: a node pointing at a parent that does not exist is dropped by the
            // renderers, which is worse than waiting for the rebuild.
            if (string.IsNullOrEmpty(live.ApMac)) continue;
            var parentId = "dev-" + NormalizeMac(live.ApMac);
            if (!nodeIds.Contains(parentId)) continue;

            // Only ever ACCELERATE a client the console can corroborate, never invent one. An
            // access point reports per-link randomized MACs for an MLO client, and any that does
            // not fold onto its MLD MAC is a station the console will never list - so it read as
            // "missing from the snapshot" on every tick and became a permanent nameless node.
            // Requiring a known name is also what keeps a raw MAC off the map.
            if (!snapshot.RecentClientNames.ContainsKey(clientMac)) continue;

            var band = NormalizeBand(live.Band);
            update.AddedClientNodes.Add(new LanNode
            {
                Id = nodeId,
                Kind = LanNodeKind.WifiClient,
                Mac = clientMac,
                Name = snapshot.RecentClientNames[clientMac],
                ParentId = parentId,
                Band = band,
                SignalDbm = live.SignalDbm is { } dbm ? (int)Math.Round(dbm) : null,
                PhyTxKbps = live.TxRateKbps > 0 ? live.TxRateKbps : null,
                PhyRxKbps = live.RxRateKbps > 0 ? live.RxRateKbps : null,
                Placement = snapshot.AnchorsByMac.GetValueOrDefault(clientMac),
            });

            var linkId = $"cli-link-{clientMac}";
            update.AddedClientLinks.Add(new LanLink
            {
                Id = linkId,
                FromNodeId = parentId,
                ToNodeId = nodeId,
                Kind = LanLinkKind.WifiClient,
                Band = band,
            });

            // The rate pass walks the snapshot's links and this one is not in it, so without this
            // the new leaf draws a dead line while its throughput sits right here.
            update.LinkRates[linkId] = new LinkLiveRates
            {
                DownstreamBps = live.TxThroughputBps ?? 0,
                UpstreamBps = live.RxThroughputBps ?? 0,
                AsOf = live.LastUpdate,
            };
        }
    }

    /// <summary>
    /// Marks clients the snapshot still lists that the access point serving them says are gone.
    /// The console can take a while to notice a client left; an AP Agent knows within seconds,
    /// because a disassociation reaches it on the hostapd control socket.
    ///
    /// The judge is the same presence verdict the Console entry points use, so this tick-rate
    /// accelerator can never disagree with the next topology rebuild - and it inherits the
    /// verdict's guards: an agent that stopped answering, or answered empty, says Unknown rather
    /// than departure, and a client another covered access point holds is Present mid-roam.
    /// </summary>
    private void MarkDepartedClients(LanFlowMapSnapshot snapshot, LanFlowMapLiveUpdate update)
    {
        var collector = _apAgentTelemetry.GetFor(_siteContext.Slug);

        foreach (var node in snapshot.Nodes)
        {
            if (node.Kind != LanNodeKind.WifiClient || string.IsNullOrEmpty(node.Mac)) continue;
            if (string.IsNullOrEmpty(node.ParentId) || !node.ParentId.StartsWith("dev-", StringComparison.OrdinalIgnoreCase)) continue;

            var apMac = node.ParentId["dev-".Length..];
            if (collector.PresenceFor(apMac, node.Mac) == AgentClientPresence.Absent)
                update.RemovedClientIds.Add(node.Id);
        }
    }

    /// <summary>
    /// Carries per-client RF onto the live tick for clients whose cache entry beats the snapshot.
    /// The snapshot rebuilds on its own slow cadence, which used to be fresh enough because the
    /// console was the only source; an AP Agent updates a client every 10 s and Client Performance
    /// drives it to 500 ms, so waiting for a rebuild strands the map minutes behind what the same
    /// client's page is showing.
    ///
    /// Console-sourced entries are skipped: that is the very data the snapshot was built from, so
    /// emitting it would cost payload to say nothing. A site with no AP Agent and nobody watching a
    /// client therefore sends exactly what it sent before.
    /// </summary>
    private void ApplyLiveClientStats(LanFlowMapSnapshot snapshot, LanFlowMapLiveUpdate update)
    {
        var now = DateTime.UtcNow;
        HashSet<string>? nodeIds = null;

        foreach (var node in snapshot.Nodes)
        {
            if (node.Kind != LanNodeKind.WifiClient || string.IsNullOrEmpty(node.Mac)) continue;

            var live = _liveStats.GetWifiClient(node.Mac);
            if (live == null || live.Source == WifiClientSource.Console) continue;
            if (now - live.LastUpdate > LiveClientMaxAge) continue;

            // Re-attaching to an access point the map never drew would strand the client's link.
            string? apNodeId = null;
            if (!string.IsNullOrEmpty(live.ApMac))
            {
                nodeIds ??= snapshot.Nodes.Select(n => n.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var candidate = "dev-" + NormalizeMac(live.ApMac);
                if (nodeIds.Contains(candidate)) apNodeId = candidate;
            }

            update.ClientStats[node.Id] = new NodeClientStats
            {
                Band = NormalizeBand(live.Band) ?? node.Band,
                SignalDbm = live.SignalDbm is { } dbm ? (int)Math.Round(dbm) : node.SignalDbm,
                PhyTxKbps = live.TxRateKbps > 0 ? live.TxRateKbps : node.PhyTxKbps,
                PhyRxKbps = live.RxRateKbps > 0 ? live.RxRateKbps : node.PhyRxKbps,
                ApNodeId = apNodeId,
            };
        }
    }

    private static string? NormalizeBand(string? radio) => radio switch
    {
        "ng" or "2.4ghz" or "2.4 GHz" or "2.4" => "2.4",
        "na" or "5ghz" or "5 GHz" or "5" => "5",
        "6e" or "6ghz" or "6 GHz" or "6" => "6",
        // 802.11ad: the Building Bridge's 60 GHz link.
        "ad" or "60ghz" or "60 GHz" or "60" => "60",
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
            // A UDB-bridged client leaf sources its historic rate from the bridge's persisted
            // downlink series, so make sure the bridge's interface rows are fetched.
            if (!string.IsNullOrEmpty(link.BridgeParentMac)) deviceMacs.Add(link.BridgeParentMac);
        }

        var ratesByDevice = new Dictionary<string, IReadOnlyList<MonitoringInfluxClient.InterfaceRatePoint>>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var mac in deviceMacs)
        {
            ct.ThrowIfCancellationRequested();
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

        // Per target as well as per type: a type bucket cannot tell one WAN's ISP from another's,
        // so on a multi-WAN site both globes would read whichever point happened to be nearest in
        // time. The live path keys off each cloud's own targets and this mirrors it.
        var latencyByTarget = new Dictionary<string, IReadOnlyList<MonitoringInfluxClient.LatencyPoint>>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var targetId in snapshot.Clouds.SelectMany(c => c.RttTargetIds).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                latencyByTarget[targetId] = await _influx.QueryLatencyAsync(targetId, from, to, ct: ct);
            }
            catch (Exception ex) { _logger.LogDebug(ex, "Historic latency fetch failed for target {Target}", targetId); }
        }

        // Combined ISP+Transit mean - the series the WAN live chart plots. WAN globe
        // loss reads from this during playback so globe and chart always agree.
        IReadOnlyList<MonitoringInfluxClient.LatencyPoint> meanIspTransit = Array.Empty<MonitoringInfluxClient.LatencyPoint>();
        try
        {
            var targetIds = (await _liveStats.GetIspTransitTargetsAsync(ct)).Select(t => t.TargetId).ToList();
            meanIspTransit = await _influx.QueryMeanIspTransitLatencyAsync(from, to, targetIds, ct: ct);
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Historic mean ISP/transit latency fetch failed"); }

        // Every catch above swallows a cancelled query as a per-item miss, so a fetch a scrub cut
        // off comes out with holes. Never return one: the caller caches it as the window, and
        // every instant inside it then reads those links as idle until the window rolls.
        ct.ThrowIfCancellationRequested();
        return new HistoricDataCache(
            from, to, ratesByDevice, wifi, wired, healthByDevice, latencyByType, latencyByTarget, meanIspTransit);
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
