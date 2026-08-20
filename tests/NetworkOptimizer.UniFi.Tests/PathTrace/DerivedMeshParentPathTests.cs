using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;
using NetworkOptimizer.UniFi.Models;
using NetworkOptimizer.UniFi.Tests.Fixtures;
using Xunit;

namespace NetworkOptimizer.UniFi.Tests.PathTrace;

/// <summary>
/// The derived-parent (absorbed) mesh case: the child's own uplink block is stale - it names a
/// switch that in reality hangs OFF the child - and the true parent is known only from that
/// parent's downlink_table. The walk must follow the claim, the hop out of the child must be a
/// mesh backhaul carrying the claim's rates INVERTED (parent RX = child TX), and none of the
/// child's stale wired fields (1 Gbps, port, signal, band) may be shown as the mesh hop's.
/// </summary>
public class DerivedMeshParentPathTests
{
    private const string GatewayMac = "aa:bb:cc:00:00:01";
    private const string CoreSwitchMac = "aa:bb:cc:00:00:02";
    private const string KitchenMac = "aa:bb:cc:00:00:05";   // true mesh parent
    private const string FrontYardMac = "aa:bb:cc:00:00:06"; // absorbed mesh child (stale uplink)
    private const string GarageMac = "aa:bb:cc:00:00:07";    // switch that really hangs off the child

    private const long ClaimTxKbps = 650_000; // parent -> child = child RX
    private const long ClaimRxKbps = 433_000; // child -> parent = child TX

    private readonly NetworkPathAnalyzer _analyzer;

    public DerivedMeshParentPathTests()
    {
        var clientProviderMock = new Mock<IUniFiClientProvider>();
        clientProviderMock.Setup(p => p.IsConnected).Returns(true);
        var loggerFactoryMock = new Mock<ILoggerFactory>();
        loggerFactoryMock.Setup(f => f.CreateLogger(It.IsAny<string>()))
            .Returns(new Mock<ILogger>().Object);
        _analyzer = new NetworkPathAnalyzer(
            clientProviderMock.Object,
            new MemoryCache(new MemoryCacheOptions()),
            loggerFactoryMock.Object);
    }

    /// <summary>
    /// Front Yard reports a WIRED 1 Gbps uplink to Garage - which is really its downstream
    /// switch - while Main Kitchen's downlink_table claims Front Yard as a mesh child.
    /// </summary>
    private TopologyBuilder BuildAbsorbedChildSite()
    {
        var builder = new TopologyBuilder()
            .WithGateway(GatewayMac, "Gateway", wanPortIdx: 5, wanSpeed: 1000,
                lanPorts: new[] { (6, 10000) })
            .WithSwitch(CoreSwitchMac, "Core Switch",
                uplinkTo: GatewayMac, uplinkRemotePort: 6, localUplinkPort: 9,
                ports: new[] { (2, 1000), (3, 1000), (9, 10000) })
            .WithAP(KitchenMac, "Main Kitchen",
                uplinkTo: CoreSwitchMac, uplinkRemotePort: 2, localUplinkPort: 1,
                ports: new[] { (1, 1000) })
            // Stale block: wired, 1 Gbps, "uplinked" to the switch below it.
            .WithAP(FrontYardMac, "Front Yard",
                uplinkTo: GarageMac, uplinkRemotePort: 8, localUplinkPort: 1,
                ports: new[] { (1, 1000) })
            .WithSwitch(GarageMac, "Garage",
                uplinkTo: FrontYardMac, uplinkRemotePort: 1, localUplinkPort: 8,
                ports: new[] { (5, 1000), (8, 1000) })
            .WithWiredClient("aa:bb:cc:00:01:01", "192.0.2.100",
                connectedTo: GarageMac, port: 5, network: "main-net")
            .WithNetwork("main-net", "Main Network", subnet: "192.0.2.0/24")
            .WithServer("192.0.2.200", connectedTo: CoreSwitchMac, port: 3, network: "main-net");

        builder.GetDevice(KitchenMac)!.DownlinkTable = new List<DownlinkTableEntry>
        {
            new() { Mac = "vwire-bssid", SerialNo = FrontYardMac, TxRate = ClaimTxKbps, RxRate = ClaimRxKbps },
        };
        return builder;
    }

