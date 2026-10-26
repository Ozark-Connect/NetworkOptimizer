using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Storage.Interfaces;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Storage.Repositories;

/// <summary>
/// Repository for one site's Change Control records. Updates copy every column of the passed
/// entity onto the stored row (<c>SetValues</c>), so a new property never needs a hand-written copy.
/// </summary>
public class ChangeControlRepository : RepositoryBase, IChangeControlRepository
{
    private readonly NetworkOptimizerDbContext _context;

    public ChangeControlRepository(NetworkOptimizerDbContext context, ILogger<ChangeControlRepository> logger)
        : base(logger)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public Task<ChangeControlSettings> GetSettingsAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(async () =>
        {
            var existing = await _context.ChangeControlSettings.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
            if (existing != null) return existing;
            var created = new ChangeControlSettings { UpdatedAt = DateTime.UtcNow };
            _context.ChangeControlSettings.Add(created);
            await _context.SaveChangesAsync(cancellationToken);
            _context.Entry(created).State = EntityState.Detached;
            return created;
        }, nameof(GetSettingsAsync));

    public Task SaveSettingsAsync(ChangeControlSettings settings, CancellationToken cancellationToken = default) =>
        ExecuteAsync(async () =>
        {
            settings.UpdatedAt = DateTime.UtcNow;
            var existing = await _context.ChangeControlSettings.FirstOrDefaultAsync(cancellationToken);
            if (existing == null)
            {
                settings.Id = 0;
                _context.ChangeControlSettings.Add(settings);
            }
            else
            {
                settings.Id = existing.Id;
                _context.Entry(existing).CurrentValues.SetValues(settings);
            }
            await _context.SaveChangesAsync(cancellationToken);
        }, nameof(SaveSettingsAsync));

    public Task<ChangeDetectionState> GetDetectionStateAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(async () =>
        {
            var existing = await _context.ChangeDetectionStates.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
            if (existing != null) return existing;
            var created = new ChangeDetectionState();
            _context.ChangeDetectionStates.Add(created);
            await _context.SaveChangesAsync(cancellationToken);
            _context.Entry(created).State = EntityState.Detached;
            return created;
        }, nameof(GetDetectionStateAsync));

    public Task SaveDetectionStateAsync(ChangeDetectionState state, CancellationToken cancellationToken = default) =>
        ExecuteAsync(async () =>
        {
            var existing = await _context.ChangeDetectionStates.FirstOrDefaultAsync(cancellationToken);
            if (existing == null)
            {
                state.Id = 0;
                _context.ChangeDetectionStates.Add(state);
            }
            else
            {
                state.Id = existing.Id;
                _context.Entry(existing).CurrentValues.SetValues(state);
            }
            await _context.SaveChangesAsync(cancellationToken);
        }, nameof(SaveDetectionStateAsync));

    public Task<ConfigSnapshot> AddSnapshotAsync(ConfigSnapshot snapshot, IEnumerable<ConfigSnapshotBlob> blobs, CancellationToken cancellationToken = default) =>
        ExecuteAsync(async () =>
        {
            await using var tx = await _context.Database.BeginTransactionAsync(cancellationToken);
            _context.ConfigSnapshots.Add(snapshot);
            await _context.SaveChangesAsync(cancellationToken);
            foreach (var blob in blobs)
            {
                blob.SnapshotId = snapshot.Id;
                _context.ConfigSnapshotBlobs.Add(blob);
            }
            await _context.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            _context.ChangeTracker.Clear();
            return snapshot;
        }, nameof(AddSnapshotAsync));

