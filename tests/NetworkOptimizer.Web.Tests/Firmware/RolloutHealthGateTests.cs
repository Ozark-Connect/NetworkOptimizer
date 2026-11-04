using FluentAssertions;
using NetworkOptimizer.Alerts.Models;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Web.Services.Firmware;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Firmware;

/// <summary>
/// The start-time health gate's verdict: any active critical alert holds a start, and a scheduled
/// start counts only the ones opened since its plan was booked.
/// </summary>
public class RolloutHealthGateTests
{
    private static readonly DateTime Booked = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    private static AlertHistoryEntry Alert(
        string title,
        DateTime triggeredAt,
        AlertSeverity severity = AlertSeverity.Critical,
        AlertStatus status = AlertStatus.Active) => new()
        {
            Title = title,
            EventType = "wan.outage",
            Source = "monitoring",
            Severity = severity,
            Status = status,
            TriggeredAt = triggeredAt,
        };

    [Fact]
    public void NoAlerts_IsHealthy()
    {
        RolloutHealthGate.Judge([], openedSince: null).Healthy.Should().BeTrue();
        RolloutHealthGate.Judge([], Booked).Healthy.Should().BeTrue();
    }

    [Theory]
    [InlineData(AlertSeverity.Info)]
    [InlineData(AlertSeverity.Warning)]
    [InlineData(AlertSeverity.Error)]
    public void BelowCritical_NeverHolds(AlertSeverity severity)
    {
        RolloutHealthGate.Judge([Alert("WAN Degraded", Booked.AddHours(1), severity)], null).Healthy.Should().BeTrue();
    }

    [Fact]
    public void OneActiveCritical_HoldsTheStartAndNamesIt()
    {
        var verdict = RolloutHealthGate.Judge([Alert("WAN Outage", Booked.AddHours(1))], null);

        verdict.Healthy.Should().BeFalse();
        verdict.Reason.Should().Be("a critical alert is open (WAN Outage)");
    }

    [Fact]
    public void SeveralCriticals_AreCountedAndTheNewestNamed()
    {
        var verdict = RolloutHealthGate.Judge(
        [
            Alert("Device Offline", Booked.AddHours(1)),
            Alert("WAN Outage", Booked.AddHours(3)),
            Alert("Gateway CPU", Booked.AddHours(2)),
        ], null);

        verdict.Reason.Should().Be("3 critical alerts are open (including WAN Outage)");
    }

    [Theory]
    [InlineData(AlertStatus.Acknowledged)]
    [InlineData(AlertStatus.Resolved)]
    [InlineData(AlertStatus.Suppressed)]
    public void ACriticalNoLongerActive_DoesNotHold(AlertStatus status)
    {
        RolloutHealthGate.Judge([Alert("WAN Outage", Booked.AddHours(1), status: status)], null).Healthy.Should().BeTrue();
    }

    [Fact]
    public void WithoutABookingTime_EveryActiveCriticalCounts_HoweverOld()
    {
        RolloutHealthGate.Judge([Alert("Target Offline", Booked.AddDays(-30))], null).Healthy.Should().BeFalse();
    }

    [Fact]
    public void ACriticalAlreadyOpenWhenThePlanWasBooked_DoesNotHold()
    {
        RolloutHealthGate.Judge([Alert("Target Offline", Booked.AddDays(-30))], Booked).Healthy.Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(24 * 6)]
    public void ACriticalOpenedSinceTheBooking_Holds(int hoursAfter)
    {
        RolloutHealthGate.Judge([Alert("WAN Outage", Booked.AddHours(hoursAfter))], Booked).Healthy.Should().BeFalse();
    }

    [Fact]
    public void OnlyTheCriticalsSinceTheBookingAreCounted()
    {
        var verdict = RolloutHealthGate.Judge(
        [
            Alert("Target Offline", Booked.AddDays(-30)),
            Alert("Old Device Offline", Booked.AddMinutes(-1)),
            Alert("WAN Outage", Booked.AddHours(2)),
        ], Booked);

        verdict.Reason.Should().Be("a critical alert is open (WAN Outage)");
    }
}
