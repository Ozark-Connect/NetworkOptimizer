using FluentAssertions;
using NetworkOptimizer.Alerts;
using NetworkOptimizer.Core.Enums;
using Xunit;

namespace NetworkOptimizer.Alerts.Tests;

public class SpeedTestAlertRuleTests
{
    [Fact]
    public void ClientRegressionRule_ShipsDisabled_AtAHighThreshold()
    {
        var rule = DefaultAlertRules.GetDefaults().Should().ContainSingle(r => r.EventTypePattern == "speedtest.client_regression").Subject;

        rule.IsEnabled.Should().BeFalse();
        rule.Source.Should().Be("speedtest");
        rule.ThresholdPercent.Should().Be(50);
        rule.MinSeverity.Should().Be(AlertSeverity.Warning);
    }
}