    public Task<ConfigSnapshot?> GetSnapshotAsync(int id, CancellationToken cancellationToken = default) =>
        ExecuteAsync(() => _context.ConfigSnapshots.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, cancellationToken), nameof(GetSnapshotAsync));

    public Task<ConfigSnapshot?> GetLatestSnapshotAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(() => _context.ConfigSnapshots.AsNoTracking()
            .OrderByDescending(s => s.TakenAt).ThenByDescending(s => s.Id)
            .FirstOrDefaultAsync(cancellationToken), nameof(GetLatestSnapshotAsync));

    public Task<ConfigSnapshot?> GetLatestSnapshotWithAsync(string collection, CancellationToken cancellationToken = default) =>
        ExecuteAsync(() => _context.ConfigSnapshots.AsNoTracking()
            .Where(s => _context.ConfigSnapshotBlobs.Any(b => b.SnapshotId == s.Id && b.Collection == collection && b.ObjectCount > 0))
            .OrderByDescending(s => s.TakenAt).ThenByDescending(s => s.Id)
            .FirstOrDefaultAsync(cancellationToken), nameof(GetLatestSnapshotWithAsync));

    public Task<ConfigSnapshot?> GetSnapshotAtOrBeforeAsync(DateTime utc, CancellationToken cancellationToken = default) =>
        ExecuteAsync(() => _context.ConfigSnapshots.AsNoTracking()
            .Where(s => s.TakenAt <= utc)
            .OrderByDescending(s => s.TakenAt).ThenByDescending(s => s.Id)
            .FirstOrDefaultAsync(cancellationToken), nameof(GetSnapshotAtOrBeforeAsync));

    public Task<List<ConfigSnapshot>> GetSnapshotsAsync(DateTime? fromUtc = null, DateTime? toUtc = null, int limit = 500, CancellationToken cancellationToken = default) =>
        ExecuteAsync(() =>
        {
            var query = _context.ConfigSnapshots.AsNoTracking();
            if (fromUtc.HasValue) query = query.Where(s => s.TakenAt >= fromUtc.Value);
            if (toUtc.HasValue) query = query.Where(s => s.TakenAt <= toUtc.Value);
            return query.OrderByDescending(s => s.TakenAt).ThenByDescending(s => s.Id).Take(limit).ToListAsync(cancellationToken);
        }, nameof(GetSnapshotsAsync));

    public Task<(int Count, long Bytes)> GetSnapshotTotalsAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(async () =>
        {
            var count = await _context.ConfigSnapshots.CountAsync(cancellationToken);
            // Stored size: each distinct blob once, since snapshots share unchanged collections.
            var bytes = await _context.ConfigSnapshotBlobs.AsNoTracking()
                .GroupBy(b => b.Hash)
                .Select(g => g.Max(b => b.SizeBytes))
                .SumAsync(cancellationToken);
            return (count, bytes);
        }, nameof(GetSnapshotTotalsAsync));

    public Task<List<ConfigSnapshotBlob>> GetSnapshotBlobsAsync(int snapshotId, CancellationToken cancellationToken = default) =>
        ExecuteAsync(() => _context.ConfigSnapshotBlobs.AsNoTracking()
            .Where(b => b.SnapshotId == snapshotId).ToListAsync(cancellationToken), nameof(GetSnapshotBlobsAsync));

    public Task UpdateSnapshotAsync(ConfigSnapshot snapshot, CancellationToken cancellationToken = default) =>
        ExecuteAsync(async () =>
        {
            var existing = await _context.ConfigSnapshots.FirstOrDefaultAsync(s => s.Id == snapshot.Id, cancellationToken);
            if (existing == null) return;
            _context.Entry(existing).CurrentValues.SetValues(snapshot);
            await _context.SaveChangesAsync(cancellationToken);
        }, nameof(UpdateSnapshotAsync));

    public Task<ConfigSnapshot?> GetBaselineSnapshotAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(() => _context.ConfigSnapshots.AsNoTracking()
            .FirstOrDefaultAsync(s => s.IsBaseline, cancellationToken), nameof(GetBaselineSnapshotAsync));

    public Task SetBaselineSnapshotAsync(int? snapshotId, CancellationToken cancellationToken = default) =>
        ExecuteAsync(async () =>
        {
            var marked = await _context.ConfigSnapshots.Where(s => s.IsBaseline || s.Id == snapshotId).ToListAsync(cancellationToken);
            foreach (var s in marked)
                s.IsBaseline = s.Id == snapshotId;
            await _context.SaveChangesAsync(cancellationToken);
        }, nameof(SetBaselineSnapshotAsync));

    public Task DeleteSnapshotsAsync(IReadOnlyCollection<int> snapshotIds, CancellationToken cancellationToken = default) =>
        ExecuteAsync(async () =>
        {
            if (snapshotIds.Count == 0) return;
            foreach (var chunk in snapshotIds.Chunk(500))
            {
                await _context.ConfigSnapshotBlobs.Where(b => chunk.Contains(b.SnapshotId)).ExecuteDeleteAsync(cancellationToken);
                await _context.ConfigSnapshots.Where(s => chunk.Contains(s.Id)).ExecuteDeleteAsync(cancellationToken);
            }
        }, nameof(DeleteSnapshotsAsync));

    public Task<HashSet<string>> GetReferencedBlobHashesAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(async () => (await _context.ConfigSnapshotBlobs.AsNoTracking()
            .Select(b => b.Hash).Distinct().ToListAsync(cancellationToken)).ToHashSet(StringComparer.OrdinalIgnoreCase),
            nameof(GetReferencedBlobHashesAsync));

    public Task<ConfigChange> AddChangeAsync(ConfigChange change, IEnumerable<ConfigChangeObject> objects, CancellationToken cancellationToken = default) =>
        ExecuteAsync(async () =>
        {
            await using var tx = await _context.Database.BeginTransactionAsync(cancellationToken);
            _context.ConfigChanges.Add(change);
            await _context.SaveChangesAsync(cancellationToken);
            foreach (var obj in objects)
            {
                obj.ChangeId = change.Id;
                _context.ConfigChangeObjects.Add(obj);
            }
            await _context.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            _context.ChangeTracker.Clear();
            return change;
        }, nameof(AddChangeAsync));

    public Task<ConfigChange?> GetChangeAsync(int id, CancellationToken cancellationToken = default) =>
        ExecuteAsync(() => _context.ConfigChanges.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, cancellationToken), nameof(GetChangeAsync));

    public Task<ConfigChange?> GetLastConfigChangeAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(() => _context.ConfigChanges.AsNoTracking()
            .Where(c => c.Kind == ConfigChangeKind.Config && (c.ActorKind != ConfigChangeActorKind.NetworkOptimizer || c.PlanId != null))
            .OrderByDescending(c => c.OccurredAt).ThenByDescending(c => c.Id)
            .FirstOrDefaultAsync(cancellationToken), nameof(GetLastConfigChangeAsync));

    public Task<List<ConfigChange>> GetChangesAsync(DateTime fromUtc, DateTime toUtc, int limit = 1000, CancellationToken cancellationToken = default) =>
        ExecuteAsync(() => _context.ConfigChanges.AsNoTracking()
            .Where(c => c.OccurredAt >= fromUtc && c.OccurredAt <= toUtc)
            .OrderByDescending(c => c.OccurredAt).ThenByDescending(c => c.Id)
            .Take(limit).ToListAsync(cancellationToken), nameof(GetChangesAsync));

    public Task<List<ConfigChangeObject>> GetChangeObjectsAsync(IReadOnlyCollection<int> changeIds, CancellationToken cancellationToken = default) =>
        ExecuteAsync(async () =>
        {
            var result = new List<ConfigChangeObject>();
            foreach (var chunk in changeIds.Chunk(500))
                result.AddRange(await _context.ConfigChangeObjects.AsNoTracking()
                    .Where(o => chunk.Contains(o.ChangeId)).ToListAsync(cancellationToken));
            return result;
        }, nameof(GetChangeObjectsAsync));

    public Task<(ConfigChange Change, ConfigChangeObject Object)?> GetLatestObjectChangeAsync(string collection, string objectId, DateTime afterUtc, DateTime toUtc, CancellationToken cancellationToken = default) =>
        ExecuteAsync<(ConfigChange, ConfigChangeObject)?>(async () =>
        {
            var row = await (from o in _context.ConfigChangeObjects.AsNoTracking()
                             join c in _context.ConfigChanges.AsNoTracking() on o.ChangeId equals c.Id
                             where o.Collection == collection && o.ObjectId == objectId
                                   && c.OccurredAt > afterUtc && c.OccurredAt <= toUtc
                             orderby c.OccurredAt descending
                             select new { c, o }).FirstOrDefaultAsync(cancellationToken);
            return row == null ? null : (row.c, row.o);
        }, nameof(GetLatestObjectChangeAsync));

    public Task UpdateChangeAsync(ConfigChange change, CancellationToken cancellationToken = default) =>
        ExecuteAsync(async () =>
        {
            var existing = await _context.ConfigChanges.FirstOrDefaultAsync(c => c.Id == change.Id, cancellationToken);
            if (existing == null) return;
            _context.Entry(existing).CurrentValues.SetValues(change);
            await _context.SaveChangesAsync(cancellationToken);
        }, nameof(UpdateChangeAsync));

    public Task<List<ConfigChange>> GetUnevaluatedChangesAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(() => _context.ConfigChanges.AsNoTracking()
            .Where(c => !c.Evaluated).OrderBy(c => c.OccurredAt).Take(200).ToListAsync(cancellationToken),
            nameof(GetUnevaluatedChangesAsync));

    public Task AddJournalEntryAsync(ConfigWriteJournalEntry entry, CancellationToken cancellationToken = default) =>
        ExecuteAsync(async () =>
        {
            _context.ConfigWriteJournal.Add(entry);
            await _context.SaveChangesAsync(cancellationToken);
            _context.Entry(entry).State = EntityState.Detached;
        }, nameof(AddJournalEntryAsync));

    public Task<List<ConfigWriteJournalEntry>> GetJournalAsync(DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken = default) =>
        ExecuteAsync(() => _context.ConfigWriteJournal.AsNoTracking()
            .Where(j => j.At >= fromUtc && j.At <= toUtc).OrderBy(j => j.At).ToListAsync(cancellationToken),
            nameof(GetJournalAsync));

    public Task PruneJournalAsync(DateTime beforeUtc, CancellationToken cancellationToken = default) =>
        ExecuteAsync(() => _context.ConfigWriteJournal.Where(j => j.At < beforeUtc).ExecuteDeleteAsync(cancellationToken),
            nameof(PruneJournalAsync));

    public Task OpenGapAsync(DateTime startedAtUtc, string reason, CancellationToken cancellationToken = default) =>
        ExecuteAsync(async () =>
        {
            if (await _context.ChangeDetectionGaps.AnyAsync(g => g.EndedAt == null, cancellationToken)) return;
            _context.ChangeDetectionGaps.Add(new ChangeDetectionGap { StartedAt = startedAtUtc, Reason = reason });
            await _context.SaveChangesAsync(cancellationToken);
        }, nameof(OpenGapAsync));

    public Task<ChangeDetectionGap?> CloseGapAsync(DateTime endedAtUtc, bool caughtUp, CancellationToken cancellationToken = default) =>
        ExecuteAsync(async () =>
        {
            var open = await _context.ChangeDetectionGaps.FirstOrDefaultAsync(g => g.EndedAt == null, cancellationToken);
            if (open == null) return null;
            open.EndedAt = endedAtUtc;
            open.CaughtUp = caughtUp;
            await _context.SaveChangesAsync(cancellationToken);
            return open;
        }, nameof(CloseGapAsync));

    public Task<List<ChangeDetectionGap>> GetGapsAsync(DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken = default) =>
        ExecuteAsync(() => _context.ChangeDetectionGaps.AsNoTracking()
            .Where(g => g.StartedAt <= toUtc && (g.EndedAt == null || g.EndedAt >= fromUtc))
            .OrderBy(g => g.StartedAt).ToListAsync(cancellationToken), nameof(GetGapsAsync));

    public Task<List<SiteHealthHour>> GetHealthHoursAsync(DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken = default) =>
        ExecuteAsync(() => _context.SiteHealthHours.AsNoTracking()
            .Where(h => h.HourStart >= fromUtc && h.HourStart < toUtc)
            .OrderBy(h => h.HourStart).ToListAsync(cancellationToken), nameof(GetHealthHoursAsync));

    public Task SaveHealthHoursAsync(IReadOnlyList<SiteHealthHour> hours, DateTime pruneBeforeUtc, CancellationToken cancellationToken = default) =>
        ExecuteAsync(async () =>
        {
            var starts = hours.Select(h => h.HourStart).ToList();
            var existing = await _context.SiteHealthHours.Where(h => starts.Contains(h.HourStart)).ToListAsync(cancellationToken);
            foreach (var hour in hours)
            {
                var row = existing.FirstOrDefault(e => e.HourStart == hour.HourStart);
                if (row == null)
                {
                    hour.Id = 0;
                    _context.SiteHealthHours.Add(hour);
                }
                else
                {
                    hour.Id = row.Id;
                    _context.Entry(row).CurrentValues.SetValues(hour);
                }
            }
            await _context.SaveChangesAsync(cancellationToken);
            await _context.SiteHealthHours.Where(h => h.HourStart < pruneBeforeUtc).ExecuteDeleteAsync(cancellationToken);
        }, nameof(SaveHealthHoursAsync));

    public Task<List<NetworkOptimizer.Alerts.Models.AlertHistoryEntry>> GetAlertsActiveAsync(
        DateTime fromUtc, DateTime toUtc, IReadOnlyCollection<string> eventTypes, CancellationToken cancellationToken = default) =>
        ExecuteAsync(() =>
        {
            var types = eventTypes.ToList();
            // An unresolved alert older than a day is stale (the resolver missed it), not an outage still running.
            var floor = fromUtc.AddDays(-1);
            return _context.AlertHistory.AsNoTracking()
                .Where(a => types.Contains(a.EventType) && a.TriggeredAt < toUtc && a.TriggeredAt >= floor
                    && (a.ResolvedAt == null || a.ResolvedAt >= fromUtc))
                .OrderBy(a => a.TriggeredAt).Take(5000).ToListAsync(cancellationToken);
        }, nameof(GetAlertsActiveAsync));

    public Task<List<ConfigChange>> GetChangesAwaitingHealthAsync(DateTime sinceUtc, DateTime occurredBeforeUtc, CancellationToken cancellationToken = default) =>
        ExecuteAsync(() => _context.ConfigChanges.AsNoTracking()
            .Where(c => c.HealthAfter == null && c.OccurredAt >= sinceUtc && c.OccurredAt < occurredBeforeUtc)
            .OrderBy(c => c.OccurredAt).Take(200).ToListAsync(cancellationToken), nameof(GetChangesAwaitingHealthAsync));

    public Task<ConfigPlan> CreatePlanAsync(ConfigPlan plan, CancellationToken cancellationToken = default) =>
        ExecuteAsync(async () =>
        {
            plan.CreatedAt = plan.CreatedAt == default ? DateTime.UtcNow : plan.CreatedAt;
            plan.UpdatedAt = DateTime.UtcNow;
            _context.ConfigPlans.Add(plan);
            await _context.SaveChangesAsync(cancellationToken);
            _context.Entry(plan).State = EntityState.Detached;
            return plan;
        }, nameof(CreatePlanAsync));

    public Task<ConfigPlan?> GetPlanAsync(int id, CancellationToken cancellationToken = default) =>
        ExecuteAsync(() => _context.ConfigPlans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, cancellationToken), nameof(GetPlanAsync));

    public Task UpdatePlanAsync(ConfigPlan plan, CancellationToken cancellationToken = default) =>
        ExecuteAsync(async () =>
        {
            var existing = await _context.ConfigPlans.FirstOrDefaultAsync(p => p.Id == plan.Id, cancellationToken);
            if (existing == null) return;
            plan.UpdatedAt = DateTime.UtcNow;
            _context.Entry(existing).CurrentValues.SetValues(plan);
            await _context.SaveChangesAsync(cancellationToken);
        }, nameof(UpdatePlanAsync));

    public Task<List<ConfigPlan>> GetPlansAsync(IReadOnlyCollection<ConfigPlanStatus>? statuses = null, int limit = 100, CancellationToken cancellationToken = default) =>
        ExecuteAsync(() =>
        {
            var query = _context.ConfigPlans.AsNoTracking();
            if (statuses != null) query = query.Where(p => statuses.Contains(p.Status));
            return query.OrderByDescending(p => p.CreatedAt).Take(limit).ToListAsync(cancellationToken);
        }, nameof(GetPlansAsync));

    public Task<List<(int ChangeId, int PlanId, DateTime? CompletedAt)>> GetChangeRollbacksAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(async () =>
        {
            var applied = new[] { ConfigPlanStatus.Applying, ConfigPlanStatus.Watching, ConfigPlanStatus.Applied };
            var rows = await _context.ConfigPlans.AsNoTracking()
                .Where(p => p.RollsBackChangeId != null && applied.Contains(p.Status))
                .OrderByDescending(p => p.CreatedAt)
                .Select(p => new { ChangeId = p.RollsBackChangeId!.Value, p.Id, p.CompletedAt })
                .ToListAsync(cancellationToken);
            return rows.Select(r => (r.ChangeId, r.Id, r.CompletedAt)).ToList();
        }, nameof(GetChangeRollbacksAsync));

    public Task<List<(NetworkOptimizer.Alerts.Models.AlertIncident Incident, List<NetworkOptimizer.Alerts.Models.AlertHistoryEntry> Alerts)>> GetIncidentsStartedAsync(
        DateTime fromUtc, DateTime toUtc, IReadOnlyCollection<string> eventTypes, CancellationToken cancellationToken = default) =>
        ExecuteAsync(async () =>
        {
            var types = eventTypes.ToArray();
            var incidents = await _context.AlertIncidents.AsNoTracking()
                .Where(i => i.FirstTriggeredAt >= fromUtc && i.FirstTriggeredAt <= toUtc
                    && _context.AlertHistory.Any(a => a.IncidentId == i.Id && types.Contains(a.EventType)))
                .OrderBy(i => i.FirstTriggeredAt).Take(500).ToListAsync(cancellationToken);
            return await WithAlertsAsync(incidents, types, cancellationToken);
        }, nameof(GetIncidentsStartedAsync));

    public Task<(NetworkOptimizer.Alerts.Models.AlertIncident Incident, List<NetworkOptimizer.Alerts.Models.AlertHistoryEntry> Alerts)?> GetIncidentAsync(
        int incidentId, IReadOnlyCollection<string> eventTypes, CancellationToken cancellationToken = default) =>
        ExecuteAsync(async () =>
        {
            var incident = await _context.AlertIncidents.AsNoTracking().FirstOrDefaultAsync(i => i.Id == incidentId, cancellationToken);
            if (incident == null) return ((NetworkOptimizer.Alerts.Models.AlertIncident, List<NetworkOptimizer.Alerts.Models.AlertHistoryEntry>)?)null;
            return (await WithAlertsAsync([incident], eventTypes.ToArray(), cancellationToken))[0];
        }, nameof(GetIncidentAsync));

    private async Task<List<(NetworkOptimizer.Alerts.Models.AlertIncident, List<NetworkOptimizer.Alerts.Models.AlertHistoryEntry>)>> WithAlertsAsync(
        List<NetworkOptimizer.Alerts.Models.AlertIncident> incidents, string[] types, CancellationToken cancellationToken)
    {
        var ids = incidents.Select(i => i.Id).ToArray();
        var alerts = (await _context.AlertHistory.AsNoTracking()
                .Where(a => a.IncidentId != null && ids.Contains(a.IncidentId.Value) && types.Contains(a.EventType))
                .ToListAsync(cancellationToken))
            .ToLookup(a => a.IncidentId!.Value);
        return incidents.Select(i => (i, alerts[i.Id].ToList())).ToList();
    }

    public Task<List<ConsoleBackupRecord>> GetConsoleBackupsAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(() => _context.ConsoleBackups.AsNoTracking().OrderByDescending(b => b.TakenAt).ToListAsync(cancellationToken),
            nameof(GetConsoleBackupsAsync));

    public Task AddConsoleBackupAsync(ConsoleBackupRecord record, CancellationToken cancellationToken = default) =>
        ExecuteAsync(async () =>
        {
            _context.ConsoleBackups.Add(record);
            await _context.SaveChangesAsync(cancellationToken);
            _context.Entry(record).State = EntityState.Detached;
        }, nameof(AddConsoleBackupAsync));

    public Task DeleteConsoleBackupsAsync(IReadOnlyCollection<int> ids, CancellationToken cancellationToken = default) =>
        ExecuteAsync(() => _context.ConsoleBackups.Where(b => ids.Contains(b.Id)).ExecuteDeleteAsync(cancellationToken),
            nameof(DeleteConsoleBackupsAsync));
}
