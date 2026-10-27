using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Storage.Interfaces;

/// <summary>
/// Repository for one site's Change Control records: snapshots and their blob references, the
/// change history, Network Optimizer's write journal, detection gaps, plans, and console backups.
/// </summary>
public interface IChangeControlRepository
{
    /// <summary>The site's settings, creating the default row on first read.</summary>
    Task<ChangeControlSettings> GetSettingsAsync(CancellationToken cancellationToken = default);

    /// <summary>Writes every field of the settings row.</summary>
    Task SaveSettingsAsync(ChangeControlSettings settings, CancellationToken cancellationToken = default);

    /// <summary>The change-detection cursor, creating it on first read.</summary>
    Task<ChangeDetectionState> GetDetectionStateAsync(CancellationToken cancellationToken = default);

    /// <summary>Writes the change-detection cursor.</summary>
    Task SaveDetectionStateAsync(ChangeDetectionState state, CancellationToken cancellationToken = default);

    /// <summary>Inserts a snapshot and its collection blob references; returns it with its Id.</summary>
    Task<ConfigSnapshot> AddSnapshotAsync(ConfigSnapshot snapshot, IEnumerable<ConfigSnapshotBlob> blobs, CancellationToken cancellationToken = default);

    /// <summary>One snapshot, or null.</summary>
    Task<ConfigSnapshot?> GetSnapshotAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>The newest snapshot, or null when the site has none.</summary>
    Task<ConfigSnapshot?> GetLatestSnapshotAsync(CancellationToken cancellationToken = default);

    /// <summary>The newest snapshot holding at least one object of a collection, or null.</summary>
    Task<ConfigSnapshot?> GetLatestSnapshotWithAsync(string collection, CancellationToken cancellationToken = default);

    /// <summary>The newest snapshot taken at or before a time.</summary>
    Task<ConfigSnapshot?> GetSnapshotAtOrBeforeAsync(DateTime utc, CancellationToken cancellationToken = default);

    /// <summary>Snapshots newest first, optionally limited to a range.</summary>
    Task<List<ConfigSnapshot>> GetSnapshotsAsync(DateTime? fromUtc = null, DateTime? toUtc = null, int limit = 500, CancellationToken cancellationToken = default);

    /// <summary>Count of snapshots kept and their combined uncompressed size.</summary>
    Task<(int Count, long Bytes)> GetSnapshotTotalsAsync(CancellationToken cancellationToken = default);

    /// <summary>A snapshot's collection blob references.</summary>
    Task<List<ConfigSnapshotBlob>> GetSnapshotBlobsAsync(int snapshotId, CancellationToken cancellationToken = default);

    /// <summary>Writes a snapshot's mutable fields (pin, baseline, audit results).</summary>
    Task UpdateSnapshotAsync(ConfigSnapshot snapshot, CancellationToken cancellationToken = default);

    /// <summary>The snapshot marked Baseline, or null.</summary>
    Task<ConfigSnapshot?> GetBaselineSnapshotAsync(CancellationToken cancellationToken = default);

    /// <summary>Marks one snapshot as the Baseline and clears the mark on every other; null clears it.</summary>
    Task SetBaselineSnapshotAsync(int? snapshotId, CancellationToken cancellationToken = default);

    /// <summary>Deletes snapshots (retention) and their blob references.</summary>
    Task DeleteSnapshotsAsync(IReadOnlyCollection<int> snapshotIds, CancellationToken cancellationToken = default);

    /// <summary>Every blob hash still referenced by a snapshot.</summary>
    Task<HashSet<string>> GetReferencedBlobHashesAsync(CancellationToken cancellationToken = default);

    /// <summary>Inserts a change and its objects; returns it with its Id.</summary>
    Task<ConfigChange> AddChangeAsync(ConfigChange change, IEnumerable<ConfigChangeObject> objects, CancellationToken cancellationToken = default);

    /// <summary>One change, or null.</summary>
    Task<ConfigChange?> GetChangeAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>The newest config change, leaving out Network Optimizer's own except those from an applied plan.</summary>
    Task<ConfigChange?> GetLastConfigChangeAsync(CancellationToken cancellationToken = default);

    /// <summary>Changes newest first in a range.</summary>
    Task<List<ConfigChange>> GetChangesAsync(DateTime fromUtc, DateTime toUtc, int limit = 1000, CancellationToken cancellationToken = default);

    /// <summary>The objects of the given changes.</summary>
    Task<List<ConfigChangeObject>> GetChangeObjectsAsync(IReadOnlyCollection<int> changeIds, CancellationToken cancellationToken = default);

    /// <summary>The latest change to one object strictly after a time, for the "When, who" column.</summary>
    Task<(ConfigChange Change, ConfigChangeObject Object)?> GetLatestObjectChangeAsync(string collection, string objectId, DateTime afterUtc, DateTime toUtc, CancellationToken cancellationToken = default);

    /// <summary>Deletes changes and their objects (a recompute replaces them).</summary>
    Task DeleteChangesAsync(IReadOnlyCollection<int> changeIds, CancellationToken cancellationToken = default);

    /// <summary>Writes a change's mutable fields (impact, evaluation flag, actor).</summary>
    Task UpdateChangeAsync(ConfigChange change, CancellationToken cancellationToken = default);

    /// <summary>Changes whose alerts have not been evaluated yet, oldest first.</summary>
    Task<List<ConfigChange>> GetUnevaluatedChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>Records one of Network Optimizer's own writes.</summary>
    Task AddJournalEntryAsync(ConfigWriteJournalEntry entry, CancellationToken cancellationToken = default);

