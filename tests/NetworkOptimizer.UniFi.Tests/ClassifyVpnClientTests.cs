using FluentAssertions;
using NetworkOptimizer.UniFi.Models;
using Xunit;

namespace NetworkOptimizer.UniFi.Tests;

/// <summary>
/// Tests for <see cref="NetworkPathAnalyzer.ClassifyVpnClient"/>, the shared VPN client
/// classifier used by both speed-test hop creation and the Client Dashboard.
/// </summary>
public class ClassifyVpnClientTests
{
    private static NetworkTopology TopologyWith(params NetworkInfo[] networks) =>
        new() { Networks = networks.ToList() };

    // A representative corporate LAN the test IPs can be checked against.
    private static NetworkInfo LanNetwork() => new()
    {
        Name = "Default",
        Purpose = "corporate",
        IpSubnet = "192.168.1.0/24"
    };

    [Theory]
    [InlineData("100.64.0.1")]
    [InlineData("100.100.100.100")]
    [InlineData("100.127.255.254")]
    public void TailscaleCgnatRange_ClassifiedAsTailscale(string ip)
    {
        NetworkPathAnalyzer.ClassifyVpnClient(ip, TopologyWith(LanNetwork()))
            .Should().Be(HopType.Tailscale);
    }

    [Theory]
    [InlineData("100.63.255.255")]  // just below the CGNAT block
    [InlineData("100.128.0.0")]     // just above the CGNAT block
    public void HundredDotOutsideCgnat_NotTailscale(string ip)
    {
        NetworkPathAnalyzer.ClassifyVpnClient(ip, TopologyWith(LanNetwork()))
            .Should().NotBe(HopType.Tailscale);
    }

    [Fact]
    public void Tailscale_ClassifiedWithNullTopology()
    {
        // Tailscale is a pure-IP (CGNAT) check and must work without topology.
        NetworkPathAnalyzer.ClassifyVpnClient("100.64.0.5", null)
            .Should().Be(HopType.Tailscale);
    }

    [Fact]
    public void Private192_NotInAnyNetwork_ClassifiedAsTeleport()
    {
        // 192.168.x.x not inside any known UniFi network is Teleport.
        NetworkPathAnalyzer.ClassifyVpnClient("192.168.77.5", TopologyWith(LanNetwork()))
            .Should().Be(HopType.Teleport);
    }

    [Fact]
    public void Private192_InsideKnownNetwork_NotVpn()
    {
        NetworkPathAnalyzer.ClassifyVpnClient("192.168.1.50", TopologyWith(LanNetwork()))
            .Should().BeNull();
    }

    [Fact]
    public void RemoteUserVpnSubnet_ClassifiedAsVpn()
    {
        var vpnNetwork = new NetworkInfo
        {
            Name = "VPN",
            Purpose = "remote-user-vpn",
            IpSubnet = "10.20.0.0/24"
        };

        NetworkPathAnalyzer.ClassifyVpnClient("10.20.0.7", TopologyWith(LanNetwork(), vpnNetwork))
            .Should().Be(HopType.Vpn);
    }

    [Fact]
    public void NormalLanClient_NotVpn()
    {
        NetworkPathAnalyzer.ClassifyVpnClient("192.168.1.100", TopologyWith(LanNetwork()))
            .Should().BeNull();
    }

    [Fact]
    public void NonPrivateExternalIp_NotVpn()
    {
        // A public IP not in any network is external, but not a VPN client type.
        NetworkPathAnalyzer.ClassifyVpnClient("203.0.113.10", TopologyWith(LanNetwork()))
            .Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-ip")]
    [InlineData("999.999.999.999")]
    public void InvalidIp_ReturnsNull(string ip)
    {
        NetworkPathAnalyzer.ClassifyVpnClient(ip, TopologyWith(LanNetwork()))
            .Should().BeNull();
    }

    [Fact]
    public void NullTopology_TeleportNotClassified()
    {
        // Teleport needs the network list to confirm the IP is not local; without
        // topology only Tailscale can be identified.
        NetworkPathAnalyzer.ClassifyVpnClient("192.168.77.5", null)
            .Should().BeNull();
    }
}
