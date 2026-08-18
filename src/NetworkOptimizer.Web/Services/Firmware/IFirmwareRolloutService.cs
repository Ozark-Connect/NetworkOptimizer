using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Gates;

namespace NetworkOptimizer.Web.Services.Firmware;

/// <summary>
/// The Firmware Rollout page's whole surface: settings, the wizard's dry run, the live plan, the
/// history and report, and the controls that start, hold and undo a rollout.
///
/// Site-scoped: a rollout upgrades the devices of the site in context, so Admin means Site Admin
/// THERE. Everything that plans or executes is Admin; every read - including the preview and the
/// report - is Viewer, because seeing what a rollout did to the network is not a privileged act.
///
/// Reads are asynchronous because a gated member must return a Task for the interceptor to
/// authorize and audit around it, not because they all touch the network.
/// </summary>
[MutatingService(SiteScoped = true)]
public interface IFirmwareRolloutService
{
    /// <summary>This site's rollout settings, defaulted on first read.</summary>
    [RequireRole(Roles.Viewer)]
    Task<FirmwareRolloutSettings> GetSettingsAsync(CancellationToken cancellationToken = default);

    /// <summary>The plan that is scheduled, running or soaking, with its steps. Null when none is.</summary>
    [RequireRole(Roles.Viewer)]
    Task<RolloutPlanView?> GetActivePlanAsync(CancellationToken cancellationToken = default);

    /// <summary>Past and present rollouts, newest first.</summary>
    /// <param name="limit">Most rows to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [RequireRole(Roles.Viewer)]
    Task<List<RolloutPlanSummaryView>> GetPlanHistoryAsync(int limit = 20, CancellationToken cancellationToken = default);

    /// <summary>One rollout's post-soak report, or null when there is no such plan.</summary>
    /// <param name="planId">Plan to report on.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [RequireRole(Roles.Viewer)]
    Task<RolloutReportView?> GetReportAsync(int planId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The wizard's dry run: refreshes the console's firmware catalog (UniFi's own "Check for
    /// Updates"), plans against the live topology, and proposes a start window. Changes nothing.
    /// </summary>
    /// <param name="settings">Settings to plan against, saved or not.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [RequireRole(Roles.Viewer)]
    Task<RolloutPreviewView> BuildPreviewAsync(FirmwareRolloutSettings settings, CancellationToken cancellationToken = default);

    /// <summary>Writes the site's rollout settings.</summary>
    /// <param name="settings">Settings to store.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.FirmwareRolloutSettingsChanged, TargetType = "firmware_rollout")]
    Task SaveSettingsAsync(FirmwareRolloutSettings settings, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the settings AND captures them as the standing Autopilot configuration. The only
    /// writer of that snapshot: a plain save cannot capture one, because every rollout commit goes
    /// through <see cref="SaveSettingsAsync"/> carrying whatever scope that one rollout used.
    /// </summary>
    /// <param name="settings">Settings to store and capture.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.FirmwareRolloutSettingsChanged, TargetType = "firmware_rollout")]
    Task SaveAutopilotSettingsAsync(FirmwareRolloutSettings settings, CancellationToken cancellationToken = default);

    /// <summary>
    /// Turns Autopilot off, keeping its captured configuration so it can be re-enabled as it was.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.FirmwareRolloutSettingsChanged, TargetType = "firmware_rollout")]
    Task DisableAutopilotAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Turns Autopilot back on, restoring the configuration captured when it was last saved.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>False when there is no captured configuration to restore.</returns>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.FirmwareRolloutSettingsChanged, TargetType = "firmware_rollout")]
    Task<bool> ReEnableAutopilotAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Plans a rollout and books it for a start time. The settings it was planned from are saved as
    /// part of committing it, because the executor reads them live as it runs.
    /// </summary>
    /// <param name="settings">Settings to plan and run with.</param>
    /// <param name="startAtUtc">When the rollout should begin.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The new plan's id.</returns>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.FirmwareRolloutScheduled, TargetType = "firmware_rollout")]
    Task<int> SchedulePlanAsync(FirmwareRolloutSettings settings, DateTime startAtUtc, CancellationToken cancellationToken = default);

    /// <summary>
    /// Plans a rollout and starts it. The health gate is advisory here: an admin who has read the
    /// warning can start anyway.
    /// </summary>
    /// <param name="settings">Settings to plan and run with.</param>
    /// <param name="overrideHealthGate">True to start despite open critical alerts.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The new plan's id.</returns>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.FirmwareRolloutStarted, TargetType = "firmware_rollout")]
    Task<int> StartNowAsync(FirmwareRolloutSettings settings, bool overrideHealthGate, CancellationToken cancellationToken = default);

    /// <summary>Holds a running rollout. Devices already mid-cycle are still watched to the end.</summary>
    /// <param name="planId">The running plan.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.FirmwareRolloutPaused, TargetType = "firmware_rollout")]
    Task PauseAsync(int planId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases a hold. On a plan paused at a wave boundary this is the per-wave approval.
    /// </summary>
    /// <param name="planId">The paused plan.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.FirmwareRolloutResumed, TargetType = "firmware_rollout")]
    Task ResumeAsync(int planId, CancellationToken cancellationToken = default);

    /// <summary>Stops a rollout for good and restores the console channels.</summary>
    /// <param name="planId">The plan to stop.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.FirmwareRolloutAborted, TargetType = "firmware_rollout")]
    Task AbortAsync(int planId, CancellationToken cancellationToken = default);

    /// <summary>Kicks a scheduled rollout off immediately instead of waiting for its window.</summary>
    /// <param name="planId">The waiting plan.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.FirmwareRolloutStarted, TargetType = "firmware_rollout")]
    Task DeployNowAsync(int planId, CancellationToken cancellationToken = default);

    /// <summary>Pushes an announced or scheduled rollout out by one window.</summary>
    /// <param name="planId">The waiting plan.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.FirmwareRolloutPostponed, TargetType = "firmware_rollout")]
    Task PostponeAsync(int planId, CancellationToken cancellationToken = default);

    /// <summary>Puts one device back on the firmware it was running before the rollout.</summary>
    /// <param name="stepId">The step to roll back.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when a rollback command was accepted.</returns>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.FirmwareRolloutRollback, TargetType = "device")]
    Task<bool> RollbackStepAsync(int stepId, CancellationToken cancellationToken = default);
}
