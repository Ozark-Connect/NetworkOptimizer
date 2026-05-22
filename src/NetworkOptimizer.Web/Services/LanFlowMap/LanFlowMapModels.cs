namespace NetworkOptimizer.Web.Services.LanFlowMap;

/// <summary>
/// One-shot snapshot the JS layer pulls when the map mounts. Contains everything
/// needed to render the topology, and seeds the initial live rates. Subsequent
/// updates come from the /live and /history endpoints.
/// </summary>
public class LanFlowMapSnapshot
{
    public DateTime GeneratedAt { get; set; }
    public List<LanNode> Nodes { get; set; } = new();
    public List<LanLink> Links { get; set; } = new();
    public List<LanCloud> Clouds { get; set; } = new();
    public Dictionary<string, LinkLiveRates> LiveRates { get; set; } = new();
    public List<SpeedTestOverlayItem> SpeedTests { get; set; } = new();
    public LanFlowMapBounds Bounds { get; set; } = new();

    /// <summary>Interface name of the primary WAN (e.g. "wan"). The JS layer
    /// uses this to route speed test results that don't carry a WanNetworkGroup.</summary>
    public string? PrimaryWanInterface { get; set; }
}

public enum LanNodeKind
{
    Gateway = 0,
    Switch = 1,
    AccessPoint = 2,
    WiredClient = 3,
    WifiClient = 4,
    Cloud = 5,
    /// <summary>Synthetic grouping node inserted when multiple wired clients share
    /// one physical switch port (e.g. a server exposing many VLAN sub-interfaces,
    /// each with its own MAC). The hub absorbs the port link from the parent
    /// switch (so the port's rate flows through it) and the member clients hang
    /// off it as zero-rate logical leaves.</summary>
    VirtualHub = 6,
}

public enum LanPlacementSource
{
    /// <summary>No placement hint - layout engine decides.</summary>
    Layout = 0,
    /// <summary>Anchored to a real coordinate from ApMapService (AP placement is ours, not UniFi).</summary>
    Anchor = 1,
    /// <summary>Interpolated from neighbours (spec 3.4 "marked as inferred").</summary>
    Interpolated = 2,
}

public class LanNode
{
    public required string Id { get; set; }
    public required LanNodeKind Kind { get; set; }

    public string? Mac { get; set; }
    public string? Ip { get; set; }
    public string? Name { get; set; }
    public string? Model { get; set; }
    public string? ParentId { get; set; }

    public LanPlacement? Placement { get; set; }

    /// <summary>Whether the device responded to our last poll (for dimming offline nodes).</summary>
    public bool Online { get; set; } = true;

    /// <summary>WiFi client band ("2.4", "5", "6") if Kind = WifiClient.</summary>
    public string? Band { get; set; }
    public int? SignalDbm { get; set; }
    public long? PhyTxKbps { get; set; }
    public long? PhyRxKbps { get; set; }
    public string? Ssid { get; set; }

    /// <summary>VLAN for client filtering ("Main", "IoT", "Guest", ...).</summary>
    public string? Network { get; set; }
    public bool IsGuest { get; set; }

    /// <summary>For wired clients: the switch port label ("Port 7", "Studio Drop", etc.)
    /// from the parent switch's UniFi port_table. Lets the tooltip show "via Port 7"
    /// without the user having to cross-reference the topology.</summary>
    public string? SwitchPortName { get; set; }

    /// <summary>For wired clients: the parent switch port's negotiated link speed
    /// in Mbps. Surfaces as "1000 Mbps" / "2.5 Gbps" in the tooltip.</summary>
    public int? WiredLinkSpeedMbps { get; set; }
}

public class LanPlacement
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public LanPlacementSource Source { get; set; }
}

public enum LanLinkKind
{
    /// <summary>Switch/AP up to its parent device. Wired.</summary>
    Uplink = 0,
    /// <summary>Wired client to its switch port.</summary>
    WiredClient = 1,
    /// <summary>WiFi client to its AP.</summary>
    WifiClient = 2,
    /// <summary>Gateway WAN port out to its access ISP cloud.</summary>
    Wan = 3,
    /// <summary>Hop between access cloud and a transit ASN cloud (primary WAN only).</summary>
    Transit = 4,
    /// <summary>Mesh AP wireless backhaul to its parent AP.</summary>
    MeshBackhaul = 5,
}

