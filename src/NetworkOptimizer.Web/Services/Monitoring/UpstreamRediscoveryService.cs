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
    private readonly UpstreamTracerService _tracer;
    private readonly ILogger<UpstreamRediscoveryService> _logger;

    public UpstreamRediscoveryService(
        IDbContextFactory<NetworkOptimizerDbContext> dbFactory,
        UpstreamTracerService tracer,
        ILogger<UpstreamRediscoveryService> logger)
    {
        _dbFactory = dbFactory;
        _tracer = tracer;
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
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
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

        await _tracer.StartDiscoveryAsync(ct);
        await _tracer.WaitForCompletionAsync();

        // After WaitForCompletionAsync, the state machine has settled (ReviewingResults
        // on success, Failed otherwise). The tracer state holds the new candidate set.
        if (_tracer.State.Step != TracerStep.ReviewingResults)
        {
            _logger.LogInformation("Re-discovery finished in state {Step}; no review flag set", _tracer.State.Step);
            return;
        }

        // Compare on a stable upstream-ASN identity scoped to the WAN this run discovered.
        // A run never writes MonitoringTargets (commit only happens on user review), so the
        // committed views are read here, where State.WanInterface is known.
        var wanInterface = _tracer.State.WanInterface ?? "wan";
        var (monitoredAsns, autoEnabledAsns) = await BuildCommittedViewsAsync(db, wanInterface, ct);
        var candidate = BuildCandidateSignature(_tracer.State);

        var priorMissCounts = await LoadMissCountsAsync(db, wanInterface, ct);
        var eval = EvaluateChange(monitoredAsns, autoEnabledAsns, candidate, priorMissCounts, RemovalConfirmRuns);

        // Removed-detection is persistence-gated: an ASN must be absent from RemovalConfirmRuns
        // runs in a row before it's confirmed removed, so a single incomplete/degraded run only
        // bumps a counter that resets the moment the ASN reappears.
        var confirmedRemoved = eval.RemovalCandidates;

        // Persist the updated counters (upserted into the same SaveChanges below). The map only
        // holds currently-absent ASNs, so reappeared/removed ones are pruned by omission.
        await SaveMissCountsAsync(db, wanInterface, eval.NewMissCounts, ct);

        if (eval.Added.Count == 0 && confirmedRemoved.Count == 0)
        {
            _logger.LogInformation("Re-discovery matched committed ASNs (no change); rolling forward LastUpstreamDiscoveryAt");
            settings.LastUpstreamDiscoveryAt = DateTime.UtcNow;
            settings.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            // Don't auto-commit; just reset the tracer state since there's nothing to review.
            _tracer.ResetToIdle();
            return;
        }

        _logger.LogInformation("Re-discovery found upstream changes; flagging for review. Added: [{Added}] Removed: [{Removed}]",
            string.Join(", ", eval.Added), string.Join(", ", confirmedRemoved));

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
    /// <item><b>AutoEnabled</b> (removed-eligibility): enabled, auto-discovered ASNs on this WAN.
    /// Only these are eligible to be flagged "removed" - UserProvided and disabled targets are
    /// excluded so a curated or intentionally-off target never nags.</item>
    /// </list>
    /// Both are reachability-independent (no relation to whether a hop answered ping this run).
    /// </summary>
    internal static async Task<(HashSet<string> Monitored, HashSet<string> AutoEnabled)> BuildCommittedViewsAsync(
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
        var autoEnabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in rows)
        {
            var key = IdentityKey(t.TargetType, t.AsnNumber, t.Address);
            monitored.Add(key);
            if (t.Enabled && t.WanInterface == wanInterface
                && (t.DiscoveryMethod == DiscoveryMethod.DirectRouter
                    || t.DiscoveryMethod == DiscoveryMethod.PathProxy
                    || t.DiscoveryMethod == DiscoveryMethod.L2Neighbor))
                autoEnabled.Add(key);
        }
        return (monitored, autoEnabled);
    }

    /// <summary>
    /// Pure change-detection. Added = discovered ASNs not already monitored (flag now). Missing =
    /// removal-eligible ASNs absent this run; each bumps a consecutive-miss counter and only
    /// becomes a removal candidate once it reaches <paramref name="removalThreshold"/> runs. The
    /// returned counter map holds only currently-absent ASNs, so reappeared ones reset by omission.
    /// </summary>
    internal static ChangeEvaluation EvaluateChange(
        HashSet<string> monitoredAsns,
        HashSet<string> autoEnabledAsns,
        HashSet<string> candidate,
        IReadOnlyDictionary<string, int> priorMissCounts,
        int removalThreshold)
    {
        var added = candidate.Except(monitoredAsns).OrderBy(x => x).ToList();

        var newCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var removalCandidates = new List<string>();
        foreach (var key in autoEnabledAsns)
        {
            if (candidate.Contains(key)) continue; // present this run - counter resets (omitted)
            var count = (priorMissCounts.TryGetValue(key, out var prev) ? prev : 0) + 1;
            newCounts[key] = count;
            if (count >= removalThreshold) removalCandidates.Add(key);
        }
        removalCandidates.Sort(StringComparer.OrdinalIgnoreCase);
        return new ChangeEvaluation(added, removalCandidates, newCounts);
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
