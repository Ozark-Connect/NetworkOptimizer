using NetworkOptimizer.Core;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.Web.Services.Monitoring.LanHealth;

/// <summary>
/// Builds the site's links from the console's device and client lists: one per wired uplink or mesh
/// backhaul between UniFi devices (from each device's own uplink block), and one per access port with
/// a link. The full device list is used, not the monitorable subset, so a device whose uplink is
/// offline keeps its identity and role.
/// </summary>
public static class LanLinkBuilder
{
    /// <summary>An end device seen on a switch port.</summary>
    public sealed record PortClient(string Mac, string Name, string? SwitchMac, int? SwitchPort, string? ApMac, bool Wired);

    /// <summary>Builds every link. <paramref name="roles"/> maps a MAC to its role when topology changes it (an Express as AP).</summary>
    [VendorSpecific("UniFi", "Reads port_table, uplink and last_uplink blocks")]
    public static List<LinkRecord> Build(
        IReadOnlyList<UniFiDeviceResponse> devices,
        IReadOnlyList<PortClient> clients,
        IReadOnlyDictionary<string, DeviceType>? roles = null,
        IReadOnlyDictionary<string, string>? offlineNames = null)
    {
        var byMac = devices.Where(d => d.Adopted && !string.IsNullOrEmpty(d.Mac))
            .GroupBy(d => Mac(d.Mac)).ToDictionary(g => g.Key, g => g.First());
        DeviceType Role(UniFiDeviceResponse d) => roles != null && roles.TryGetValue(Mac(d.Mac), out var r) ? r : d.DeviceType;

        // Uplink tree: child -> parent, for the clients and devices behind a link.
        var parent = new Dictionary<string, string>();
        foreach (var d in byMac.Values)
        {
            if (Role(d) == DeviceType.Gateway) continue;
            var up = Uplink(d);
            if (up != null && Mac(up.UplinkMac) is { Length: > 0 } p && p != Mac(d.Mac) && byMac.ContainsKey(p))
                parent[Mac(d.Mac)] = p;
        }
        var children = parent.GroupBy(kv => kv.Value).ToDictionary(g => g.Key, g => g.Select(kv => kv.Key).ToList());
        var clientsByDevice = new Dictionary<string, int>();
        foreach (var c in clients)
        {
            var at = Mac(c.Wired ? c.SwitchMac : c.ApMac);
            if (at.Length > 0) clientsByDevice[at] = clientsByDevice.GetValueOrDefault(at) + 1;
        }
        IEnumerable<string> Subtree(string mac)
        {
            var seen = new HashSet<string>();
            var stack = new Stack<string>([mac]);
            while (stack.Count > 0)
            {
                var m = stack.Pop();
                if (!seen.Add(m)) continue;
                yield return m;
                foreach (var c in children.GetValueOrDefault(m) ?? []) stack.Push(c);
            }
        }

        var links = new List<LinkRecord>();
        var fabricPorts = new HashSet<(string, int)>();

        foreach (var d in byMac.Values)
        {
            if (Role(d) == DeviceType.Gateway) continue;
            var up = Uplink(d);
            if (up == null) continue;
            var upMac = Mac(up.UplinkMac);
            if (upMac.Length == 0 || !byMac.TryGetValue(upMac, out var upDevice)) continue;
            var downMac = Mac(d.Mac);
            var subtree = Subtree(downMac).ToList();
            var behind = subtree.Sum(m => clientsByDevice.GetValueOrDefault(m));
            var wireless = string.Equals(up.Type, "wireless", StringComparison.OrdinalIgnoreCase);

            if (wireless)
            {
                var mlo = (up.MloLinks ?? []).Select(m => new MloMember(m.Radio ?? "", m.Channel, m.Signal, m.TxRate, m.RxRate)).ToList();
                links.Add(new LinkRecord
                {
                    Id = MeshId(upMac, downMac),
                    Kind = mlo.Count > 1 ? LinkKind.FabricMeshMlo : LinkKind.FabricMesh,
                    Upstream = End(upDevice, Role(upDevice), null),
                    Downstream = End(d, Role(d), null),
                    SpeedMbps = up.TxRate > 0 ? (int)(up.TxRate / 1000) : null,
                    ClientsBehind = behind,
                    DevicesBehind = subtree.Count,
                    Mlo = mlo,
                    MeshSignalDbm = up.Signal,
                    MeshTxRateKbps = up.TxRate > 0 ? up.TxRate : null,
                    UplinkDepth = d.UplinkDepth,
                });
                continue;
            }

            var upPort = upDevice.PortTable?.FirstOrDefault(p => p.PortIdx == up.UplinkRemotePort);
            var downPort = up.PortIdx is int dp ? d.PortTable?.FirstOrDefault(p => p.PortIdx == dp) : null;
            if (up.UplinkRemotePort > 0) fabricPorts.Add((upMac, up.UplinkRemotePort));
            var upIsGateway = Role(upDevice) == DeviceType.Gateway;
            var portSpeed = LinkSpeedResolver.Negotiated(upIsGateway, upPort?.Speed, null);
            links.Add(new LinkRecord
            {
                Id = WiredId(upMac, up.UplinkRemotePort, downMac, up.PortIdx),
                Kind = LinkKind.FabricWired,
                Upstream = End(upDevice, Role(upDevice), upPort),
                Downstream = End(d, Role(d), downPort, up.PortIdx),
                SpeedMbps = LinkSpeedResolver.WiredLink(portSpeed, up.Speed, false),
                FullDuplex = upPort?.FullDuplex ?? (up.Speed > 0 ? up.FullDuplex : null),
                Forced = upPort is { Autoneg: false },
                CapabilityMbps = LinkSpeedResolver.Capability(PortSpeedCaps.MaxMbps(upPort?.SpeedCaps), PortSpeedCaps.MaxMbps(downPort?.SpeedCaps)),
                ClientsBehind = behind,
                DevicesBehind = subtree.Count,
                UpstreamPort = Config(upPort),
                DownstreamPort = downPort != null ? Config(downPort) : null,
                UplinkDepth = d.UplinkDepth,
            });
        }

        // Access ports: up (or recently used) LAN ports with no UniFi device behind them.
        var wiredByPort = clients.Where(c => c.Wired && c.SwitchPort is > 0 && !string.IsNullOrEmpty(c.SwitchMac))
            .GroupBy(c => (Mac(c.SwitchMac), c.SwitchPort!.Value))
            .ToDictionary(g => g.Key, g => g.ToList());
        var deviceMacs = byMac.Keys.ToHashSet();
        foreach (var d in byMac.Values)
        {
            var role = Role(d);
            if (role is not (DeviceType.Switch or DeviceType.Gateway)) continue;
            var mac = Mac(d.Mac);
            foreach (var port in d.PortTable ?? [])
            {
                if (!LanHealthPorts.IsScored(port, port.IfName, role == DeviceType.Gateway)) continue;
                if (fabricPorts.Contains((mac, port.PortIdx))) continue;
                if (port.AggregatedBy is > 0) continue;
                if (!port.Up && !RecentlyUsed(port)) continue;
                var occupants = wiredByPort.GetValueOrDefault((mac, port.PortIdx)) ?? [];
                // A UniFi device here with no uplink block naming the port is still fabric, not a client.
                if (port.LastConnection?.Mac is { } lc && deviceMacs.Contains(Mac(lc))) continue;
                var client = occupants.Count == 1 ? occupants[0] : null;
                var clientMac = Mac(client?.Mac ?? port.LastConnection?.Mac);
                links.Add(new LinkRecord
                {
                    Id = AccessId(mac, port.PortIdx),
                    Kind = LinkKind.Access,
                    Upstream = End(d, role, port),
                    Downstream = new LinkEnd
                    {
                        DeviceMac = clientMac,
                        DeviceName = client?.Name is { Length: > 0 } n ? n
                            : occupants.Count > 1 ? $"{occupants.Count} clients"
                            : clientMac.Length > 0 ? offlineNames?.GetValueOrDefault(clientMac) ?? clientMac
                            : PortLabel(port),
                        DeviceType = DeviceType.Unknown,
                        IsUniFi = false,
                    },
                    SpeedMbps = LinkSpeedResolver.Negotiated(role == DeviceType.Gateway, port.Up ? port.Speed : null, null),
                    FullDuplex = port.Up ? port.FullDuplex : null,
                    Forced = !port.Autoneg,
                    ClientsBehind = Math.Max(1, occupants.Count),
                    DevicesBehind = 0,
                    UpstreamPort = Config(port),
                    ClientMac = clientMac.Length > 0 ? clientMac : null,
                });
            }
        }
        // The swap step's neighbouring port, and who is on it.
        foreach (var l in links.Where(l => l.Kind == LinkKind.FabricWired && l.Upstream.PortIdx is int))
        {
            var p = l.Upstream.PortIdx!.Value;
            var other = p > 1 ? p - 1 : p + 1;
            l.SwapNeighbor = links.FirstOrDefault(n => n.Upstream.DeviceMac == l.Upstream.DeviceMac && n.Upstream.PortIdx == other)?.Downstream.DeviceName;
        }
        return links;
    }

