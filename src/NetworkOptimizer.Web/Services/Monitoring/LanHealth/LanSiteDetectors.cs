using System.Text.Json;
using NetworkOptimizer.Core;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Diagnostics.Analyzers;
using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.Web.Services.Monitoring.LanHealth;

/// <summary>What the site-wide detectors read: the device list and configuration, plus each link's evaluation.</summary>
public sealed class SiteEvidence
{
    public IReadOnlyList<UniFiDeviceResponse> Devices { get; init; } = [];
    public IReadOnlyList<UniFiNetworkConfig> Networks { get; init; } = [];
    public IReadOnlyList<UniFiPortProfile> PortProfiles { get; init; } = [];
    public IReadOnlyList<UniFiClientResponse> Clients { get; init; } = [];
    public JsonDocument? Settings { get; init; }
    public IReadOnlyList<LinkHealth> Links { get; init; } = [];
    public IReadOnlyDictionary<string, LinkEvidence> Evidence { get; init; } = new Dictionary<string, LinkEvidence>();
    /// <summary>STP state changes per device in the window and per hour over its baseline.</summary>
    public IReadOnlyDictionary<string, (double Window, double BaselinePerHour)> StpChanges { get; init; } = new Dictionary<string, (double, double)>();
    /// <summary>Per device: CPU and memory peaks in the window, temperature now.</summary>
    public IReadOnlyDictionary<string, (double? Cpu, double? Memory, double? TempC)> DeviceHealth { get; init; } = new Dictionary<string, (double?, double?, double?)>();
    /// <summary>MAC addresses seen on more than one switch port within minutes, with how often.</summary>
    public IReadOnlyList<(string Mac, string Name, int Moves, IReadOnlyList<string> Ports)> MacMoves { get; init; } = [];
    /// <summary>Gateway LAN speed tests in the window vs their usual, for detector 32.</summary>
    public (int Tests, double RatioToNorm)? GatewayForwarding { get; init; }
    /// <summary>Inform failures per AP in the window, while its LAN link stayed up.</summary>
    public IReadOnlyDictionary<string, int> InformFailures { get; init; } = new Dictionary<string, int>();
    /// <summary>Duplicate IPs (two MACs answering for one IP) from the client list.</summary>
    public IReadOnlyList<(string Ip, IReadOnlyList<string> Macs)> DuplicateIps { get; init; } = [];
    public FindingText Text { get; init; } = FindingText.Utc;
    public double WindowHours { get; init; } = 48;
}

/// <summary>
/// The detectors that look across links and devices: loops, STP churn and configuration, LACP,
/// native VLAN and MTU consistency (the Config Optimizer checks, called, not copied), multicast
/// hygiene, bottleneck uplinks, hairpin load, management plane, device health, duplicate IPs,
/// capacity trend, and gateway forwarding. All are findings at 0 pts except where the spec scores
/// them (a loop is a storm; gateway forwarding scores the gateway's links).
/// </summary>
public static class LanSiteDetectors
{
    public static List<LinkFinding> Run(SiteEvidence s)
    {
        var f = new List<LinkFinding>();
        f.AddRange(Loop(s));
        f.AddRange(StpChurn(s));
        f.AddRange(StpConfig(s));
        f.AddRange(Lacp(s));
        f.AddRange(NativeVlanMismatch(s));
        f.AddRange(LinkConfig(s));
        f.AddRange(MulticastHygiene(s));
        f.AddRange(Bottleneck(s));
        f.AddRange(HairpinLoad(s));
        f.AddRange(ManagementPlane(s));
        f.AddRange(FabricDeviceHealth(s));
        f.AddRange(MacMoves(s));
        f.AddRange(DuplicateIps(s));
        f.AddRange(CapacityTrend(s));
        f.AddRange(GatewayForwarding(s));
        f.AddRange(ProfileChurn(s));
        foreach (var x in f) if (!Detectors.Scores(x.Detector)) x.Points = 0;
        return f;
    }

    private static LinkFinding Site(int detector, string cls, string subject, string body, FindingSeverity severity = FindingSeverity.Info,
        string linkId = "", double points = 0, LinkStatus? status = null, params string[] chips) => new()
        {
            Detector = detector,
            LinkId = linkId,
            Severity = severity,
            Class = cls,
            Title = $"{cls}: {subject}",
            Body = body,
            Chips = chips.ToList(),
            Points = points,
            Status = status,
        };

