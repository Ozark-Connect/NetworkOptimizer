using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Gates;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// The mutating slice of the Security Audit engine: running a scan (which talks to the UniFi Console
/// and writes a result) and curating its findings. The read surface stays on
/// <see cref="AuditService"/> itself - this interface exists so the actions a Viewer must not take
/// are gated and audited at the service layer (design doc 06, gate 9) rather than only hidden in the UI.
/// </summary>
[MutatingService(SiteScoped = true)]
public interface IAuditScanService
{
    /// <summary>Runs a security audit against the current site's UniFi Console.</summary>
    [RequireGlobalRole(GlobalRoles.Admin)]
    [AuditAction(AuditActions.AuditScanRun, TargetType = "security_audit")]
    Task<AuditResult> RunAuditAsync(AuditOptions options);

    /// <summary>Dismisses a finding so it stops appearing in the active list.</summary>
    [RequireGlobalRole(GlobalRoles.Admin)]
    [AuditAction(AuditActions.SettingsChanged, Category = AuditCategories.Settings, TargetType = "audit_issue")]
    Task DismissIssueAsync(AuditIssue issue);

    /// <summary>Restores a previously dismissed finding.</summary>
    [RequireGlobalRole(GlobalRoles.Admin)]
    [AuditAction(AuditActions.SettingsChanged, Category = AuditCategories.Settings, TargetType = "audit_issue")]
    Task RestoreIssueAsync(AuditIssue issue);

    /// <summary>Restores every dismissed finding.</summary>
    [RequireGlobalRole(GlobalRoles.Admin)]
    [AuditAction(AuditActions.SettingsChanged, Category = AuditCategories.Settings, TargetType = "audit_issue")]
    Task ClearDismissedIssuesAsync();

    /// <summary>Overrides the inferred purpose of a network, which changes how rules score it.</summary>
    [RequireGlobalRole(GlobalRoles.Admin)]
    [AuditAction(AuditActions.SettingsChanged, Category = AuditCategories.Settings, TargetType = "network_purpose")]
    Task SaveNetworkPurposeOverrideAsync(string networkId, string? purpose);
}
