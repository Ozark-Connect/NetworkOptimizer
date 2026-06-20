using System.Text.RegularExpressions;
using NetworkOptimizer.Core.Helpers;
using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.UniFi;

/// <summary>
/// Resolves friendly display labels for a gateway's Linux interface names from the
/// authoritative UniFi sources (the device's WAN objects, port table, last_geo_info
/// and mbb cellular state). Runs agent-side so the resolved map can later be
/// persisted as time series; for now the polling agent caches it in memory.
///
/// Pass 1 covers WAN interfaces (incl. cellular GRE tunnels like "gre1"); WireGuard /
/// OpenVPN / SQM (ifb) labels that need networkconf land in a later pass.
/// </summary>
public static class InterfaceLabelResolver
{
    // UniFi's default, unnamed port labels ("Port 7", "SFP 1", "SFP+ 2"). When a WAN
    // port still carries one of these we prefer the carrier name over it.
    private static readonly Regex DefaultPortName =
        new(@"^(port|sfp\+?|rj45)\s*\d+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>True for UniFi's generic placeholder port names ("Port 7", "SFP+ 1").</summary>
    public static bool IsDefaultPortName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && DefaultPortName.IsMatch(name.Trim());

    /// <summary>
    /// Builds a Linux-ifname → display-label map for the device's WAN interfaces.
    /// Every interface that can carry a WAN (its name, ifname and uplink ifname) maps
    /// to "WANn - {name}", where {name} is the custom UniFi port name when present,
    /// otherwise the resolved carrier. Cellular WANs get a "(5G)"/"(LTE)" suffix.
    /// </summary>
    public static Dictionary<string, string> BuildWanLabels(UniFiDeviceResponse device)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (device == null) return map;

        var portNameByIdx = new Dictionary<int, string>();
        if (device.PortTable != null)
            foreach (var p in device.PortTable)
                if (p.PortIdx > 0 && !string.IsNullOrWhiteSpace(p.Name))
                    portNameByIdx[p.PortIdx] = p.Name;

        foreach (var wan in device.GetWanInterfaces())
        {
            // "wan"/"wan2" → "WAN1"/"WAN2" (canonical UniFi UI naming).
            var wanDisplay = NetworkFormatHelpers.FormatWanInterfaceName(wan.Key);

            // Custom (non-default) UniFi port name wins; otherwise the carrier.
            string? custom = null;
            if (wan.PortIdx is int idx
                && portNameByIdx.TryGetValue(idx, out var pn)
                && !IsDefaultPortName(pn))
                custom = pn.Trim();

            var namePart = custom ?? ResolveCarrier(device, GatewayWanHelper.WanNetworkGroupFromKey(wan.Key));
            var label = string.IsNullOrWhiteSpace(namePart) ? wanDisplay : $"{wanDisplay} - {namePart}";

            if (wan.IsCellular)
            {
                var tag = wan.Type is "lte" or "wireless_lte" ? "LTE" : "5G";
                if (!label.Contains(tag, StringComparison.OrdinalIgnoreCase))
                    label += $" ({tag})";
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
