using FluentAssertions;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services;
using NetworkOptimizer.Web.Services.ChangeControl;
using Xunit;
using AuditSeverity = NetworkOptimizer.Audit.Models.AuditSeverity;

namespace NetworkOptimizer.Web.Tests.ChangeControl;

/// <summary>
/// One incident per Change Control change: the key its alerts share, what the follow-up Security Audit carries, and
/// which of that audit's alerts still report something new.
/// </summary>
public class ChangeIncidentGroupingTests
{
    #region Incident key

    [Fact]
    public void IncidentKey_PerChange_EvenWithinOneDetection()
    {
        // Two changes in one poll can come from different actors; each keeps its own incident and first delivery
        var a = new ConfigChange { Id = 1, SnapshotId = 7 };
        var b = new ConfigChange { Id = 2, SnapshotId = 7 };

        ChangeAlerts.IncidentKey(a).Should().Be("change:1");
        ChangeAlerts.IncidentKey(b).Should().Be("change:2");
    }

    #endregion

    #region Pending follow-up audit

    [Fact]
    public void PendingAudit_First_TakesKeysAndIncident()
    {
        var pending = ChangeEvaluator.PendingAudit.Merge(null, ["a"], "change:1");

        pending.AlertedKeys.Should().BeEquivalentTo(["a"]);
        pending.IncidentKey.Should().Be("change:1");
    }

    [Fact]
    public void PendingAudit_Rescheduled_UnionsKeysAndKeepsFirstIncident()
    {
        var held = ChangeEvaluator.PendingAudit.Merge(null, ["a"], "change:1");

        var pending = ChangeEvaluator.PendingAudit.Merge(held, ["b", "a"], "change:2");

        pending.AlertedKeys.Should().BeEquivalentTo(["a", "b"]);
        pending.IncidentKey.Should().Be("change:1");
    }

    [Fact]
    public void PendingAudit_HeldWithoutIncident_TakesTheNewOne()
    {
        var held = ChangeEvaluator.PendingAudit.Merge(null, [], null);

        ChangeEvaluator.PendingAudit.Merge(held, [], "change:2").IncidentKey.Should().Be("change:2");
    }

    #endregion

    #region Follow-up audit alerts

    private static AuditIssue Issue(string title, AuditSeverity severity = AuditSeverity.Critical, string category = "Firewall Rules") =>
        new() { Title = title, Severity = severity, Category = category };

    private static string Key(AuditIssue issue) => ImpactAnalyzer.IssueKey(issue);

    [Fact]
    public void AlertableCriticals_LeavesOutAlertedSystemAndNonCritical()
    {
        var alerted = Issue("Firewall: Gateway Management Open to Internet");
        var fresh = Issue("Firewall: Any-Any Rule");
        var system = Issue("Fingerprint database unavailable", category: "System");
        var recommended = Issue("Missing MAC Restriction", AuditSeverity.Recommended);

        var result = AuditService.AlertableCriticals([alerted, fresh, system, recommended], new HashSet<string> { Key(alerted) });

        result.Should().ContainSingle().Which.Should().BeSameAs(fresh);
    }

    [Fact]
    public void AlertableCriticals_NoAlertedKeys_EveryCritical()
    {
        AuditService.AlertableCriticals([Issue("A"), Issue("B")], null).Should().HaveCount(2);
    }

    [Fact]
    public void OnlyAlertedAreNew_NewFindingWasAlerted_True()
    {
        var old = Issue("Old");
        var alerted = Issue("Firewall: Gateway Management Open to Internet");

        AuditService.OnlyAlertedAreNew([old, alerted], Json(old), new HashSet<string> { Key(alerted) }).Should().BeTrue();
    }

    [Fact]
    public void OnlyAlertedAreNew_AnotherNewFinding_False()
    {
        var old = Issue("Old");
        var alerted = Issue("Firewall: Gateway Management Open to Internet");
        var other = Issue("Firewall: Any-Any Rule");

        AuditService.OnlyAlertedAreNew([old, alerted, other], Json(old), new HashSet<string> { Key(alerted) }).Should().BeFalse();
    }

    [Fact]
    public void OnlyAlertedAreNew_NoAlertedKeys_False()
    {
        var old = Issue("Old");

        AuditService.OnlyAlertedAreNew([old], Json(old), null).Should().BeFalse();
        AuditService.OnlyAlertedAreNew([old], Json(old), new HashSet<string>()).Should().BeFalse();
    }

    [Fact]
    public void OnlyAlertedAreNew_NoPreviousFindings_False()
    {
        var alerted = Issue("Firewall: Gateway Management Open to Internet");

        AuditService.OnlyAlertedAreNew([alerted], null, new HashSet<string> { Key(alerted) }).Should().BeFalse();
    }

    private static string Json(params AuditIssue[] issues) => System.Text.Json.JsonSerializer.Serialize(issues);

    #endregion
}
