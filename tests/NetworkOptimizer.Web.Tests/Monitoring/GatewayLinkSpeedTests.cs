using FluentAssertions;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.UniFi.Models;
using NetworkOptimizer.Web.Services.Monitoring;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring;

/// <summary>
/// The pieces of the gateway link speed pass that decide what is recorded and what a port is
/// expected to reach: snapshot selection, WAN port matching, the plan floor, and the history
/// condensing the Config Optimizer reads.
/// </summary>
public class GatewayLinkSpeedTests
{
    private const string GatewayMac = "aa:bb:cc:dd:ee:ff";

    private static UniFiDeviceResponse Gateway(long lastSeen, int state = 1, params SwitchPort[] ports) => new()
    {
        Mac = GatewayMac.ToUpperInvariant(),
        Name = "Gateway",
        Type = "uxg",
        Adopted = true,
        State = state,
        LastSeen = lastSeen,
        PortTable = ports.ToList(),
    };

    private static SwitchPort Port(int idx, string ifName, int speed, bool up = true, string? network = null, bool? fullDuplex = true) => new()
    {
        PortIdx = idx,
        Name = $"Port {idx}",
        IfName = ifName,
        Speed = speed,
        Up = up,
        FullDuplex = fullDuplex,
        NetworkName = network,
    };

    // --- GatewayLinkSpeedSnapshots ---

    [Fact]
    public void Snapshot_TakenOncePerLastSeen()
    {
        var snapshots = new GatewayLinkSpeedSnapshots();
        var gw = Gateway(1000, 1, Port(1, "eth0", 1000, network: "wan"));

        snapshots.Take(new[] { gw }).Should().ContainSingle();
        snapshots.Take(new[] { gw }).Should().BeEmpty("a repeated last_seen is the same snapshot");

        gw.LastSeen = 1030;
        snapshots.Take(new[] { gw }).Should().ContainSingle();
    }

    [Fact]
    public void Snapshot_OlderLastSeen_IsIgnored()
    {
        var snapshots = new GatewayLinkSpeedSnapshots();
        snapshots.Take(new[] { Gateway(2000, 1, Port(1, "eth0", 1000)) });

        snapshots.Take(new[] { Gateway(1990, 1, Port(1, "eth0", 100)) }).Should().BeEmpty();
    }

    [Fact]
    public void Snapshot_OfflineGateway_AndNonGateways_AreSkipped()
    {
        var snapshots = new GatewayLinkSpeedSnapshots();
        var offline = Gateway(1000, state: 0, Port(1, "eth0", 1000));
        var sw = Gateway(1000, 1, Port(1, "eth0", 1000));
        sw.Type = "usw";
        sw.Mac = "00:11:22:33:44:55";

        snapshots.Take(new[] { offline, sw }).Should().BeEmpty();
    }

    [Fact]
    public void Snapshot_DownPort_CarriesNoSpeed()
    {
        var snapshots = new GatewayLinkSpeedSnapshots();
        var readings = snapshots.Take(new[] { Gateway(1000, 1,
            Port(1, "eth0", 10, up: false),
            Port(2, "eth1", 100, up: true, fullDuplex: false)) });

        readings.Should().HaveCount(2);
        readings[0].SpeedMbps.Should().BeNull("a down port's 10 is not a 10 Mbps link");
        readings[0].FullDuplex.Should().BeNull();
        readings[1].SpeedMbps.Should().Be(100);
        readings[1].FullDuplex.Should().BeFalse();
        readings[1].DeviceMac.Should().Be(GatewayMac);
    }

    // --- WanLinkSpeedExpectations ---

    [Fact]
    public void MatchWan_ByPhysicalInterface_ThenByNetworkGroup()
    {
        var vlanWan = new WanProfile { WanNetworkgroup = "WAN", DataPathInterface = "eth4.100", GatewayMac = GatewayMac, Name = "Fiber" };
        var wan2 = new WanProfile { WanNetworkgroup = "WAN2", DataPathInterface = "ppp0", GatewayMac = GatewayMac, Name = "DSL" };
        var profiles = new[] { vlanWan, wan2 };

        WanLinkSpeedExpectations.MatchWan(GatewayMac, "eth4", null, profiles).Should().BeSameAs(vlanWan);
        WanLinkSpeedExpectations.MatchWan(GatewayMac, "eth1", "wan2", profiles).Should().BeSameAs(wan2);
        WanLinkSpeedExpectations.MatchWan(GatewayMac, "eth2", "lan", profiles).Should().BeNull();
        WanLinkSpeedExpectations.MatchWan("00:11:22:33:44:55", "eth4", null, profiles).Should().BeNull("another gateway's WAN");
    }