    [Fact]
    public void AbsorbedChildAsTarget_HopIsMeshWithInvertedClaimRates_NotTheStaleWire()
    {
        var builder = BuildAbsorbedChildSite();
        var frontYard = builder.GetDevice(FrontYardMac)!;
        var path = new NetworkPath
        {
            SourceHost = "192.0.2.200",
            DestinationHost = frontYard.IpAddress,
            RequiresRouting = false,
            TargetIsAccessPoint = true,
        };

        _analyzer.BuildHopList(path, builder.BuildServerPosition(), frontYard, null,
            builder.BuildTopology(), builder.BuildRawDevices());

        var frontHop = path.Hops.Single(h => h.DeviceMac == FrontYardMac);
        frontHop.IsWirelessEgress.Should().BeTrue("the true uplink is a mesh backhaul");
        frontHop.IsWirelessIngress.Should().BeTrue();
        frontHop.EgressPortName.Should().Be("wireless mesh");
        frontHop.EgressPort.Should().BeNull("the stale wired port belongs to a different link");
        // Claim rates are the parent's perspective: parent RX = child TX, parent TX = child RX.
        frontHop.WirelessTxRateMbps.Should().Be((int)(ClaimRxKbps / 1000));
        frontHop.WirelessRxRateMbps.Should().Be((int)(ClaimTxKbps / 1000));
        frontHop.EgressSpeedMbps.Should().Be((int)(ClaimRxKbps / 1000),
            "egress toward the gateway is capped by the child's TX = the parent's RX");
        frontHop.EgressSpeedMbps.Should().NotBe(1000, "the stale wired 1 Gbps must not label the backhaul");
        frontHop.WirelessSignalDbm.Should().BeNull("the child's signal cannot be known from the parent's claim");
        frontHop.WirelessEgressBand.Should().BeNull("band lives on the missing half");

        // The walk follows the claim upward: Main Kitchen, then the server's switch. Garage is
        // downstream of the target and must not appear (that loop is what repeated hops five times).
        path.Hops.Should().Contain(h => h.DeviceMac == KitchenMac);
        path.Hops.Should().NotContain(h => h.DeviceMac == GarageMac);
        path.Hops.Select(h => (h.DeviceMac, h.Order)).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void ClientBehindTheAbsorbedChild_WalksGarageThenMeshThenParent_NoLoop()
    {
        var builder = BuildAbsorbedChildSite();
        var client = builder.GetClient("aa:bb:cc:00:01:01")!;
        var path = new NetworkPath
        {
            SourceHost = "192.0.2.200",
            DestinationHost = client.IpAddress,
            RequiresRouting = false,
        };

        _analyzer.BuildHopList(path, builder.BuildServerPosition(), null, client,
            builder.BuildTopology(), builder.BuildRawDevices());

        // Client -> Garage -> Front Yard (mesh) -> Main Kitchen -> Core Switch -> Server, each once.
        path.Hops.Where(h => h.DeviceMac == GarageMac).Should().HaveCount(1);
        path.Hops.Where(h => h.DeviceMac == FrontYardMac).Should().HaveCount(1);
        path.Hops.Should().Contain(h => h.DeviceMac == KitchenMac);
        path.Hops.Should().Contain(h => h.DeviceMac == CoreSwitchMac);

        var frontHop = path.Hops.Single(h => h.DeviceMac == FrontYardMac);
        frontHop.IsWirelessEgress.Should().BeTrue();
        frontHop.EgressPortName.Should().Be("wireless mesh");
        frontHop.WirelessTxRateMbps.Should().Be((int)(ClaimRxKbps / 1000));
        frontHop.WirelessRxRateMbps.Should().Be((int)(ClaimTxKbps / 1000));
        frontHop.EgressSpeedMbps.Should().Be((int)(ClaimRxKbps / 1000));
        // Ingress from Garage rides the REAL wire (garage genuinely hangs off this AP), so the
        // wired 1 Gbps is correct on ingress even though it must not appear on the mesh egress.
        frontHop.IngressSpeedMbps.Should().Be(1000);

        // The mesh parent's ingress has no wired port; it borrows the backhaul's egress speed.
        var kitchenHop = path.Hops.Single(h => h.DeviceMac == KitchenMac);
        kitchenHop.IngressPort.Should().BeNull("the stale remote port must not be read on the mesh parent");
        kitchenHop.IngressSpeedMbps.Should().Be((int)(ClaimRxKbps / 1000));
    }

    [Fact]
    public void AgreeingMeshChild_KeepsItsOwnReport_DerivedPathInert()
    {
        // Healthy pair: the child self-reports a wireless uplink to the same parent the
        // downlink_table names. Its own fields (band, channel, signal, rates) must win.
        var builder = new TopologyBuilder()
            .WithGateway(GatewayMac, "Gateway", wanPortIdx: 5, wanSpeed: 1000,
                lanPorts: new[] { (6, 10000) })
            .WithSwitch(CoreSwitchMac, "Core Switch",
                uplinkTo: GatewayMac, uplinkRemotePort: 6, localUplinkPort: 9,
                ports: new[] { (2, 1000), (3, 1000), (9, 10000) })
            .WithAP(KitchenMac, "Main Kitchen",
                uplinkTo: CoreSwitchMac, uplinkRemotePort: 2, localUplinkPort: 1,
                ports: new[] { (1, 1000) })
            .WithMeshAP(FrontYardMac, "Front Yard",
                parentApMac: KitchenMac,
                txRateKbps: 866000, rxRateKbps: 585000,
                band: "na", channel: 36, signal: -61)
            .WithNetwork("main-net", "Main Network", subnet: "192.0.2.0/24")
            .WithServer("192.0.2.200", connectedTo: CoreSwitchMac, port: 3, network: "main-net");
        // The parent also claims it, with different (parent-perspective) numbers.
        builder.GetDevice(KitchenMac)!.DownlinkTable = new List<DownlinkTableEntry>
        {
            new() { Mac = "vwire-bssid", SerialNo = FrontYardMac, TxRate = 585_000, RxRate = 866_000 },
        };

        var frontYard = builder.GetDevice(FrontYardMac)!;
        var path = new NetworkPath
        {
            SourceHost = "192.0.2.200",
            DestinationHost = frontYard.IpAddress,
            RequiresRouting = false,
            TargetIsAccessPoint = true,
        };

        _analyzer.BuildHopList(path, builder.BuildServerPosition(), frontYard, null,
            builder.BuildTopology(), builder.BuildRawDevices());

        var frontHop = path.Hops.Single(h => h.DeviceMac == FrontYardMac);
        frontHop.IsWirelessEgress.Should().BeTrue();
        frontHop.WirelessTxRateMbps.Should().Be(866, "the child's own TX rate wins when it agrees");
        frontHop.WirelessRxRateMbps.Should().Be(585);
        frontHop.WirelessEgressBand.Should().Be("na");
        frontHop.WirelessChannel.Should().Be(36);
        frontHop.WirelessSignalDbm.Should().Be(-61);
    }
}
