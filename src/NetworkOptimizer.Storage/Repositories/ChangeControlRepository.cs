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

    public Task<List<NetworkOptimizer.Alerts.Models.AlertIncident>> GetIncidentsStartedAsync(DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken = default) =>
        ExecuteAsync(() => _context.AlertIncidents.AsNoTracking()
            .Where(i => i.FirstTriggeredAt >= fromUtc && i.FirstTriggeredAt <= toUtc)
            .OrderBy(i => i.FirstTriggeredAt).Take(500).ToListAsync(cancellationToken), nameof(GetIncidentsStartedAsync));

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