public class LanLink
{
    public required string Id { get; set; }
    public required string FromNodeId { get; set; }
    public required string ToNodeId { get; set; }
    public required LanLinkKind Kind { get; set; }

    /// <summary>Capacity (PHY/negotiated/port speed) in bps. Backdrop pipe radius scales from this.</summary>
    public long? CapacityBps { get; set; }

    /// <summary>Wireless band ("2.4"/"5"/"6") for wifi-client and mesh links.</summary>
    public string? Band { get; set; }

    /// <summary>Stable correlation key for wired throughput chain: device MAC + ifName (spec 3.7).
    /// Also the lookup key for MonitoringLiveStats.GetPortRate per-port live tick refreshes.</summary>
    public string? PortKey { get; set; }
}

public enum LanCloudKind
{
    AccessIsp = 0,
    Transit = 1,
}

public enum LanCloudTier
{
    /// <summary>DirectRouter / UserProvided - render solid cloud, full live stats.</summary>
    Solid = 0,
    /// <summary>PathProxy - render dashed cloud with "via path" badge (spec 5.7).</summary>
    PathProxy = 1,
    /// <summary>Unresolved - neutral cloud, no live stats yet (discovery pending).</summary>
    Unresolved = 2,
}

public class LanCloud
{
    public required string Id { get; set; }
    public required LanCloudKind Kind { get; set; }

    public string? Name { get; set; }
    public int? Asn { get; set; }
    public string? AsnName { get; set; }

    public double? RttAvgMs { get; set; }
    public double? LossPercent { get; set; }

    /// <summary>Spec 5.7: cloud monitored via path-proxy fallback renders more tentative
    /// (dashed/"via path" tag). MonitoringPathView's DiscoveryMethod is the tier signal.</summary>
    public LanCloudTier Tier { get; set; } = LanCloudTier.Solid;

    /// <summary>Display ordering along the WAN chain: 0 = access cloud, 1 = first transit, ...</summary>
    public int Order { get; set; }

    /// <summary>The WAN interface this cloud belongs to (e.g. "wan" / "wan2").</summary>
    public string? WanInterface { get; set; }

    // ---- Access ISP cloud only fields ----

    /// <summary>"Gpon" / "XgsPon" / "Docsis" / "Pppoe" / etc. from MonitoringPathView.AccessIspCloud.</summary>
    public string? AccessTechnology { get; set; }

    /// <summary>Vendor name from the L2 neighbour's OUI ("Calix", "Arris", ...).</summary>
    public string? L2NeighborOui { get; set; }

    /// <summary>True when the WAN sits behind CGNAT (no public IPv4).</summary>
    public bool IsCgnat { get; set; }

    /// <summary>Discovery pending state - the access cloud frame is real but
    /// upstream Hops haven't been resolved yet (tracer wizard not run / in progress).</summary>
    public bool IsDiscoveryPending { get; set; }
}

public class LinkLiveRates
{
    // Direction convention locked per spec 5.7.1 (review 2026-05-21):
    //   Downstream particles flow gateway -> device, rendered blue (--speed-download-color).
    //   Upstream particles flow device -> gateway, rendered green (--speed-upload-color).
    // Per-link mapping the server pre-resolves:
    //   - Wired infra/client (SNMP):   DownstreamBps = bytes_out from upstream port (switch -> device).
    //                                  UpstreamBps   = bytes_in  on upstream port (device -> switch).
    //   - WiFi client (UniFi API):     DownstreamBps = AP TX (to client).
    //                                  UpstreamBps   = AP RX (from client).
    //   - WAN (gateway port):          DownstreamBps = gateway download (internet -> gateway).
    //                                  UpstreamBps   = gateway upload   (gateway -> internet).

    /// <summary>Bits-per-second flowing toward the device (gateway -> device).
    /// Rendered with the blue (--speed-download-color) particle stream.</summary>
    public double DownstreamBps { get; set; }

