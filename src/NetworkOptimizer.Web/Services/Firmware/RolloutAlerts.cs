namespace NetworkOptimizer.Web.Services.Firmware;

/// <summary>
/// Event types and the shared source/link the rollout publishes alerts under. Every one of these
/// has a seeded rule in <c>DefaultAlertRules</c>; adding a type here means adding one there.
/// </summary>
public static class RolloutAlerts
{
    /// <summary>Alert source for everything the rollout publishes.</summary>
    public const string Source = "rollout";

    /// <summary>Relative link to the Firmware Rollout page.</summary>
    public const string SourceUrl = "/firmware-rollout";

    /// <summary>A scheduled rollout is coming up (heads-up before an unattended run).</summary>
    public const string Upcoming = "rollout.upcoming";

    /// <summary>
    /// The fixed reminder ahead of a start that was booked far enough out for the first heads-up
    /// to have gone stale.
    /// </summary>
    public const string StartingSoon = "rollout.starting_soon";

    /// <summary>A rollout has begun.</summary>
    public const string Started = "rollout.started";

    /// <summary>A scheduled rollout was cancelled because everything in it was updated out of band.</summary>
    public const string NothingLeft = "rollout.nothing_left";

    /// <summary>A wave boundary is waiting for a Site Admin to approve the next wave.</summary>
    public const string WaveAwaitingApproval = "rollout.wave_awaiting_approval";

    /// <summary>A device has been offline past its class budget and is not coming back on its own.</summary>
    public const string DeviceStuckOffline = "rollout.device_stuck_offline";

    /// <summary>An SKU's canary failed, so the rest of that SKU was dropped.</summary>
    public const string SkuAborted = "rollout.sku_aborted";

    /// <summary>A device took its new firmware and then failed the post-upgrade check.</summary>
    public const string PostUpgradeCheckFailed = "rollout.post_upgrade_check_failed";

    /// <summary>
    /// The UniFi Network application update did not bring the application back inside its budget.
    /// A warning rather than a stop: the device upgrades carry on regardless.
    /// </summary>
    public const string NetworkAppUpdateStuck = "rollout.network_app_update_stuck";

    /// <summary>A device's CPU or memory use moved appreciably up after the upgrade.</summary>
    public const string ResourceRegression = "rollout.resource_regression";

    /// <summary>A device's CPU or memory use moved appreciably down after the upgrade.</summary>
    public const string ResourceImprovement = "rollout.resource_improvement";

    /// <summary>Every step has settled.</summary>
    public const string Completed = "rollout.completed";

    /// <summary>The post-soak report is built and readable.</summary>
    public const string ReportReady = "rollout.report_ready";

    /// <summary>The start was deferred because the site was not healthy enough to upgrade.</summary>
    public const string PostponedHealth = "rollout.postponed_health";

    /// <summary>A device was put back on its previous firmware.</summary>
    public const string RollbackExecuted = "rollout.rollback_executed";

    /// <summary>
    /// The rollout has lost sight of the site (a dark console or a dropped agent tunnel). Every
    /// deadline stops counting while this is true, so the run stalls rather than blaming devices.
    /// </summary>
    public const string VisibilityLost = "rollout.visibility_lost";

    /// <summary>Sight of the site is back and the rollout is moving again.</summary>
    public const string VisibilityRestored = "rollout.visibility_restored";
}
