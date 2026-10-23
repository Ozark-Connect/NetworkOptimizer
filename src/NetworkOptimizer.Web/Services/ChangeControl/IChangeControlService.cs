using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Gates;

namespace NetworkOptimizer.Web.Services.ChangeControl;

/// <summary>
/// Change Control for the site in context (spec "RBAC"): Viewer reads everything (secrets always
/// redacted); Operator pins, downloads redacted YAML, edits and builds plans, and applies them (or
/// submits them when approvals are on); Site Admin exports secrets, sets the Baseline, approves, and
/// changes Change Control's settings.
/// </summary>
[MutatingService(SiteScoped = true)]
public interface IChangeControlService
{
    /// <summary>Changes in a range, newest first. NO's own changes are left out unless the setting shows them.</summary>
    [RequireRole(Roles.Viewer)]
    Task<List<ChangeRowView>> GetChangesAsync(DateTime fromUtc, DateTime toUtc, bool? includeOwn = null, CancellationToken cancellationToken = default);

    /// <summary>A change's detail: field diffs, impact, before/after numbers, incidents after.</summary>
    [RequireRole(Roles.Viewer)]
    Task<ChangeDetailView?> GetChangeDetailAsync(int changeId, CancellationToken cancellationToken = default);

    /// <summary>Changes in the two hours before an incident, for Alerts &amp; Schedule - Incidents.</summary>
    [RequireRole(Roles.Viewer)]
    Task<List<ChangeRowView>> GetChangesBeforeAsync(DateTime incidentStartUtc, CancellationToken cancellationToken = default);

    /// <summary>The Snapshots tab.</summary>
    [RequireRole(Roles.Viewer)]
    Task<SnapshotListView> GetSnapshotsAsync(CancellationToken cancellationToken = default);

    /// <summary>Compares two snapshots; a null side is the live config ("Now").</summary>
    [RequireRole(Roles.Viewer)]
    Task<CompareView> CompareAsync(int? fromSnapshotId, int? toSnapshotId, CancellationToken cancellationToken = default);

    /// <summary>The timeline lanes for this site.</summary>
    [RequireRole(Roles.Viewer)]
    Task<List<TimelineLane>> GetLanesAsync(DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken = default);

    /// <summary>Takes a snapshot now.</summary>
    [RequireRole(Roles.Operator)]
    [AuditAction(AuditActions.ChangeControlSnapshotTaken, TargetType = "config_snapshot")]
    Task<int?> TakeSnapshotAsync(CancellationToken cancellationToken = default);

    /// <summary>Pins a snapshot so retention never thins it.</summary>
    [RequireRole(Roles.Operator)]
    [AuditAction(AuditActions.ChangeControlSnapshotPinned, TargetType = "config_snapshot")]
    Task PinSnapshotAsync(int snapshotId, CancellationToken cancellationToken = default);

    /// <summary>Unpins a snapshot.</summary>
    [RequireRole(Roles.Operator)]
    [AuditAction(AuditActions.ChangeControlSnapshotUnpinned, TargetType = "config_snapshot")]
    Task UnpinSnapshotAsync(int snapshotId, CancellationToken cancellationToken = default);

    /// <summary>A snapshot (null: the live config) as YAML, secrets redacted.</summary>
    [RequireRole(Roles.Operator)]
    [AuditAction(AuditActions.ChangeControlYamlDownloaded, TargetType = "config_snapshot")]
    Task<string> DownloadYamlAsync(int? snapshotId, IReadOnlyCollection<string>? collections = null, CancellationToken cancellationToken = default);

    /// <summary>A snapshot (null: live) as YAML with secrets in clear text. Admin only, audited.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.ChangeControlSecretsExported, TargetType = "config_snapshot")]
    Task<string> DownloadYamlWithSecretsAsync(int? snapshotId, CancellationToken cancellationToken = default);

    /// <summary>The comparison as text, for Download Diff.</summary>
    [RequireRole(Roles.Viewer)]
    Task<string> DownloadDiffAsync(int? fromSnapshotId, int? toSnapshotId, CancellationToken cancellationToken = default);

    /// <summary>Edit tab: the YAML of one collection to start from, and per-collection object counts.</summary>
    [RequireRole(Roles.Operator)]
    Task<(string Yaml, Dictionary<string, int> Counts)> GetEditBaseAsync(int? snapshotId, string collection, CancellationToken cancellationToken = default);

    /// <summary>Edit tab: checks an edited file as it is typed.</summary>
    [RequireRole(Roles.Operator)]
    Task<List<YamlProblem>> ValidateYamlAsync(int? snapshotId, IReadOnlyDictionary<string, string> filesByCollection, CancellationToken cancellationToken = default);

    /// <summary>Builds a Roll Back plan to a snapshot.</summary>
    [RequireRole(Roles.Operator)]
    [AuditAction(AuditActions.ChangeControlPlanSaved, TargetType = "config_plan")]
    Task<int> BuildRollbackToSnapshotAsync(int snapshotId, CancellationToken cancellationToken = default);

