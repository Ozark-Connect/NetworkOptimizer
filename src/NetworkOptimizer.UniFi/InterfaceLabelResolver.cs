using System.Text.RegularExpressions;
using NetworkOptimizer.Core.Helpers;
using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.UniFi;

/// <summary>
/// Resolves friendly display labels for a gateway's Linux interface names from the
/// authoritative UniFi sources (the device's WAN objects, port table, last_geo_info,
/// mbb cellular state, and the network configuration). Runs agent-side so the
/// resolved map can later be persisted as time series; for now the polling agent
/// caches it in memory and the port stats endpoint reads it.
///
/// Coverage: physical/WAN ports (incl. cellular GRE like "gre1"), WireGuard clients
/// ("wgclt{wireguard_id}"), OpenVPN, SQM shaping ("ifb{parent}") and honeypot/bridge
/// interfaces (by VLAN → network name).
/// </summary>
public static class InterfaceLabelResolver
{
    private static readonly Regex DefaultPortName =
        new(@"^(port|sfp\+?|rj45)\s*\d+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TrailingVlan = new(@"(\d+)$", RegexOptions.Compiled);

    // VLAN sub-interface: "<base>.<vlan>" (e.g. "eth0.100").
    private static readonly Regex SubInterface = new(@"^(.+)\.(\d+)$", RegexOptions.Compiled);

    /// <summary>True for UniFi's generic placeholder port names ("Port 7", "SFP+ 1").</summary>
    public static bool IsDefaultPortName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && DefaultPortName.IsMatch(name.Trim());

    /// <summary>
    /// Builds a Linux-ifname → display-label map for the given interface names, using
    /// the device config and networkconf. Only interfaces we can confidently name are
    /// included; callers fall back to the raw ifname for the rest.
    /// </summary>
    public static Dictionary<string, string> BuildLabels(
        UniFiDeviceResponse device,
        IReadOnlyList<NetworkInfo>? networks,
        IEnumerable<string> ifNames)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (device == null) return result;
        networks ??= Array.Empty<NetworkInfo>();

        var wanMap = BuildWanLabels(device);

        // WireGuard clients: wgclt{wireguard_id} → "{configured tunnel name} (WG VPN)".
        var wgMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in networks)
            if (n.WireguardId is int wid
                && !string.IsNullOrWhiteSpace(n.Name)
                && (n.VpnType?.Contains("wireguard", StringComparison.OrdinalIgnoreCase) ?? false))
                wgMap[$"wgclt{wid}"] = $"{n.Name.Trim()} (WG VPN)";

        // Custom UniFi port names keyed by Linux ifname, for VLAN sub-interface and SQM
        // base labels on non-WAN ports.
        var portNameByIfName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (device.PortTable != null)
            foreach (var p in device.PortTable)
                if (!string.IsNullOrWhiteSpace(p.IfName) && !string.IsNullOrWhiteSpace(p.Name))
                    portNameByIfName[p.IfName!] = p.Name.Trim();

        // OpenVPN client interface naming is firmware-specific; if there's exactly one
        // configured OpenVPN client, use its name, otherwise a generic label.
        var ovpnNames = networks
            .Where(n => n.VpnType?.Contains("openvpn", StringComparison.OrdinalIgnoreCase) ?? false)
            .Select(n => n.Name).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        var ovpnLabel = ovpnNames.Count == 1 ? $"{ovpnNames[0]!.Trim()} (OpenVPN)" : "OpenVPN";

        // VLAN id → corporate/LAN network name (for honeypot/bridge interfaces).
        string? NetworkNameForVlan(int vlan)
        {
            var n = networks.FirstOrDefault(x =>
                !x.IsWan && (x.VlanId ?? 0) == vlan && !string.IsNullOrWhiteSpace(x.Name));
            return n?.Name?.Trim();
        }

