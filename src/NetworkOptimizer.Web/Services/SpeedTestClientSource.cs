using System.Net;
using System.Net.Sockets;
using NetworkOptimizer.Core.Helpers;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.Web.Services.Tailscale;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Labels where a client speed test came from, for the Source badges on Client Speed Test and
/// the speed test map: "Tailscale", "VPN", "Teleport", "WAN", or null for the LAN.
/// </summary>
public static class SpeedTestClientSource
{
    /// <summary>
    /// The source label for a client address.
    /// </summary>
    /// <param name="clientIp">The address the test came from.</param>
    /// <param name="networks">The site's networks; null when not loaded, which skips every network-based label.</param>
    /// <param name="clientMac">The device the result was tied to, if any. An IPv6 source tied to a device
    /// is on the LAN even when its prefix is not a configured subnet (a delegated prefix usually is not).</param>
    /// <param name="tailnetPeer">Whether the site's tailnet device list holds an address, for a custom IP pool.</param>
    /// <param name="pathHops">The hop types of the trace stored with the result, when it has one. A Teleport label
    /// needs a Teleport hop there: the address guess reads today's networks, so a test from a 192.168.x.x LAN
    /// the site has since renumbered away from would otherwise read as Teleport.</param>
    public static string? Detect(string? clientIp, IReadOnlyCollection<NetworkInfo>? networks, string? clientMac = null,
        Func<string, bool>? tailnetPeer = null, IReadOnlyCollection<NetworkOptimizer.UniFi.Models.HopType>? pathHops = null)
    {
        var label = DetectByAddress(clientIp, networks, clientMac, tailnetPeer);
        if (label == "Teleport" && pathHops is { Count: > 0 } && !pathHops.Contains(NetworkOptimizer.UniFi.Models.HopType.Teleport))
            return null;
        return label;
    }

    private static string? DetectByAddress(string? clientIp, IReadOnlyCollection<NetworkInfo>? networks, string? clientMac,
        Func<string, bool>? tailnetPeer)
    {
        if (string.IsNullOrEmpty(clientIp) || !IPAddress.TryParse(clientIp, out var address))
            return null;

        // An IPv4 client captured on a dual-stack socket can be stored as ::ffff:a.b.c.d
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
            clientIp = address.ToString();
        }

        // Tailscale's ranges (100.64.0.0/10, fd7a:115c:a1e0::/48), checked regardless of the site's networks
        if (NetworkUtilities.IsTailscaleAddress(address) || tailnetPeer?.Invoke(clientIp) == true)
            return "Tailscale";

        if (networks == null)
            return null;

        var matchingNetwork = networks.FirstOrDefault(n => NetworkUtilities.IsIpInSubnet(clientIp, n.IpSubnet));

        if (matchingNetwork?.Purpose == "remote-user-vpn")
            return "VPN";

        // 192.168.x.x outside every known network is a Teleport client. Only with networks
        // loaded: an empty list cannot tell a Teleport address from an unconfigured LAN.
        if (clientIp.StartsWith("192.168.") && matchingNetwork == null && networks.Count > 0)
            return "Teleport";

        if (matchingNetwork == null && !IsPrivate(address, clientIp)
            && !(address.AddressFamily == AddressFamily.InterNetworkV6 && !string.IsNullOrEmpty(clientMac)))
            return "WAN";

