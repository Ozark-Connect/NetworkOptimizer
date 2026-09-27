using FluentAssertions;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

/// <summary>
/// Source labels on Client Speed Test and the speed test map (#568). The IPv4 rows pin the labels
/// the two pages computed before they shared this code; the IPv6 rows are new.
/// </summary>
public class SpeedTestClientSourceTests
{
    private static readonly List<NetworkInfo> Networks =
    [
        new() { Id = "lan", Name = "LAN", Purpose = "corporate", IpSubnet = "10.0.1.0/24" },
        new() { Id = "vpn", Name = "VPN", Purpose = "remote-user-vpn", IpSubnet = "10.0.9.0/24" },
    ];

    [Theory]
    [InlineData("10.0.1.20", null)]              // On a configured network
    [InlineData("100.64.0.5", "Tailscale")]
    [InlineData("100.127.255.1", "Tailscale")]
    [InlineData("100.128.0.1", "WAN")]           // Just outside CGNAT
    [InlineData("10.0.9.4", "VPN")]
    [InlineData("192.168.50.10", "Teleport")]    // 192.168.x.x outside every network
    [InlineData("172.20.0.5", null)]             // Private, not configured: not WAN
    [InlineData("172.32.0.5", "WAN")]            // Just outside 172.16/12
    [InlineData("10.200.0.5", null)]
    [InlineData("203.0.113.7", "WAN")]
    [InlineData("::ffff:203.0.113.7", "WAN")]    // IPv4-mapped reads as its IPv4 address
    [InlineData("::ffff:10.0.1.20", null)]
    [InlineData("not-an-ip", null)]
    [InlineData("", null)]
    public void Ipv4Sources_KeepTheirLabels(string clientIp, string? expected)
    {
        SpeedTestClientSource.Detect(clientIp, Networks).Should().Be(expected);
    }

    [Fact]
    public void Ipv4PublicSource_WithAMac_IsStillWan()
    {
        // The MAC exemption is for IPv6 only; an IPv4 source keeps today's label
        SpeedTestClientSource.Detect("203.0.113.7", Networks, "aa:bb:cc:dd:ee:ff").Should().Be("WAN");
    }

    [Fact]
    public void NoNetworksLoaded_OnlyTailscaleIsLabeled()
    {
        SpeedTestClientSource.Detect("100.64.0.5", null).Should().Be("Tailscale");
        SpeedTestClientSource.Detect("203.0.113.7", null).Should().BeNull();
        SpeedTestClientSource.Detect("192.168.50.10", null).Should().BeNull();
    }

    [Fact]
    public void EmptyNetworkList_DoesNotLabelTeleport()
    {
        SpeedTestClientSource.Detect("192.168.50.10", []).Should().BeNull();
    }

    [Theory]
    [InlineData("fd00:10::20", null, null)]                         // ULA: never the internet
    [InlineData("fe80::1", null, null)]                             // Link-local
    [InlineData("2001:db8:10::20", null, "WAN")]                   // Global, not tied to a device
    [InlineData("2001:db8:10::20", "aa:bb:cc:dd:ee:ff", null)]     // Global, tied to a LAN device
    public void Ipv6Sources(string clientIp, string? clientMac, string? expected)
    {
        SpeedTestClientSource.Detect(clientIp, Networks, clientMac).Should().Be(expected);
    }
}