        // Recursively resolves a friendly label, or null when we can't confidently name
        // the interface. SQM (ifb*) is checked before the VLAN sub-interface rule so
        // "ifbeth0.100" resolves as "<eth0.100 label> SQM", not as a sub-interface.
        string? Resolve(string ifName)
        {
            if (string.IsNullOrWhiteSpace(ifName)) return null;
            var lower = ifName.ToLowerInvariant();

            if (wgMap.TryGetValue(ifName, out var g)) return g;

            if (lower.StartsWith("ifb"))
            {
                // SQM shaping attached to a parent (ifbeth0.100 → eth0.100); the bare
                // ifb0/ifb1 root devices are left unresolved (and hidden when down).
                var parent = ifName[3..];
                if (parent.Length == 0 || parent.All(char.IsDigit)) return null;
                return $"{BaseOf(parent)} SQM";
            }

            // VLAN sub-interface: "{base port label} ({network name | VLAN id})", e.g.
            // "Office Backhaul (Management)" or "WAN1 - Fiber ISP (Guest)". Checked before
            // the direct WAN lookup so a WAN tagged on the sub-interface itself still
            // surfaces its VLAN id (e.g. "WAN1 - Fiber ISP (100)") instead of the bare
            // WAN label.
            var sub = SubInterface.Match(ifName);
            if (sub.Success)
            {
                var vlan = sub.Groups[2].Value;
                var tag = int.TryParse(vlan, out var v) && NetworkNameForVlan(v) is { } net ? net : vlan;
                var baseLabel = wanMap.TryGetValue(ifName, out var wsub) ? wsub : BaseOf(sub.Groups[1].Value);
                return $"{baseLabel} ({tag})";
            }

            if (wanMap.TryGetValue(ifName, out var w)) return w;

            if (lower.StartsWith("honeypot"))
            {
                var vlan = TrailingVlanId(ifName);
                var net = vlan.HasValue ? NetworkNameForVlan(vlan.Value) : null;
                return net != null ? $"Honeypot ({net})"
                    : vlan is > 0 ? $"Honeypot (VLAN {vlan})" : "Honeypot";
            }
            if (lower.StartsWith("br"))
            {
                var vlan = TrailingVlanId(ifName);
                return vlan.HasValue ? NetworkNameForVlan(vlan.Value) : null;
            }
            if (lower.StartsWith("tun") || lower.StartsWith("ovpn") || lower.StartsWith("vtun"))
                return ovpnLabel;

            return null;
        }

        // Base label for a SQM/VLAN parent: its own resolved label, then the custom
        // UniFi port name, then the raw ifname.
        string BaseOf(string ifName) =>
            Resolve(ifName) ?? (portNameByIfName.TryGetValue(ifName, out var pn) ? pn : ifName);

        foreach (var ifName in ifNames)
            if (Resolve(ifName) is { } label)
                result[ifName] = label;

        return result;
    }

    private static int? TrailingVlanId(string ifName)
    {
        var m = TrailingVlan.Match(ifName);
        return m.Success && int.TryParse(m.Groups[1].Value, out var v) ? v : null;
    }

    /// <summary>
    /// Linux-ifname → "WANn - {name}" for the device's WAN interfaces, where {name} is
    /// the custom UniFi port name when present, otherwise the resolved carrier; cellular
    /// WANs get a "(5G)"/"(LTE)" suffix.
    /// </summary>
    private static Dictionary<string, string> BuildWanLabels(UniFiDeviceResponse device)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var portNameByIdx = new Dictionary<int, string>();
        if (device.PortTable != null)
            foreach (var p in device.PortTable)
                if (p.PortIdx > 0 && !string.IsNullOrWhiteSpace(p.Name))
                    portNameByIdx[p.PortIdx] = p.Name;

        foreach (var wan in device.GetWanInterfaces())
        {
            var wanDisplay = NetworkFormatHelpers.FormatWanInterfaceName(wan.Key);

            string? custom = null;
            if (wan.PortIdx is int idx
                && portNameByIdx.TryGetValue(idx, out var pn)
                && !IsDefaultPortName(pn))
                custom = pn.Trim();

            var namePart = custom ?? ResolveCarrier(device, GatewayWanHelper.WanNetworkGroupFromKey(wan.Key));
            var label = string.IsNullOrWhiteSpace(namePart) ? wanDisplay : $"{wanDisplay} - {namePart}";

            if (wan.IsCellular)
            {
                // No parens: a parenthesised tag doubles up when this label is later
                // wrapped (e.g. "SQM (WAN3 - Carrier 5G)").
                var tag = wan.Type is "lte" or "wireless_lte" ? "LTE" : "5G";
                if (!label.Contains(tag, StringComparison.OrdinalIgnoreCase))
                    label += $" {tag}";
            }

            foreach (var ifName in new[] { wan.Name, wan.IfName, wan.UplinkIfName })
                if (!string.IsNullOrWhiteSpace(ifName))
                    map[ifName!] = label;
        }

        return map;
    }

    /// <summary>
    /// Carrier/ISP for a WAN group: the active SIM's serving operator (mbb) when the
    /// device has cellular state, otherwise the geo-IP ISP from last_geo_info.
    /// </summary>
    private static string? ResolveCarrier(UniFiDeviceResponse device, string wanGroup)
    {
        var sim = device.Mbb?.Sim?.FirstOrDefault(s => s.Active == true)
                  ?? device.Mbb?.Sim?.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(sim?.CarrierName)) return sim!.CarrierName;

        if (device.LastGeoInfo != null && device.LastGeoInfo.TryGetValue(wanGroup, out var geo))
            return geo.AnyName;
        return null;
    }
}