    private static UplinkInfo? Uplink(UniFiDeviceResponse d) =>
        d.Uplink is { } u && !string.IsNullOrEmpty(u.UplinkMac) ? u : d.LastUplink is { } l && !string.IsNullOrEmpty(l.UplinkMac) ? l : null;

    private static bool RecentlyUsed(SwitchPort port) =>
        port.Enable && port.LastConnection?.LastSeen is long seen
        && DateTimeOffset.FromUnixTimeSeconds(seen).UtcDateTime > DateTime.UtcNow.AddDays(-2);

    private static LinkEnd End(UniFiDeviceResponse d, DeviceType role, SwitchPort? port, int? portIdx = null) => new()
    {
        DeviceMac = Mac(d.Mac),
        DeviceName = string.IsNullOrWhiteSpace(d.Name) ? Mac(d.Mac) : d.Name.Trim(),
        DeviceType = role,
        Model = d.FriendlyModelName,
        PortIdx = port?.PortIdx ?? portIdx,
        HasJumboSetting = d.JumboFrameEnabled.HasValue,
        IfName = port?.IfName,
        PortName = port != null ? PortLabel(port) : null,
    };

    private static string PortLabel(SwitchPort p) => string.IsNullOrWhiteSpace(p.Name) ? $"Port {p.PortIdx}" : p.Name.Trim();