    // ---- 9 Loop: a storm on several ports at once, with STP changes ----
    internal static IEnumerable<LinkFinding> Loop(SiteEvidence s)
    {
        var storms = s.Links.SelectMany(l => l.Findings).Where(f => f.Class == "Broadcast Storm").ToList();
        if (storms.Count < 2) yield break;
        var churn = s.StpChanges.Values.Any(v => v.Window > Math.Max(5, v.BaselinePerHour * s.WindowHours * 3));
        if (!churn) yield break;
        // One alert for the loop, not one per port: the per-port storms fold into it.
        yield return Site(Detectors.L2Instability, "Network Loop", $"{storms.Count} ports",
            $"Broadcast storms on {storms.Count} ports at once, with STP changes: a loop. Look for a cable between two ports, or a switch or AP connected twice.",
            FindingSeverity.Critical, storms[0].LinkId, 60, LinkStatus.Storm, "Loop");
    }

    // ---- 19 STP churn ----
    internal static IEnumerable<LinkFinding> StpChurn(SiteEvidence s)
    {
        foreach (var (mac, v) in s.StpChanges)
        {
            var expected = v.BaselinePerHour * s.WindowHours;
            if (v.Window < 10 || v.Window < expected * 3) continue;
            var name = DeviceName(s, mac);
            yield return Site(Detectors.StpChurn, "STP Churn", name,
                $"{v.Window:0} STP state changes in {s.Text.Range}, against {expected:0} normally. Each one floods traffic until the switches relearn MACs.",
                chips: "STP");
        }
    }

