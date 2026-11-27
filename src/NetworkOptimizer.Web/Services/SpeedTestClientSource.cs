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
    /// The name to show for a client speed test result in its tables and cards.
    /// A Tailscale test shows its tailnet name, a Teleport test shows "Teleport Client",
    /// and anything else shows the stored device name. Null when there is no name: the caller shows the IP.
    /// Stored names are skipped for remote results - an older build stored the gateway name there.
    /// </summary>
    /// <param name="result">The result to name.</param>
    /// <param name="tailnet">The site's tailnet, which names Tailscale devices.</param>
    /// <param name="networks">The site's networks for the Teleport address guess; null skips that guess.</param>
    public static string? DisplayName(Iperf3Result result, TailnetSnapshot tailnet, IReadOnlyCollection<NetworkInfo>? networks = null)
    {
        var hops = result.PathAnalysis?.Path?.Hops;

        // The remote client's hop is the one prepended at order -1. A LAN test that merely transits
        // a tailnet or Teleport tunnel carries those hops at later orders, not -1.
        if (hops is { Count: > 0 })
        {
            if (hops.Any(h => h.Order == -1 && h.Type == NetworkOptimizer.UniFi.Models.HopType.Tailscale))
                return tailnet.NameFor(result.DeviceHost);
            if (hops.Any(h => h.Order == -1 && h.Type == NetworkOptimizer.UniFi.Models.HopType.Teleport))
                return "Teleport Client";
        }

        var label = Detect(result.DeviceHost, networks, result.ClientMac, tailnet.IsPeer,
            hops?.Select(h => h.Type).ToList());
        return label switch
        {
            "Tailscale" => tailnet.NameFor(result.DeviceHost),
            "Teleport" => "Teleport Client",
            _ => string.IsNullOrEmpty(result.DeviceName) ? tailnet.NameFor(result.DeviceHost) : result.DeviceName
        };
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
