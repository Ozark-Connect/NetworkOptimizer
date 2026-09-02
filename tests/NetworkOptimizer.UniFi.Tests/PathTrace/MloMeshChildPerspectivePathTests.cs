using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;
using NetworkOptimizer.UniFi.Models;
using NetworkOptimizer.UniFi.Tests.Fixtures;
using Xunit;

namespace NetworkOptimizer.UniFi.Tests.PathTrace;

/// <summary>
/// An MLO mesh backhaul is described from both ends: the child's uplink.mlo_links (its own
/// signal reading per link, rates already child-perspective and summed into the block's
/// top-level tx/rx) and the parent's downlink_table (the parent's reading, direction reversed).
/// The hop out of the child is the child's side of the link, so its per-link detail must be the
/// child's own; the parent's account is only the fallback for a child that reports no links.
/// </summary>
public class MloMeshChildPerspectivePathTests
{
    private const string GatewayMac = "aa:bb:cc:00:00:01";
    private const string CoreSwitchMac = "aa:bb:cc:00:00:02";
    private const string ParentMac = "aa:bb:cc:00:00:05";
    private const string ChildMac = "aa:bb:cc:00:00:06";

    // Child's own readings (uplink.mlo_links), and what the parent reads for the same links.
    private const int ChildSixGhzSignal = -41;
    private const int ParentSixGhzSignal = -59;
    private const int ChildFiveGhzSignal = -36;
    private const int ParentFiveGhzSignal = -47;
    private const long SixGhzTxKbps = 4_804_000;  // child -> parent
    private const long SixGhzRxKbps = 4_804_000;  // parent -> child
    private const long FiveGhzTxKbps = 1_729_400;
    private const long FiveGhzRxKbps = 2_161_800;

    private readonly NetworkPathAnalyzer _analyzer;

    public MloMeshChildPerspectivePathTests()
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
    /// The child's uplink block names its 5 GHz STA link with the aggregate rates; the parent
    /// lists one is_mlo entry per link with its own signal readings.
    /// </summary>
    private static TopologyBuilder BuildMloPair(long childTxKbps, long childRxKbps)
    {
        var builder = new TopologyBuilder()
            .WithGateway(GatewayMac, "Gateway", wanPortIdx: 5, wanSpeed: 1000,
                lanPorts: new[] { (6, 10000) })
            .WithSwitch(CoreSwitchMac, "Core Switch",
                uplinkTo: GatewayMac, uplinkRemotePort: 6, localUplinkPort: 9,
                ports: new[] { (2, 1000), (3, 1000), (9, 10000) })
            .WithAP(ParentMac, "Parent",
                uplinkTo: CoreSwitchMac, uplinkRemotePort: 2, localUplinkPort: 1,
                ports: new[] { (1, 1000) })
            .WithMeshAP(ChildMac, "Child", ParentMac,
                txRateKbps: (int)childTxKbps, rxRateKbps: (int)childRxKbps,
                band: "na", channel: 40, signal: ChildFiveGhzSignal)
            .WithNetwork("main-net", "Main Network", subnet: "192.0.2.0/24")
            .WithServer("192.0.2.200", connectedTo: CoreSwitchMac, port: 3, network: "main-net");

        builder.GetDevice(ParentMac)!.DownlinkTable =
        [
            new DownlinkTableEntry
            {
                Mac = "vwire-6e", SerialNo = ChildMac, MldMac = ChildMac, IsMlo = true, Radio = "6e", Channel = 5,
                Signal = ParentSixGhzSignal, TxRate = SixGhzRxKbps, RxRate = SixGhzTxKbps,
            },
            new DownlinkTableEntry
            {
                Mac = "vwire-na", SerialNo = ChildMac, MldMac = ChildMac, IsMlo = true, Radio = "na", Channel = 40,
                Signal = ParentFiveGhzSignal, TxRate = FiveGhzRxKbps, RxRate = FiveGhzTxKbps,
            },
        ];
        return builder;
    }

    private NetworkHop TraceToChild(TopologyBuilder builder)
    {
        var child = builder.GetDevice(ChildMac)!;
        var path = new NetworkPath
        {
            SourceHost = "192.0.2.200",
            DestinationHost = child.IpAddress,
            RequiresRouting = false,
            TargetIsAccessPoint = true,
        };
        _analyzer.BuildHopList(path, builder.BuildServerPosition(), child, null,
            builder.BuildTopology(), builder.BuildRawDevices());
        return path.Hops.Single(h => h.DeviceMac == ChildMac);
    }

    [Fact]
    public void ChildReportsItsLinks_HopCarriesTheChildsOwnSignalPerLink_NotTheParents()
    {
        var builder = BuildMloPair(SixGhzTxKbps + FiveGhzTxKbps, SixGhzRxKbps + FiveGhzRxKbps);
        var child = builder.GetDevice(ChildMac)!;
        child.UplinkIsMlo = true;
        child.UplinkMloLinks =
        [
            new MeshBackhaulLink { Band = "6e", Channel = 5, WidthMhz = 320, SignalDbm = ChildSixGhzSignal, TxRateMbps = 4804, RxRateMbps = 4804 },
            new MeshBackhaulLink { Band = "na", Channel = 40, WidthMhz = 160, SignalDbm = ChildFiveGhzSignal, TxRateMbps = 1729, RxRateMbps = 2161 },
        ];

        var hop = TraceToChild(builder);

        hop.IsMloMeshBackhaul.Should().BeTrue();
        hop.MeshMloLinks.Should().HaveCount(2);
        var sixGhz = hop.MeshMloLinks!.Single(l => l.Band == "6e");
        sixGhz.SignalDbm.Should().Be(ChildSixGhzSignal, "the hop is the child's side of the link");
        sixGhz.SignalDbm.Should().NotBe(ParentSixGhzSignal);
        sixGhz.WidthMhz.Should().Be(320);
        sixGhz.TxRateMbps.Should().Be(4804);
        hop.MeshMloLinks!.Single(l => l.Band == "na").SignalDbm.Should().Be(ChildFiveGhzSignal);
        // The child's top-level rates are already the sum over its links.
        hop.WirelessTxRateMbps.Should().Be((int)((SixGhzTxKbps + FiveGhzTxKbps) / 1000));
        hop.WirelessRxRateMbps.Should().Be((int)((SixGhzRxKbps + FiveGhzRxKbps) / 1000));
        hop.EgressSpeedMbps.Should().Be(hop.WirelessTxRateMbps);
    }

    [Fact]
    public void ChildReportsNoLinks_HopFallsBackToTheParentsLinksFlipped()
    {
        // A firmware that describes the backhaul only from the parent's end: the child's block
        // carries its STA link alone, so the parent's per-link sum raises the hop's rates and
        // only the STA link gets the child's own signal.
        var builder = BuildMloPair(FiveGhzTxKbps, FiveGhzRxKbps);

        var hop = TraceToChild(builder);

        hop.IsMloMeshBackhaul.Should().BeTrue();
        hop.MeshMloLinks.Should().HaveCount(2);
        hop.MeshMloLinks!.Single(l => l.Band == "6e").SignalDbm.Should().Be(ParentSixGhzSignal);
        hop.MeshMloLinks!.Single(l => l.Band == "na").SignalDbm.Should().Be(ChildFiveGhzSignal);
        hop.WirelessTxRateMbps.Should().Be((int)((SixGhzTxKbps + FiveGhzTxKbps) / 1000));
        hop.WirelessRxRateMbps.Should().Be((int)((SixGhzRxKbps + FiveGhzRxKbps) / 1000));
    }
}