        return null;
    }

    /// <summary>
    /// Whether a client address is a Tailscale device: in Tailscale's ranges or in the site's tailnet.
    /// </summary>
    /// <param name="clientIp">The address the test came from.</param>
    /// <param name="tailnetPeer">Whether the site's tailnet device list holds an address, for a custom IP pool.</param>
    public static bool IsTailscale(string? clientIp, Func<string, bool>? tailnetPeer = null) =>
        Detect(clientIp, networks: null, tailnetPeer: tailnetPeer) == "Tailscale";

    /// <summary>
    /// The name to show for a speed test result in its tables and cards. Null when there is no name:
    /// the caller shows the IP. A client test from a remote client (Tailscale, VPN, WAN) never shows its
    /// stored name: an older build stored the gateway name there. A Teleport client shows the name UniFi
    /// Network gave it, stored at test time, unless that is the gateway's own name. Anything else shows its
    /// stored name, and a Tailscale address with no stored name shows its tailnet name.
    /// </summary>
    /// <param name="result">The result to name.</param>
    /// <param name="tailnet">The site's tailnet, which names Tailscale devices.</param>
    /// <param name="networks">The site's networks for the Teleport address guess; null skips that guess.</param>
    public static string? DisplayName(Iperf3Result result, TailnetSnapshot tailnet, IReadOnlyCollection<NetworkInfo>? networks = null)
    {
        var hops = result.PathAnalysis?.Path?.Hops;

        var remoteHop = RemoteHop(result);
        if (remoteHop != null)
            return remoteHop.Type == NetworkOptimizer.UniFi.Models.HopType.Teleport
                ? TrustedStoredName(result)
                : tailnet.NameFor(result.DeviceHost);

        if (!string.IsNullOrEmpty(result.DeviceName))
            return result.DeviceName;

        // The address alone is a guess (a LAN can use 100.64.0.0/10), so it only fills in a missing name
        return Detect(result.DeviceHost, networks, result.ClientMac, tailnet.IsPeer, hops?.Select(h => h.Type).ToList()) == "Teleport"
            ? null
            : tailnet.NameFor(result.DeviceHost);
    }

    /// <summary>
    /// Whether a result came from a Teleport client: its stored trace says so, or, with no trace, its address does.
    /// </summary>
    /// <param name="result">The result.</param>
    /// <param name="networks">The site's networks for the address guess; null skips it.</param>
    public static bool IsTeleport(Iperf3Result result, IReadOnlyCollection<NetworkInfo>? networks = null)
    {
        if (RemoteHop(result) is { } hop)
            return hop.Type == NetworkOptimizer.UniFi.Models.HopType.Teleport;
        var hops = result.PathAnalysis?.Path?.Hops;
        return Detect(result.DeviceHost, networks, result.ClientMac, pathHops: hops?.Select(h => h.Type).ToList()) == "Teleport";
    }

    /// <summary>
    /// The hop that proves a client is remote, from its stored trace. A client LAN test prepends it at order -1 (a LAN
    /// test that transits a tunnel carries that hop at a later order); a Client WAN Test ends at it. Server-initiated
    /// tests keep the name the user gave the device, whatever its path, so they have none.
    /// </summary>
    private static NetworkOptimizer.UniFi.Models.NetworkHop? RemoteHop(Iperf3Result result)
    {
        var hops = result.PathAnalysis?.Path?.Hops;
        if (hops == null || hops.Count == 0)
            return null;
        return result.Direction switch
        {
            SpeedTestDirection.ClientToServer or SpeedTestDirection.BrowserToServer => hops.FirstOrDefault(h => h.Order == -1),
            SpeedTestDirection.OpenSpeedTestWan => hops.MaxBy(h => h.Order) is { Type: NetworkOptimizer.UniFi.Models.HopType.Teleport
                or NetworkOptimizer.UniFi.Models.HopType.Tailscale or NetworkOptimizer.UniFi.Models.HopType.Vpn } last ? last : null,
            _ => null,
        };
    }

    /// <summary>A remote result's stored name, unless it is the gateway's own: an older build stored that there.</summary>
    private static string? TrustedStoredName(Iperf3Result result)
    {
        if (string.IsNullOrEmpty(result.DeviceName))
            return null;
        var gatewayName = result.PathAnalysis?.Path?.Hops?
            .FirstOrDefault(h => h.Type == NetworkOptimizer.UniFi.Models.HopType.Gateway)?.DeviceName;
        return string.Equals(result.DeviceName, gatewayName, StringComparison.Ordinal) ? null : result.DeviceName;
    }

    /// <summary>
    /// RFC 1918 or CGNAT for IPv4; unique-local, link-local, or loopback for IPv6.
    /// </summary>
    private static bool IsPrivate(IPAddress address, string clientIp)
    {
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return NetworkUtilities.IsPrivateIpAddress(address);

        if (clientIp.StartsWith("10.") || clientIp.StartsWith("192.168.") || IsCgnat(clientIp))
            return true;

        // 172.16.0.0/12 (172.16.x.x - 172.31.x.x)
        var parts = clientIp.Split('.');
        return clientIp.StartsWith("172.") && parts.Length >= 2
            && int.TryParse(parts[1], out var second) && second is >= 16 and <= 31;
    }

    private static bool IsCgnat(string clientIp)
    {
        if (!clientIp.StartsWith("100."))
            return false;
        var parts = clientIp.Split('.');
        return parts.Length >= 2 && int.TryParse(parts[1], out var second) && second is >= 64 and <= 127;
    }
}
