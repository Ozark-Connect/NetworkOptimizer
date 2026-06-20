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

        // WireGuard clients: wgclt{wireguard_id} → configured tunnel name.
        var wgMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in networks)
            if (n.WireguardId is int wid
                && !string.IsNullOrWhiteSpace(n.Name)
                && (n.VpnType?.Contains("wireguard", StringComparison.OrdinalIgnoreCase) ?? false))
                wgMap[$"wgclt{wid}"] = n.Name.Trim();

        // OpenVPN client interface naming is firmware-specific; if there's exactly one
        // configured OpenVPN client, use its name, otherwise a generic label.
        var ovpnNames = networks
            .Where(n => n.VpnType?.Contains("openvpn", StringComparison.OrdinalIgnoreCase) ?? false)
            .Select(n => n.Name).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        var ovpnLabel = ovpnNames.Count == 1 ? ovpnNames[0]!.Trim() : "OpenVPN";

        // VLAN id → corporate/LAN network name (for honeypot/bridge interfaces).
        string? NetworkNameForVlan(int vlan)
        {
            var n = networks.FirstOrDefault(x =>
                !x.IsWan && (x.VlanId ?? 0) == vlan && !string.IsNullOrWhiteSpace(x.Name));
            return n?.Name?.Trim();
        }

        // Resolves the label of a base interface for SQM (ifb) parent lookups.
        string BaseLabel(string ifName) =>
            wanMap.TryGetValue(ifName, out var w) ? w
            : wgMap.TryGetValue(ifName, out var g) ? g
            : ifName;

        foreach (var ifName in ifNames)
        {
            if (string.IsNullOrWhiteSpace(ifName)) continue;
            var lower = ifName.ToLowerInvariant();

            // A VLAN sub-interface inherits from its base: a WAN base gives the WAN
            // label plus the tag (eth0.100 → "WAN1 - Fiber ISP (100)"); otherwise name
            // it after the network on that VLAN (eth0.100 → "Management (100)"), the
            // same VLAN→network resolution used for honeypot interfaces.
            var sub = SubInterface.Match(ifName);
            if (sub.Success)
            {
                var subVlan = sub.Groups[2].Value;
                if (wanMap.TryGetValue(sub.Groups[1].Value, out var baseWan))
                {
                    result[ifName] = $"{baseWan} ({subVlan})";
                    continue;
                }
                if (int.TryParse(subVlan, out var subVlanId)
                    && NetworkNameForVlan(subVlanId) is { } subNet)
                {
                    result[ifName] = $"{subNet} ({subVlan})";
                    continue;
                }
            }

            if (wanMap.TryGetValue(ifName, out var wan)) { result[ifName] = wan; continue; }
            if (wgMap.TryGetValue(ifName, out var wg)) { result[ifName] = wg; continue; }

            if (lower.StartsWith("ifb"))
            {
                // Only per-parent shaping interfaces (ifbeth0.100 → eth0.100) get an SQM
                // label; the bare ifb0/ifb1 root devices are left unresolved (and hidden
                // by the table when down).
                var parent = ifName[3..];
                if (parent.Length > 0 && !parent.All(char.IsDigit))
                    result[ifName] = $"SQM ({BaseLabel(parent)})";
            }
            else if (lower.StartsWith("honeypot"))
            {
                var vlan = TrailingVlanId(ifName);
                var net = vlan.HasValue ? NetworkNameForVlan(vlan.Value) : null;
                result[ifName] = net != null ? $"Honeypot ({net})"
                    : vlan is > 0 ? $"Honeypot (VLAN {vlan})" : "Honeypot";
            }
            else if (lower.StartsWith("br"))
            {
                var vlan = TrailingVlanId(ifName);
                var net = vlan.HasValue ? NetworkNameForVlan(vlan.Value) : null;
                if (net != null) result[ifName] = net;
            }
            else if (lower.StartsWith("tun") || lower.StartsWith("ovpn") || lower.StartsWith("vtun"))
            {
                result[ifName] = ovpnLabel;
            }
        }

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
