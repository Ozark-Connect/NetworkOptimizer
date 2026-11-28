using FluentAssertions;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.UniFi.Models;
using NetworkOptimizer.Web.Services;
using NetworkOptimizer.Web.Services.Tailscale;
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

    [Fact]
    public void StoredTrace_DecidesTeleport()
    {
        const string ip = "192.168.77.10";
        // A LAN the site has since renumbered away from: the trace stored with the test has no Teleport hop.
        SpeedTestClientSource.Detect(ip, Networks,
            pathHops: [NetworkOptimizer.UniFi.Models.HopType.Gateway, NetworkOptimizer.UniFi.Models.HopType.Client]).Should().BeNull();
        // A real Teleport test keeps its label.
        SpeedTestClientSource.Detect(ip, Networks,
            pathHops: [NetworkOptimizer.UniFi.Models.HopType.Teleport, NetworkOptimizer.UniFi.Models.HopType.Gateway]).Should().Be("Teleport");
        // No trace: the address guess stands.
        SpeedTestClientSource.Detect(ip, Networks, pathHops: []).Should().Be("Teleport");
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

    [Fact]
    public void TailnetIpv6_IsTailscale()
    {
        SpeedTestClientSource.Detect("fd7a:115c:a1e0::5", Networks).Should().Be("Tailscale");
    }

    [Fact]
    public void DeviceListHit_IsTailscale()
    {
        SpeedTestClientSource.Detect("10.200.0.5", Networks, tailnetPeer: ip => ip == "10.200.0.5").Should().Be("Tailscale");
    }

    [Theory]
    [InlineData("100.100.1.2", true)]        // Tailscale's default IPv4 range
    [InlineData("::ffff:100.100.1.2", true)] // Same, captured on a dual-stack socket
    [InlineData("10.200.0.5", true)]         // Custom pool, in the device list
    [InlineData("10.200.0.6", false)]
    [InlineData("203.0.113.5", false)]
    [InlineData(null, false)]
    public void IsTailscale_RangeOrDeviceList(string? clientIp, bool expected)
    {
        SpeedTestClientSource.IsTailscale(clientIp, ip => ip == "10.200.0.5").Should().Be(expected);
    }

    // --- DisplayName: the name the tables and cards show ---

    private static TailnetSnapshot TailnetWithPeer(string name, string ip) =>
        new([new TailnetPeer { Name = name, Addresses = [ip] }], null, []);

    private static Iperf3Result Result(string host, string? deviceName = null,
        params (int Order, HopType Type)[] hops) => Result(SpeedTestDirection.ClientToServer, host, deviceName, hops);

    private static Iperf3Result Result(SpeedTestDirection direction, string host, string? deviceName,
        params (int Order, HopType Type)[] hops) => new()
    {
        Direction = direction,
        DeviceHost = host,
        DeviceName = deviceName,
        PathAnalysis = hops.Length == 0
            ? null
            : new PathAnalysisResult { Path = new NetworkPath { Hops = hops.Select(h => new NetworkHop { Order = h.Order, Type = h.Type }).ToList() } }
    };

    [Fact]
    public void TailscaleTrace_ShowsTailnetName_NotTheStoredGatewayName()
    {
        var result = Result("100.64.0.10", deviceName: "Gateway",
            (-1, HopType.Tailscale), (0, HopType.Gateway));
        SpeedTestClientSource.DisplayName(result, TailnetWithPeer("t-iphone", "100.64.0.10")).Should().Be("t-iphone");
    }

    [Fact]
    public void TailscaleTrace_WithNoTailnetConfigured_ShowsNoName()
    {
        // No Tailscale integration: the caller shows the IP, never the stored gateway name
        var result = Result("100.64.0.10", deviceName: "Gateway",
            (-1, HopType.Tailscale), (0, HopType.Gateway));
        SpeedTestClientSource.DisplayName(result, TailnetSnapshot.Empty).Should().BeNull();
    }

    [Fact]
    public void TailscaleWithoutTrace_ShowsTailnetName()
    {
        SpeedTestClientSource.DisplayName(Result("100.64.0.10"), TailnetWithPeer("t-iphone", "100.64.0.10"))
            .Should().Be("t-iphone");
    }

    [Fact]
    public void TailscaleWithoutTrace_NotInTailnet_ShowsNoName()
    {
        SpeedTestClientSource.DisplayName(Result("100.64.0.10"), TailnetSnapshot.Empty).Should().BeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CgnatLanClient_KeepsItsStoredName(bool tailnetConfigured)
    {
        // A LAN on 100.64.0.0/10 reads as Tailscale by address alone; only a trace proves a client is remote
        var result = Result("100.64.0.10", deviceName: "Laptop", (0, HopType.WirelessClient), (1, HopType.AccessPoint));
        var tailnet = tailnetConfigured ? TailnetWithPeer("other-device", "100.64.0.99") : TailnetSnapshot.Empty;
        SpeedTestClientSource.DisplayName(result, tailnet, Networks).Should().Be("Laptop");
    }

    [Fact]
    public void TeleportTrace_ShowsTeleportClient_NotTheStoredGatewayName()
    {
        // The stored Teleport hop decides the name; the address only matters for the no-trace guess
        var result = Result("198.51.100.10", deviceName: "Gateway",
            (-1, HopType.Teleport), (0, HopType.Gateway));
        SpeedTestClientSource.DisplayName(result, TailnetSnapshot.Empty).Should().Be("Teleport Client");
    }

    [Fact]
    public void TeleportWithoutTrace_UsesTheAddressGuess()
    {
        // The address guess needs the 192.168 prefix the app classifies on; 192.168.77.0/24 is
        // outside this file's Networks fixture, matching StoredTrace_DecidesTeleport
        SpeedTestClientSource.DisplayName(Result("192.168.77.10"), TailnetSnapshot.Empty, Networks).Should().Be("Teleport Client");
    }

    [Fact]
    public void TeleportAddressGuess_DoesNotHideAStoredName()
    {
        // No trace, on a 192.168 LAN the site has since renumbered: the stored name is the real client
        SpeedTestClientSource.DisplayName(Result("192.168.77.10", deviceName: "Laptop"), TailnetSnapshot.Empty, Networks)
            .Should().Be("Laptop");
    }

    [Theory]
    [InlineData(HopType.Vpn)]
    [InlineData(HopType.Wan)]
    public void OtherRemoteTrace_HidesTheStoredGatewayName(HopType remoteType)
    {
        var result = Result("10.0.9.4", deviceName: "Gateway", (-1, remoteType), (0, HopType.Gateway));
        SpeedTestClientSource.DisplayName(result, TailnetSnapshot.Empty, Networks).Should().BeNull();
    }

    [Fact]
    public void ClientWanTest_KeepsItsStoredName()
    {
        // A Client WAN Test runs on a LAN client; its WAN hop sits at order 0
        var result = Result(SpeedTestDirection.OpenSpeedTestWan, "10.0.1.20", "Laptop",
            (0, HopType.Wan), (1, HopType.Gateway), (2, HopType.WirelessClient));
        SpeedTestClientSource.DisplayName(result, TailnetSnapshot.Empty).Should().Be("Laptop");
    }

    [Fact]
    public void ServerToDevice_OverATailnet_KeepsTheConfiguredName()
    {
        // A device the user configured by its Tailscale IP keeps the name they gave it
        var result = Result(SpeedTestDirection.ServerToDevice, "100.64.0.10", "Office NAS",
            (-1, HopType.Tailscale), (0, HopType.Gateway));
        SpeedTestClientSource.DisplayName(result, TailnetWithPeer("nas-tailnet", "100.64.0.10")).Should().Be("Office NAS");
    }

    [Fact]
    public void LanResult_KeepsItsStoredName()
    {
        var result = Result("10.0.1.20", deviceName: "Laptop");
        SpeedTestClientSource.DisplayName(result, TailnetSnapshot.Empty, Networks).Should().Be("Laptop");
    }

    [Fact]
    public void LanResult_WithoutAName_ShowsNoName()
    {
        SpeedTestClientSource.DisplayName(Result("10.0.1.20"), TailnetSnapshot.Empty, Networks).Should().BeNull();
    }

    [Fact]
    public void LanTest_TransitingATailnet_KeepsItsClientName()
    {
        // The Tailscale hop is at order 1, not -1: the test device is the LAN client
        var result = Result("10.0.1.20", deviceName: "Laptop",
            (0, HopType.WirelessClient), (1, HopType.AccessPoint), (2, HopType.Tailscale));
        SpeedTestClientSource.DisplayName(result, TailnetSnapshot.Empty, Networks).Should().Be("Laptop");
    }
}
