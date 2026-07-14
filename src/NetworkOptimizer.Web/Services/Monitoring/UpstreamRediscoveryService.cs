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

    // An auto-discovered ASN must be absent from this many gated increments before it's a removal
    // candidate. Combined with AbsentIncrementGate, that's a real-time floor (3 increments >= 6h
    // apart => absence sustained >= ~12h), long enough to ride out maintenance windows and to keep
    // a transit provider's brief drain from confirming.
    private const int RemovalConfirmRuns = 3;

    // Per-ASN minimum time between miss-counter increments. A completed run that finds an ASN still
    // absent but within this window of its last increment HOLDS the count instead of advancing, so
    // rapid manual traces (or a tight recheck) can't stack the counter - absence has to persist
    // across real time, not just across runs. Kept in step with PendingRecheckInterval so scheduled
    // rechecks reliably clear the gate.
    private static readonly TimeSpan AbsentIncrementGate = TimeSpan.FromHours(6);

    // While a removal counter is pending (some ASN currently absent), re-check on this shorter
    // cadence instead of the full threshold, so a real removal confirms in ~12-18h rather than
    // ~3 weeks and a transient miss clears quickly. Matched to AbsentIncrementGate so each recheck
    // lands just past the gate and actually advances the counter.
    private static readonly TimeSpan PendingRecheckInterval = TimeSpan.FromHours(6);

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
        var ispChange = tracer.State.IspChange;

        if (added.Count == 0 && removedToPause.Count == 0 && ispChange == null)
        {
            _logger.LogInformation("Re-discovery matched committed ASNs (no actionable change); rolling forward LastUpstreamDiscoveryAt");
            settings.LastUpstreamDiscoveryAt = DateTime.UtcNow;
            settings.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            // Don't auto-commit; just reset the tracer state since there's nothing to review.
            tracer.ResetToIdle();
            return;
        }

        _logger.LogInformation("Re-discovery found upstream changes; flagging for review. Added: [{Added}] Off-path (to pause): [{Removed}] ISP change: {IspChange}",
            string.Join(", ", added), string.Join(", ", removedToPause.Select(r => $"AS{r.AsnNumber}")),
            ispChange == null ? "none" : $"AS{ispChange.OldAsnNumber} -> AS{ispChange.NewAsnNumber}");

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
        Dictionary<string, MissRecord> NewMisses);

    /// <summary>
    /// A per-ASN absence counter: the consecutive-miss <paramref name="Count"/> plus
    /// <paramref name="LastIncrementUtc"/>, the time it last advanced, so the increment can be
    /// time-gated (at most once per <see cref="AbsentIncrementGate"/>). Legacy stored counters
    /// (a bare int) load with <see cref="DateTime.MinValue"/>, i.e. immediately gate-eligible.
    /// </summary>
    internal readonly record struct MissRecord(int Count, DateTime LastIncrementUtc);

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
    /// removal-eligible ASNs absent this run; each bumps a consecutive-miss counter, but only when
    /// it's been at least <paramref name="incrementGate"/> since that ASN last incremented (a
    /// too-soon miss holds the count and its timestamp, so absence must persist across real time,
    /// not just across runs). An ASN becomes a removal candidate once its count reaches
    /// <paramref name="removalThreshold"/> - a confirmed ASN that's gated this run stays a
    /// candidate. The returned map holds only currently-absent ASNs, so reappeared ones reset by
    /// omission. <paramref name="nowUtc"/> is passed in so this stays pure and deterministic.
    /// </summary>
    internal static ChangeEvaluation EvaluateChange(
        HashSet<string> monitoredAsns,
        HashSet<string> removalEligibleAsns,
        HashSet<string> candidate,
        IReadOnlyDictionary<string, MissRecord> priorMisses,
        DateTime nowUtc,
        TimeSpan incrementGate,
        int removalThreshold)
    {
        var added = candidate.Except(monitoredAsns).OrderBy(x => x).ToList();

        var newMisses = new Dictionary<string, MissRecord>(StringComparer.OrdinalIgnoreCase);
        var removalCandidates = new List<string>();
        foreach (var key in removalEligibleAsns)
        {
            if (candidate.Contains(key)) continue; // present this run - counter resets (omitted)
            var hadPrior = priorMisses.TryGetValue(key, out var prior);
            int count;
            DateTime lastIncrement;
            if (hadPrior && nowUtc - prior.LastIncrementUtc < incrementGate)
            {
                // Gated: absent again, but too soon since the last increment - hold as-is.
                count = prior.Count;
                lastIncrement = prior.LastIncrementUtc;
            }
            else
            {
                count = (hadPrior ? prior.Count : 0) + 1;
                lastIncrement = nowUtc;
            }
            newMisses[key] = new MissRecord(count, lastIncrement);
            if (count >= removalThreshold) removalCandidates.Add(key);
        }
        removalCandidates.Sort(StringComparer.OrdinalIgnoreCase);
        return new ChangeEvaluation(added, removalCandidates, newMisses);
    }

    /// <summary>
    /// Shared post-run change evaluation, called by the tracer whenever a discovery run settles
    /// in ReviewingResults - scheduled AND manually-started runs alike, so removal evidence
    /// accumulates the same regardless of who initiated the run. Bumps the per-WAN miss counters
    /// (time-gated: at most one increment per ASN per AbsentIncrementGate, so rapid manual traces
    /// can't stack them), prunes confirmations with no pause action, persists the counters, and
    /// stages the added keys plus the confirmed off-path transit ASNs on the tracer state for the
    /// review UI and the scheduler's gate. Removed-detection stays persistence-gated: a single
    /// incomplete/degraded run only bumps a counter that resets the moment the ASN reappears.
    /// </summary>
    internal static async Task EvaluateCompletedRunAsync(
        NetworkOptimizerDbContext db, UpstreamTracerState state, CancellationToken ct)
    {
        var wanInterface = state.WanInterface ?? "wan";
        var (monitoredAsns, removalEligibleAsns) = await BuildCommittedViewsAsync(db, wanInterface, ct);
        var candidate = BuildCandidateSignature(state);

        // A changed access ISP supersedes per-transit off-path handling: the whole upstream path
        // is replaced at once, so staging N individual transit removals alongside would be noise.
        // Stage only the ISP-change question (plus the added keys for the scheduler's gate) and
        // leave the miss counters frozen - a confirm wipes them wholesale, and a decline lets the
        // next unchanged run resume advancing them normally. No multi-run miss-gate here: the
        // access ASN is the stable first hop and the event is user-confirmed, so one run with a
        // valid different ASN is enough to prompt.
        state.IspChange = await DetectAccessIspChangeAsync(db, wanInterface, state, ct);
        if (state.IspChange != null)
        {
            state.DiscoveryAddedAsns = candidate.Except(monitoredAsns).OrderBy(x => x).ToList();
            state.RemovedTransitAsns = new();
            state.PendingRemovalTransitAsns = new();
            return;
        }

        var priorMisses = await LoadMissCountsAsync(db, wanInterface, ct);
        var eval = EvaluateChange(monitoredAsns, removalEligibleAsns, candidate, priorMisses,
            DateTime.UtcNow, AbsentIncrementGate, RemovalConfirmRuns);

        var removedToPause = await BuildRemovedTransitAsnsAsync(db, wanInterface, eval.RemovalCandidates, ct);

        // Confirmed keys with no pause action (access/path tiers, which this detector doesn't act
        // on) must not keep a counter pinned at the threshold: any non-null counter map holds
        // pendingRecheck on, which would lock the site into the recheck cadence forever. Prune them
        // so the evidence re-accumulates instead.
        var actionable = new HashSet<string>(
            removedToPause.Select(r => "transit:as" + r.AsnNumber), StringComparer.OrdinalIgnoreCase);
        foreach (var key in eval.RemovalCandidates)
            if (!actionable.Contains(key)) eval.NewMisses.Remove(key);

        // The map only holds currently-absent ASNs, so reappeared/removed ones prune by omission.
        await SaveMissCountsAsync(db, wanInterface, eval.NewMisses, ct);
        await db.SaveChangesAsync(ct);

        // Transit ASNs absent this run but still below the confirm threshold: surface them read-only
        // in the review as "tracking toward removal" so the detection isn't invisible until it
        // suddenly confirms. Pending and confirmed are mutually exclusive (one count per key), and
        // an eligible ASN always has >=1 enabled target, so BuildRemovedTransitAsnsAsync resolves a
        // target count for each. RunsRemaining is remaining gated increments; each is >= the gate
        // apart, so it's also a rough remaining-time floor.
        var pendingKeys = eval.NewMisses
            .Where(kv => kv.Value.Count < RemovalConfirmRuns
                && kv.Key.StartsWith("transit:as", StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Key)
            .ToList();
        var pendingInfo = await BuildRemovedTransitAsnsAsync(db, wanInterface, pendingKeys, ct);
        var pending = pendingInfo
            .Select(r => new PendingRemovalTransitAsn
            {
                AsnNumber = r.AsnNumber,
                AsnName = r.AsnName,
                TargetCount = r.TargetCount,
                ManualCount = r.ManualCount,
                MissCount = eval.NewMisses["transit:as" + r.AsnNumber].Count,
                RunsRemaining = RemovalConfirmRuns - eval.NewMisses["transit:as" + r.AsnNumber].Count,
            })
            .OrderBy(p => p.AsnNumber)
            .ToList();

        state.DiscoveryAddedAsns = eval.Added;
        state.RemovedTransitAsns = removedToPause;
        state.PendingRemovalTransitAsns = pending;
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
    /// Detects a candidate access-ISP change for the run: every access ASN this run resolved is
    /// different from every access ASN committed for the WAN. Guards: a run that failed to
    /// attribute any access ASN never triggers (null/unresolved is not a change); no committed
    /// auto-discovered access ASN means there's no baseline to differ from (first run); any
    /// overlap between the two sets means the provider is unchanged; and a new primary ASN the
    /// user already declined is suppressed. Returns the staged candidate with the reset scope
    /// pre-counted for the review, or null.
    /// </summary>
    internal static async Task<IspChangeCandidate?> DetectAccessIspChangeAsync(
        NetworkOptimizerDbContext db, string wanInterface, UpstreamTracerState state, CancellationToken ct)
    {
        var newAsns = state.AccessHops
            .Where(h => h.AsnNumber.HasValue)
            .Select(h => h.AsnNumber!.Value)
            .ToList();
        if (newAsns.Count == 0) return null;

        // The stored access ISP: auto-discovered AccessIsp rows on this WAN, enabled or not - a
        // paused access target still records who the provider was. UserProvided rows are excluded
        // since a hand-added access target carries no discovery evidence of the provider.
        var oldRows = await db.MonitoringTargets
            .Where(t => t.TargetType == MonitoringTargetType.AccessIsp
                && t.WanInterface == wanInterface
                && t.AsnNumber != null
                && t.DiscoveryMethod != DiscoveryMethod.UserProvided)
            .Select(t => new { t.AsnNumber, t.AsnName })
            .ToListAsync(ct);
        if (oldRows.Count == 0) return null;

        var oldSet = oldRows.Select(r => r.AsnNumber!.Value).ToHashSet();
        if (newAsns.Any(oldSet.Contains)) return null;

        var newPrimary = newAsns
            .GroupBy(a => a)
            .OrderByDescending(g => g.Count()).ThenBy(g => newAsns.IndexOf(g.Key))
            .First().Key;

        var declinedRow = await db.SystemSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == DeclinedAccessAsnKey(wanInterface), ct);
        if (int.TryParse(declinedRow?.Value, out var declinedAsn) && declinedAsn == newPrimary)
            return null;

        var oldPrimary = oldRows
            .GroupBy(r => r.AsnNumber!.Value)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key)
            .First().Key;
        var oldName = oldRows.Where(r => r.AsnNumber == oldPrimary)
            .Select(r => r.AsnName).FirstOrDefault(n => !string.IsNullOrEmpty(n)) ?? $"AS{oldPrimary}";
        var newName = state.AccessHops
            .Where(h => h.AsnNumber == newPrimary)
            .Select(h => h.AsnName).FirstOrDefault(n => !string.IsNullOrEmpty(n)) ?? $"AS{newPrimary}";

        var scope = await LoadIspResetScopeAsync(db, wanInterface, ct);
        return new IspChangeCandidate
        {
            OldAsnNumber = oldPrimary,
            OldAsnName = oldName,
            NewAsnNumber = newPrimary,
            NewAsnName = newName,
            TargetCount = scope.Count,
            ManualCount = scope.Count(t => t.DiscoveryMethod == DiscoveryMethod.UserProvided),
        };
    }

    /// <summary>
    /// The targets a confirmed ISP-change reset pauses: every enabled upstream monitoring target
    /// for the connection across all three tiers (access, transit, path-proxy). Auto-discovered
    /// rows are scoped to this WAN; UserProvided rows count when assigned to this WAN or to no
    /// WAN (hand-added rows are often WAN-agnostic), so a reset on one WAN never touches manual
    /// targets pinned to another.
    /// </summary>
    internal static Task<List<MonitoringTarget>> LoadIspResetScopeAsync(
        NetworkOptimizerDbContext db, string wanInterface, CancellationToken ct) =>
        db.MonitoringTargets
            .Where(t => t.Enabled
                && (t.TargetType == MonitoringTargetType.AccessIsp
                    || t.TargetType == MonitoringTargetType.Transit
                    || t.TargetType == MonitoringTargetType.InternetService)
                && (t.DiscoveryMethod == DiscoveryMethod.UserProvided
                    ? (t.WanInterface == wanInterface || t.WanInterface == null || t.WanInterface == "")
                    : t.WanInterface == wanInterface))
            .ToListAsync(ct);

    /// <summary>
    /// Applies a confirmed ISP change for the WAN: pauses (Enabled = false, never deletes) every
    /// target in the reset scope, wipes the WAN's absent-ASN miss counters (they described the
    /// old provider's path), and clears any recorded decline. The caller then commits the fresh
    /// candidates as the new baseline. Does not SaveChanges; the caller's does. Returns how many
    /// targets were paused.
    /// </summary>
    internal static async Task<int> ApplyIspChangeResetAsync(
        NetworkOptimizerDbContext db, string wanInterface, CancellationToken ct)
    {
        var stale = await LoadIspResetScopeAsync(db, wanInterface, ct);
        foreach (var t in stale) t.Enabled = false;

        await SaveMissCountsAsync(db, wanInterface, new(StringComparer.OrdinalIgnoreCase), ct);

        var declined = await db.SystemSettings
            .FirstOrDefaultAsync(s => s.Key == DeclinedAccessAsnKey(wanInterface), ct);
        if (declined != null)
        {
            declined.Value = null;
            declined.UpdatedAt = DateTime.UtcNow;
        }
        return stale.Count;
    }

    /// <summary>
    /// Records a declined ISP change: the user said the new access ASN is not a provider switch,
    /// so detection suppresses that ASN for the WAN instead of re-prompting every run. Does not
    /// SaveChanges; the caller's does.
    /// </summary>
    internal static async Task RecordDeclinedIspChangeAsync(
        NetworkOptimizerDbContext db, string wanInterface, int declinedNewAsn, CancellationToken ct)
    {
        var key = DeclinedAccessAsnKey(wanInterface);
        var row = await db.SystemSettings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (row == null)
        {
            db.SystemSettings.Add(new SystemSetting
            {
                Key = key,
                Value = declinedNewAsn.ToString(),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
        }
        else
        {
            row.Value = declinedNewAsn.ToString();
            row.UpdatedAt = DateTime.UtcNow;
        }
    }

    private static string DeclinedAccessAsnKey(string wanInterface) =>
        SystemSettingKeys.UpstreamDeclinedAccessAsnPrefix + wanInterface;

    /// <summary>
    /// Removes the given identity keys from the WAN's absent-ASN miss counters. Called by commit
    /// for the surfaced off-path ASNs: a kept ASN would otherwise still sit at the confirm
    /// threshold and re-flag review on the very next daily recheck - clearing it makes the
    /// evidence re-accumulate from zero instead. Does not SaveChanges; the caller's does.
    /// </summary>
    internal static async Task ClearMissCountKeysAsync(
        NetworkOptimizerDbContext db, string wanInterface, IEnumerable<string> keys, CancellationToken ct)
    {
        var misses = await LoadMissCountsAsync(db, wanInterface, ct);
        var removed = false;
        foreach (var key in keys)
            removed |= misses.Remove(key);
        if (removed)
            await SaveMissCountsAsync(db, wanInterface, misses, ct);
    }

    private static string MissCountsKey(string wanInterface) =>
        SystemSettingKeys.UpstreamAbsentAsnCountsPrefix + wanInterface;

    /// <summary>
    /// Loads the per-WAN absent-ASN counters. The stored value is a JSON map of identity key to
    /// <c>{"c":count,"t":lastIncrementUtc}</c>. Legacy values (a bare int per key, from before the
    /// increment gate) parse with <see cref="DateTime.MinValue"/> so they're immediately
    /// gate-eligible on the next run - no migration needed.
    /// </summary>
    private static async Task<Dictionary<string, MissRecord>> LoadMissCountsAsync(
        NetworkOptimizerDbContext db, string wanInterface, CancellationToken ct)
    {
        var result = new Dictionary<string, MissRecord>(StringComparer.OrdinalIgnoreCase);
        var row = await db.SystemSettings.FirstOrDefaultAsync(s => s.Key == MissCountsKey(wanInterface), ct);
        if (string.IsNullOrEmpty(row?.Value)) return result;
        try
        {
            using var doc = JsonDocument.Parse(row.Value);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.Number)
                {
                    // Legacy {key:int}: no timestamp recorded, so treat as long-ago (not gated).
                    var legacy = prop.Value.GetInt32();
                    if (legacy > 0) result[prop.Name] = new MissRecord(legacy, DateTime.MinValue);
                }
                else if (prop.Value.ValueKind == JsonValueKind.Object)
                {
                    var count = prop.Value.TryGetProperty("c", out var c) ? c.GetInt32() : 0;
                    var when = prop.Value.TryGetProperty("t", out var t) && t.TryGetDateTime(out var dt)
                        ? DateTime.SpecifyKind(dt, DateTimeKind.Utc)
                        : DateTime.MinValue;
                    if (count > 0) result[prop.Name] = new MissRecord(count, when);
                }
            }
        }
        catch
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
        return result;
    }

    /// <summary>Upserts the counter map into the SystemSetting row. Does not SaveChanges - the
    /// caller's SaveChanges persists it alongside the settings update.</summary>
    private static async Task SaveMissCountsAsync(
        NetworkOptimizerDbContext db, string wanInterface, Dictionary<string, MissRecord> misses, CancellationToken ct)
    {
        var key = MissCountsKey(wanInterface);
        var row = await db.SystemSettings.FirstOrDefaultAsync(s => s.Key == key, ct);
        var value = misses.Count == 0
            ? null
            : JsonSerializer.Serialize(misses.ToDictionary(
                kv => kv.Key,
                kv => new { c = kv.Value.Count, t = kv.Value.LastIncrementUtc }));
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
