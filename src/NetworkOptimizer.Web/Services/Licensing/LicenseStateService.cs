using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services.Licensing;

/// <summary>
/// Owns the per-site licensing state machine. Recomputes a cached snapshot on a
/// timer and after every licensing mutation or phone-home result; enforcement
/// points read <see cref="IsSiteOperational"/> as a cheap dictionary lookup.
/// Fail-open by design: before the first successful compute, and if a compute
/// throws, every site reads as operational - licensing problems must never take
/// monitoring down harder than the policy demands.
/// </summary>
public class LicenseStateService : BackgroundService, NetworkOptimizer.Core.ISiteWorkGate
{
    /// <summary>How long sites keep working after their covering key expires or coverage is lost.</summary>
    public static readonly TimeSpan GracePeriod = TimeSpan.FromDays(10);

    private static readonly TimeSpan RecomputeInterval = TimeSpan.FromMinutes(15);

    private readonly IDbContextFactory<NetworkOptimizerDbContext> _mainDbFactory;
    private readonly TimeProvider _time;
    private readonly ILogger<LicenseStateService> _logger;

    private volatile LicenseSnapshot? _snapshot;

    /// <summary>Raised after a recompute whose result differs from the previous snapshot.</summary>
    public event Action? OnStateChanged;

    public LicenseStateService(
        IDbContextFactory<NetworkOptimizerDbContext> mainDbFactory,
        TimeProvider time,
        ILogger<LicenseStateService> logger)
    {
        _mainDbFactory = mainDbFactory;
        _time = time;
        _logger = logger;
    }

    /// <summary>The most recent snapshot, or null before the first compute.</summary>
    public LicenseSnapshot? Snapshot => _snapshot;

    /// <summary>True when at least one key is active and current.</summary>
    public bool AnyKeysActive => _snapshot?.AnyKeysActive ?? false;

    /// <summary>Sum of active, current key allowances (grace keys grant no new-site headroom).</summary>
    public int TotalAllowance => _snapshot?.TotalAllowance ?? 0;

    /// <summary>
    /// Cheap per-site gate for hot paths: false only when the site is known to
    /// be Restricted. Unknown slugs and the pre-compute window read as operational.
    /// </summary>
    public bool IsSiteOperational(string? slug)
    {
        var snapshot = _snapshot;
        if (snapshot == null)
            return true;
        // Alerts/Threats identify the default site by an empty site key; the snapshot keys it by slug.
        var key = string.IsNullOrEmpty(slug) ? Services.SiteManagementService.DefaultSiteSlug : slug;
        return !snapshot.States.TryGetValue(key, out var status) || status.IsOperational;
    }

    /// <summary>The computed status for a site, or null when unknown.</summary>
    public SiteLicenseStatus? GetStatus(string slug)
    {
        var snapshot = _snapshot;
        return snapshot != null && snapshot.States.TryGetValue(slug, out var status) ? status : null;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecomputeAsync();
        using var timer = new PeriodicTimer(RecomputeInterval, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await RecomputeAsync();
        }
        catch (OperationCanceledException)
        {
            // shutdown
        }
    }

    /// <summary>
    /// Reloads licensing data from the main database and swaps in a fresh
    /// snapshot. Safe to call from any thread; errors keep the previous snapshot.
    /// </summary>
    /// <param name="alwaysNotify">
    /// When true, raise <see cref="OnStateChanged"/> even if the recomputed snapshot
    /// is equivalent to the previous one. Used by callers (e.g. toggling multi-site)
    /// whose change affects dependent UI without altering the license snapshot itself.
    /// </param>
    public async Task RecomputeAsync(bool alwaysNotify = false)
    {
        try
        {
            var now = _time.GetUtcNow().UtcDateTime;

            List<Site> sites;
            List<LicenseKeyRecord> keys;
            List<SiteLicenseAssignment> assignments;
            Dictionary<string, DateTime> uncoveredSince;

            await using (var db = await _mainDbFactory.CreateDbContextAsync())
            {
                sites = await db.Sites.AsNoTracking().ToListAsync();
                keys = await db.LicenseKeyRecords.AsNoTracking().ToListAsync();
                assignments = await db.SiteLicenseAssignments.AsNoTracking().ToListAsync();
                uncoveredSince = await LoadUncoveredSinceAsync(db);
            }

            var states = ComputeStates(sites, keys, assignments, uncoveredSince, now);

            var anyActive = keys.Any(k => IsActiveCurrent(k, now));
            var totalAllowance = keys.Where(k => IsActiveCurrent(k, now)).Sum(k => k.SiteAllowance);
            var fresh = new LicenseSnapshot(states, anyActive, totalAllowance, now);

            await PersistUncoveredSinceAsync(states, uncoveredSince, now);

            var previous = _snapshot;
            _snapshot = fresh;

            if (previous == null || !SnapshotsEquivalent(previous, fresh))
            {
                LogTransitions(previous, fresh);
                OnStateChanged?.Invoke();
            }
            else if (alwaysNotify)
            {
                OnStateChanged?.Invoke();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "License state recompute failed; keeping previous snapshot");
        }
    }