    /// <summary>Journal entries in a range, oldest first.</summary>
    Task<List<ConfigWriteJournalEntry>> GetJournalAsync(DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken = default);

    /// <summary>Removes journal entries older than a time.</summary>
    Task PruneJournalAsync(DateTime beforeUtc, CancellationToken cancellationToken = default);

    /// <summary>Opens a detection gap unless one is already open.</summary>
    Task OpenGapAsync(DateTime startedAtUtc, string reason, CancellationToken cancellationToken = default);

    /// <summary>Closes the open gap, if any, and returns it.</summary>
    Task<ChangeDetectionGap?> CloseGapAsync(DateTime endedAtUtc, bool caughtUp, CancellationToken cancellationToken = default);

    /// <summary>Gaps overlapping a range.</summary>
    Task<List<ChangeDetectionGap>> GetGapsAsync(DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken = default);

    /// <summary>Site Health hours starting in [from, to), oldest first.</summary>
    Task<List<SiteHealthHour>> GetHealthHoursAsync(DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken = default);

    /// <summary>Inserts or replaces Site Health hours by hour start, and drops hours older than <paramref name="pruneBeforeUtc"/>.</summary>
    Task SaveHealthHoursAsync(IReadOnlyList<SiteHealthHour> hours, DateTime pruneBeforeUtc, CancellationToken cancellationToken = default);

    /// <summary>Alerts of the given types that were active at any point in [from, to), oldest first.</summary>
    Task<List<NetworkOptimizer.Alerts.Models.AlertHistoryEntry>> GetAlertsActiveAsync(
        DateTime fromUtc, DateTime toUtc, IReadOnlyCollection<string> eventTypes, CancellationToken cancellationToken = default);

    /// <summary>Firmware Rollout steps whose device went down before <paramref name="toUtc"/> and was not back before <paramref name="fromUtc"/>. A step its ended plan left down is back when the plan ended.</summary>
    Task<List<FirmwareRolloutStep>> GetRolloutDowntimeAsync(DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken = default);

    /// <summary>Fabric monitoring targets that ping a device: target ID to device MAC.</summary>
    Task<Dictionary<string, string>> GetFabricTargetDevicesAsync(CancellationToken cancellationToken = default);

    /// <summary>Changes since a time whose health after has not been scored yet, oldest first.</summary>
    Task<List<ConfigChange>> GetChangesAwaitingHealthAsync(DateTime sinceUtc, DateTime occurredBeforeUtc, CancellationToken cancellationToken = default);

    /// <summary>Inserts a plan; returns it with its Id.</summary>
    Task<ConfigPlan> CreatePlanAsync(ConfigPlan plan, CancellationToken cancellationToken = default);

    /// <summary>One plan, or null.</summary>
    Task<ConfigPlan?> GetPlanAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>Writes a plan's mutable fields.</summary>
    Task UpdatePlanAsync(ConfigPlan plan, CancellationToken cancellationToken = default);

    /// <summary>Plans in the given statuses (all when null), newest first.</summary>
    Task<List<ConfigPlan>> GetPlansAsync(IReadOnlyCollection<ConfigPlanStatus>? statuses = null, int limit = 100, CancellationToken cancellationToken = default);

    /// <summary>Roll Back plans of one change that applied (or are applying), newest first: the change, the plan, and when it finished.</summary>
    Task<List<(int ChangeId, int PlanId, DateTime? CompletedAt)>> GetChangeRollbacksAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Alert incidents that started in a range and hold at least one alert of the given types
    /// (Changes and incidents: before / after), each with its alerts of those types.
    /// </summary>
    Task<List<(NetworkOptimizer.Alerts.Models.AlertIncident Incident, List<NetworkOptimizer.Alerts.Models.AlertHistoryEntry> Alerts)>> GetIncidentsStartedAsync(
        DateTime fromUtc, DateTime toUtc, IReadOnlyCollection<string> eventTypes, CancellationToken cancellationToken = default);

    /// <summary>One alert incident with its alerts of the given types, or null when it does not exist.</summary>
    Task<(NetworkOptimizer.Alerts.Models.AlertIncident Incident, List<NetworkOptimizer.Alerts.Models.AlertHistoryEntry> Alerts)?> GetIncidentAsync(
        int incidentId, IReadOnlyCollection<string> eventTypes, CancellationToken cancellationToken = default);

    /// <summary>Console backups, newest first.</summary>
    Task<List<ConsoleBackupRecord>> GetConsoleBackupsAsync(CancellationToken cancellationToken = default);

    /// <summary>Inserts a console backup record.</summary>
    Task AddConsoleBackupAsync(ConsoleBackupRecord record, CancellationToken cancellationToken = default);

    /// <summary>Deletes console backup records.</summary>
    Task DeleteConsoleBackupsAsync(IReadOnlyCollection<int> ids, CancellationToken cancellationToken = default);

    /// <summary>Dismissed Needs Attention items.</summary>
    Task<List<AttentionDismissal>> GetDismissalsAsync(CancellationToken cancellationToken = default);

    /// <summary>Records a dismissal; an item already dismissed is left as it is.</summary>
    Task AddDismissalAsync(AttentionDismissal dismissal, CancellationToken cancellationToken = default);

    /// <summary>Removes a dismissal, so the item shows again.</summary>
    Task RemoveDismissalAsync(string itemKey, CancellationToken cancellationToken = default);
}
