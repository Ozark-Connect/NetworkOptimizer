using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Storage.Interfaces;

/// <summary>
/// Repository for Firmware Rollout settings, plans, per-device steps, and learned model timings.
/// </summary>
public interface IFirmwareRolloutRepository
{
    /// <summary>
    /// Returns the site's rollout settings, creating and persisting the default row the
    /// first time it is asked for so callers never have to handle a missing singleton.
    /// </summary>
    Task<FirmwareRolloutSettings> GetSettingsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the settings singleton. Updates copy every field onto the stored row, so a
    /// detached instance from the UI replaces the persisted state exactly - except the autopilot
    /// snapshot, which only <see cref="SaveAutopilotSnapshotAsync"/> writes.
    /// </summary>
    Task SaveSettingsAsync(FirmwareRolloutSettings settings, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the standing Autopilot configuration, and nothing else on the row.
    /// </summary>
    /// <param name="snapshotJson">Serialized settings, or null to forget them.</param>
    Task SaveAutopilotSnapshotAsync(string? snapshotJson, CancellationToken cancellationToken = default);

    /// <summary>Inserts a new plan and returns it with its assigned Id.</summary>
    Task<FirmwareRolloutPlan> CreatePlanAsync(FirmwareRolloutPlan plan, CancellationToken cancellationToken = default);

    /// <summary>
    /// The one plan that is not in a terminal status (Reported / Aborted / Failed), newest
    /// first if history ever leaves more than one behind. Null when nothing is in flight.
    /// </summary>
    Task<FirmwareRolloutPlan?> GetActivePlanAsync(CancellationToken cancellationToken = default);

    /// <summary>Gets one plan by id, or null.</summary>
    Task<FirmwareRolloutPlan?> GetPlanAsync(int planId, CancellationToken cancellationToken = default);

    /// <summary>Past and present plans, newest first.</summary>
    Task<List<FirmwareRolloutPlan>> GetPlanHistoryAsync(int limit = 50, CancellationToken cancellationToken = default);

    /// <summary>Persists a plan's mutable state (status, timestamps, plan/channel/report JSON). No-op for an unknown id.</summary>
    Task UpdatePlanAsync(FirmwareRolloutPlan plan, CancellationToken cancellationToken = default);

    /// <summary>Inserts the steps of a plan in one round trip.</summary>
    Task AddStepsAsync(IEnumerable<FirmwareRolloutStep> steps, CancellationToken cancellationToken = default);

    /// <summary>Persists one step's transition. No-op for an unknown id.</summary>
    Task UpdateStepAsync(FirmwareRolloutStep step, CancellationToken cancellationToken = default);

    /// <summary>A plan's steps in execution order (wave, then id).</summary>
    Task<List<FirmwareRolloutStep>> GetStepsAsync(int planId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Folds one measured downtime into the model's learned timing, creating the row on
    /// first sight, and returns the updated aggregate.
    /// </summary>
    Task<FirmwareModelTiming> RecordModelTimingAsync(string model, int downtimeSeconds, CancellationToken cancellationToken = default);

    /// <summary>All learned model timings, by model.</summary>
    Task<List<FirmwareModelTiming>> GetModelTimingsAsync(CancellationToken cancellationToken = default);

    /// <summary>The learned timing for one model, or null when the site has never upgraded it.</summary>
    Task<FirmwareModelTiming?> GetModelTimingAsync(string model, CancellationToken cancellationToken = default);
}
