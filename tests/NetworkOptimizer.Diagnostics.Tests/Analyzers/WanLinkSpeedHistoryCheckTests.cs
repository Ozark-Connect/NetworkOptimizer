using FluentAssertions;
using NetworkOptimizer.Audit.Services;
using NetworkOptimizer.Diagnostics.Analyzers;
using NetworkOptimizer.Diagnostics.Models;
using NetworkOptimizer.UniFi.Models;
using Xunit;

namespace NetworkOptimizer.Diagnostics.Tests.Analyzers;

/// <summary>
/// The Config Optimizer recommendation for a gateway WAN port with a history of dropping below its
/// usual speed.
/// </summary>
public class WanLinkSpeedHistoryCheckTests
{
    private const string GatewayMac = "aa:bb:cc:00:00:01";
    private readonly PerformanceAnalyzer _analyzer = new(new DeviceTypeDetectionService());

    private static UniFiDeviceResponse Gateway(bool autoneg) => new()
    {
        Id = "gateway1",
        Mac = GatewayMac.ToUpperInvariant(),
        Name = "Test Gateway",
        Type = "ugw",
        PortTable = new List<SwitchPort>
        {
            new() { PortIdx = 4, Name = "Fiber Uplink", IfName = "eth3", Speed = 100, Up = true, Autoneg = autoneg, NetworkName = "wan" },
        },
    };

    private static WanLinkSpeedHistory History(int episodes, string ifName = "eth3") => new()
    {
        DeviceMac = GatewayMac,
        IfName = ifName,
        UsualMbps = 1000,
        LowestMbps = 100,
        DegradedEpisodes = episodes,
        DegradedTime = TimeSpan.FromHours(5),
        Lookback = TimeSpan.FromDays(30),
    };

    [Fact]
    public void RepeatedDrops_OnAutonegPort_RecommendForcingSpeed()
    {
        var issues = _analyzer.CheckWanLinkSpeedHistory(new List<UniFiDeviceResponse> { Gateway(autoneg: true) }, new[] { History(3) });

        var issue = issues.Should().ContainSingle().Subject;
        issue.Severity.Should().Be(PerformanceSeverity.Recommendation);
        issue.Category.Should().Be(PerformanceCategory.Performance);
        issue.DeviceName.Should().Be("Test Gateway");
        issue.Description.Should().Contain("Fiber Uplink").And.Contain("3 times").And.Contain("30 days").And.Contain("100 Mbps");
        issue.Recommendation.Should().Contain("1000 Mbps full duplex");
    }

    [Fact]
    public void RepeatedDrops_OnForcedPort_PointAtTheCable()
    {
        var issues = _analyzer.CheckWanLinkSpeedHistory(new List<UniFiDeviceResponse> { Gateway(autoneg: false) }, new[] { History(2) });

        issues.Should().ContainSingle().Which.Recommendation.Should().Contain("cable").And.NotContain("full duplex");
    }

    [Fact]
    public void SingleDrop_IsLeftToTheAlert()
    {
        _analyzer.CheckWanLinkSpeedHistory(new List<UniFiDeviceResponse> { Gateway(autoneg: true) }, new[] { History(1) })
            .Should().BeEmpty();
    }

    [Fact]
    public void NoHistory_OrPortNotOnTheGateway_ReturnsEmpty()
    {
        var devices = new List<UniFiDeviceResponse> { Gateway(autoneg: true) };

        _analyzer.CheckWanLinkSpeedHistory(devices, null).Should().BeEmpty();
        _analyzer.CheckWanLinkSpeedHistory(devices, new[] { History(5, ifName: "eth9") }).Should().BeEmpty();
    }

    [Fact]
    public void Recommendation_HtmlEncodesNames()
    {
        var gw = Gateway(autoneg: true);
        gw.Name = "<b>GW</b>";

        _analyzer.CheckWanLinkSpeedHistory(new List<UniFiDeviceResponse> { gw }, new[] { History(2) })
            .Should().ContainSingle().Which.Recommendation.Should().Contain("&lt;b&gt;GW&lt;/b&gt;");
    }
}
