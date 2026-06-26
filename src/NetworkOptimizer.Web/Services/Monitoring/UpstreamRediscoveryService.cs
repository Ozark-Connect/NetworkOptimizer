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

        var sinceLast = DateTime.UtcNow - settings.LastUpstreamDiscoveryAt.Value;
        if (sinceLast < RediscoveryThreshold) return;

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
        // committed set is read here, where State.WanInterface is known.
        var wanInterface = _tracer.State.WanInterface ?? "wan";
        var committedSignature = await BuildCommittedSignatureAsync(db, wanInterface, ct);
        var newSignature = BuildCandidateSignature(_tracer.State);
        if (committedSignature.SetEquals(newSignature))
        {
            _logger.LogInformation("Re-discovery matched committed targets; rolling forward LastUpstreamDiscoveryAt");
            settings.LastUpstreamDiscoveryAt = DateTime.UtcNow;
            settings.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            // Don't auto-commit; just reset the tracer state since there's nothing for
            // the user to review.
            _tracer.ResetToIdle();
            return;
        }

        var added = newSignature.Except(committedSignature).OrderBy(x => x).ToList();
        var removed = committedSignature.Except(newSignature).OrderBy(x => x).ToList();
        _logger.LogInformation("Re-discovery found upstream changes; flagging for review. Added: [{Added}] Removed: [{Removed}]",
            string.Join(", ", added), string.Join(", ", removed));

        settings.UpstreamDiscoveryNeedsReview = true;
        settings.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        // Leave the tracer in ReviewingResults so the user lands on the candidate set
        // when they open the Monitoring page and click the banner.
    }

    internal static async Task<HashSet<string>> BuildCommittedSignatureAsync(NetworkOptimizerDbContext db, string wanInterface, CancellationToken ct)
    {
        // Tracer-origin targets for this WAN, enabled only. We compare on a stable ASN-level
        // identity (see IdentityKey), NOT raw per-hop TargetIds: traceroutes load-balance across
        // multiple hop IPs within the same ASN run-to-run, so a TargetId comparison never
        // converges and would re-flag the banner on every cycle. Enabled-only and WAN-scoped so
        // user-disabled targets, other WANs, and stale history the commit path never prunes don't
        // register as phantom changes. User-added customs (DiscoveryMethod == null) are excluded.
        var targets = await db.MonitoringTargets
            .Where(t => t.Enabled
                && t.WanInterface == wanInterface
                && (t.DiscoveryMethod == DiscoveryMethod.DirectRouter
                    || t.DiscoveryMethod == DiscoveryMethod.PathProxy
                    || t.DiscoveryMethod == DiscoveryMethod.L2Neighbor))
            .Select(t => new { t.TargetType, t.AsnNumber, t.Address })
            .ToListAsync(ct);
        var sig = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in targets)
            sig.Add(IdentityKey(t.TargetType, t.AsnNumber, t.Address));
        return sig;
    }

    internal static HashSet<string> BuildCandidateSignature(UpstreamTracerState state)
    {
        var sig = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hop in state.AccessHops.Where(h => h.Enabled))
            sig.Add(IdentityKey(MonitoringTargetType.AccessIsp, hop.AsnNumber, hop.Address));
        foreach (var transit in state.TransitAsns.Where(t => t.Enabled))
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