    [Theory]
    [InlineData("wan", true)]
    [InlineData("WAN2", true)]
    [InlineData("lan", false)]
    [InlineData(null, false)]
    public void IsWanPort_ByNetworkName(string? network, bool expected) =>
        WanLinkSpeedExpectations.IsWanPort(network, null).Should().Be(expected);

    [Theory]
    [InlineData(null, null, "10GE", null)]
    [InlineData(100.0, 20.0, "10GE", null)]
    [InlineData(300.0, 30.0, "GE", 1000)]
    [InlineData(50.0, 500.0, "2.5GE", 1000)]
    [InlineData(1000.0, 1000.0, "10GE", 1000)]
    [InlineData(2000.0, 2000.0, "10GE", 2500)]
    [InlineData(2000.0, 2000.0, "2.5GE", 2500)]
    [InlineData(5000.0, 5000.0, "10GE", 5000)]
    [InlineData(8000.0, 8000.0, "10GE", 10000)]
    [InlineData(25000.0, 25000.0, "10GE", 10000)]
    // A plan faster than the port is a hardware limit, never a standing alert.
    [InlineData(2000.0, 2000.0, "GE", 1000)]
    [InlineData(5000.0, 5000.0, "2.5GE", 2500)]
    // SFP cages report the cage, not the module, and unknown media is treated the same.
    [InlineData(2000.0, 2000.0, "SFP+", 1000)]
    [InlineData(2000.0, 2000.0, null, 1000)]
    public void PlanFloor_SlowestRateThatCarriesThePlan_CappedByThePort(double? down, double? up, string? media, int? expected) =>
        WanLinkSpeedExpectations.PlanFloorMbps(new WanProfile { DownloadMbps = down, UploadMbps = up }, media).Should().Be(expected);

    [Fact]
    public void Expected_IsTheHighestKnown()
    {
        WanLinkSpeedExpectations.Expected(null, null, null).Should().BeNull();
        WanLinkSpeedExpectations.Expected(2500, 1000, null).Should().Be(2500);
        WanLinkSpeedExpectations.Expected(null, 100, 1000).Should().Be(1000);
    }

    // --- WanLinkSpeedHistoryReader.Summarize ---

    private static readonly TimeSpan W = WanLinkSpeedHistoryReader.Window;
    private static readonly DateTime T0 = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    private static List<(DateTime, int)> Series(params int[] mins) =>
        mins.Select((m, i) => (T0 + W * i, m)).ToList();

    [Fact]
    public void Summarize_CountsSustainedDrops_IgnoresSingleWindowBlips()
    {
        // 1000, a one-window blip, back, a three-window drop to 100, back, a two-window drop to 10.
        var h = WanLinkSpeedHistoryReader.Summarize(GatewayMac, "eth3",
            Series(1000, 1000, 100, 1000, 100, 100, 100, 1000, 10, 10, 1000), W, TimeSpan.FromDays(30))!;

        h.UsualMbps.Should().Be(1000);
        h.DegradedEpisodes.Should().Be(2);
        h.LowestMbps.Should().Be(10);
        h.DegradedTime.Should().Be(W * 5);
    }

    [Fact]
    public void Summarize_MissingWindows_DoNotSplitOneDrop()
    {
        // One long drop with the server restarting twice through it: still one drop, not three.
        var windows = new List<(DateTime, int)>
        {
            (T0, 1000),
            (T0 + W, 100),
            (T0 + W * 2, 100),
            (T0 + TimeSpan.FromHours(2), 100),
            (T0 + TimeSpan.FromHours(5), 100),
            (T0 + TimeSpan.FromHours(5) + W, 1000),
        };

        var h = WanLinkSpeedHistoryReader.Summarize(GatewayMac, "eth3", windows, W, TimeSpan.FromDays(30))!;

        h.DegradedEpisodes.Should().Be(1);
        h.DegradedTime.Should().Be(W * 4, "only windows with a snapshot count as time spent degraded");
    }

    [Fact]
    public void Summarize_Lookback_IsWhatTheHistoryCovers()
    {
        var h = WanLinkSpeedHistoryReader.Summarize(GatewayMac, "eth3",
            Series(1000, 100, 100, 1000), W, TimeSpan.FromDays(30))!;

        h.Lookback.Should().Be(W * 4);
    }

    [Fact]
    public void Summarize_SteadySpeed_HasNoEpisodes_AndEmptyIsNull()
    {
        WanLinkSpeedHistoryReader.Summarize(GatewayMac, "eth3", Series(100, 100, 100), W, TimeSpan.FromDays(30))!
            .DegradedEpisodes.Should().Be(0);
        WanLinkSpeedHistoryReader.Summarize(GatewayMac, "eth3", Series(), W, TimeSpan.FromDays(30)).Should().BeNull();
    }
}