    // ---- STP configuration checks (suggestions only) ----
    [VendorSpecific("UniFi", "stp_priority, stp_version, stp_role, stp_edge_port, port_overrides")]
    internal static IEnumerable<LinkFinding> StpConfig(SiteEvidence s)
    {
        var switches = s.Devices.Where(d => d.DeviceType == DeviceType.Switch && d.Adopted).ToList();
        if (switches.Count == 0) yield break;

        // Root left to chance: every priority at the default, so the lowest MAC wins.
        var priorities = switches.Select(d => d.StpPriority ?? 32768).ToList();
        if (switches.Count >= 2 && priorities.All(p => p == 32768))
            yield return Site(Detectors.StpConfig, "STP Root Left to Chance", "all switches",
                "Every switch has the default STP priority, so the lowest MAC address wins root. Give your core switch the lowest priority, and a backup the next.",
                chips: "STP");

        // Root placement: the root should be the gateway-adjacent switch, never one behind a mesh link or a Flex Mini.
        var root = switches.OrderBy(d => d.StpPriority ?? 32768).ThenBy(d => d.Mac, StringComparer.OrdinalIgnoreCase).First();
        var behindMesh = string.Equals(root.Uplink?.Type, "wireless", StringComparison.OrdinalIgnoreCase);
        var flexMini = root.FriendlyModelName.Contains("Flex Mini", StringComparison.OrdinalIgnoreCase);
        var depth = root.UplinkDepth ?? 1;
        if (switches.Count >= 2 && (behindMesh || flexMini || depth > 2))
            yield return Site(Detectors.StpConfig, "STP Root Placement", root.Name,
                $"{root.Name} is the STP root{(behindMesh ? ", behind a mesh link" : flexMini ? ", and it's a Flex Mini" : $", {depth} hops from the gateway")}. The root should be the core switch next to the gateway.",
                chips: "STP");

        // Mixed STP versions in one L2 domain.
        var versions = switches.Select(d => (d.StpVersion ?? "rstp").ToLowerInvariant()).Distinct().ToList();
        if (versions.Count > 1)
            yield return Site(Detectors.StpConfig, "Mixed STP Versions", string.Join(", ", versions.Select(v => v.ToUpperInvariant())),
                "Your switches run different STP versions. Set them all to the same one.", chips: "STP");

        foreach (var d in switches)
        {
            var (configured, detected) = d.GetPortStpOverrides();
            var lldp = d.LldpTable?.Count(e => e.IsWired) ?? 0;

            // STP disabled with redundancy: more than one switch-facing link to the rest of the network.
            if (string.Equals(d.StpVersion, "disabled", StringComparison.OrdinalIgnoreCase) && lldp >= 2)
                yield return Site(Detectors.StpConfig, "STP Disabled with Redundancy", d.Name,
                    $"STP is off on {d.Name}, and LLDP shows {lldp} neighbors. A second path between switches will loop.", FindingSeverity.Warning, chips: "STP");

            foreach (var port in d.PortTable ?? [])
            {
                var link = s.Links.FirstOrDefault(l => l.Link.Upstream.DeviceMac == LanLinkBuilder.Mac(d.Mac) && l.Link.Upstream.PortIdx == port.PortIdx);
                var conf = configured.FirstOrDefault(o => o.PortIdx == port.PortIdx);
                if (link == null) continue;
                // The port's configured mode decides; the switch's live stp_edge flag only where nothing is
                // configured, since it can lag a change (it still read Edge after Change Control saw it set off).
                var edge = !string.IsNullOrEmpty(conf?.StpEdgeState)
                    ? string.Equals(conf.StpEdgeState, "enabled", StringComparison.OrdinalIgnoreCase)
                    : port.StpEdgePort == true;
                // Edge on a fabric port: a loop waiting to happen, but only toward a device that forwards
                // frames between ports (a switch, an AP, a bridge). A 5G Backup or other leaf device is an
                // edge device: its GRE tunnel rides the LAN, or it is a WAN port on the gateway.
                if (link.Link.IsFabric && link.Link.Downstream.DeviceType is DeviceType.Switch or DeviceType.AccessPoint
                        or DeviceType.BuildingBridge or DeviceType.DeviceBridge
                    && edge)
                    yield return Site(Detectors.StpConfig, "Edge Port on a Fabric Link", link.Link.Name,
                        "STP is set to Edge, but a UniFi device is plugged in. Set it to Infrastructure.", FindingSeverity.Warning, link.Link.Id, chips: "STP");
                // Infrastructure forced on an access port: every client link-up waits 30 s or more.
                if (!link.Link.IsFabric && string.Equals(conf?.StpEdgeState, "disabled", StringComparison.OrdinalIgnoreCase))
                    yield return Site(Detectors.StpConfig, "Infrastructure Mode on an Access Port", link.Link.Name,
                        "STP is set to Infrastructure on a client port, so each link-up waits 30 seconds or more. Set it to Edge or Auto.", linkId: link.Link.Id, chips: "STP");
                // BPDU guard off on a client-facing port where a user can plug in a switch.
                if (!link.Link.IsFabric && conf != null && conf.StpBpduGuardEnabled == false && edge)
                    yield return Site(Detectors.StpConfig, "BPDU Guard Off", link.Link.Name,
                        "BPDU guard is off on this client port, so a switch plugged in here joins STP.", linkId: link.Link.Id, chips: "STP");
                // A blocked port that was not meant as redundancy.
                if (port.Up && (string.Equals(port.StpState, "blocking", StringComparison.OrdinalIgnoreCase) || string.Equals(port.StpRole, "alternate", StringComparison.OrdinalIgnoreCase)))
                    yield return Site(Detectors.StpConfig, "Blocked Port", link.Link.Name,
                        "STP is blocking this port: there's a second path between the same switches. Remove the extra cable unless it's for redundancy.", linkId: link.Link.Id, chips: "STP");
                // Path cost overrides that send traffic down the slower path.
                if (port.StpPathCost is int cost && cost > 0 && link.Link.SpeedMbps is int mbps && mbps >= 10000 && cost > 20000)
                    yield return Site(Detectors.StpConfig, "STP Path Cost", link.Link.Name,
                        $"This port's STP path cost is {cost}, which is high for a {DisplayFormatters.FormatLinkSpeed(mbps)} link, so STP may send traffic over a slower path.", linkId: link.Link.Id, chips: "STP");
                _ = detected;
            }
        }
    }

