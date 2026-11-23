using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.UniFi;

/// <summary>
/// Which interfaces LAN Health scores: physical LAN ports only. A positive rule, not another deny
/// list: an interface counts only when it is a real port_table entry, that entry is not a WAN port,
/// and the interface is not a VLAN sub-interface of it. Bridges, VPN, GRE, IFB, PPPoE, tunnels,
/// Wi-Fi radios and switch conduits never appear in a port table, so they fall out by construction.
/// The collection filters (<c>InterfaceMetrics.ShouldMonitor</c>, <c>SnmpConfiguration.ExcludeInterfacePatterns</c>)
/// keep several of those on purpose for WAN and VPN features; this rule does not inherit them.
/// </summary>
public static class LanHealthPorts
{
    /// <summary>True when the port is a LAN port LAN Health scores.</summary>
    /// <param name="port">The device's port_table entry the interface correlates to; null when none.</param>
    /// <param name="ifName">The interface name, when the observation names one (SNMP, agents).</param>
    /// <param name="isGateway">The port is on a gateway, where <c>is_uplink</c> marks a WAN port.</param>
    public static bool IsScored(SwitchPort? port, string? ifName, bool isGateway)
    {
        if (port == null || port.PortIdx <= 0) return false;
        if (IsVlanSubInterface(ifName)) return false;
        if (IsWanPort(port, isGateway)) return false;
        return true;
    }

    /// <summary>A gateway WAN port: flagged as the uplink, or serving a WAN network.</summary>
    public static bool IsWanPort(SwitchPort port, bool isGateway) =>
        (isGateway && port.IsUplink)
        || (port.NetworkName?.StartsWith("wan", StringComparison.OrdinalIgnoreCase) ?? false);

    /// <summary>"eth0.100": a VLAN view that repeats its parent's counters.</summary>
    public static bool IsVlanSubInterface(string? ifName) =>
        !string.IsNullOrEmpty(ifName) && ifName.Contains('.');

    /// <summary>
    /// The class an interface LAN Health leaves out belongs to, for the coverage footer; null for an
    /// interface that could be a LAN port.
    /// </summary>
    public static string? ExcludedClass(string ifName, bool isWan)
    {
        if (isWan) return "WAN ports";
        var n = ifName.ToLowerInvariant();
        if (n.Contains('.')) return "VLAN sub-interfaces";
        if (n.StartsWith("br") || n.StartsWith("switch")) return "bridges";
        if (n.StartsWith("wg") || n.StartsWith("tun") || n.StartsWith("tap") || n.StartsWith("vti") || n.StartsWith("ip6tnl")) return "VPN tunnels";
        if (n.StartsWith("gre")) return "GRE tunnels";
        if (n.StartsWith("ifb")) return "IFB queues";
        if (n.StartsWith("ppp")) return "PPPoE";
        if (n.StartsWith("wifi") || n.StartsWith("ath") || n.StartsWith("ra") || n.StartsWith("wlan") || n.StartsWith("vwire") || n.StartsWith("vap")) return "Wi-Fi radios";
        return null;
    }
}
