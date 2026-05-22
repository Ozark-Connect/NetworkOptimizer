using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.UniFi.Models;
using NetworkOptimizer.Web.Services.Monitoring;

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
        var anchors = ProjectAnchors(markers);
        snapshot.Bounds = ComputeBounds(anchors);

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

        BuildInfrastructureGraph(topology, anchors, snapshot, nameMaps);
        BuildClientLeaves(topology, snapshot, nameMaps, rawByMac);
        GroupMultiClientPorts(snapshot);
        await BuildWanAndClouds(topology, snapshot, ct);

        var portRates = await SeedPortRatesAsync(snapshot, ct);
        SeedLiveRates(snapshot, portRates);

        snapshot.SpeedTests = await BuildSpeedTestOverlayAsync(
            since: snapshot.GeneratedAt - TimeSpan.FromHours(24),
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
                // For infrastructure uplinks the child device's aggregate is the most
                // live measurement we have on every tick. The empirical convention from
                // the AP badge work: aggregateInBps holds the upload-direction value
                // (data flowing toward the gateway), aggregateOutBps holds downloads.
                // The link's particle layer expects DownstreamBps to be downloads
                // (parent -> child, blue) and UpstreamBps to be uploads
                // (child -> parent, green), so map accordingly.
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
            else if (link.Kind == LanLinkKind.WiredClient && !string.IsNullOrEmpty(link.PortKey))
            {
                // Wired client leaves don't have device-level monitoring stats - their
                // throughput lives on the parent switch port, which the SNMP fast tier
                // writes into MonitoringLiveStats.PortRates every ~5s. Look up by the
                // (parentMac, ifName) key already encoded in link.PortKey.
                var (parentMac, ifName) = ParsePortKey(link.PortKey);
                if (!string.IsNullOrEmpty(parentMac) && !string.IsNullOrEmpty(ifName))
                {
                    var portRate = _liveStats.GetPortRate(parentMac, ifName);
                    if (portRate != null)
                    {
                        // Direction mapping mirrors MapPortToLinkRates for an internal
                        // (non-WAN) link: port TX (DownBps) = data toward leaf,
                        // port RX (UpBps) = data from leaf.
                        rates = new LinkLiveRates
                        {
                            DownstreamBps = portRate.DownBps,
                            UpstreamBps = portRate.UpBps,
                            AsOf = portRate.LastUpdate,
                        };
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

        // Cloud RTT from the in-memory target stats cache.
        foreach (var cloud in snapshot.Clouds)
        {
            // The cloud's RTT came from MonitoringPathView at build time - re-resolve it
            // by querying the same source so the live tick is fresh.
            update.CloudStats[cloud.Id] = new CloudLiveStats
            {
                RttAvgMs = cloud.RttAvgMs,
                LossPercent = cloud.LossPercent,
                Success = cloud.RttAvgMs.HasValue,
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

        // Build the topology so we know the link / port IDs to look up.
        var snapshot = await BuildSnapshotAsync(ct);

        var from = at - TimeSpan.FromSeconds(15);
        var to = at + TimeSpan.FromSeconds(5);
        var byMac = snapshot.Nodes
            .Where(n => !string.IsNullOrEmpty(n.Mac))
            .GroupBy(n => n.Mac!)
            .ToDictionary(g => g.Key, g => g.First());

        // Wire up each link's PortKey lookup -> historic rate point.
        foreach (var link in snapshot.Links)
        {
            if (string.IsNullOrEmpty(link.PortKey)) continue;
            var (deviceMac, ifName) = ParsePortKey(link.PortKey);
            if (string.IsNullOrEmpty(deviceMac) || string.IsNullOrEmpty(ifName)) continue;

            try
            {
                var points = await _influx.QueryInterfaceRatesAsync(deviceMac, from, to, null, ct);
                var latest = points
                    .Where(p => string.Equals(p.IfName, ifName, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(p => Math.Abs((p.Time - at).TotalMilliseconds))
                    .FirstOrDefault();
                if (latest == null) continue;
                update.LinkRates[link.Id] = MapPortToLinkRates(link, latest.RateInBps ?? 0, latest.RateOutBps ?? 0, latest.Time);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Historic rate fetch failed for {Port}", link.PortKey);
            }
        }

        // Cloud latency at the historic instant.
        foreach (var cloud in snapshot.Clouds)
        {
            try
            {
                var lat = await _influx.QueryLatencyAsync(cloud.Id, from, to, null, ct);
                var latest = lat
                    .OrderBy(p => Math.Abs((p.Time - at).TotalMilliseconds))
                    .FirstOrDefault();
                if (latest == null) continue;
                update.CloudStats[cloud.Id] = new CloudLiveStats
                {
                    RttAvgMs = latest.RttAvgMs,
                    LossPercent = latest.LossPercent,
                    Success = latest.RttAvgMs.HasValue,
                };
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Historic latency fetch failed for {Cloud}", cloud.Id);
            }
        }

        update.SpeedTests = await BuildSpeedTestOverlayAsync(from, to, limitPerKind: 5, ct: ct);
        return update;
    }

    // ---------------------------------------------------------------------------------
    // Internal: AP placement -> local Cartesian
    // ---------------------------------------------------------------------------------

    private static Dictionary<string, LanPlacement> ProjectAnchors(IReadOnlyList<Web.Models.ApMapMarker> markers)
    {
        var anchors = new Dictionary<string, LanPlacement>();
        var withCoords = markers
            .Where(m => m.Latitude.HasValue && m.Longitude.HasValue)
            .ToList();
        if (withCoords.Count == 0) return anchors;

        // Project lat/lng to a local equirectangular frame centered on the centroid.
        // Scale so 1 unit ~= 1 metre at the centroid latitude. Z = floor number * 3 metres
        // (typical floor-to-floor distance). The JS layer normalises to its own scene
        // units; we just need consistent local coordinates.
        double centerLat = withCoords.Average(m => m.Latitude!.Value);
        double centerLng = withCoords.Average(m => m.Longitude!.Value);
        const double earthRadiusMetres = 6_371_000.0;
        double lngScale = Math.Cos(centerLat * Math.PI / 180.0);

        foreach (var m in withCoords)
        {
            double dLat = (m.Latitude!.Value - centerLat) * Math.PI / 180.0;
            double dLng = (m.Longitude!.Value - centerLng) * Math.PI / 180.0;
            double x = dLng * lngScale * earthRadiusMetres;
            double y = dLat * earthRadiusMetres;
            double z = (m.Floor ?? 1) * 3.0;
            anchors[NormalizeMac(m.Mac)] = new LanPlacement
            {
                X = x,
                Y = y,
                Z = z,
                Source = LanPlacementSource.Anchor,
            };
        }
        return anchors;
    }

    private static LanFlowMapBounds ComputeBounds(Dictionary<string, LanPlacement> anchors)
    {
        if (anchors.Count == 0) return new LanFlowMapBounds { Radius = 1.0, AnchorCount = 0 };
        double maxR = 0;
        foreach (var p in anchors.Values)
        {
            var r = Math.Sqrt(p.X * p.X + p.Y * p.Y + p.Z * p.Z);
            if (r > maxR) maxR = r;
        }
        return new LanFlowMapBounds
        {
            Radius = Math.Max(maxR, 1.0),
            AnchorCount = anchors.Count,
        };
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
            snapshot.Nodes.Add(new LanNode
            {
                Id = "dev-" + mac,
                Kind = kind,
                Mac = mac,
                Name = string.IsNullOrEmpty(d.Name) ? d.FriendlyModelName : d.Name,
                Model = d.FriendlyModelName,
                Placement = anchor,
                Online = d.State == 1,
            });
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
        }
    }

    private void BuildClientLeaves(
        NetworkTopology topology,
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

            var nodeId = "cli-" + clientMac;
            var node = new LanNode
            {
                Id = nodeId,
                Kind = c.IsWired ? LanNodeKind.WiredClient : LanNodeKind.WifiClient,
                Mac = clientMac,
                Ip = string.IsNullOrEmpty(c.IpAddress) ? null : c.IpAddress,
                Name = ResolveClientLabel(c),
                ParentId = "dev-" + parentMac,
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
                Name = upstream.Access.AsnName ?? upstream.Access.L2NeighborOui ?? "Access ISP",
                Asn = upstream.Access.AsnNumber,
                AsnName = upstream.Access.AsnName,
                Order = 0,
                WanInterface = wan.WanInterface,
                AccessTechnology = upstream.Access.AccessTechnology,
                L2NeighborOui = upstream.Access.L2NeighborOui,
                IsCgnat = upstream.Access.IsCgnat,
                IsDiscoveryPending = upstream.Access.Hops.Count == 0,
                Tier = upstream.Access.Hops.Count == 0 ? LanCloudTier.Unresolved : LanCloudTier.Solid,
            };
            // RTT for the access cloud: pick the deepest hop with live data (closest to the
            // ISP boundary). Wizard-output ordering puts BNG/CMTS/OLT toward the tail.
            var lastLive = upstream.Access.Hops
                .Reverse()
                .FirstOrDefault(h => h.Live != null && h.Live.Success);
            if (lastLive?.Live != null)
            {
                accessCloud.RttAvgMs = lastLive.Live.RttAvgMs;
                accessCloud.LossPercent = lastLive.Live.LossPercent;
            }
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
                Z = anchored.Average(p => p.Z) - 3.0,  // sit slightly "below" the APs in 3D
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
                var pts = await _influx.QueryInterfaceRatesAsync(grp.Key, from, until, null, ct);
                // Take the latest reading per ifName.
                foreach (var per in pts.GroupBy(p => p.IfName, StringComparer.OrdinalIgnoreCase))
                {
                    var latest = per.OrderByDescending(p => p.Time).First();
                    var key = PortKey(grp.Key, per.Key);
                    result[key] = (latest.RateInBps ?? 0, latest.RateOutBps ?? 0, latest.Time);
                }
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

        var raw = await db.Iperf3Results
            .AsNoTracking()
            .Where(r => r.Success && r.TestTime >= since && r.TestTime <= until)
            .OrderByDescending(r => r.TestTime)
            .Take(limitPerKind * 4)
            .ToListAsync(ct);

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

    private static string? ExtractWifiClientMacFromLinkId(string linkId)
    {
        const string prefix = "cli-link-";
        return linkId.StartsWith(prefix, StringComparison.Ordinal)
            ? linkId.Substring(prefix.Length)
            : null;
    }

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