    // ---- 10 / 20 LACP: a member down, members at different speeds, hash imbalance ----
    internal static IEnumerable<LinkFinding> Lacp(SiteEvidence s)
    {
        foreach (var d in s.Devices.Where(d => d.PortTable != null))
        {
            var groups = d.PortTable!.Where(p => p.LagIdx is > 0).GroupBy(p => p.LagIdx!.Value);
            foreach (var g in groups)
            {
                var members = g.ToList();
                if (members.Count < 2) continue;
                var subject = $"{d.Name} LAG {g.Key}";
                var down = members.Where(m => m.Enable && !m.Up).ToList();
                if (down.Count > 0)
                    yield return Site(Detectors.LacpMemberDrop, "LACP Member Down", subject,
                        $"{down.Count} of {members.Count} aggregate members are down ({string.Join(", ", down.Select(m => $"Port {m.PortIdx}"))}).",
                        FindingSeverity.Warning, points: 30, chips: "LACP");
                var speeds = members.Where(m => m.Up).Select(m => m.Speed).Distinct().ToList();
                if (speeds.Count > 1)
                    yield return Site(Detectors.LacpHealth, "LACP Speed Mismatch", subject,
                        $"The aggregate's members are running at different speeds ({string.Join(", ", speeds.Select(DisplayFormatters.FormatLinkSpeed))}).", chips: "LACP");
                var bytes = members.Where(m => m.Up).Select(m => (double)(m.TxBytes + m.RxBytes)).ToList();
                if (bytes.Count > 1 && bytes.Sum() > 1e11 && bytes.Max() > bytes.Sum() * 0.85)
                    yield return Site(Detectors.LacpHealth, "LACP Imbalance", subject,
                        "One aggregate member is carrying most of the traffic while the others sit nearly idle. The hashing is putting most flows on one link.", chips: "LACP");
            }
        }
    }

    // ---- 21 Native VLAN mismatch (trunk consistency is the Config Optimizer's analyzer) ----
    internal static IEnumerable<LinkFinding> NativeVlanMismatch(SiteEvidence s)
    {
        foreach (var l in s.Links.Where(l => l.Link.Kind == LinkKind.FabricWired && l.Link.DownstreamPort != null))
        {
            var up = l.Link.UpstreamPort.NativeNetworkId;
            var down = l.Link.DownstreamPort!.NativeNetworkId;
            if (string.IsNullOrEmpty(up) || string.IsNullOrEmpty(down) || up == down) continue;
            var upName = s.Networks.FirstOrDefault(n => n.Id == up)?.Name ?? up;
            var downName = s.Networks.FirstOrDefault(n => n.Id == down)?.Name ?? down;
            yield return Site(Detectors.NativeVlanMismatch, "Native VLAN Mismatch", l.Link.Name,
                $"The two ends use different native networks: {upName} on {l.Link.Upstream.DeviceName}, {downName} on {l.Link.Downstream.DeviceName}.",
                FindingSeverity.Warning, l.Link.Id, chips: "VLAN");
        }

        if (s.Devices.Count == 0 || s.Networks.Count == 0) yield break;
        List<Diagnostics.Models.TrunkConsistencyIssue> trunk;
        try { trunk = new TrunkConsistencyAnalyzer().Analyze(s.Devices, s.PortProfiles, s.Networks); }
        catch (Exception) { yield break; }
        foreach (var issue in trunk)
        {
            var link = s.Links.FirstOrDefault(l => l.Link.Kind == LinkKind.FabricWired
                && (Same(l.Link.Upstream.DeviceMac, issue.Link.DeviceAMac) && Same(l.Link.Downstream.DeviceMac, issue.Link.DeviceBMac)
                    || Same(l.Link.Upstream.DeviceMac, issue.Link.DeviceBMac) && Same(l.Link.Downstream.DeviceMac, issue.Link.DeviceAMac)));
            var subject = link?.Link.Name ?? $"{issue.Link.DeviceAName} to {issue.Link.DeviceBName}";
            yield return Site(Detectors.NativeVlanMismatch, "Trunk VLAN Mismatch", subject,
                $"{string.Join(", ", issue.Mismatches.Select(m => m.NetworkName))} is allowed on one end of this trunk and blocked on the other. {issue.Recommendation}",
                FindingSeverity.Info, link?.Link.Id ?? "", chips: "VLAN");
        }
    }

    // ---- Flow Control / Jumbo Frames consistency (Config Optimizer's checks, called) ----
    /// <summary>
    /// Config Optimizer's Jumbo Frames and Flow Control checks plus the per-link jumbo comparison, one
    /// finding per cause: a device's "Jumbo Frames Disabled" result folds into the link findings it
    /// causes, as their fix, instead of showing beside them.
    /// </summary>
    internal static List<LinkFinding> LinkConfig(SiteEvidence s)
    {
        var config = ConfigOptimizerIssues(s);
        var disabled = config.Where(i => i.Title.StartsWith("Jumbo Frames Disabled on ", StringComparison.Ordinal) && i.DeviceName != null)
            .GroupBy(i => i.DeviceName!).ToDictionary(g => g.Key, g => g.First());
        var links = MtuOnPath(s, disabled).ToList();
        var folded = links.SelectMany(f => disabled.Keys.Where(d => f.Body.Contains(d, StringComparison.Ordinal))).ToHashSet();
        var result = config
            .Where(i => !(i.DeviceName != null && folded.Contains(i.DeviceName) && disabled.ContainsKey(i.DeviceName)))
            .Select(i => Site(i.Title.Contains("Jumbo", StringComparison.OrdinalIgnoreCase) ? Detectors.MtuMismatch : Detectors.FlowControl,
                i.Title.Contains("Jumbo", StringComparison.OrdinalIgnoreCase) ? "Jumbo Frames" : "Flow Control",
                i.DeviceName ?? "site", $"{i.Description} {i.Recommendation}".Trim(), chips: "Config"))
            .ToList();
        result.AddRange(links);
        return result;
    }