    /// <summary>
    /// Pure state machine core (unit-test target). Implements the locked
    /// policies: free tier is a floor at three or fewer total sites; once any
    /// key is active and sites exceed the free limit every site needs coverage;
    /// expired coverage and lost coverage both get the 10-day grace period;
    /// with no live licensing and more than three sites, the default site plus
    /// the two oldest others stay operational on the free-tier floor.
    /// </summary>
    /// <param name="sites">All registered sites.</param>
    /// <param name="keys">All license key records.</param>
    /// <param name="assignments">All site-to-key assignments.</param>
    /// <param name="uncoveredSince">When each currently uncovered site (by slug) lost coverage; missing entries mean coverage was lost now.</param>
    /// <param name="nowUtc">Evaluation time (UTC).</param>
    public static IReadOnlyDictionary<string, SiteLicenseStatus> ComputeStates(
        IReadOnlyList<Site> sites,
        IReadOnlyList<LicenseKeyRecord> keys,
        IReadOnlyList<SiteLicenseAssignment> assignments,
        IReadOnlyDictionary<string, DateTime> uncoveredSince,
        DateTime nowUtc)
    {
        var result = new Dictionary<string, SiteLicenseStatus>();
        var keysById = keys.ToDictionary(k => k.Id);

        // Cap each key's assignments at its allowance, oldest first, so a
        // shrunk or stale allowance can never over-cover.
        var effectiveAssignment = new Dictionary<int, LicenseKeyRecord>(); // SiteId -> covering key
        foreach (var group in assignments.GroupBy(a => a.LicenseKeyRecordId))
        {
            if (!keysById.TryGetValue(group.Key, out var key))
                continue;
            foreach (var assignment in group.OrderBy(a => a.CreatedAt).ThenBy(a => a.Id).Take(Math.Max(0, key.SiteAllowance)))
                effectiveAssignment[assignment.SiteId] = key;
        }

        var anyCurrent = keys.Any(k => IsActiveCurrent(k, nowUtc));
        var anyInGrace = keys.Any(k => IsInGrace(k, nowUtc));
        var withinFreeTier = sites.Count <= Services.SiteManagementService.FreeSiteLimit;

        // Free-tier floor slots for the no-live-licensing case: the default
        // site plus the two oldest non-default sites, deterministically.
        var floorSlugs = sites
            .OrderByDescending(s => s.IsDefault)
            .ThenBy(s => s.CreatedAt)
            .ThenBy(s => s.Id)
            .Take(Services.SiteManagementService.FreeSiteLimit)
            .Select(s => s.Slug)
            .ToHashSet();

        foreach (var site in sites)
        {
            var covering = effectiveAssignment.GetValueOrDefault(site.Id);

            if (covering != null && IsActiveCurrent(covering, nowUtc))
            {
                result[site.Slug] = new SiteLicenseStatus(site.Slug, SiteLicenseState.Licensed, null, LicenseRestrictionReason.None, covering.Org);
                continue;
            }

            // Free-tier floor: at or under the free limit, uncovered sites are
            // simply free-tier regardless of what key rows exist.
            if (withinFreeTier)
            {
                result[site.Slug] = new SiteLicenseStatus(site.Slug, SiteLicenseState.FreeTier, null, LicenseRestrictionReason.None, null);
                continue;
            }

            if (covering != null && IsInGrace(covering, nowUtc))
            {
                var deadline = covering.PaidThrough!.Value + GracePeriod;
                result[site.Slug] = new SiteLicenseStatus(site.Slug, SiteLicenseState.Grace, deadline, LicenseRestrictionReason.KeyExpired, covering.Org);
                continue;
            }

            if (covering != null && covering.Status == LicenseKeyStatuses.Revoked)
            {
                result[site.Slug] = new SiteLicenseStatus(site.Slug, SiteLicenseState.Restricted, null, LicenseRestrictionReason.KeyRevoked, covering.Org);
                continue;
            }

            if (anyCurrent || anyInGrace)
            {
                // Covering key expired and its own grace already elapsed: the
                // site had its 10 days anchored to the paid-through date; no
                // second countdown.
                if (covering != null && covering.Status == LicenseKeyStatuses.Active
                    && covering.Model == LicenseKeyModels.Term
                    && covering.PaidThrough != null
                    && covering.PaidThrough.Value + GracePeriod < nowUtc)
                {
                    result[site.Slug] = new SiteLicenseStatus(site.Slug, SiteLicenseState.Restricted, null, LicenseRestrictionReason.KeyExpired, covering.Org);
                    continue;
                }

                // Licensing is in play but nothing covers this site (no
                // assignment, or the assigned key is pending/inert): 10-day
                // countdown anchored to when coverage was lost.
                var since = uncoveredSince.TryGetValue(site.Slug, out var t) ? t : nowUtc;
                var deadline = since + GracePeriod;
                result[site.Slug] = nowUtc < deadline
                    ? new SiteLicenseStatus(site.Slug, SiteLicenseState.Grace, deadline, LicenseRestrictionReason.Unassigned, null)
                    : new SiteLicenseStatus(site.Slug, SiteLicenseState.Restricted, null, LicenseRestrictionReason.Unassigned, null);
                continue;
            }

            // No live licensing at all with more than the free limit of sites:
            // free-tier floor keeps the default plus two oldest operational.
            result[site.Slug] = floorSlugs.Contains(site.Slug)
                ? new SiteLicenseStatus(site.Slug, SiteLicenseState.FreeTier, null, LicenseRestrictionReason.None, null)
                : new SiteLicenseStatus(site.Slug, SiteLicenseState.Restricted, null, LicenseRestrictionReason.OverFreeLimit, null);
        }

        return result;
    }