    /// <summary>Bits-per-second flowing away from the device (device -> gateway).
    /// Rendered with the green (--speed-upload-color) particle stream.</summary>
    public double UpstreamBps { get; set; }

    public long? ErrorsIn { get; set; }
    public long? ErrorsOut { get; set; }

    public DateTime AsOf { get; set; }
}

public class SpeedTestOverlayItem
{
    public int Id { get; set; }
    public DateTime TestTime { get; set; }

    /// <summary>"wan" or "lan" - controls which path the overlay decorates.</summary>
    public required string TestType { get; set; }

    /// <summary>WAN network group ("wan" / "wan2") for WAN tests when known. Null tags
    /// the test as "applies to the primary WAN" (fallback default).</summary>
    public string? WanNetworkGroup { get; set; }

    public double? DownloadMbps { get; set; }
    public double? UploadMbps { get; set; }

    /// <summary>Ordered hops with directional throughput, ready to paint per spec 5.7.2.
    /// Direction values are pre-resolved on the server so the JS layer never has to
    /// remember which property maps to which direction on which hop type.</summary>
    public List<SpeedTestHop> Hops { get; set; } = new();
}

public class SpeedTestHop
{
    public string? DeviceMac { get; set; }
    public string? HopType { get; set; }

    /// <summary>Pre-resolved bits-per-second toward the device (blue, From Device).
    /// Server already applied the CLAUDE.md "Speed Test Directional Concepts" mapping:
    /// wireless hop -> EgressSpeedMbps, WAN/VPN hop -> IngressSpeedMbps, wired -> symmetric.</summary>
    public double? FromDeviceBps { get; set; }

    /// <summary>Pre-resolved bits-per-second away from the device (green, To Device).
    /// wireless hop -> IngressSpeedMbps, WAN/VPN hop -> EgressSpeedMbps, wired -> symmetric.</summary>
    public double? ToDeviceBps { get; set; }
}

public class LanFlowMapBounds
{
    /// <summary>Maximum extent of anchor coordinates so the JS layout can normalise.</summary>
    public double Radius { get; set; } = 1.0;
    public int AnchorCount { get; set; }
}

/// <summary>
/// Delta payload returned by the /live endpoint. JS layer merges into its local state.
/// </summary>
public class LanFlowMapLiveUpdate
{
    public DateTime AsOf { get; set; }
    public Dictionary<string, LinkLiveRates> LinkRates { get; set; } = new();
    public Dictionary<string, NodeLiveBadge> NodeBadges { get; set; } = new();
    public Dictionary<string, CloudLiveStats> CloudStats { get; set; } = new();
}

public class NodeLiveBadge
{
    public double? AggregateInBps { get; set; }
    public double? AggregateOutBps { get; set; }
    /// <summary>Switch fabric sum(rx)/sum(tx) across every port_table entry.
    /// Populated only for switches; the 3D map's node label prefers these
    /// over the trunk-only Aggregate{In,Out}Bps so multi-trunk switches
    /// don't under-count egress.</summary>
    public double? FabricIngressBps { get; set; }
    public double? FabricEgressBps { get; set; }
    public bool Online { get; set; }
    public double? CpuPercent { get; set; }
    public double? MemoryUsedPercent { get; set; }
    public double? TemperatureC { get; set; }
    public long? UptimeSeconds { get; set; }
}

public class CloudLiveStats
{
    public double? RttAvgMs { get; set; }
    public double? LossPercent { get; set; }
    public bool Success { get; set; }
}

/// <summary>
/// Historic snapshot at a point in time, used by the timeline scrubber.
/// Same shape as a live update but at the requested timestamp.
/// </summary>
public class LanFlowMapHistoricUpdate
{
    public DateTime At { get; set; }
    public Dictionary<string, LinkLiveRates> LinkRates { get; set; } = new();
    public Dictionary<string, CloudLiveStats> CloudStats { get; set; } = new();

    /// <summary>Speed tests whose TestTime falls within the scrub window (or just before it).</summary>
    public List<SpeedTestOverlayItem> SpeedTests { get; set; } = new();
}
