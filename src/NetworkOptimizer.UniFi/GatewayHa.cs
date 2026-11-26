using System.Text.Json;
using NetworkOptimizer.Core;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.UniFi;

/// <summary>
/// The default LAN's gateway addresses. <see cref="Vip"/> is the address clients use; on any HA
/// pair only the active unit answers it. Per-network VRRP units also hold their own address.
/// </summary>
/// <param name="Vip">The default LAN's gateway address.</param>
/// <param name="PrimaryUnitIp">Per-network VRRP primary's own address (vrrp_ip_subnet_gw1, else VIP + 1).</param>
/// <param name="SecondaryUnitIp">Per-network VRRP secondary's own address (vrrp_ip_subnet_gw2, else VIP + 2).</param>
public sealed record GatewayLanAddress(string Vip, string? PrimaryUnitIp, string? SecondaryUnitIp)
{
    /// <summary>
    /// The address that reaches a unit with this gateway_vrrp_mode: its own for a per-network
    /// VRRP primary or secondary, the VIP otherwise.
    /// </summary>
    public string AddressFor(string? gatewayVrrpMode) => gatewayVrrpMode?.ToLowerInvariant() switch
    {
        "primary" => PrimaryUnitIp ?? Vip,
        "secondary" => SecondaryUnitIp ?? Vip,
        _ => Vip
    };

    /// <summary>
    /// The addresses of one network: dhcpd_gateway, else the ip_subnet host, as the VIP. Null when
    /// neither is set.
    /// </summary>
    public static GatewayLanAddress? FromNetwork(UniFiNetworkConfig? network)
    {
        if (network == null) return null;
        var vip = !string.IsNullOrEmpty(network.DhcpdGateway) ? network.DhcpdGateway
            : !string.IsNullOrEmpty(network.IpSubnet) ? network.IpSubnet.Split('/')[0]
            : null;
        if (string.IsNullOrEmpty(vip)) return null;
        return new GatewayLanAddress(vip,
            Host(network.VrrpIpSubnetGw1) ?? Offset(vip, 1),
            Host(network.VrrpIpSubnetGw2) ?? Offset(vip, 2));
    }

    private static string? Host(string? cidr) =>
        string.IsNullOrWhiteSpace(cidr) ? null : cidr.Split('/')[0].Trim();

    private static string? Offset(string ip, int by)
    {
        if (!System.Net.IPAddress.TryParse(ip, out var addr)
            || addr.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return null;
        var b = addr.GetAddressBytes();
        var n = ((uint)b[0] << 24 | (uint)b[1] << 16 | (uint)b[2] << 8 | b[3]) + (uint)by;
        return new System.Net.IPAddress(new[] { (byte)(n >> 24), (byte)(n >> 16), (byte)(n >> 8), (byte)n }).ToString();
    }
}

/// <summary>A gateway's place in a high-availability pair.</summary>
public enum GatewayHaRole
{
    /// <summary>Not in a pair.</summary>
    None,

    /// <summary>The pair member that owns the config. Keeps the label after a failover.</summary>
    Primary,

    /// <summary>The Managed Shadow Gateway. Carries traffic only while it is VRRP master.</summary>
    Shadow
}

/// <summary>
/// The fields that decide which gateway is which on a site with more than one gateway-class unit.
/// Implemented by <see cref="DiscoveredDevice"/> and <see cref="UniFiDeviceResponse"/>, so every
/// gateway pick uses one rule set.
/// </summary>
public interface IGatewayHaFacts
{
    /// <summary>The device's role on the site (Gateway for a shadow too).</summary>
    DeviceType HaRoleType { get; }

    /// <summary>device_mode_override as reported.</summary>
    string? HaModeOverride { get; }

    /// <summary>vrrp.state as reported, or null.</summary>
    string? HaVrrpState { get; }

    /// <summary>gateway_vrrp_mode as reported, or null.</summary>
    string? HaGatewayVrrpMode { get; }

    /// <summary>application_mode_enabled is true.</summary>
    bool HaApplicationMode { get; }

