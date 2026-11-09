using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services.Firmware;

namespace NetworkOptimizer.Web.Services.ChangeControl;

/// <summary>
/// Firmware Rollout under Change Control (plan step 11): the auto-pin before a rollout, and the
/// post-upgrade diff and impact as a health-gate input. Resolves the registry per call, since the
/// registry itself reads rollout state.
/// </summary>
public sealed class RolloutConfigGuard : IRolloutConfigGuard
{
    /// <summary>Snapshot detail that ties a pinned snapshot to its rollout plan.</summary>
    public static string PinDetail(int planId) => $"Firmware Rollout #{planId}";

    private readonly IServiceProvider _sp;
    private readonly ILogger<RolloutConfigGuard> _logger;

    public RolloutConfigGuard(IServiceProvider sp, ILogger<RolloutConfigGuard> logger)
    {
        _sp = sp;
        _logger = logger;
    }

    private ChangeControlSiteEngine Engine(string slug) => _sp.GetRequiredService<ChangeControlRegistry>().GetFor(slug);

    public async Task<int?> PinBeforeRolloutAsync(string siteSlug, int planId, CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await Engine(siteSlug).TakeSnapshotAsync(ConfigSnapshotTrigger.BeforeFirmwareRollout, PinDetail(planId),
                "Before Firmware Rollout", cancellationToken);
            return snapshot?.Id;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A rollout never waits on a snapshot; the console's own backup still runs.
            _logger.LogWarning(ex, "Could not pin a config snapshot before Firmware Rollout {Plan} on site {Site}", planId, siteSlug);
            return null;
        }
    }

    public async Task<string?> CheckAfterNetworkAppAsync(string siteSlug, DateTime rolloutStartedUtc, CancellationToken cancellationToken)
    {
        try
        {
            var engine = Engine(siteSlug);
            // A detection pass now records the upgrade and attributes its rewrites to it, then evaluates them.
            await engine.PollNowAsync(cancellationToken);
            var changes = await engine.Repositories.UseAsync((r, c) => r.GetChangesAsync(rolloutStartedUtc, DateTime.UtcNow, 500, c), cancellationToken);
            var config = changes.Where(c => c.Kind == ConfigChangeKind.Config && c.ActorKind != ConfigChangeActorKind.NetworkOptimizer).ToList();
            var regressions = config.Sum(c => c.RegressionCount);
            if (regressions > 0)
                return $"the UniFi Network update left {regressions} security {(regressions == 1 ? "regression" : "regressions")} in the config. Review it in Change Control before resuming.";
            var unattributed = config.Count(c => c.ActorKind == ConfigChangeActorKind.NoAdminRecorded);
            if (unattributed > 0)
                return $"{unattributed} unattributed config {(unattributed == 1 ? "change" : "changes")} appeared during the rollout. Review in Change Control before resuming.";
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Post-upgrade config check failed for site {Site}; the rollout carries on", siteSlug);
            return null;
        }
    }
}
