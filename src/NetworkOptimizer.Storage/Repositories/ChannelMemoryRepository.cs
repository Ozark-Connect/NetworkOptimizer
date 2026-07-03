using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Storage.Interfaces;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Storage.Repositories;

/// <summary>
/// SQLite-backed store for the Channel Recommendation engine's outcome memory.
/// Per-site: channel history/neighbor sightings are per-site rows. Constructed with the
/// owning site's slug (default site uses the main database, non-default sites their own),
/// so the per-site background collector and the scoped web services each read their site.
/// </summary>
public class ChannelMemoryRepository : IChannelMemoryRepository
{
    private readonly IDbContextFactory<NetworkOptimizerDbContext> _dbFactory;
    private readonly Services.SiteDbContextFactory _siteDbFactory;
    private readonly ILogger<ChannelMemoryRepository> _logger;
    private readonly string _siteSlug;
    private readonly bool _isDefault;

    public ChannelMemoryRepository(
        IDbContextFactory<NetworkOptimizerDbContext> dbFactory,
        Services.SiteDbContextFactory siteDbFactory,
        ILogger<ChannelMemoryRepository> logger,
        string siteSlug = "",
        bool isDefault = true)
    {
        _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
        _siteDbFactory = siteDbFactory ?? throw new ArgumentNullException(nameof(siteDbFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _siteSlug = siteSlug ?? string.Empty;
        _isDefault = isDefault;
    }

    /// <summary>Context for the database holding this instance's site data.</summary>
    private async Task<NetworkOptimizerDbContext> CreateSiteDb(CancellationToken ctok)
    {
        if (!_isDefault)
            return _siteDbFactory.CreateForSite(_siteSlug, isDefault: false);
        return await _dbFactory.CreateDbContextAsync(ctok);
    }

    /// <inheritdoc />
    public async Task AddOutcomeSamplesAsync(
        IReadOnlyCollection<ChannelOutcomeSample> samples, CancellationToken cancellationToken = default)
    {
        if (samples.Count == 0) return;

        await using var db = await CreateSiteDb(cancellationToken);
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
        await using var db = await CreateSiteDb(cancellationToken);

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
        await using var db = await CreateSiteDb(cancellationToken);
        return await db.ApChannelOutcomes
            .AsNoTracking()
            .Where(o => o.BucketDate >= sinceUtc.Date)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<List<ApChannelChange>> GetChangesSinceAsync(
        DateTime sinceUtc, CancellationToken cancellationToken = default)
    {
        await using var db = await CreateSiteDb(cancellationToken);
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
        await using var db = await CreateSiteDb(cancellationToken);
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

        await using var db = await CreateSiteDb(cancellationToken);
        AddChangesCore(db, changes);
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task UpsertNeighborSightingsAsync(
        IReadOnlyCollection<NeighborSightingSample> sightings, CancellationToken cancellationToken = default)
    {
        if (sightings.Count == 0) return;

        await using var db = await CreateSiteDb(cancellationToken);

        // Group first: the same (AP, band, BSSID, channel) can occur multiple times in one
        // batch, and two Adds for the same key would violate the unique index.
        var grouped = sightings.GroupBy(s => (
            ApMac: s.ApMac.ToLowerInvariant(),
            s.Band,
            Bssid: s.Bssid.ToLowerInvariant(),
            s.Channel));

        foreach (var group in grouped)
        {
            var key = group.Key;
            var newest = group.OrderByDescending(s => s.SeenAtUtc).First();
            var maxSignal = group.Max(s => s.SignalDbm);
            var firstSeen = group.Min(s => s.SeenAtUtc);

            var row = await db.ApNeighborSightings.FirstOrDefaultAsync(n =>
                n.ApMac == key.ApMac && n.Band == key.Band &&
                n.Bssid == key.Bssid && n.Channel == key.Channel, cancellationToken);

            if (row == null)
            {
                db.ApNeighborSightings.Add(new ApNeighborSighting
                {
                    ApMac = key.ApMac,
                    Band = key.Band,
                    Bssid = key.Bssid,
                    Channel = key.Channel,
                    WidthMhz = newest.WidthMhz,
                    SignalDbm = maxSignal,
                    Ssid = newest.Ssid,
                    SightingCount = 1,
                    FirstSeenUtc = firstSeen,
                    LastSeenUtc = newest.SeenAtUtc
                });
                continue;
            }

            // One increment per upsert call: the collector runs this once per cycle with the
            // current neighbor picture, so the count measures cycles-seen (the persistence
            // signal), not how many samples happened to be in the batch.
            row.SightingCount++;
            row.SignalDbm = Math.Max(row.SignalDbm, maxSignal);
            if (newest.SeenAtUtc > row.LastSeenUtc)
            {
                row.LastSeenUtc = newest.SeenAtUtc;
                if (newest.WidthMhz > 0) row.WidthMhz = newest.WidthMhz;
                if (!string.IsNullOrEmpty(newest.Ssid)) row.Ssid = newest.Ssid;
            }
            if (firstSeen < row.FirstSeenUtc) row.FirstSeenUtc = firstSeen;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<List<ApNeighborSighting>> GetNeighborSightingsSinceAsync(
        DateTime lastSeenSinceUtc, CancellationToken cancellationToken = default)
    {
        await using var db = await CreateSiteDb(cancellationToken);
        return await db.ApNeighborSightings
            .AsNoTracking()
            .Where(n => n.LastSeenUtc >= lastSeenSinceUtc)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task PruneAsync(int retentionDays, int neighborRetentionDays, CancellationToken cancellationToken = default)
    {
        await using var db = await CreateSiteDb(cancellationToken);
        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);

        var outcomesPruned = await db.ApChannelOutcomes
            .Where(o => o.BucketDate < cutoff.Date)
            .ExecuteDeleteAsync(cancellationToken);

        // Keep the newest change per (ApMac, Band) regardless of age - it is the last known config.
        var keepIds = await QueryLatestChangeIdsAsync(db, cancellationToken);

        var changesPruned = await db.ApChannelChanges
            .Where(c => c.ChangedAtUtc < cutoff && !keepIds.Contains(c.Id))
            .ExecuteDeleteAsync(cancellationToken);

        var neighborCutoff = DateTime.UtcNow.AddDays(-neighborRetentionDays);
        var sightingsPruned = await db.ApNeighborSightings
            .Where(n => n.LastSeenUtc < neighborCutoff)
            .ExecuteDeleteAsync(cancellationToken);

        if (outcomesPruned > 0 || changesPruned > 0 || sightingsPruned > 0)
            _logger.LogDebug("Channel memory prune: removed {Outcomes} outcome buckets, {Changes} change records, {Sightings} neighbor sightings",
                outcomesPruned, changesPruned, sightingsPruned);
    }

    /// <inheritdoc />
    public async Task<DateTime?> GetCollectionWatermarkAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await CreateSiteDb(cancellationToken);
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
        await using var db = await CreateSiteDb(cancellationToken);
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
