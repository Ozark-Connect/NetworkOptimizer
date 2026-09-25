using FluentAssertions;
using NetworkOptimizer.Alerts.Models;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Web.Components.Shared;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Components;

public class ActiveAlertsPanelRankTests
{
    private static readonly DateTime Now = new(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc);

    private static AlertHistoryEntry Alert(int id, AlertSeverity severity, AlertStatus status, int minutesAgo) => new()
    {
        Id = id,
        Severity = severity,
        Status = status,
        TriggeredAt = Now.AddMinutes(-minutesAgo),
        Title = $"Alert {id}"
    };

    [Fact]
    public void RankTop_HighestSeverityFirst_ThenActive_ThenNewest()
    {
        var alerts = new[]
        {
            Alert(1, AlertSeverity.Warning, AlertStatus.Active, 1),
            Alert(2, AlertSeverity.Critical, AlertStatus.Acknowledged, 1),
            Alert(3, AlertSeverity.Critical, AlertStatus.Active, 60),
            Alert(4, AlertSeverity.Critical, AlertStatus.Active, 5),
            Alert(5, AlertSeverity.Info, AlertStatus.Active, 0),
        };

        ActiveAlertsPanel.RankTop(alerts, 3).Select(a => a.Id).Should().Equal(4, 3, 2);
    }

    [Fact]
    public void RankTop_SkipsResolvedAndSuppressed()
    {
        var alerts = new[]
        {
            Alert(1, AlertSeverity.Critical, AlertStatus.Resolved, 1),
            Alert(2, AlertSeverity.Critical, AlertStatus.Suppressed, 1),
            Alert(3, AlertSeverity.Info, AlertStatus.Active, 1),
        };

        ActiveAlertsPanel.RankTop(alerts, 3).Select(a => a.Id).Should().Equal(3);
    }
}