    private static List<Diagnostics.Models.PerformanceIssue> ConfigOptimizerIssues(SiteEvidence s)
    {
        if (s.Devices.Count == 0) return [];
        try
        {
            var analyzer = new PerformanceAnalyzer(new NetworkOptimizer.Audit.Services.DeviceTypeDetectionService());
            return analyzer.AnalyzeLinkConfig(s.Devices.ToList(), s.Networks.ToList(), s.Clients.ToList(), s.Settings, s.PortProfiles.ToList());
        }
        catch (Exception) { return []; }
    }

    // ---- 22 MTU / jumbo on a path: jumbo on some hops only ----
    internal static IEnumerable<LinkFinding> MtuOnPath(SiteEvidence s,
        IReadOnlyDictionary<string, Diagnostics.Models.PerformanceIssue>? disabledByDevice = null)
    {
        // Compared only where both devices report a jumbo frame setting: an AP's port reads jumbo off
        // with no setting to change, so a mismatch there would suggest something nobody can enable.
        foreach (var l in s.Links.Where(l => l.Link.Kind == LinkKind.FabricWired
                     && l.Link.Upstream.HasJumboSetting && l.Link.Downstream.HasJumboSetting))
        {
            var up = l.Link.UpstreamPort.Jumbo;
            var down = l.Link.DownstreamPort?.Jumbo;
            if (up == null || down == null || up == down) continue;
            var body = $"Jumbo frames are {(up == true ? "on" : "off")} on {l.Link.Upstream.DeviceName} and {(down == true ? "on" : "off")} on {l.Link.Downstream.DeviceName}, so large frames get dropped on this link.";
            // The cause, when Config Optimizer found it: that device is off the global setting.
            var cause = disabledByDevice == null ? null
                : disabledByDevice.GetValueOrDefault(l.Link.Upstream.DeviceName) ?? disabledByDevice.GetValueOrDefault(l.Link.Downstream.DeviceName);
            if (cause != null) body += $" {cause.Description} {cause.Recommendation}";
            yield return Site(Detectors.MtuMismatch, "Jumbo Frames Mismatch", l.Link.Name, body.Trim(),
                FindingSeverity.Warning, l.Link.Id, chips: "MTU");
        }
    }

    // ---- 23 Multicast hygiene ----
    [VendorSpecific("UniFi", "setting igmp_snooping (enabled, network_ids, querier_mode); networkconf igmp_snooping on older releases")]
    internal static IEnumerable<LinkFinding> MulticastHygiene(SiteEvidence s)
    {
        var heavy = s.Evidence.Values.Where(e => e.Hours.Any(h => h.MulticastPps is > 500 && h.PacketsPps is > 0 && h.MulticastPps / h.PacketsPps > 0.3)).ToList();
        var igmp = IgmpSnooping(s.Settings);
        bool Snooping(UniFiNetworkConfig n) => n.IgmpSnooping || (igmp.Enabled && igmp.NetworkIds.Contains(n.Id));
        var snoopingOff = s.Networks.Where(n => n.Purpose is "corporate" or "guest" && !Snooping(n)).ToList();
        if (heavy.Count > 0 && snoopingOff.Count > 0)
            yield return Site(Detectors.MulticastHygiene, "Multicast Flooding", string.Join(", ", snoopingOff.Select(n => n.Name)),
                $"Heavy multicast on {heavy.Count} {(heavy.Count == 1 ? "link" : "links")} with IGMP snooping off, so every port receives every stream.", chips: "Multicast");
        // UniFi sets the querier to PRIMARY_AND_FAILOVER when snooping is turned on; OFF leaves the networks with none.
        var snoopingOn = s.Networks.Where(Snooping).ToList();
        if (snoopingOn.Count > 0 && string.Equals(igmp.QuerierMode, "OFF", StringComparison.OrdinalIgnoreCase))
            yield return Site(Detectors.MulticastHygiene, "No IGMP Querier", string.Join(", ", snoopingOn.Select(n => n.Name)),
                "IGMP snooping is on, but there's no IGMP querier, so group memberships time out and multicast streams stop.", chips: "Multicast");
    }

