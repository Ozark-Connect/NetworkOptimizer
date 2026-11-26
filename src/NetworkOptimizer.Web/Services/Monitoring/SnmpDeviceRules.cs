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
    /// UniFi device (an Express (UX, UX7) adopted as an AP) reports type "udm" but is not
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
        ResolveUnitPollAddress(device, allDevices,
            string.IsNullOrEmpty(gatewayLanIp) ? null : new GatewayLanAddress(gatewayLanIp, null, null));

    /// <summary>
    /// Poll address for a device. A gateway is reached on the LAN side, never at UniFi's WAN public
    /// IP: the VIP for the active unit, its own address for a per-network VRRP unit. Empty for the
    /// backup of a Managed Shadow pair, which no LAN address reaches (a poll at the VIP would record
    /// the master's numbers under the backup's MAC), so callers skip it. A UDM in application mode is
    /// a console only and keeps its own address.
    /// </summary>
    public static string ResolveUnitPollAddress(
        UniFiDeviceResponse device, IEnumerable<UniFiDeviceResponse> allDevices, GatewayLanAddress? lan)
    {
        if (!IsSiteGateway(device, allDevices) || device.ApplicationModeEnabled == true) return device.Ip;
        var list = allDevices as IList<UniFiDeviceResponse> ?? allDevices.ToList();
        if (GatewayHa.IsUnreachableStandby(device, list)) return string.Empty;
        return lan == null ? device.Ip : lan.AddressFor(device.GatewayVrrpMode);
    }

    /// <summary>
    /// Resolves the gateway's LAN-side IP via the default-LAN network config,
    /// the same way Device Status / UniFiDiscovery does it. UniFi reports the
    /// gateway's "ip" field as the WAN public IP, which never answers SNMP
    /// from inside the LAN. Returns null if no LAN gateway can be derived.
    /// </summary>
    public static async Task<string?> ResolveGatewayLanIpAsync(UniFiApiClient client, CancellationToken ct) =>
        (await ResolveGatewayLanAsync(client, ct))?.Vip;

    /// <summary>
    /// The default LAN's gateway addresses: the VIP, plus each per-network VRRP unit's own address.
    /// Null if no LAN gateway can be derived.
    /// </summary>
    public static async Task<GatewayLanAddress?> ResolveGatewayLanAsync(UniFiApiClient client, CancellationToken ct)
    {
        var networks = await client.GetNetworkConfigsAsync(ct);
        var defaultLan = networks
            .Where(n => n.Purpose == "corporate" && n.Enabled)
            .OrderBy(n => n.Vlan ?? 0) // prefer no VLAN (0) first
            .FirstOrDefault();
        return GatewayLanAddress.FromNetwork(defaultLan);
    }
}