    /// <summary>Builds a Roll Back plan for one change (its objects only).</summary>
    [RequireRole(Roles.Operator)]
    [AuditAction(AuditActions.ChangeControlPlanSaved, TargetType = "config_plan")]
    Task<int> BuildRollbackOfChangeAsync(int changeId, CancellationToken cancellationToken = default);

    /// <summary>Builds a plan from edited YAML files.</summary>
    [RequireRole(Roles.Operator)]
    [AuditAction(AuditActions.ChangeControlPlanSaved, TargetType = "config_plan")]
    Task<int> BuildEditPlanAsync(int? snapshotId, IReadOnlyDictionary<string, string> filesByCollection, CancellationToken cancellationToken = default);

    /// <summary>Builds a Return to Baseline plan.</summary>
    [RequireRole(Roles.Operator)]
    [AuditAction(AuditActions.ChangeControlPlanSaved, TargetType = "config_plan")]
    Task<int> BuildReturnToBaselineAsync(CancellationToken cancellationToken = default);

    /// <summary>A plan for Review Plan.</summary>
    [RequireRole(Roles.Viewer)]
    Task<PlanView?> GetPlanAsync(int planId, CancellationToken cancellationToken = default);

    /// <summary>Plans in progress and recent ones, newest first.</summary>
    [RequireRole(Roles.Viewer)]
    Task<List<PlanSummaryView>> GetPlansAsync(bool inProgressOnly, CancellationToken cancellationToken = default);

    /// <summary>Saves acknowledgements, accepted regressions, and the typed confirmation (Save Draft).</summary>
    [RequireRole(Roles.Operator)]
    [AuditAction(AuditActions.ChangeControlPlanSaved, TargetType = "config_plan")]
    Task SavePlanAsync(int planId, PlanAcknowledgements acks, DateTime? scheduledForUtc, bool applyOnApproval, CancellationToken cancellationToken = default);

    /// <summary>Submits a plan for Site Admin approval.</summary>
    [RequireRole(Roles.Operator)]
    [AuditAction(AuditActions.ChangeControlPlanSubmitted, TargetType = "config_plan")]
    Task SubmitPlanAsync(int planId, CancellationToken cancellationToken = default);

    /// <summary>Approves a plan.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.ChangeControlPlanApproved, TargetType = "config_plan")]
    Task ApprovePlanAsync(int planId, CancellationToken cancellationToken = default);

    /// <summary>Applies a plan now. Returns why it did not, or null.</summary>
    [RequireRole(Roles.Operator)]
    [AuditAction(AuditActions.ChangeControlPlanApplied, TargetType = "config_plan")]
    Task<string?> ApplyPlanAsync(int planId, CancellationToken cancellationToken = default);

    /// <summary>Discards a plan that has not applied.</summary>
    [RequireRole(Roles.Operator)]
    [AuditAction(AuditActions.ChangeControlPlanCancelled, TargetType = "config_plan")]
    Task CancelPlanAsync(int planId, CancellationToken cancellationToken = default);

    /// <summary>Reverts an applied plan to the snapshot pinned before it.</summary>
    [RequireRole(Roles.Operator)]
    [AuditAction(AuditActions.ChangeControlPlanReverted, TargetType = "config_plan")]
    Task<string?> RevertPlanAsync(int planId, CancellationToken cancellationToken = default);

    /// <summary>An applied plan's before/after report as PDF, or null when not built yet.</summary>
    [RequireRole(Roles.Viewer)]
    Task<byte[]?> GetPlanReportPdfAsync(int planId, CancellationToken cancellationToken = default);

    /// <summary>Change Control settings for the site.</summary>
    [RequireRole(Roles.Viewer)]
    Task<ChangeControlSettings> GetSettingsAsync(CancellationToken cancellationToken = default);

    /// <summary>Saves Change Control settings.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.ChangeControlSettingsChanged, TargetType = "change_control")]
    Task SaveSettingsAsync(ChangeControlSettings settings, CancellationToken cancellationToken = default);

    /// <summary>Marks a snapshot as the Baseline (null clears it).</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.ChangeControlBaselineSet, TargetType = "config_snapshot")]
    Task SetBaselineAsync(int? snapshotId, CancellationToken cancellationToken = default);

    /// <summary>Takes a snapshot of the live config and marks it as the Baseline (Accept as Baseline).</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.ChangeControlBaselineSet, TargetType = "config_snapshot")]
    Task AcceptLiveAsBaselineAsync(CancellationToken cancellationToken = default);

    /// <summary>Uses a template as the Baseline (null clears it).</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.ChangeControlBaselineSet, TargetType = "config_template")]
    Task SetBaselineTemplateAsync(int? templateId, CancellationToken cancellationToken = default);

    /// <summary>Drift from the Baseline.</summary>
    [RequireRole(Roles.Viewer)]
    Task<DriftView> GetDriftAsync(CancellationToken cancellationToken = default);

    /// <summary>Whether the caller is a Site Admin here (for showing admin-only controls).</summary>
    [RequireRole(Roles.Viewer)]
    Task<bool> IsSiteAdminAsync(CancellationToken cancellationToken = default);
}
