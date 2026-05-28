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

        // Snapshot the current committed signature before running discovery, since the
        // tracer mutates State as it runs.
        var committedSignature = await BuildCommittedSignatureAsync(db, ct);

        await _tracer.StartDiscoveryAsync(ct);
        await _tracer.WaitForCompletionAsync();

        // After WaitForCompletionAsync, the state machine has settled (ReviewingResults
        // on success, Failed otherwise). The tracer state holds the new candidate set.
        if (_tracer.State.Step != TracerStep.ReviewingResults)
        {
            _logger.LogInformation("Re-discovery finished in state {Step}; no review flag set", _tracer.State.Step);
            return;
        }

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

        _logger.LogInformation("Re-discovery found {Added} new and {Removed} removed targets; flagging for review",
            newSignature.Except(committedSignature).Count(),
            committedSignature.Except(newSignature).Count());

        settings.UpstreamDiscoveryNeedsReview = true;
        settings.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        // Leave the tracer in ReviewingResults so the user lands on the candidate set
        // when they open the Monitoring page and click the banner.
    }

    private static async Task<HashSet<string>> BuildCommittedSignatureAsync(NetworkOptimizerDbContext db, CancellationToken ct)
    {
        // Tracer-origin targets only - user-added customs aren't part of the comparison.
        var targets = await db.MonitoringTargets
            .Where(t => t.DiscoveryMethod != null
                && (t.DiscoveryMethod == DiscoveryMethod.DirectRouter
                    || t.DiscoveryMethod == DiscoveryMethod.PathProxy
                    || t.DiscoveryMethod == DiscoveryMethod.L2Neighbor))
            .Select(t => t.TargetId)
            .ToListAsync(ct);
        return new HashSet<string>(targets, StringComparer.OrdinalIgnoreCase);
    }

    private static HashSet<string> BuildCandidateSignature(UpstreamTracerState state)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hop in state.AccessHops.Where(h => h.Enabled))
            set.Add(hop.TargetId);
        foreach (var transit in state.TransitAsns.Where(t => t.Enabled && !string.IsNullOrEmpty(t.TargetId)))
            set.Add(transit.TargetId!);
        return set;
    }
}