    /// <summary>Active key whose entitlement is current (perpetual, or term inside its paid-through date).</summary>
    public static bool IsActiveCurrent(LicenseKeyRecord key, DateTime nowUtc) =>
        key.Status == LicenseKeyStatuses.Active
        && (key.Model == LicenseKeyModels.Perpetual
            || (key.PaidThrough != null && key.PaidThrough.Value >= nowUtc));

    /// <summary>Active term key past its paid-through date but inside the grace period.</summary>
    public static bool IsInGrace(LicenseKeyRecord key, DateTime nowUtc) =>
        key.Status == LicenseKeyStatuses.Active
        && key.Model == LicenseKeyModels.Term
        && key.PaidThrough != null
        && key.PaidThrough.Value < nowUtc
        && key.PaidThrough.Value + GracePeriod >= nowUtc;

    private static async Task<Dictionary<string, DateTime>> LoadUncoveredSinceAsync(NetworkOptimizerDbContext db)
    {
        var setting = await db.SystemSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == SystemSettingKeys.LicensingUncoveredSince);
        if (string.IsNullOrEmpty(setting?.Value))
            return new Dictionary<string, DateTime>();
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, DateTime>>(setting.Value) ?? new Dictionary<string, DateTime>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, DateTime>();
        }
    }

    /// <summary>
    /// Maintains the uncovered-since bookkeeping map: sites currently in an
    /// uncovered countdown keep their original timestamp, newly uncovered sites
    /// are stamped now, and re-covered sites are dropped so a later loss of
    /// coverage starts a fresh countdown.
    /// </summary>
    private async Task PersistUncoveredSinceAsync(
        IReadOnlyDictionary<string, SiteLicenseStatus> states,
        Dictionary<string, DateTime> previous,
        DateTime nowUtc)
    {
        var fresh = new Dictionary<string, DateTime>();
        foreach (var (slug, status) in states)
        {
            // Only countdowns anchored to coverage loss are tracked here; a
            // key's own expiry countdown derives from its paid-through date.
            // Entries persist through Restricted so a recompute cannot restart
            // the clock; re-covered sites drop out and a later loss starts fresh.
            if (status.Reason == LicenseRestrictionReason.Unassigned)
                fresh[slug] = previous.TryGetValue(slug, out var t) ? t : nowUtc;
        }

        if (fresh.Count == previous.Count && fresh.All(kv => previous.TryGetValue(kv.Key, out var t) && t == kv.Value))
            return;

        await using var db = await _mainDbFactory.CreateDbContextAsync();
        var setting = await db.SystemSettings.FirstOrDefaultAsync(s => s.Key == SystemSettingKeys.LicensingUncoveredSince);
        var json = JsonSerializer.Serialize(fresh);
        if (setting == null)
        {
            db.SystemSettings.Add(new SystemSetting { Key = SystemSettingKeys.LicensingUncoveredSince, Value = json });
        }
        else
        {
            setting.Value = json;
            setting.UpdatedAt = nowUtc;
        }
        await db.SaveChangesAsync();
    }

    private static bool SnapshotsEquivalent(LicenseSnapshot a, LicenseSnapshot b)
    {
        if (a.AnyKeysActive != b.AnyKeysActive || a.TotalAllowance != b.TotalAllowance || a.States.Count != b.States.Count)
            return false;
        foreach (var (slug, status) in b.States)
        {
            if (!a.States.TryGetValue(slug, out var prev) || prev != status)
                return false;
        }
        return true;
    }

    private void LogTransitions(LicenseSnapshot? previous, LicenseSnapshot fresh)
    {
        foreach (var (slug, status) in fresh.States)
        {
            var prevState = previous != null && previous.States.TryGetValue(slug, out var p) ? p.State : (SiteLicenseState?)null;
            if (prevState != status.State)
                _logger.LogInformation("Site {Slug} license state: {Previous} -> {State} ({Reason})",
                    slug, prevState?.ToString() ?? "none", status.State, status.Reason);
        }
    }
}
