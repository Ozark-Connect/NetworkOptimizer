using FluentAssertions;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.Web.Endpoints;
using NetworkOptimizer.Web.Services.LanFlowMap;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring;

/// <summary>
/// Link speed at a past instant, in Live View playback and the Port Statistics historic scrub. A
/// gateway's built-in switch ports report the 10G switch conduit over SNMP, so a gateway port
/// takes only the console-recorded speed; switches and APs keep their (correct) SNMP speed.
/// </summary>
public class PlaybackLinkSpeedTests
{
    private static readonly DateTime At = new(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);

    private static MonitoringInfluxClient.InterfaceRatePoint Rate(int secondsFromAt, long? speedBps, int? linkSpeedMbps = null) => new()
    {
        Time = At.AddSeconds(secondsFromAt),
        IfName = "eth1",
        RateInBps = 1000,
        RateOutBps = 1000,
        SpeedBps = speedBps,
        LinkSpeedMbps = linkSpeedMbps,
    };

    // --- LanFlowMapService.SpeedMbpsAt (playback tooltips) ---

    [Fact]
    public void Gateway_UsesTheRecordedSpeed_NotTheSnmpConduit()
    {
        var points = new[] { Rate(-5, 10_000_000_000, linkSpeedMbps: 100) };

        LanFlowMapService.SpeedMbpsAt(points, "eth1", At, isGateway: true).Should().Be(100);
    }

    [Fact]
    public void Gateway_WithoutRecordedSpeed_FallsBackToToday_NeverTheConduit()
    {
        var points = new[] { Rate(-5, 10_000_000_000) };

        LanFlowMapService.SpeedMbpsAt(points, "eth1", At, isGateway: true).Should().BeNull();
    }

    [Fact]
    public void Switch_KeepsItsSnmpSpeedAtTheInstant()
    {
        // A WOL port idling at 100 Mbps while the host was off.
        var points = new[] { Rate(-5, 100_000_000), Rate(600, 1_000_000_000) };

        LanFlowMapService.SpeedMbpsAt(points, "eth1", At, isGateway: false).Should().Be(100);
    }

    // --- LanFlowMapService.GatewayMacs (which ports count as gateway ports) ---

    [Fact]
    public void GatewayMacs_HoldsEveryGateway_Normalized_AndNothingElse()
    {
        var snapshot = new LanFlowMapSnapshot
        {
            Nodes =
            {
                // An HA pair: both gateways count. One MAC arrives upper-case with dashes.
                new LanNode { Id = "dev-gw1", Kind = LanNodeKind.Gateway, Mac = "AA-BB-CC-00-00-01" },
                new LanNode { Id = "dev-gw2", Kind = LanNodeKind.Gateway, Mac = "aa:bb:cc:00:00:02" },
                new LanNode { Id = "dev-sw", Kind = LanNodeKind.Switch, Mac = "aa:bb:cc:00:00:03" },
            },
        };

        var macs = LanFlowMapService.GatewayMacs(snapshot);

        macs.Should().BeEquivalentTo("aa:bb:cc:00:00:01", "aa:bb:cc:00:00:02");
        macs.Contains("AA:BB:CC:00:00:02").Should().BeTrue("lookups ignore case");
        macs.Contains("aa:bb:cc:00:00:03").Should().BeFalse("a switch is not a gateway");
    }

    // --- PortStatsEndpoints.LinkSpeedAt (Port Statistics historic scrub) ---

    private static MonitoringInfluxClient.PortStatsPoint Port(long? speedBps, int? linkSpeedMbps = null, int operStatus = 1) => new()
    {
        Time = At,
        DeviceMac = "aa:bb:cc:dd:ee:ff",
        IfName = "eth1",
        OperStatus = operStatus,
        SpeedBps = speedBps,
        LinkSpeedMbps = linkSpeedMbps,
    };

    [Fact]
    public void Live_AlwaysUsesTheNameMap()
    {
        PortStatsEndpoints.LinkSpeedAt(Port(100_000_000, 100), "switch", historic: false).Should().BeNull();
    }

    [Fact]
    public void Historic_Gateway_RecordedSpeedOrNothing()
    {
        PortStatsEndpoints.LinkSpeedAt(Port(10_000_000_000, 100), "gateway", historic: true).Should().Be(100);
        PortStatsEndpoints.LinkSpeedAt(Port(10_000_000_000), "gateway", historic: true).Should().BeNull();
    }

    [Theory]
    [InlineData("switch")]
    [InlineData("ap")]
    public void Historic_SwitchOrAp_UsesSnmpWhileUp(string type)
    {
        PortStatsEndpoints.LinkSpeedAt(Port(100_000_000), type, historic: true).Should().Be(100);
        PortStatsEndpoints.LinkSpeedAt(Port(1_000_000_000, operStatus: 2), type, historic: true).Should().BeNull();
    }

    [Fact]
    public void Historic_UnknownDeviceType_FallsBackToToday()
    {
        PortStatsEndpoints.LinkSpeedAt(Port(10_000_000_000), "unknown", historic: true).Should().BeNull();
    }
}