    /// <summary>The console reports the unit connected (UniFi <c>state</c>).</summary>
    bool HaOnline { get; }
}

/// <summary>
/// UniFi Network's own gateway rules (ace.jar, Network 11.1.58), applied to our device models.
/// </summary>
/// <remarks>
/// Two "one gateway" meanings exist and callers must pick the right one, as UniFi does:
/// <see cref="Active{T}"/> carries traffic (WAN counters, live rates, speed tests, conntrack);
/// <see cref="Primary{T}"/> owns the config (Audit, Change Control, config reads). A failover
/// changes only the shadow's VRRP state; the labels stay until the primary is forgotten.
/// </remarks>
[VendorSpecific("UniFi", "Managed Shadow Gateway, per-network VRRP, and UDM application mode rules from ace.jar")]
public static class GatewayHa
{
    /// <summary>device_mode_override value of a Managed Shadow Gateway.</summary>
    public const string ShadowModeOverride = "managed_shadow_gateway";

    /// <summary>The VRRP state of the unit that holds the virtual addresses.</summary>
    public const string VrrpMaster = "master";

    /// <summary>Whether the unit is labelled as a Managed Shadow Gateway.</summary>
    public static bool IsShadow(IGatewayHaFacts d) =>
        d.HaRoleType == DeviceType.Gateway
        && string.Equals(d.HaModeOverride, ShadowModeOverride, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// UniFi's isGateway: a role gateway that is neither a UDM in application mode nor a
    /// per-network VRRP secondary. Only these can be the site gateway.
    /// </summary>
    public static bool IsGateway(IGatewayHaFacts d) =>
        d.HaRoleType == DeviceType.Gateway
        && !d.HaApplicationMode
        && !string.Equals(d.HaGatewayVrrpMode, "secondary", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// UniFi's per-device isActiveGateway: a gateway that is not a shadow, or a shadow that is VRRP
    /// master. After a failover the primary still passes it, so it cannot pick one unit: use
    /// <see cref="Active{T}"/> for that.
    /// </summary>
    public static bool IsActive(IGatewayHaFacts d) =>
        IsGateway(d) && (!IsShadow(d) || IsMaster(d));

    private static bool IsMaster(IGatewayHaFacts d) =>
        string.Equals(d.HaVrrpState, VrrpMaster, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The gateway carrying the site's traffic: the shadow while it is VRRP master (UniFi's own
    /// pick), else any VRRP master, else the primary. With no primary listed (it was forgotten
    /// before UniFi cleared the label), the shadow. Null when no unit is a gateway.
    /// </summary>
    /// <remarks>
    /// "Any VRRP master" covers Console Shadow Mode (UDM Pro, UDM SE, UDM Pro Max, UDM-Beast, EFG, EF Core):
    /// its pair carries vrrp.state but no shadow label, so the state is the only thing that can
    /// tell its units apart if the console ever lists both.
    /// </remarks>
    public static T? Active<T>(IEnumerable<T>? devices) where T : class, IGatewayHaFacts
    {
        if (devices == null) return null;
        var gateways = devices.Where(IsGateway).ToList();
        // Online units first: after a Console Shadow Mode takeover the live gateway row is the
        // shadow's own (new MAC and _id), and the failed primary's row can stay listed with its
        // last VRRP state still reading master.
        return PickActive(gateways.Where(d => d.HaOnline).ToList()) ?? PickActive(gateways);
    }

    private static T? PickActive<T>(List<T> gateways) where T : class, IGatewayHaFacts =>
        gateways.FirstOrDefault(d => IsShadow(d) && IsMaster(d))
        ?? gateways.FirstOrDefault(IsMaster)
        ?? gateways.FirstOrDefault(d => !IsShadow(d))
        ?? gateways.FirstOrDefault();

    // A second, online unit holds VRRP master and this one does not: this one is the pair's backup.
    // An offline row's last state proves nothing about who holds the VIP now.
    private static bool AnotherIsMaster<T>(T device, IEnumerable<T> devices) where T : class, IGatewayHaFacts =>
        !IsMaster(device) && devices.Any(d => !ReferenceEquals(d, device) && IsGateway(d) && IsMaster(d) && d.HaOnline);

    /// <summary>
    /// The gateway that owns the config: the first non-shadow gateway, else the shadow when it
    /// is the only one listed. Null when no unit is a gateway.
    /// </summary>
    public static T? Primary<T>(IEnumerable<T>? devices) where T : class, IGatewayHaFacts
    {
        if (devices == null) return null;
        var gateways = devices.Where(IsGateway).ToList();
        return gateways.FirstOrDefault(d => !IsShadow(d)) ?? gateways.FirstOrDefault();
    }

    /// <summary>
    /// True for a role gateway that is not carrying the site's traffic: a UDM in application mode,
    /// a VRRP secondary, a shadow in backup, or a unit whose pair partner is VRRP master. Use it to
    /// skip units in a walk over every device. A lone gateway is never standby.
    /// </summary>
    public static bool IsStandby<T>(T device, IEnumerable<T> devices) where T : class, IGatewayHaFacts
    {
        if (device.HaRoleType != DeviceType.Gateway) return false;
        if (!IsGateway(device)) return true;
        if (IsShadow(device)) return !IsMaster(device);
        return AnotherIsMaster(device, devices);
    }

    /// <summary>
    /// True for the backup member of a Shadow Mode pair (managed or console). It holds no IPv4
    /// address on any LAN (every LAN address is a VIP on the master; its own address is on the HA
    /// link), so SNMP, SSH, and ping cannot reach it: anything sent to the LAN gateway address lands
    /// on the master. Per-network VRRP units have their own LAN addresses and are never this.
    /// </summary>
    public static bool IsUnreachableStandby<T>(T device, IEnumerable<T> devices) where T : class, IGatewayHaFacts
    {
        if (!IsGateway(device) || !string.IsNullOrEmpty(device.HaGatewayVrrpMode)) return false;
        if (IsShadow(device)) return !IsMaster(device);
        return AnotherIsMaster(device, devices);
    }

    /// <summary>
    /// The HA fields of one raw stat/device entry, for code that walks the device JSON directly.
    /// </summary>
    public static IGatewayHaFacts FromJson(JsonElement device) => new JsonFacts(device);

    private sealed class JsonFacts : IGatewayHaFacts
    {
        public JsonFacts(JsonElement d)
        {
            string? Str(JsonElement e, string name) =>
                e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
                    ? p.GetString() : null;
            HaModeOverride = Str(d, "device_mode_override");
            HaRoleType = UniFiProductDatabase.ClassifyDeviceType(Str(d, "type"), Str(d, "model"), Str(d, "shortname"), HaModeOverride);
            HaVrrpState = d.ValueKind == JsonValueKind.Object && d.TryGetProperty("vrrp", out var v) ? Str(v, "state") : null;
            HaGatewayVrrpMode = Str(d, "gateway_vrrp_mode");
            HaApplicationMode = d.ValueKind == JsonValueKind.Object
                && d.TryGetProperty("application_mode_enabled", out var a) && a.ValueKind == JsonValueKind.True;
            HaOnline = d.ValueKind == JsonValueKind.Object && d.TryGetProperty("state", out var s)
                && s.ValueKind == JsonValueKind.Number && s.TryGetInt32(out var state) && UniFiDeviceStateMap.IsOnline(state);
        }

        public DeviceType HaRoleType { get; }
        public string? HaModeOverride { get; }
        public string? HaVrrpState { get; }
        public string? HaGatewayVrrpMode { get; }
        public bool HaApplicationMode { get; }
        public bool HaOnline { get; }
    }

    /// <summary>
    /// The unit's place in a pair on this site: Shadow for a labelled shadow, Primary for the
    /// config owner when a shadow is present, else None.
    /// </summary>
    public static GatewayHaRole RoleOf<T>(T device, IEnumerable<T> devices) where T : class, IGatewayHaFacts
    {
        if (!IsGateway(device)) return GatewayHaRole.None;
        if (IsShadow(device)) return GatewayHaRole.Shadow;
        var list = devices as IList<T> ?? devices.ToList();
        return list.Any(d => IsGateway(d) && IsShadow(d)) && ReferenceEquals(Primary(list), device)
            ? GatewayHaRole.Primary
            : GatewayHaRole.None;
    }
}
