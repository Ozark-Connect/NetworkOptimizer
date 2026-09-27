using System.Net;
using System.Net.Sockets;
using NetworkOptimizer.Core.Helpers;
using NetworkOptimizer.UniFi;

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
    public static string? Detect(string? clientIp, IReadOnlyCollection<NetworkInfo>? networks, string? clientMac = null)
    {
        if (string.IsNullOrEmpty(clientIp) || !IPAddress.TryParse(clientIp, out var address))
            return null;

        // An IPv4 client captured on a dual-stack socket can be stored as ::ffff:a.b.c.d
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
            clientIp = address.ToString();
        }

        // Tailscale CGNAT range: 100.64.0.0/10, checked regardless of the site's networks
        if (IsCgnat(clientIp))
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
