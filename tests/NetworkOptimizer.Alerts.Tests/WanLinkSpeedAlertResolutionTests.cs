using FluentAssertions;
using Xunit;

namespace NetworkOptimizer.Alerts.Tests;

/// <summary>
/// The open/close pair for WAN link speed alerts: one open alert per gateway WAN port, closed by
/// its recovery and superseded by a re-publish on the same port.
/// </summary>
public class WanLinkSpeedAlertResolutionTests
{
    private const string Port = "aa:bb:cc:dd:ee:ff/eth3";

    [Fact]
    public void Recovery_ClosesTheDegradedAlertOnTheSamePort()
    {
        var targets = AlertProcessingService.GetWanLinkSpeedAlertsToResolve("wan.link_speed_recovered", Port);

        targets.Should().ContainSingle();
        targets[0].EventTypes.Should().Equal("wan.link_speed_degraded");
        targets[0].DeviceId.Should().Be(Port);
    }

    [Fact]
    public void Republish_SupersedesItsOwnOpenAlert()
    {
        var targets = AlertProcessingService.GetWanLinkSpeedAlertsToResolve("wan.link_speed_degraded", Port);

        targets.Should().ContainSingle().Which.DeviceId.Should().Be(Port);
    }

    [Theory]
    [InlineData("wan.link_speed_recovered", null)]
    [InlineData("wan.link_speed_recovered", " ")]
    [InlineData("wan.speed_degradation", Port)]
    [InlineData("monitoring.wan_recovered", Port)]
    public void OtherEvents_OrNoPort_CloseNothing(string eventType, string? deviceId) =>
        AlertProcessingService.GetWanLinkSpeedAlertsToResolve(eventType, deviceId).Should().BeEmpty();

    /// <summary>
    /// A re-publish closes its predecessor before rules run and shares its cooldown key, so a
    /// cooldown on either rule would drop the replacement (same reasoning as the Starlink rules).
    /// </summary>
    [Fact]
    public void LinkSpeedRules_ShipEnabled_WithNoCooldown()
    {
        var rules = DefaultAlertRules.GetDefaults()
            .Where(r => r.EventTypePattern.StartsWith("wan.link_speed_", StringComparison.Ordinal))
            .ToList();

        rules.Should().HaveCount(2);
        rules.Should().OnlyContain(r => r.CooldownSeconds == 0 && r.IsEnabled && r.Source == "wan");
    }
}
