using FluentAssertions;
using Xunit;

namespace NetworkOptimizer.UniFi.Tests;

/// <summary>
/// Whether a speed test source is traced out through the WAN (#568). The IPv4 rows pin today's
/// behavior; an IPv6 unique-local or link-local source is never external.
/// </summary>
public class NetworkPathAnalyzerExternalIpTests
{
    private static readonly NetworkTopology Topology = new()
    {
        Networks =
        [
            new NetworkInfo { Id = "lan", Name = "LAN", Purpose = "corporate", IpSubnet = "192.0.2.0/24" },
            new NetworkInfo { Id = "vpn", Name = "VPN", Purpose = "remote-user-vpn", IpSubnet = "198.51.100.0/24" },
        ]
    };

    [Theory]
    [InlineData("192.0.2.20", false)]       // On a configured network
    [InlineData("198.51.100.5", true)]      // Remote-user VPN reads as external
    [InlineData("203.0.113.7", true)]       // Outside every network
    [InlineData("10.200.0.5", true)]        // Outside every network, private or not
    [InlineData("not-an-ip", false)]
    [InlineData("fd00:10::20", false)]      // ULA
    [InlineData("fe80::1", false)]          // Link-local
    [InlineData("2001:db8::20", true)]      // Global IPv6 outside every network
    public void IsExternalIp_ReturnsExpected(string ip, bool expected)
    {
        NetworkPathAnalyzer.IsExternalIp(ip, Topology).Should().Be(expected);
    }
}
