using FluentAssertions;
using NetworkOptimizer.Alerts.Models;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Web.Services.Firmware;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Firmware;

/// <summary>Which open rollout alerts a moved start corrects, and that nothing else is touched.</summary>
public class RolloutAlertEditorTests
{
    private const string Moved = "1 device will be upgraded starting later.";
    private static readonly string[] Types = [RolloutAlerts.Upcoming, RolloutAlerts.StartingSoon];

    private static AlertHistoryEntry Alert(int id, string eventType, AlertStatus status = AlertStatus.Active, string message = "old") => new()
    {
        Id = id,
        EventType = eventType,
        Source = RolloutAlerts.Source,
        Status = status,
        Title = "Firmware Rollout Scheduled",
        Message = message,
    };

    [Fact]
    public void ActiveAndAcknowledgedAlertsOfTheListedTypes_AreRewritten()
    {
        var alerts = new[]
        {
            Alert(1, RolloutAlerts.Upcoming),
            Alert(2, RolloutAlerts.StartingSoon, AlertStatus.Acknowledged),
        };

        var changed = RolloutAlertEditor.Rewrite(alerts, Types, Moved);

        changed.Select(a => a.Id).Should().Equal(1, 2);
        alerts.Should().OnlyContain(a => a.Message == Moved);
    }

    [Fact]
    public void ResolvedAlerts_KeepWhatTheySaid()
    {
        var resolved = Alert(1, RolloutAlerts.Upcoming, AlertStatus.Resolved);

        RolloutAlertEditor.Rewrite([resolved], Types, Moved).Should().BeEmpty();
        resolved.Message.Should().Be("old");
    }

    [Fact]
    public void OtherAlerts_AreNotTouched()
    {
        var started = Alert(1, RolloutAlerts.Started);
        var postponed = Alert(2, RolloutAlerts.PostponedHealth);

        RolloutAlertEditor.Rewrite([started, postponed], Types, Moved).Should().BeEmpty();
        started.Message.Should().Be("old");
        postponed.Message.Should().Be("old");
    }

    [Fact]
    public void AnAlertAlreadySayingIt_IsNotWrittenAgain()
    {
        RolloutAlertEditor.Rewrite([Alert(1, RolloutAlerts.Upcoming, message: Moved)], Types, Moved).Should().BeEmpty();
    }

    [Fact]
    public void NoOpenAlerts_ChangesNothing()
    {
        RolloutAlertEditor.Rewrite([], Types, Moved).Should().BeEmpty();
    }
}