    [VendorSpecific("UniFi", "port_table configuration fields")]
    private static PortConfig Config(SwitchPort? p) => p == null ? new PortConfig() : new PortConfig
    {
        FlowControlRx = p.FlowControlRx,
        FlowControlTx = p.FlowControlTx,
        Jumbo = p.Jumbo,
        StormControlBroadcast = p.StormControlBroadcast,
        StormControlMulticast = p.StormControlMulticast,
        Autoneg = p.Autoneg,
        PortProfileId = p.PortConfId,
        NativeNetworkId = p.NativeNetworkConfId,
        Forward = p.Forward,
        ExcludedNetworkIds = p.ExcludedNetworkConfIds ?? [],
        StpState = p.StpState,
        StpEnabled = p.StpPortMode,
        MacTableCount = p.MacTableCount,
        PoeMode = p.PoeMode,
        PoePowerW = double.TryParse(p.PoePower, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var w) ? w : null,
        PoeClass = p.PoeClass,
        LinkDownCount = p.LinkDownCount,
        Anomalies = p.Anomalies,
        Media = p.Media,
        IsLagMember = p.AggregatedBy is > 0 || p.LagIdx is > 0,
        LagIdx = p.LagIdx,
    };

    /// <summary>A wired fabric link's id.</summary>
    public static string WiredId(string upMac, int upPort, string downMac, int? downPort) => $"{upMac}|{upPort}-{downMac}|{downPort?.ToString() ?? ""}";

    /// <summary>A mesh backhaul's id.</summary>
    public static string MeshId(string upMac, string downMac) => $"{upMac}|mesh-{downMac}|mesh";

    /// <summary>An access port's id.</summary>
    public static string AccessId(string switchMac, int port) => $"{switchMac}|{port}-access";

    internal static string Mac(string? mac) => string.IsNullOrWhiteSpace(mac) ? "" : mac.Trim().ToLowerInvariant().Replace('-', ':');
}
