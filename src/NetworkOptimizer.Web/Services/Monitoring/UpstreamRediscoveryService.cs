using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>
/// Re-runs upstream tracer discovery on a 7-day cadence (locked Gate 2 decision 6).
/// When the new candidate set differs from what's currently committed, flips
/// MonitoringSettings.UpstreamDiscoveryNeedsReview = true so the Monitoring page
/// can surface a banner. Never silently replaces targets - the user reviews and
/// commits, just like the first run.
///
/// Ticks hourly to evaluate the threshold; the actual discovery sweep only happens
/// when 7 days have elapsed since LastUpstreamDiscoveryAt.
/// </summary>
public class UpstreamRediscoveryService : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan RediscoveryThreshold = TimeSpan.FromDays(7);

    // An auto-discovered ASN must be absent from this many consecutive runs before it's a
    // removal candidate - long enough (3 cycles) to ride out an incomplete/degraded run.
    private const int RemovalConfirmRuns = 3;

    // While a removal counter is pending (some ASN currently absent), re-check on this shorter
    // cadence instead of the full threshold, so a real removal confirms in ~3 days rather than
    // ~3 weeks and a transient miss clears the next day.
    private static readonly TimeSpan PendingRecheckInterval = TimeSpan.FromHours(24);

    private readonly IDbContextFactory<NetworkOptimizerDbContext> _dbFactory;
    private readonly NetworkOptimizer.Storage.Services.SiteDbContextFactory _siteDbFactory;
    private readonly UpstreamTracerRegistry _tracerRegistry;
    private readonly ILogger<UpstreamRediscoveryService> _logger;

    public UpstreamRediscoveryService(
        IDbContextFactory<NetworkOptimizerDbContext> dbFactory,
        NetworkOptimizer.Storage.Services.SiteDbContextFactory siteDbFactory,
        UpstreamTracerRegistry tracerRegistry,
        ILogger<UpstreamRediscoveryService> logger)
    {
        _dbFactory = dbFactory;
        _siteDbFactory = siteDbFactory;
        _tracerRegistry = tracerRegistry;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // First tick after a short warm-up so we don't run on the same boot as the
        // app starting. Re-discovery is cheap but it does fire 10 traceroutes.
        await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogWarning(ex, "Upstream re-discovery tick failed"); }

            try { await Task.Delay(TickInterval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        // Each enabled site re-discovers independently: its own per-site tracer (own
        // vantage + gateway) writing to its own DB. Default site first.
        List<(string Slug, bool IsDefault)> sites;
        try
        {
            await using var mainDb = await _dbFactory.CreateDbContextAsync(ct);
            sites = (await mainDb.Sites.AsNoTracking().Where(s => s.Enabled)
                    .Select(s => new { s.Slug, s.IsDefault }).ToListAsync(ct))
                .Select(s => (s.Slug, s.IsDefault))
                .OrderBy(x => x.IsDefault ? 0 : 1)
                .ToList();
        }
        catch { sites = new(); }
        // Pre-multisite installs have no Sites rows; fall back to the default site.
        if (sites.Count == 0)
            sites = new() { (SiteManagementService.DefaultSiteSlug, true) };

        foreach (var (slug, isDefault) in sites)
        {
            if (ct.IsCancellationRequested) return;
            try { await TickSiteAsync(slug, isDefault, ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "Upstream re-discovery tick failed for site {Slug}", slug); }
        }
    }

    private async Task TickSiteAsync(string slug, bool isDefault, CancellationToken ct)
    {
        var tracer = _tracerRegistry.GetFor(slug);
        await using var db = isDefault
            ? await _dbFactory.CreateDbContextAsync(ct)
            : _siteDbFactory.CreateForSite(slug, isDefault: false);
        var settings = await db.MonitoringSettings.FirstOrDefaultAsync(ct);
        if (settings == null || !settings.Enabled) return;
        if (settings.UpstreamDiscoveryNeedsReview) return; // already flagged - waiting for user
        if (!settings.LastUpstreamDiscoveryAt.HasValue) return; // never committed - nothing to re-discover

        // While a removal counter is pending for any WAN, run on the shorter recheck cadence so a
        // genuine removal confirms quickly and a transient miss clears, instead of waiting a full
        // 7-day cycle each time. SaveMissCounts stores null when no ASN is absent, so a non-null
        // value means a counter is in flight.
        var pendingRecheck = await db.SystemSettings.AnyAsync(
            s => s.Key.StartsWith(SystemSettingKeys.UpstreamAbsentAsnCountsPrefix) && s.Value != null, ct);
        var threshold = pendingRecheck ? PendingRecheckInterval : RediscoveryThreshold;

        var sinceLast = DateTime.UtcNow - settings.LastUpstreamDiscoveryAt.Value;
        if (sinceLast < threshold) return;

        _logger.LogInformation("Running scheduled upstream re-discovery (last commit {Days:0.0} days ago)", sinceLast.TotalDays);

        await tracer.StartDiscoveryAsync(ct);
        await tracer.WaitForCompletionAsync();

        // After WaitForCompletionAsync, the state machine has settled (ReviewingResults
        // on success, Failed otherwise). The tracer state holds the new candidate set.
        if (tracer.State.Step != TracerStep.ReviewingResults)
        {
            _logger.LogInformation("Re-discovery finished in state {Step}; no review flag set", tracer.State.Step);
            return;
        }

        // The tracer already ran the shared post-run evaluation when the run settled in
        // ReviewingResults - it does the same for manually-started runs, so the absence counters
        // advance exactly once per completed run regardless of who initiated it. Read the staged
        // outcome here.
        var added = tracer.State.DiscoveryAddedAsns;
        var removedToPause = tracer.State.RemovedTransitAsns;

        if (added.Count == 0 && removedToPause.Count == 0)
        {
            _logger.LogInformation("Re-discovery matched committed ASNs (no actionable change); rolling forward LastUpstreamDiscoveryAt");
            settings.LastUpstreamDiscoveryAt = DateTime.UtcNow;
            settings.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            // Don't auto-commit; just reset the tracer state since there's nothing to review.
            tracer.ResetToIdle();
            return;
        }

        _logger.LogInformation("Re-discovery found upstream changes; flagging for review. Added: [{Added}] Off-path (to pause): [{Removed}]",
            string.Join(", ", added), string.Join(", ", removedToPause.Select(r => $"AS{r.AsnNumber}")));

        settings.UpstreamDiscoveryNeedsReview = true;
        settings.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        // Leave the tracer in ReviewingResults so the user lands on the candidate set
        // when they open the Monitoring page and click the banner.
    }

    /// <summary>Result of comparing a run's discovered ASNs against the committed views.</summary>
    internal sealed record ChangeEvaluation(
        List<string> Added,
        List<string> RemovalCandidates,
        Dictionary<string, int> NewMissCounts);

    /// <summary>
    /// Two committed views, both keyed on the stable ASN identity (see IdentityKey):
    /// <list type="bullet">
    /// <item><b>Monitored</b> (added-suppression): every ASN already monitored or curated -
    /// auto-discovered (DirectRouter/PathProxy/L2Neighbor) on this WAN, plus all UserProvided
    /// (WAN-agnostic, since a hand-added Cogent may carry an empty/other WanInterface). Discovery
    /// finding one of these is not "added".</item>
    /// <item><b>RemovalEligible</b> (removed-eligibility): ASNs auto-discovered
    /// (DirectRouter/PathProxy/L2Neighbor) on this WAN at some point - enabled OR NOT, since a
    /// flaky auto target the user paused must stay eligible so its dangling hand-added siblings
    /// get caught when the ASN goes dark - that still have at least one enabled target row
    /// (auto on this WAN or UserProvided on any WAN). A fully-disabled ASN has nothing to pause,
    /// so counting it would only pin the recheck cadence; a manual-only ASN carries no auto
    /// evidence, so we can't conclude it's off-path.</item>
    /// </list>
    /// Both are reachability-independent (no relation to whether a hop answered ping this run).
    /// </summary>
    internal static async Task<(HashSet<string> Monitored, HashSet<string> RemovalEligible)> BuildCommittedViewsAsync(
        NetworkOptimizerDbContext db, string wanInterface, CancellationToken ct)
    {
        var rows = await db.MonitoringTargets
            .Where(t => t.DiscoveryMethod == DiscoveryMethod.UserProvided
                || ((t.DiscoveryMethod == DiscoveryMethod.DirectRouter
                        || t.DiscoveryMethod == DiscoveryMethod.PathProxy
                        || t.DiscoveryMethod == DiscoveryMethod.L2Neighbor)
                    && t.WanInterface == wanInterface))
            .Select(t => new { t.TargetType, t.AsnNumber, t.Address, t.Enabled, t.DiscoveryMethod, t.WanInterface })
            .ToListAsync(ct);

        var monitored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var autoEvidence = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hasEnabledRow = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in rows)
        {
            var key = IdentityKey(t.TargetType, t.AsnNumber, t.Address);
            monitored.Add(key);
            if (t.WanInterface == wanInterface
                && (t.DiscoveryMethod == DiscoveryMethod.DirectRouter
                    || t.DiscoveryMethod == DiscoveryMethod.PathProxy
                    || t.DiscoveryMethod == DiscoveryMethod.L2Neighbor))
                autoEvidence.Add(key);
            if (t.Enabled)
                hasEnabledRow.Add(key);
        }
        var removalEligible = new HashSet<string>(autoEvidence, StringComparer.OrdinalIgnoreCase);
        removalEligible.IntersectWith(hasEnabledRow);
        return (monitored, removalEligible);
    }

    /// <summary>
    /// Pure change-detection. Added = discovered ASNs not already monitored (flag now). Missing =
    /// removal-eligible ASNs absent this run; each bumps a consecutive-miss counter and only
    /// becomes a removal candidate once it reaches <paramref name="removalThreshold"/> runs. The
    /// returned counter map holds only currently-absent ASNs, so reappeared ones reset by omission.
    /// </summary>
    internal static ChangeEvaluation EvaluateChange(
        HashSet<string> monitoredAsns,
        HashSet<string> removalEligibleAsns,
        HashSet<string> candidate,
        IReadOnlyDictionary<string, int> priorMissCounts,
        int removalThreshold)
    {
        var added = candidate.Except(monitoredAsns).OrderBy(x => x).ToList();

        var newCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var removalCandidates = new List<string>();
        foreach (var key in removalEligibleAsns)
        {
            if (candidate.Contains(key)) continue; // present this run - counter resets (omitted)
            var count = (priorMissCounts.TryGetValue(key, out var prev) ? prev : 0) + 1;
            newCounts[key] = count;
            if (count >= removalThreshold) removalCandidates.Add(key);
        }
        removalCandidates.Sort(StringComparer.OrdinalIgnoreCase);
        return new ChangeEvaluation(added, removalCandidates, newCounts);
    }

    /// <summary>
    /// Shared post-run change evaluation, called by the tracer whenever a discovery run settles
    /// in ReviewingResults - scheduled AND manually-started runs alike, so the "absent for
    /// RemovalConfirmRuns consecutive runs" evidence advances exactly once per completed run
    /// regardless of who initiated it. Bumps the per-WAN consecutive-miss counters, prunes
    /// confirmations with no pause action, persists the counters, and stages the added keys plus
    /// the confirmed off-path transit ASNs on the tracer state for the review UI and the
    /// scheduler's gate. Removed-detection stays persistence-gated: a single incomplete/degraded
    /// run only bumps a counter that resets the moment the ASN reappears.
    /// </summary>
    internal static async Task EvaluateCompletedRunAsync(
        NetworkOptimizerDbContext db, UpstreamTracerState state, CancellationToken ct)
    {
        var wanInterface = state.WanInterface ?? "wan";
        var (monitoredAsns, removalEligibleAsns) = await BuildCommittedViewsAsync(db, wanInterface, ct);
        var candidate = BuildCandidateSignature(state);

        var priorMissCounts = await LoadMissCountsAsync(db, wanInterface, ct);
        var eval = EvaluateChange(monitoredAsns, removalEligibleAsns, candidate, priorMissCounts, RemovalConfirmRuns);

        var removedToPause = await BuildRemovedTransitAsnsAsync(db, wanInterface, eval.RemovalCandidates, ct);

        // Confirmed keys with no pause action (access/path tiers, which this detector doesn't act
        // on) must not keep a counter pinned at the threshold: any non-null counter map holds
        // pendingRecheck on, which would lock the site into daily re-discovery forever. Prune them
        // so the evidence re-accumulates instead.
        var actionable = new HashSet<string>(
            removedToPause.Select(r => "transit:as" + r.AsnNumber), StringComparer.OrdinalIgnoreCase);
        foreach (var key in eval.RemovalCandidates)
            if (!actionable.Contains(key)) eval.NewMissCounts.Remove(key);

        // The map only holds currently-absent ASNs, so reappeared/removed ones prune by omission.
        await SaveMissCountsAsync(db, wanInterface, eval.NewMissCounts, ct);
        await db.SaveChangesAsync(ct);

        state.DiscoveryAddedAsns = eval.Added;
        state.RemovedTransitAsns = removedToPause;
    }

    /// <summary>
    /// Maps confirmed-removed identity keys to review entries: <c>transit:as{n}</c> keys only,
    /// resolved to the enabled Transit targets that would be paused - auto targets scoped to this
    /// WAN, UserProvided targets matched by ASN regardless of WAN (hand-added rows are
    /// WAN-agnostic). An ASN with nothing enabled yields no entry (nothing to do, no nag).
    /// </summary>
    internal static async Task<List<RemovedTransitAsn>> BuildRemovedTransitAsnsAsync(
        NetworkOptimizerDbContext db, string wanInterface, IReadOnlyList<string> confirmedRemoved, CancellationToken ct)
    {
        var asns = new List<int>();
        foreach (var key in confirmedRemoved)
        {
            if (!key.StartsWith("transit:as", StringComparison.OrdinalIgnoreCase)) continue;
            if (int.TryParse(key.AsSpan("transit:as".Length), out var asn)) asns.Add(asn);
        }
        if (asns.Count == 0) return new();

        var targets = await db.MonitoringTargets
            .Where(t => t.TargetType == MonitoringTargetType.Transit && t.Enabled
                && t.AsnNumber != null && asns.Contains(t.AsnNumber.Value)
                && (t.DiscoveryMethod == DiscoveryMethod.UserProvided || t.WanInterface == wanInterface))
            .Select(t => new { t.AsnNumber, t.AsnName, t.DiscoveryMethod })
            .ToListAsync(ct);

        return targets
            .GroupBy(t => t.AsnNumber!.Value)
            .Select(g => new RemovedTransitAsn
            {
                AsnNumber = g.Key,
                AsnName = g.Select(x => x.AsnName).FirstOrDefault(n => !string.IsNullOrEmpty(n)) ?? $"AS{g.Key}",
                TargetCount = g.Count(),
                ManualCount = g.Count(x => x.DiscoveryMethod == DiscoveryMethod.UserProvided),
                Keep = false,
            })
            .OrderBy(r => r.AsnNumber)
            .ToList();
    }

    /// <summary>
    /// Removes the given identity keys from the WAN's absent-ASN miss counters. Called by commit
    /// for the surfaced off-path ASNs: a kept ASN would otherwise still sit at the confirm
    /// threshold and re-flag review on the very next daily recheck - clearing it makes the
    /// evidence re-accumulate from zero instead. Does not SaveChanges; the caller's does.
    /// </summary>
    internal static async Task ClearMissCountKeysAsync(
        NetworkOptimizerDbContext db, string wanInterface, IEnumerable<string> keys, CancellationToken ct)
    {
        var counts = await LoadMissCountsAsync(db, wanInterface, ct);
        var removed = false;
        foreach (var key in keys)
            removed |= counts.Remove(key);
        if (removed)
            await SaveMissCountsAsync(db, wanInterface, counts, ct);
    }

    private static string MissCountsKey(string wanInterface) =>
        SystemSettingKeys.UpstreamAbsentAsnCountsPrefix + wanInterface;

    private static async Task<Dictionary<string, int>> LoadMissCountsAsync(
        NetworkOptimizerDbContext db, string wanInterface, CancellationToken ct)
    {
        var row = await db.SystemSettings.FirstOrDefaultAsync(s => s.Key == MissCountsKey(wanInterface), ct);
        if (string.IsNullOrEmpty(row?.Value)) return new(StringComparer.OrdinalIgnoreCase);
        try
        {
            var map = JsonSerializer.Deserialize<Dictionary<string, int>>(row.Value);
            return map == null ? new(StringComparer.OrdinalIgnoreCase) : new(map, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>Upserts the counter map into the SystemSetting row. Does not SaveChanges - the
    /// caller's SaveChanges persists it alongside the settings update.</summary>
    private static async Task SaveMissCountsAsync(
        NetworkOptimizerDbContext db, string wanInterface, Dictionary<string, int> counts, CancellationToken ct)
    {
        var key = MissCountsKey(wanInterface);
        var row = await db.SystemSettings.FirstOrDefaultAsync(s => s.Key == key, ct);
        var value = counts.Count == 0 ? null : JsonSerializer.Serialize(counts);
        if (row == null)
        {
            if (value == null) return;
            db.SystemSettings.Add(new SystemSetting { Key = key, Value = value, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        }
        else
        {
            row.Value = value;
            row.UpdatedAt = DateTime.UtcNow;
        }
    }

    internal static HashSet<string> BuildCandidateSignature(UpstreamTracerState state)
    {
        // Reachability-independent (no Enabled filter): every ASN discovered on the path this
        // run, so a hop that flapped the ping gate doesn't drop its ASN and read as a change.
        var sig = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hop in state.AccessHops)
            sig.Add(IdentityKey(MonitoringTargetType.AccessIsp, hop.AsnNumber, hop.Address));
        foreach (var transit in state.TransitAsns)
        {
            var type = transit.Method == DiscoveryMethod.PathProxy
                ? MonitoringTargetType.InternetService
                : MonitoringTargetType.Transit;
            sig.Add(IdentityKey(type, transit.AsnNumber, transit.HopAddress ?? transit.PathProxyTarget));
        }
        return sig;
    }

    // Stable change-detection identity: the upstream ASN within its tier namespace, so ECMP
    // hop-IP churn within an ASN doesn't read as a change. Falls back to the hop address only
    // when no ASN could be attributed (e.g. a private first-mile hop).
    internal static string IdentityKey(MonitoringTargetType type, int? asn, string? address)
    {
        var ns = type switch
        {
            MonitoringTargetType.AccessIsp => "access",
            MonitoringTargetType.Transit => "transit",
            MonitoringTargetType.InternetService => "path",
            _ => "other"
        };
        var id = asn.HasValue ? $"as{asn.Value}" : (string.IsNullOrEmpty(address) ? "?" : address);
        return $"{ns}:{id}";
    }
}
