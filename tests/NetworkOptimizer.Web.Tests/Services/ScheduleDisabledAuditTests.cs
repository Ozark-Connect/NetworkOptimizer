using FluentAssertions;
using NetworkOptimizer.Alerts.Models;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services;
using NetworkOptimizer.Web.Services.Auditing;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Services;

/// <summary>
/// A schedule the app disables on its own is audited with Network Optimizer as the actor, in the
/// same shape as a user's toggle, so both show up together in the Audit Log.
/// </summary>
public class ScheduleDisabledAuditTests
{
    private sealed class CapturingAuditLogger : IAuditLogger
    {
        public List<AuditEvent> Events { get; } = new();
        public void Log(AuditEvent auditEvent) => Events.Add(auditEvent);
    }

    [Fact]
    public void LogScheduleDisabled_RecordsSystemActorAndReason()
    {
        var audit = new CapturingAuditLogger();
        var task = new ScheduledTask { Id = 7, Name = "WAN Speed Test (WAN2)" };

        ScheduleExecutorRegistration.LogScheduleDisabled(audit, "main", task, "could not reconcile");

        var e = audit.Events.Should().ContainSingle().Subject;
        e.Action.Should().Be(AuditActions.ScheduleChanged);
        e.Category.Should().Be(AuditCategories.Action);
        e.TargetType.Should().Be("schedule");
        e.TargetId.Should().Be("7");
        e.TargetName.Should().Be("WAN Speed Test (WAN2)");
        e.SiteSlug.Should().Be("main");
        e.ActorName.Should().Be("Network Optimizer");
        e.ActorAuthMethod.Should().Be("system");
        e.ActorUserId.Should().BeNull();
        e.DetailsJson.Should().Be("{\"Enabled\":false,\"Reason\":\"could not reconcile\"}");
    }
}
