using FluentAssertions;
using NetworkOptimizer.UniFi.Models;
using Xunit;

namespace NetworkOptimizer.UniFi.Tests;

/// <summary>
/// The client Wi-Fi fit re-rates the wireless hop after the trace. The path's bottleneck, max,
/// and description were derived from the trace-time rate and must follow the hop, or a phone
/// traced at 216 Mbps and tested at 2.2 Gbps keeps "216 Mbps link" in its stored path.
/// </summary>
public class RecalculateBottleneckTests
{
    private static NetworkPath PhoneBehindMeshTracedAtLowPhy() => new()
    {
        Hops =
        [
            new() { Order = 0, Type = HopType.WirelessClient, DeviceName = "Phone", IngressSpeedMbps = 216, EgressSpeedMbps = 216, IsWirelessIngress = true, IsWirelessEgress = true, IsBottleneck = true },
            new() { Order = 1, Type = HopType.AccessPoint, DeviceName = "Child AP", EgressPort = 0, EgressPortName = "wireless mesh", EgressSpeedMbps = 6533, IsWirelessEgress = true },
            new() { Order = 2, Type = HopType.AccessPoint, DeviceName = "Parent AP", IngressPort = 0, IngressPortName = "Port 0", IngressSpeedMbps = 6533, EgressPort = 3, EgressPortName = "AP Backhaul", EgressSpeedMbps = 2500, EgressPortDeviceName = "Switch" },
            new() { Order = 3, Type = HopType.Switch, DeviceName = "Switch", IngressPort = 3, IngressPortName = "AP Backhaul", IngressSpeedMbps = 2500, EgressPort = 10, EgressSpeedMbps = 10000 },
        ],
        TheoreticalMaxMbps = 216,
        RealisticMaxMbps = 162,
        BottleneckDescription = "216 Mbps link at Phone (wireless)",
        HasRealBottleneck = true,
    };

    [Fact]
    public void ReRatedClientHop_MovesTheMaxAndDescriptionWithIt()
    {
        var path = PhoneBehindMeshTracedAtLowPhy();
        path.Hops[0].IngressSpeedMbps = 2161;
        path.Hops[0].EgressSpeedMbps = 2401;

        NetworkPathAnalyzer.RecalculateBottleneck(path);

        path.TheoreticalMaxMbps.Should().Be(2161);
        path.RealisticMaxMbps.Should().Be((int)(2161 * 0.75), "the client Wi-Fi link is still the bottleneck");
        path.BottleneckDescription.Should().Be("2.2 Gbps link at Phone (wireless)");
        path.Hops.Single(h => h.IsBottleneck).DeviceName.Should().Be("Phone");
    }

    [Fact]
    public void ReRatedClientHopAboveTheWire_MovesTheBottleneckToTheWire()
    {
        var path = PhoneBehindMeshTracedAtLowPhy();
        path.Hops[0].IngressSpeedMbps = 2882;
        path.Hops[0].EgressSpeedMbps = 2882;

        NetworkPathAnalyzer.RecalculateBottleneck(path);

        path.TheoreticalMaxMbps.Should().Be(2500);
        path.BottleneckDescription.Should().Be("2.5 Gbps link at Switch (AP Backhaul)");
        path.Hops.Single(h => h.IsBottleneck).DeviceName.Should().Be("Parent AP");
        path.Hops[0].IsBottleneck.Should().BeFalse("the stale flag from the trace is cleared");
    }
}