    // ---- 15 Bottleneck uplink / oversubscription ----
    internal static IEnumerable<LinkFinding> Bottleneck(SiteEvidence s)
    {
        foreach (var l in s.Links.Where(l => l.Link.Kind == LinkKind.FabricWired && l.Link.SpeedMbps is > 0))
        {
            // Compared as usable capacity with the speed test path's overhead factors: a mesh backhaul
            // carries about 55% of its PHY rate, a wired link about 94%.
            static double Usable(LinkRecord r) => r.SpeedMbps!.Value
                * (r.IsMesh ? NetworkOptimizer.UniFi.Models.PathAnalysisResult.MeshBackhaulOverheadFactor
                            : NetworkOptimizer.UniFi.Models.PathAnalysisResult.WiredOverheadFactor);
            var below = s.Links.Where(c => c.Link.IsFabric && c.Link.Upstream.DeviceMac == l.Link.Downstream.DeviceMac && c.Link.SpeedMbps is > 0)
                .OrderByDescending(c => Usable(c.Link)).FirstOrDefault();
            if (below == null || Usable(below.Link) <= Usable(l.Link)) continue;
            var fastest = below.Link.SpeedMbps!.Value;
            var usableNote = below.Link.IsMesh ? $" (about {DisplayFormatters.FormatLinkSpeed((int)Math.Round(Usable(below.Link)))} usable)" : "";
            var peak = s.Evidence.TryGetValue(l.Link.Id, out var ev) ? ev.Hours.Select(h => LanHealthDetectors.Utilization(l.Link, h)).DefaultIfEmpty(0).Max() : 0;
            yield return Site(Detectors.BottleneckUplink, "Bottleneck Uplink", l.Link.Name,
                $"{l.Link.Downstream.DeviceName} uplinks at {DisplayFormatters.FormatLinkSpeed(l.Link.SpeedMbps!.Value)} while a link downstream of it runs at {DisplayFormatters.FormatLinkSpeed(fastest)}{usableNote}"
                + (peak > 0 ? $". Its busiest hour saturated {peak * 100:0}% of the uplink." : "."),
                linkId: l.Link.Id, chips: "Capacity");
        }
    }

    // ---- 24 Inter-VLAN hairpin load on the gateway trunk ----
    internal static IEnumerable<LinkFinding> HairpinLoad(SiteEvidence s)
    {
        var gateway = s.Devices.FirstOrDefault(d => d.DeviceType == DeviceType.Gateway);
        if (gateway == null) yield break;
        var gwMac = LanLinkBuilder.Mac(gateway.Mac);
        var trunk = s.Links.Where(l => l.Link.Kind == LinkKind.FabricWired && l.Link.Upstream.DeviceMac == gwMac)
            .OrderByDescending(l => l.Link.ClientsBehind).FirstOrDefault();
        if (trunk == null || !s.Evidence.TryGetValue(trunk.Link.Id, out var ev)) yield break;
        var cpu = s.DeviceHealth.TryGetValue(gwMac, out var h) ? h.Cpu : null;
        var both = ev.Hours.Where(x => (x.MaxInBps ?? 0) > 0 && (x.MaxOutBps ?? 0) > 0
            && Math.Min(x.MaxInBps!.Value, x.MaxOutBps!.Value) / Math.Max(x.MaxInBps.Value, x.MaxOutBps.Value) > 0.8
            && LanHealthDetectors.Utilization(trunk.Link, x) > 0.4).ToList();
        if (both.Count < 2 || cpu is not >= 70) yield break;
        yield return Site(Detectors.HairpinLoad, "Inter-VLAN Hairpin", trunk.Link.Name,
            $"For {both.Count} hours the gateway trunk ran nearly equal both ways with the gateway CPU at {cpu:0}%: traffic between VLANs crosses it twice.",
            linkId: trunk.Link.Id, chips: "Capacity");
    }

