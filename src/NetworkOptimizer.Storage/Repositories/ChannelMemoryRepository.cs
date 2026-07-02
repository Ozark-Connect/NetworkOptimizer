using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Storage.Interfaces;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Storage.Repositories;

/// <summary>
/// SQLite-backed store for the Channel Recommendation engine's outcome memory.
/// Factory-based (not scoped-context) so both the singleton background collector and
/// scoped web services can share one registration.
/// </summary>
public class ChannelMemoryRepository : IChannelMemoryRepository
{
    private readonly IDbContextFactory<NetworkOptimizerDbContext> _dbFactory;
    private readonly ILogger<ChannelMemoryRepository> _logger;

    public ChannelMemoryRepository(
        IDbContextFactory<NetworkOptimizerDbContext> dbFactory,
        ILogger<ChannelMemoryRepository> logger)
    {
        _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task AddOutcomeSamplesAsync(
        IReadOnlyCollection<ChannelOutcomeSample> samples, CancellationToken cancellationToken = default)
    {
        if (samples.Count == 0) return;

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await UpsertSamplesCoreAsync(db, samples, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task CommitCollectionAsync(
        IReadOnlyCollection<ChannelOutcomeSample> samples,
        IReadOnlyCollection<ApChannelChange> changes,
        DateTime watermarkUtc,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        await UpsertSamplesCoreAsync(db, samples, cancellationToken);
        AddChangesCore(db, changes);
        await SetWatermarkCoreAsync(db, watermarkUtc, cancellationToken);

        // Single SaveChanges = single transaction: samples, changes, and watermark are atomic.
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<List<ApChannelOutcome>> GetOutcomesSinceAsync(
        DateTime sinceUtc, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.ApChannelOutcomes
            .AsNoTracking()
            .Where(o => o.BucketDate >= sinceUtc.Date)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<List<ApChannelChange>> GetChangesSinceAsync(
        DateTime sinceUtc, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.ApChannelChanges
            .AsNoTracking()
            .Where(c => c.ChangedAtUtc >= sinceUtc)
            .OrderBy(c => c.ChangedAtUtc)
            .ThenBy(c => c.Id)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<List<ApChannelChange>> GetLatestConfigsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var latestIds = await QueryLatestChangeIdsAsync(db, cancellationToken);

        return await db.ApChannelChanges
            .AsNoTracking()
            .Where(c => latestIds.Contains(c.Id))
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task AddChangesAsync(
        IReadOnlyCollection<ApChannelChange> changes, CancellationToken cancellationToken = default)
    {
        if (changes.Count == 0) return;

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        AddChangesCore(db, changes);
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task PruneAsync(int retentionDays, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);

        var outcomesPruned = await db.ApChannelOutcomes
            .Where(o => o.BucketDate < cutoff.Date)
            .ExecuteDeleteAsync(cancellationToken);

        // Keep the newest change per (ApMac, Band) regardless of age - it is the last known config.
        var keepIds = await QueryLatestChangeIdsAsync(db, cancellationToken);

        var changesPruned = await db.ApChannelChanges
            .Where(c => c.ChangedAtUtc < cutoff && !keepIds.Contains(c.Id))
            .ExecuteDeleteAsync(cancellationToken);

        if (outcomesPruned > 0 || changesPruned > 0)
            _logger.LogDebug("Channel memory prune: removed {Outcomes} outcome buckets, {Changes} change records",
                outcomesPruned, changesPruned);
    }

    /// <inheritdoc />
    public async Task<DateTime?> GetCollectionWatermarkAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var setting = await db.SystemSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == SystemSettingKeys.ChannelMemoryCollectionWatermark, cancellationToken);

        if (setting?.Value == null) return null;
        return DateTime.TryParse(setting.Value, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
    }

    /// <inheritdoc />
    public async Task SetCollectionWatermarkAsync(DateTime watermarkUtc, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await SetWatermarkCoreAsync(db, watermarkUtc, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Stage sample aggregation into daily buckets on the given context (no save).
    /// </summary>
    private static async Task UpsertSamplesCoreAsync(
        NetworkOptimizerDbContext db, IReadOnlyCollection<ChannelOutcomeSample> samples, CancellationToken cancellationToken)
    {
        var grouped = samples.GroupBy(s => (
            ApMac: s.ApMac.ToLowerInvariant(),
            s.Band,
            s.Channel,
            s.WidthMhz,
            BucketDate: s.TimestampUtc.Date));

        foreach (var group in grouped)
        {
            var key = group.Key;
            var bucket = await db.ApChannelOutcomes.FirstOrDefaultAsync(o =>
                o.ApMac == key.ApMac && o.Band == key.Band && o.Channel == key.Channel &&
                o.WidthMhz == key.WidthMhz && o.BucketDate == key.BucketDate, cancellationToken);

            if (bucket == null)
            {
                bucket = new ApChannelOutcome
                {
                    ApMac = key.ApMac,
                    Band = key.Band,
                    Channel = key.Channel,
                    WidthMhz = key.WidthMhz,
                    BucketDate = key.BucketDate
                };
                db.ApChannelOutcomes.Add(bucket);
            }

            foreach (var sample in group)
            {
                bucket.UtilizationSum += sample.Utilization;
                bucket.InterferenceSum += sample.Interference;
                bucket.TxRetrySum += sample.TxRetryPct;
                bucket.SampleCount++;
                if (sample.TimestampUtc > bucket.LastSampleUtc)
                    bucket.LastSampleUtc = sample.TimestampUtc;
            }
        }
    }

    /// <summary>
    /// Stage change records on the given context (no save).
    /// </summary>
    private static void AddChangesCore(NetworkOptimizerDbContext db, IReadOnlyCollection<ApChannelChange> changes)
    {
        foreach (var change in changes)
        {
            change.ApMac = change.ApMac.ToLowerInvariant();
            db.ApChannelChanges.Add(change);
        }
    }

    /// <summary>
    /// Stage the watermark setting on the given context (no save).
    /// </summary>
    private static async Task SetWatermarkCoreAsync(
        NetworkOptimizerDbContext db, DateTime watermarkUtc, CancellationToken cancellationToken)
    {
        var setting = await db.SystemSettings
            .FirstOrDefaultAsync(s => s.Key == SystemSettingKeys.ChannelMemoryCollectionWatermark, cancellationToken);

        if (setting == null)
        {
            setting = new SystemSetting { Key = SystemSettingKeys.ChannelMemoryCollectionWatermark };
            db.SystemSettings.Add(setting);
        }
        setting.Value = watermarkUtc.ToString("O");
        setting.UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Ids of the most recent change record per (ApMac, Band); Id breaks ties for
    /// same-timestamp records.
    /// </summary>
    private static Task<List<int>> QueryLatestChangeIdsAsync(
        NetworkOptimizerDbContext db, CancellationToken cancellationToken)
    {
        return db.ApChannelChanges
            .GroupBy(c => new { c.ApMac, c.Band })
            .Select(g => g.OrderByDescending(c => c.ChangedAtUtc).ThenByDescending(c => c.Id).First().Id)
            .ToListAsync(cancellationToken);
    }
}
