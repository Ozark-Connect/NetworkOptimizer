using NetworkOptimizer.UniFi;
using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>
/// Device-selection rules shared by the local monitoring collection agent and
/// the agent-tunnel SNMP config push, so both collectors poll the same set of
/// devices at the same addresses.
/// </summary>
public static class SnmpDeviceRules
{
    /// <summary>Adopted, online devices with an address - the monitorable population.</summary>
    public static bool IsMonitorable(UniFiDeviceResponse device) =>
        device.Adopted && UniFiDeviceStateMap.IsOnline(device.State)
        && !string.IsNullOrEmpty(device.Ip) && !string.IsNullOrEmpty(device.Mac);

    /// <summary>
    /// UniFi requires snmp_location or snmp_contact to be set for SNMP to be
    /// enabled on a device. Both empty/null = SNMP off.
    /// </summary>
    public static bool HasSnmpEnabled(UniFiDeviceResponse device) =>
        !string.IsNullOrEmpty(device.SnmpLocation) || !string.IsNullOrEmpty(device.SnmpContact);

    /// <summary>
    /// Whether the device is the site's gateway. A gateway-class console that uplinks to another
    /// UniFi device (an Express or Dream Router adopted as an AP) reports type "udm" but is not
    /// the gateway - the same rule discovery uses (issue #1244).
    /// </summary>
    public static bool IsSiteGateway(UniFiDeviceResponse device, IEnumerable<UniFiDeviceResponse> allDevices) =>
        UniFiDiscovery.GetEffectiveDeviceType(device, allDevices) == NetworkOptimizer.Core.Enums.DeviceType.Gateway;

    /// <summary>
    /// Poll address for a device. For the site's gateway, swap UniFi's WAN public IP for
    /// the LAN-side gateway IP so the poll actually reaches the device.
    /// </summary>
    public static string ResolvePollAddress(
        UniFiDeviceResponse device, IEnumerable<UniFiDeviceResponse> allDevices, string? gatewayLanIp) =>
        !string.IsNullOrEmpty(gatewayLanIp) && IsSiteGateway(device, allDevices)
            ? gatewayLanIp!
            : device.Ip;

    /// <summary>
    /// Resolves the gateway's LAN-side IP via the default-LAN network config,
    /// the same way Device Status / UniFiDiscovery does it. UniFi reports the
    /// gateway's "ip" field as the WAN public IP, which never answers SNMP
    /// from inside the LAN. Returns null if no LAN gateway can be derived.
    /// </summary>
    public static async Task<string?> ResolveGatewayLanIpAsync(UniFiApiClient client, CancellationToken ct)
    {
        var networks = await client.GetNetworkConfigsAsync(ct);
        var defaultLan = networks
            .Where(n => n.Purpose == "corporate" && n.Enabled)
            .OrderBy(n => n.Vlan ?? 0) // prefer no VLAN (0) first
            .FirstOrDefault();
        if (!string.IsNullOrEmpty(defaultLan?.DhcpdGateway))
            return defaultLan!.DhcpdGateway;
        if (!string.IsNullOrEmpty(defaultLan?.IpSubnet))
            return defaultLan!.IpSubnet.Split('/')[0];
        return null;
    }
}