    // ---- 27 Management plane: inform failures while the LAN link stays up ----
    internal static IEnumerable<LinkFinding> ManagementPlane(SiteEvidence s)
    {
        foreach (var (mac, count) in s.InformFailures)
        {
            if (count < 3) continue;
            var link = s.Links.FirstOrDefault(l => l.Link.IsFabric && l.Link.Downstream.DeviceMac == mac);
            if (link != null && link.Drops > 0) continue;
            yield return Site(Detectors.ManagementPlane, "Console Reachability", DeviceName(s, mac),
                $"{count} inform failures to the UniFi Console in {s.Text.Range}, while its LAN link stayed up.",
                linkId: link?.Link.Id ?? "", chips: "Management");
        }
    }

    // ---- 29 Device health on the fabric (switches: CPU, memory, temperature; firmware drift) ----
    internal static IEnumerable<LinkFinding> FabricDeviceHealth(SiteEvidence s)
    {
        foreach (var d in s.Devices.Where(d => d.DeviceType == DeviceType.Switch && d.Adopted))
        {
            if (!s.DeviceHealth.TryGetValue(LanLinkBuilder.Mac(d.Mac), out var h)) continue;
            if (h.Cpu is >= 90 || h.Memory is >= 95 || h.TempC is >= 80)
                yield return Site(Detectors.FabricDeviceHealth, "Switch Health", d.Name,
                    string.Join(" ", new[]
                    {
                        h.Cpu is >= 90 ? $"CPU is at {h.Cpu:0}%." : null,
                        h.Memory is >= 95 ? $"Memory is at {h.Memory:0}%." : null,
                        h.TempC is >= 80 ? $"Temperature is {h.TempC:0} °C." : null,
                    }.Where(x => x != null)), chips: "Device");
        }
        foreach (var g in s.Devices.Where(d => d.Adopted && !string.IsNullOrEmpty(d.Model)).GroupBy(d => d.Model))
        {
            // By version only: one build reads "6.5.87" on one device and "6.5.87.14874" on another (FirmwareVersionFormat.SameBuild).
            var versions = g.Select(d => d.DisplayableVersion ?? d.Version).Where(v => !string.IsNullOrEmpty(v))
                .Select(v => NetworkOptimizer.Core.Helpers.FirmwareVersionFormat.Short(v!)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (versions.Count > 1)
                yield return Site(Detectors.FabricDeviceHealth, "Firmware Drift", g.First().FriendlyModelName,
                    $"{g.Count()} devices of the same model are running {versions.Count} different firmware versions ({string.Join(", ", versions)}).", chips: "Firmware");
        }
    }

    // ---- 18 MAC moves / flapping ----
    internal static IEnumerable<LinkFinding> MacMoves(SiteEvidence s)
    {
        foreach (var m in s.MacMoves.Where(m => m.Moves >= 3))
            yield return Site(Detectors.MacMoves, "MAC Flapping", m.Name,
                $"{m.Name} moved between {string.Join(" and ", m.Ports)} {m.Moves} times in {s.Text.Range}. That points to a loop, a duplicate MAC, a bridged VM, or a misconfigured bond.",
                FindingSeverity.Warning, chips: "L2");
    }

    // ---- 30 Duplicate IP ----
    internal static IEnumerable<LinkFinding> DuplicateIps(SiteEvidence s)
    {
        foreach (var d in s.DuplicateIps)
            yield return Site(Detectors.DuplicateIp, "Duplicate IP", d.Ip,
                $"{d.Macs.Count} devices are answering for {d.Ip} ({string.Join(", ", d.Macs)}).", FindingSeverity.Warning, chips: "IP");
    }

    // ---- 31 Capacity trend: p95 growth toward 50% / 80% ----
    internal static IEnumerable<LinkFinding> CapacityTrend(SiteEvidence s)
    {
        foreach (var (id, ev) in s.Evidence)
        {
            if (!ev.Link.IsFabric || ev.BaselineHours.Count < 24 * 14) continue;
            var weeks = ev.BaselineHours.Concat(ev.Hours).GroupBy(h => (int)((h.Hour - ev.BaselineHours[0].Hour).TotalDays / 7))
                .Select(g => (Week: g.Key, P95: LanHealthDetectors.Percentile(g.Select(h => LanHealthDetectors.Utilization(ev.Link, h)).ToList(), 0.95) ?? 0))
                .OrderBy(w => w.Week).ToList();
            if (weeks.Count < 3) continue;
            var slope = (weeks[^1].P95 - weeks[0].P95) / Math.Max(1, weeks[^1].Week - weeks[0].Week);
            var now = weeks[^1].P95;
            if (slope <= 0.01 || now >= 0.8) continue;
            var target = now < 0.5 ? 0.5 : 0.8;
            var weeksLeft = (target - now) / slope;
            if (weeksLeft > 26) continue;
            yield return Site(Detectors.CapacityTrend, "Capacity Trend", ev.Link.Name,
                $"Busy-hour load on this link is growing about {slope * 100:0.#} points a week. If that continues, it will pass {target * 100:0}% of the link in about {Math.Ceiling(weeksLeft):0} weeks.",
                linkId: id, chips: "Capacity");
        }
    }

    // ---- 32 Gateway forwarding degradation ----
    internal static IEnumerable<LinkFinding> GatewayForwarding(SiteEvidence s)
    {
        if (s.GatewayForwarding is not { } g || g.Tests < 2 || g.RatioToNorm >= 0.6) yield break;
        var gateway = s.Devices.FirstOrDefault(d => d.DeviceType == DeviceType.Gateway);
        var link = s.Links.Where(l => gateway != null && l.Link.Upstream.DeviceMac == LanLinkBuilder.Mac(gateway.Mac) && l.Link.IsFabric)
            .OrderByDescending(l => l.Link.ClientsBehind).FirstOrDefault();
        yield return Site(Detectors.GatewayForwarding, "Gateway Forwarding", gateway?.Name ?? "Gateway",
            $"{g.Tests} LAN speed tests through the gateway averaged {g.RatioToNorm * 100:0}% of normal. A gateway low on memory or /tmp space forwards slowly until it restarts.",
            FindingSeverity.Warning, link?.Link.Id ?? "", 20, chips: "Gateway");
    }

    // ---- Exclusions: suggest one where most access ports on a profile churn the same way ----
    internal static IEnumerable<LinkFinding> ProfileChurn(SiteEvidence s)
    {
        foreach (var g in s.Links.Where(l => l.Link.Kind == LinkKind.Access && !string.IsNullOrEmpty(l.Link.UpstreamPort.PortProfileId) && l.Exclusion == null)
                     .GroupBy(l => l.Link.UpstreamPort.PortProfileId!))
        {
            var ports = g.ToList();
            var churning = ports.Count(l => l.Drops + l.DropsExplained >= LanHealthDetectors.FlappingDropsAccess);
            if (ports.Count < 3 || churning * 10 < ports.Count * 6) continue;
            var name = s.PortProfiles.FirstOrDefault(p => p.Id == g.Key)?.Name ?? g.Key;
            yield return Site(Detectors.ChurnByDesign, "Churn by Design", name,
                $"{churning} of {ports.Count} access ports on the {name} profile relink many times a day. If that's expected (guest rooms, a lab), use Mute alerts or Exclude from score on any of its ports.",
                chips: "Exclusions");
        }
    }

    /// <summary>The site's IGMP snooping setting: on or off, the networks it covers, and the querier mode. Settings arrive as an array or under "data".</summary>
    [VendorSpecific("UniFi", "rest/setting key igmp_snooping")]
    internal static (bool Enabled, HashSet<string> NetworkIds, string? QuerierMode) IgmpSnooping(JsonDocument? settings)
    {
        var none = (false, new HashSet<string>(), (string?)null);
        if (settings == null) return none;
        var root = settings.RootElement;
        var items = root.ValueKind == JsonValueKind.Array ? root
            : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Array ? d : default;
        if (items.ValueKind != JsonValueKind.Array) return none;
        foreach (var x in items.EnumerateArray())
        {
            if (x.ValueKind != JsonValueKind.Object || !x.TryGetProperty("key", out var k) || k.GetString() != "igmp_snooping") continue;
            var enabled = x.TryGetProperty("enabled", out var e) && e.ValueKind == JsonValueKind.True;
            var ids = x.TryGetProperty("network_ids", out var n) && n.ValueKind == JsonValueKind.Array
                ? n.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToHashSet() : [];
            var mode = x.TryGetProperty("querier_mode", out var q) && q.ValueKind == JsonValueKind.String ? q.GetString() : null;
            return (enabled, ids, mode);
        }
        return none;
    }

    private static bool Same(string a, string b) => string.Equals(LanLinkBuilder.Mac(a), LanLinkBuilder.Mac(b), StringComparison.Ordinal);

    private static string DeviceName(SiteEvidence s, string mac) =>
        s.Devices.FirstOrDefault(d => Same(d.Mac, mac))?.Name is { Length: > 0 } n ? n : mac;
}
