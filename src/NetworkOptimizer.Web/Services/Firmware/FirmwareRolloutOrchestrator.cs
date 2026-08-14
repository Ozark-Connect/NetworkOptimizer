using System.Collections.Concurrent;
using System.Text.Json;
using NetworkOptimizer.Alerts.Events;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Core.Models;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.Web.Services.Firmware;

/// <summary>
/// One site's firmware rollout executor: the per-device state machine, the canary holds, the
/// channel group switches, and every alert a rollout publishes.
///
/// Two rules from live testing shape the whole machine and must not be softened:
/// an accepted command (rc:ok) is acceptance and not success, and even a full offline/online cycle
/// is not success - a revert once cycled an AP and brought it back on the version it started on.
/// A step only passes when the version the console reports after the device is back EQUALS the
/// step's target. The second rule is the mirror: a device that never goes into Upgrading or
/// Offline inside the grace window never took the command, so the SSH path is tried before the
/// step is failed.
///
/// Every transition is persisted immediately against a DETACHED read, because the repository's
/// plan and step reads are AsNoTracking - mutating the instance a create returned writes fields
/// the update path deliberately leaves alone.
/// </summary>
public class FirmwareRolloutOrchestrator : BackgroundService
{
    /// <summary>How often the executor looks at the world. Modest: upgrades take minutes.</summary>
    public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(20);

    /// <summary>
    /// How long after a command a device has to enter Upgrading or go offline before the command is
    /// treated as not taken. The same window applies again after the SSH escalation.
    /// </summary>
    public static readonly TimeSpan CommandGraceWindow = TimeSpan.FromMinutes(5);

    /// <summary>Settling time after a device is back, so boot spikes are not read as regressions.</summary>
    public static readonly TimeSpan CoolDown = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How much of the cool-down the short litmus ignores. The first minutes after a boot are all
    /// spike, so the canary is judged on the quiet tail of the cool-down.
    /// </summary>
    public static readonly TimeSpan ShortLitmusSettle = TimeSpan.FromMinutes(5);

    /// <summary>Length of the before and after windows the resource comparison averages over.</summary>
    public static readonly TimeSpan ResourceWindow = TimeSpan.FromHours(1);

    /// <summary>How long to wait for the console to stage a device's target build before commanding anyway.</summary>
    public static readonly TimeSpan CatalogReflectWait = TimeSpan.FromMinutes(5);

    /// <summary>How far a health-gated start is pushed out. One window, per the approved behavior.</summary>
    public static readonly TimeSpan HealthPostponeWindow = TimeSpan.FromHours(24);

    /// <summary>
    /// How long the UniFi Network application gets to restart into its new build. Past this the
    /// rollout stops waiting and upgrades devices anyway.
    /// </summary>
    public static readonly TimeSpan NetworkAppUpdateBudget = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How long the console gets to come back from a UniFi OS update. The cloud gateway class
    /// budget, because that is exactly what this is: the gateway's own full cycle.
    /// </summary>
    public static readonly TimeSpan UniFiOsUpdateBudget =
        TimeSpan.FromSeconds(FirmwareTimingEstimator.CloudGatewayOfflineBudgetSeconds);

    /// <summary>
    /// No OS-update judgment before this much has passed since the trigger: the state fields can
    /// lag the accept, and download runs with the console still answering on the old version.
    /// </summary>
    public static readonly TimeSpan UniFiOsJudgeDelay = TimeSpan.FromMinutes(2);

    /// <summary>How long a firmware catalog read is reused before the console is asked again.</summary>
    private static readonly TimeSpan CatalogCacheTtl = TimeSpan.FromMinutes(5);

    private readonly IFirmwareRolloutRepositoryAccessor _repositories;
    private readonly IFirmwareCommandClient _commands;
    private readonly IRolloutDeviceObserver _observer;
    private readonly IRolloutLitmusService _litmus;
    private readonly IRolloutHealthGate _health;
    private readonly IMeshRepairQueue _meshRepairs;
    private readonly RolloutChannelManager _channels;
    private readonly RolloutSuppressionRegistry _suppression;
    private readonly IAlertEventBus _eventBus;
    private readonly TimeProvider _time;
    private readonly ILogger<FirmwareRolloutOrchestrator> _logger;
    private readonly string _siteSlug;
    private readonly string _siteSuffix;

    // In-memory only. Losing any of it across a restart costs at most a repeated escalation or a
    // re-queued mesh re-pair; none of it changes whether a step succeeds.
    private readonly ConcurrentDictionary<int, DateTime> _escalatedAt = new();
    private readonly ConcurrentDictionary<int, DateTime> _commandWaitSince = new();
    private readonly HashSet<string> _meshRepairsQueued = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _skuAbortsPublished = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _tickLock = new(1, 1);
    private IReadOnlyList<UniFiFirmwareCatalogEntry> _catalog = [];
    private DateTime _catalogReadAt = DateTime.MinValue;
    private DateTime? _lastWaveSettledAt;
    private int _reconciledPlanId;
    private bool _restoreSweepDone;

    /// <param name="repositories">Site-pinned rollout repository access.</param>
    /// <param name="commands">Firmware command surface.</param>
    /// <param name="observer">Live device state.</param>
    /// <param name="litmus">Post-upgrade checks.</param>
    /// <param name="health">Start-time health gate.</param>
    /// <param name="meshRepairs">Background mesh re-pair queue.</param>
    /// <param name="channels">Console channel set and restore.</param>
    /// <param name="suppression">Standard-alert suppression windows.</param>
    /// <param name="eventBus">Site-stamped alert bus.</param>
    /// <param name="time">Clock.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="siteSlug">Site this instance executes for.</param>
    public FirmwareRolloutOrchestrator(
        IFirmwareRolloutRepositoryAccessor repositories,
        IFirmwareCommandClient commands,
        IRolloutDeviceObserver observer,
        IRolloutLitmusService litmus,
        IRolloutHealthGate health,
        IMeshRepairQueue meshRepairs,
        RolloutChannelManager channels,
        RolloutSuppressionRegistry suppression,
        IAlertEventBus eventBus,
        TimeProvider time,
        ILogger<FirmwareRolloutOrchestrator> logger,
        string siteSlug = SiteManagementService.DefaultSiteSlug)
    {
        _repositories = repositories;
        _commands = commands;
        _observer = observer;
        _litmus = litmus;
        _health = health;
        _meshRepairs = meshRepairs;
        _channels = channels;
        _suppression = suppression;
        _eventBus = eventBus;
        _time = time;
        _logger = logger;
        _siteSlug = string.IsNullOrEmpty(siteSlug) ? SiteManagementService.DefaultSiteSlug : siteSlug;
        _siteSuffix = _siteSlug == SiteManagementService.DefaultSiteSlug ? "" : $" (site {_siteSlug})";
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    /// <summary>Step states the executor is actively watching.</summary>
    private static bool IsInFlight(FirmwareRolloutStep step) => step.State
        is FirmwareRolloutStepState.Commanded
        or FirmwareRolloutStepState.Down
        or FirmwareRolloutStepState.BackOnline
        or FirmwareRolloutStepState.CoolDown;

    /// <summary>Step states nothing more will happen to as part of the rollout.</summary>
    private static bool IsSettled(FirmwareRolloutStep step) => step.State
        is FirmwareRolloutStepState.LitmusPassed
        or FirmwareRolloutStepState.RegressionFlagged
        or FirmwareRolloutStepState.Failed
        or FirmwareRolloutStepState.SkippedExcluded
        or FirmwareRolloutStepState.AbortedSku;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A rollout that stops watching is worse than one that logs and retries: devices
                // are mid-cycle and nothing else is going to move them along.
                _logger.LogError(ex, "Firmware rollout pass failed for site {Site}", _siteSlug);
            }

            try { await Task.Delay(TickInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// One pass of the state machine: start anything due, move every in-flight step, release or
    /// abort canary peers, run comparisons that have come of age, and open the next wave.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task TickAsync(CancellationToken cancellationToken = default)
    {
        await _tickLock.WaitAsync(cancellationToken);
        try
        {
            await SweepPendingChannelRestoreAsync(cancellationToken);

            var plan = await _repositories.UseAsync((r, c) => r.GetActivePlanAsync(c), cancellationToken);
            if (plan == null)
            {
                _suppression.ClearSite(_siteSlug);
                return;
            }

            if (plan.Status is FirmwareRolloutStatus.Scheduled or FirmwareRolloutStatus.Announced)
            {
                if (plan.ScheduledStartAt is DateTime due && due <= Now)
                    await BeginAsync(plan, overrideHealthGate: false, cancellationToken);
                return;
            }

            // The before/after resource windows only close an hour past a device's cool-down, which
            // is long after the last wave settles - so the soak keeps being ticked for them.
            if (plan.Status == FirmwareRolloutStatus.SoakWait)
            {
                var soaking = await _repositories.UseAsync((r, c) => r.GetStepsAsync(plan.Id, c), cancellationToken);
                await RunDueResourceComparisonsAsync(soaking, cancellationToken);
                return;
            }

            if (plan.Status is not (FirmwareRolloutStatus.Running or FirmwareRolloutStatus.Paused))
                return;

            await ReconcileOnResumeAsync(plan, cancellationToken);
            await AdvanceAsync(plan, cancellationToken);
        }
        finally
        {
            _tickLock.Release();
        }
    }

    /// <summary>
    /// Starts a plan straight away. The health gate is advisory here: a Site Admin who has read the
    /// warning can start anyway, which is what <paramref name="overrideHealthGate"/> carries.
    /// </summary>
    /// <param name="planId">Plan to start.</param>
    /// <param name="overrideHealthGate">True to start despite open critical alerts.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when the plan is now running.</returns>
    public async Task<bool> StartNowAsync(int planId, bool overrideHealthGate, CancellationToken cancellationToken = default)
    {
        var plan = await _repositories.UseAsync((r, c) => r.GetPlanAsync(planId, c), cancellationToken);
        if (plan == null)
        {
            _logger.LogWarning("Cannot start firmware rollout {Id} on site {Site}: it no longer exists", planId, _siteSlug);
            return false;
        }

        if (plan.Status is FirmwareRolloutStatus.Running)
            return true;

        return await BeginAsync(plan, overrideHealthGate, cancellationToken);
    }

    /// <summary>
    /// Starts any scheduled plan whose time has come. Driven by the registry's reconcile tick so a
    /// site with nothing running still starts its overnight rollout.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task StartDueScheduledPlansAsync(CancellationToken cancellationToken = default)
    {
        var plan = await _repositories.UseAsync((r, c) => r.GetActivePlanAsync(c), cancellationToken);
        if (plan is null or { Status: not (FirmwareRolloutStatus.Scheduled or FirmwareRolloutStatus.Announced) })
            return;
        if (plan.ScheduledStartAt is not DateTime due || due > Now)
            return;

        await BeginAsync(plan, overrideHealthGate: false, cancellationToken);
    }

    /// <summary>
    /// Where autopilot plan creation hooks in (Phase 7). Deliberately a no-op: the executor runs
    /// whatever plan exists, and nothing here decides that one should.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task CreateAutopilotPlanIfDueAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>Holds a running rollout. In-flight devices are still watched to the end of their cycle.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task PauseAsync(CancellationToken cancellationToken = default)
    {
        var plan = await _repositories.UseAsync((r, c) => r.GetActivePlanAsync(c), cancellationToken);
        if (plan is not { Status: FirmwareRolloutStatus.Running }) return;

        plan.Status = FirmwareRolloutStatus.Paused;
        await PersistPlanAsync(plan, cancellationToken);
        _logger.LogInformation("Firmware rollout {Id} on site {Site} paused", plan.Id, _siteSlug);
    }

    /// <summary>
    /// Releases a hold. When the plan was paused at a wave boundary for approval, this is the
    /// approval: the waiting wave is recorded as approved so the boundary is not hit again.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        var plan = await _repositories.UseAsync((r, c) => r.GetActivePlanAsync(c), cancellationToken);
        if (plan is not { Status: FirmwareRolloutStatus.Paused }) return;

        var document = ParseDocument(plan);
        if (document.WaitingApprovalWave is int wave)
        {
            document.ApprovedThroughWave = Math.Max(document.ApprovedThroughWave, wave);
            document.WaitingApprovalWave = null;
            plan.PlanJson = JsonSerializer.Serialize(document);
        }

        plan.Status = FirmwareRolloutStatus.Running;
        await PersistPlanAsync(plan, cancellationToken);
        _logger.LogInformation("Firmware rollout {Id} on site {Site} resumed", plan.Id, _siteSlug);
    }

    /// <summary>
    /// Pushes a rollout that has not started yet out by one window. No alert: an admin who
    /// postponed a rollout does not need to be told they did, and the action is audited where it
    /// was taken - unlike the health-gated postpone, which nobody asked for.
    /// </summary>
    /// <param name="planId">Plan to push out.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when the plan was pushed out.</returns>
    public async Task<bool> PostponeAsync(int planId, CancellationToken cancellationToken = default)
    {
        var plan = await _repositories.UseAsync((r, c) => r.GetPlanAsync(planId, c), cancellationToken);
        if (plan is not { Status: FirmwareRolloutStatus.Scheduled or FirmwareRolloutStatus.Announced })
            return false;

        plan.ScheduledStartAt = (plan.ScheduledStartAt ?? Now) + HealthPostponeWindow;
        plan.Status = FirmwareRolloutStatus.Scheduled;
        await PersistPlanAsync(plan, cancellationToken);

        _logger.LogInformation(
            "Firmware rollout {Id} on site {Site} postponed to {When}", plan.Id, _siteSlug, plan.ScheduledStartAt);
        return true;
    }

    /// <summary>
    /// Stops a rollout for good, puts the console channels back, and drops every device that had
    /// not started. Devices already mid-cycle are left to finish - nothing can call them back.
    /// </summary>
    /// <param name="reason">Why it was stopped, recorded on the dropped steps.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task AbortAsync(string reason, CancellationToken cancellationToken = default)
    {
        var plan = await _repositories.UseAsync((r, c) => r.GetActivePlanAsync(c), cancellationToken);
        if (plan == null) return;

        var steps = await _repositories.UseAsync((r, c) => r.GetStepsAsync(plan.Id, c), cancellationToken);
        foreach (var step in steps.Where(s => s.State is FirmwareRolloutStepState.Pending or FirmwareRolloutStepState.Held))
        {
            // AbortedSku is the only "queued, then dropped" state on the step machine, so a manual
            // abort lands there too rather than leaving rows looking like they are still coming.
            step.State = FirmwareRolloutStepState.AbortedSku;
            step.Error = $"Rollout aborted: {reason}";
            await PersistStepAsync(step, cancellationToken);
        }

        await RestoreChannelsAsync(plan, cancellationToken);

        plan.Status = FirmwareRolloutStatus.Aborted;
        plan.CompletedAt = Now;
        await PersistPlanAsync(plan, cancellationToken);
        _suppression.ClearSite(_siteSlug);
        _logger.LogWarning("Firmware rollout {Id} on site {Site} aborted: {Reason}", plan.Id, _siteSlug, reason);
    }

    /// <summary>
    /// Puts one device back on the firmware it was running before the rollout, over SSH first.
    /// <para>
    /// SSH leads because the console's own arbitrary-version command was observed to burn a reboot
    /// cycle without changing the version. The step goes back through the normal machine afterwards
    /// with the prior version as its target, so the same version comparison decides whether the
    /// rollback worked.
    /// </para>
    /// </summary>
    /// <param name="stepId">Step to roll back.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when a rollback command was accepted.</returns>
    public async Task<bool> RollbackStepAsync(int stepId, CancellationToken cancellationToken = default)
    {
        var plan = await _repositories.UseAsync((r, c) => r.GetActivePlanAsync(c), cancellationToken);
        if (plan == null) return false;

        var steps = await _repositories.UseAsync((r, c) => r.GetStepsAsync(plan.Id, c), cancellationToken);
        var step = steps.FirstOrDefault(s => s.Id == stepId);
        if (step == null) return false;

        if (step.State is not (FirmwareRolloutStepState.LitmusPassed or FirmwareRolloutStepState.RegressionFlagged))
        {
            _logger.LogWarning(
                "Not rolling back {Device} on site {Site}: the step is {State}, not an upgraded one",
                step.DeviceName, _siteSlug, step.State);
            return false;
        }

        var document = ParseDocument(plan);
        var prior = document.PriorVersions
            .FirstOrDefault(p => string.Equals(p.Mac, step.DeviceMac, StringComparison.OrdinalIgnoreCase));
        if (prior?.Url == null)
        {
            _logger.LogWarning(
                "Not rolling back {Device} on site {Site}: no image URL was cached for {Version} ({Reason})",
                step.DeviceName, _siteSlug, prior?.Version ?? step.FromVersion ?? "its previous version",
                prior?.UnavailableReason ?? "the release feed carries no such build");
            return false;
        }

        var observations = await _observer.ObserveAsync(cancellationToken);
        var observation = observations.FirstOrDefault(o => o.Mac == step.DeviceMac);

        var result = await _commands.TriggerSshUpgradeAsync(observation?.IpAddress ?? string.Empty, prior.Url, cancellationToken);
        if (!result.IsOk)
            result = await _commands.TriggerExternalUpgradeAsync(step.DeviceMac, prior.Url, cancellationToken);

        if (!result.IsOk)
        {
            _logger.LogError("Rollback of {Device} on site {Site} was not accepted: {Message}",
                step.DeviceName, _siteSlug, result.Message);
            return false;
        }

        var upgradedTo = step.ToVersion;
        step.FromVersion = upgradedTo;
        step.ToVersion = prior.Version;
        step.State = FirmwareRolloutStepState.Commanded;
        step.CommandedAt = Now;
        step.WentDownAt = null;
        step.BackAt = null;
        step.DowntimeSeconds = null;
        step.PostStatsJson = null;
        step.Error = null;
        _escalatedAt.TryRemove(step.Id, out _);
        await PersistStepAsync(step, cancellationToken);

        await PublishAsync(
            RolloutAlerts.RollbackExecuted,
            AlertSeverity.Info,
            $"Firmware Rolled Back: {step.DeviceName}{_siteSuffix}",
            $"{step.DeviceName} ({step.Model}) is going back from {upgradedTo ?? "its new firmware"} to {prior.Version ?? "its previous firmware"}.",
            step.DeviceMac,
            step.DeviceName,
            cancellationToken);

        return true;
    }

    // --- Start ---------------------------------------------------------------------------------

    private async Task<bool> BeginAsync(FirmwareRolloutPlan plan, bool overrideHealthGate, CancellationToken cancellationToken)
    {
        if (!overrideHealthGate)
        {
            var verdict = await _health.EvaluateAsync(cancellationToken);
            if (!verdict.Healthy)
            {
                await PostponeAsync(plan, verdict.Reason ?? "the site is not healthy", cancellationToken);
                return false;
            }
        }

        var settings = await _repositories.UseAsync((r, c) => r.GetSettingsAsync(c), cancellationToken);
        if (!settings.WaiveBackup && !await RunPreFlightBackupAsync(plan, cancellationToken))
            return false;

        var document = ParseDocument(plan);
        if (document.IncludesUniFiOsUpdate && await IsStandaloneConsoleAsync(cancellationToken))
        {
            // Standalone UniFi OS Server consoles are often custom deploys, so their OS is never
            // ours to update. The UniFi Network application stays in scope on them.
            document.IncludesUniFiOsUpdate = false;
            document.Notes.Add("UniFi OS is not updated on a self-hosted console; this rollout covers network devices only.");
            plan.PlanJson = JsonSerializer.Serialize(document);
            _logger.LogWarning(
                "Refusing the UniFi OS step of rollout {Id} on site {Site}: the console is a self-hosted UniFi OS Server",
                plan.Id, _siteSlug);
        }

        // The catalog refresh IS UniFi's "Check for Updates" - it stages builds as well as listing
        // them, so a rollout that skipped it would command devices the console has nothing ready for.
        await RefreshCatalogAsync(force: true, cancellationToken);

        await TriggerNetworkAppUpdateAsync(document, cancellationToken);

        plan.PlanJson = JsonSerializer.Serialize(document);
        plan.Status = FirmwareRolloutStatus.Running;
        plan.StartedAt = Now;
        await PersistPlanAsync(plan, cancellationToken);

        var steps = await _repositories.UseAsync((r, c) => r.GetStepsAsync(plan.Id, c), cancellationToken);
        var upgrading = steps.Count(s => !IsSettled(s));
        await PublishAsync(
            RolloutAlerts.Started,
            AlertSeverity.Info,
            $"Firmware Rollout Started{_siteSuffix}",
            $"Upgrading {upgrading} device{(upgrading == 1 ? "" : "s")} across {document.Waves.Count} wave{(document.Waves.Count == 1 ? "" : "s")}.",
            null, null, cancellationToken);

        _logger.LogInformation("Firmware rollout {Id} started on site {Site}", plan.Id, _siteSlug);
        return true;
    }

    private async Task<bool> RunPreFlightBackupAsync(FirmwareRolloutPlan plan, CancellationToken cancellationToken)
    {
        var backup = await _commands.TriggerBackupAsync(cancellationToken);
        if (backup.IsOk)
            return true;

        // No backup, no rollout. The whole point of the gate is that there is a restore point
        // before anything reboots, and the site can waive it in settings if it disagrees.
        await PostponeAsync(plan, $"the pre-flight console backup failed - {backup.Message}", cancellationToken);
        return false;
    }

    private async Task PostponeAsync(FirmwareRolloutPlan plan, string reason, CancellationToken cancellationToken)
    {
        plan.ScheduledStartAt = (plan.ScheduledStartAt ?? Now) + HealthPostponeWindow;
        plan.Status = FirmwareRolloutStatus.Scheduled;
        await PersistPlanAsync(plan, cancellationToken);

        await PublishAsync(
            RolloutAlerts.PostponedHealth,
            AlertSeverity.Info,
            $"Firmware Rollout Postponed{_siteSuffix}",
            $"The rollout did not start because {reason}. It will try again at {plan.ScheduledStartAt:yyyy-MM-dd HH:mm} UTC.",
            null, null, cancellationToken);

        _logger.LogInformation(
            "Firmware rollout {Id} on site {Site} postponed to {When}: {Reason}",
            plan.Id, _siteSlug, plan.ScheduledStartAt, reason);
    }

    private async Task<bool> IsStandaloneConsoleAsync(CancellationToken cancellationToken)
    {
        var info = await _commands.GetConsoleSystemInfoAsync(cancellationToken);
        return info?.IsStandaloneConsole == true;
    }

    // --- The pass ------------------------------------------------------------------------------

    private async Task AdvanceAsync(FirmwareRolloutPlan plan, CancellationToken cancellationToken)
    {
        var document = ParseDocument(plan);
        var settings = await _repositories.UseAsync((r, c) => r.GetSettingsAsync(c), cancellationToken);
        var steps = await _repositories.UseAsync((r, c) => r.GetStepsAsync(plan.Id, c), cancellationToken);
        if (steps.Count == 0) return;

        var observations = await _observer.ObserveAsync(cancellationToken);
        var byMac = observations.ToDictionary(o => o.Mac, StringComparer.OrdinalIgnoreCase);
        var consoleDark = observations.Count == 0;

        foreach (var step in steps.Where(IsInFlight).ToList())
        {
            if (settings.SuppressStandardAlerts)
                _suppression.Refresh(_siteSlug, step.DeviceMac, Now);

            byMac.TryGetValue(step.DeviceMac, out var observation);
            await ProgressStepAsync(plan, document, steps, step, observation, consoleDark, cancellationToken);

            if (IsSettled(step))
            {
                _suppression.Clear(_siteSlug, step.DeviceMac);
                _lastWaveSettledAt = Now;
            }
        }

        await PropagateCanaryOutcomesAsync(document, steps, cancellationToken);
        await RunDueResourceComparisonsAsync(steps, cancellationToken);
        EnqueueDueMeshRepairs(document, steps);

        // Wave 0. The UniFi Network application update aligns the firmware catalog every device
        // step then works from, so no device is commanded until it has been through.
        var networkAppSettled = await AdvanceNetworkAppUpdateAsync(plan, document, consoleDark, cancellationToken);

        if (networkAppSettled && plan.Status == FirmwareRolloutStatus.Running)
            await OpenNextWaveAsync(plan, document, settings, steps, byMac, cancellationToken);

        if (!steps.All(IsSettled))
            return;

        // The planner forces the gateway's channel group last, so every device step being settled
        // is exactly "the gateway step has settled" - and is also the right point on a plan that
        // has no gateway step at all.
        if (!await AdvanceUniFiOsUpdateAsync(plan, document, steps, cancellationToken))
            return;

        await CompleteAsync(plan, document, steps, cancellationToken);
    }

    /// <summary>
    /// Commands the UniFi Network application update once, at rollout start. A console with nothing
    /// to install says so by refusing, which is not a failure of anything.
    /// </summary>
    private async Task TriggerNetworkAppUpdateAsync(RolloutPlanDocument document, CancellationToken cancellationToken)
    {
        var state = document.NetworkAppUpdate;
        if (!document.IncludesUniFiNetworkUpdate)
        {
            state.Settled = true;
            state.Outcome = "skipped";
            return;
        }

        if (state.Triggered || state.Settled)
            return;

        if (!await _commands.TriggerNetworkApplicationUpdateAsync(cancellationToken))
        {
            // Nothing staged, or the console would not take it. Either way there is nothing to
            // wait for and nothing worth telling anyone about.
            state.Settled = true;
            state.Outcome = "nothing-to-update";
            _logger.LogInformation(
                "No UniFi Network application update to install on site {Site}; going straight to the devices", _siteSlug);
            return;
        }

        state.Triggered = true;
        state.TriggeredAt = Now;
        _logger.LogInformation("Installing the UniFi Network application update on site {Site}", _siteSlug);
    }

    /// <summary>
    /// Waits out the application restart. Returns true once there is nothing left to wait for -
    /// including when it never came back, because an application update that failed must not
    /// strand the device upgrades behind it.
    /// </summary>
    private async Task<bool> AdvanceNetworkAppUpdateAsync(
        FirmwareRolloutPlan plan, RolloutPlanDocument document, bool consoleDark, CancellationToken cancellationToken)
    {
        var state = document.NetworkAppUpdate;
        if (state.Settled) return true;
        if (!state.Triggered) return true;

        var triggeredAt = state.TriggeredAt ?? Now;

        // Not on the pass that commanded it: the application takes a moment to go down, and an API
        // that has not stopped answering yet is indistinguishable from one that already came back.
        if (Now <= triggeredAt)
            return false;

        if (!consoleDark)
        {
            state.Settled = true;
            state.Outcome = "updated";
            await PersistDocumentAsync(plan, document, cancellationToken);
            _logger.LogInformation("The UniFi Network application on site {Site} is answering again", _siteSlug);
            return true;
        }

        if (Now - triggeredAt < NetworkAppUpdateBudget)
            return false;

        state.Settled = true;
        state.Outcome = "stuck";
        await PersistDocumentAsync(plan, document, cancellationToken);

        await PublishAsync(
            RolloutAlerts.NetworkAppUpdateStuck,
            AlertSeverity.Warning,
            $"UniFi Network Application Not Back{_siteSuffix}",
            $"The UniFi Network application was updated and has not answered for {NetworkAppUpdateBudget.TotalMinutes:0} minutes. The device upgrades are going ahead anyway.",
            null, null, cancellationToken);

        _logger.LogError(
            "The UniFi Network application on site {Site} has not returned within {Budget}; upgrading devices anyway",
            _siteSlug, NetworkAppUpdateBudget);
        return true;
    }

    /// <summary>
    /// The last step of a rollout: the console's own UniFi OS update, once every device is through.
    /// Returns true when there is nothing left to wait for.
    /// <para>
    /// Success is the version check every device step gets: Cloud Gateways report the installed
    /// UniFi OS as hardware.firmwareVersion in catalog-comparable form. Consoles without that field
    /// fall back to "the build it accepted is no longer on offer". Download and install run BEFORE
    /// the reboot with the console still answering and the build still offered, so no judgment is
    /// made while the update state machine reports work in progress or inside the judge delay.
    /// </para>
    /// </summary>
    private async Task<bool> AdvanceUniFiOsUpdateAsync(
        FirmwareRolloutPlan plan,
        RolloutPlanDocument document,
        List<FirmwareRolloutStep> steps,
        CancellationToken cancellationToken)
    {
        var state = document.UniFiOsUpdate;
        if (state.Settled) return true;

        if (!document.IncludesUniFiOsUpdate)
        {
            state.Settled = true;
            state.Outcome = "skipped";
            await PersistDocumentAsync(plan, document, cancellationToken);
            return true;
        }

        if (!state.Triggered)
            return await StartUniFiOsUpdateAsync(plan, document, cancellationToken);

        var triggeredAt = state.TriggeredAt ?? Now;
        var info = await _commands.GetConsoleSystemInfoAsync(cancellationToken);

        if (info == null)
        {
            if (Now - triggeredAt < UniFiOsUpdateBudget)
                return false;

            await SettleUniFiOsAsync(plan, document, "stuck", cancellationToken);
            await PublishAsync(
                RolloutAlerts.DeviceStuckOffline,
                AlertSeverity.Critical,
                $"Console Stuck Offline After UniFi OS Update{_siteSuffix}",
                $"The console was updated to UniFi OS {state.TargetVersion ?? "its pending build"} and has not answered for {UniFiOsUpdateBudget.TotalMinutes:0} minutes.",
                steps.FirstOrDefault(IsGatewayStep)?.DeviceMac,
                steps.FirstOrDefault(IsGatewayStep)?.DeviceName,
                cancellationToken);
            return true;
        }

        if (UniFiOsUpdateInProgress(info) || Now - triggeredAt < UniFiOsJudgeDelay)
        {
            if (Now - triggeredAt < UniFiOsUpdateBudget)
                return false;

            await SettleUniFiOsAsync(plan, document, "stuck", cancellationToken);
            await PublishAsync(
                RolloutAlerts.DeviceStuckOffline,
                AlertSeverity.Critical,
                $"Console Stuck Updating UniFi OS{_siteSuffix}",
                $"The UniFi OS {state.TargetVersion ?? "update"} install has been running for {UniFiOsUpdateBudget.TotalMinutes:0} minutes without finishing.",
                steps.FirstOrDefault(IsGatewayStep)?.DeviceMac,
                steps.FirstOrDefault(IsGatewayStep)?.DeviceName,
                cancellationToken);
            return true;
        }

        var installed = info.InstalledOsVersion;
        if (installed != null)
        {
            if (OsVersionMatches(installed, state.TargetVersion))
            {
                await SettleUniFiOsAsync(plan, document, "updated", cancellationToken);
                _logger.LogInformation(
                    "The console on site {Site} is back on UniFi OS {Version}", _siteSlug, installed);
            }
            else
            {
                await SettleUniFiOsAsync(plan, document, "unchanged", cancellationToken);
                _logger.LogError(
                    "The console on site {Site} came back on UniFi OS {Installed}, not {Target}, so the update did not take",
                    _siteSlug, installed, state.TargetVersion);
            }
            return true;
        }

        var pending = await _commands.GetPendingUniFiOsUpdateAsync(cancellationToken);
        if (pending?.Version != null && VersionsMatch(pending.Version, state.TargetVersion))
        {
            await SettleUniFiOsAsync(plan, document, "unchanged", cancellationToken);
            _logger.LogError(
                "The console on site {Site} came back still offering UniFi OS {Version}, so the update did not take",
                _siteSlug, state.TargetVersion);
            return true;
        }

        await SettleUniFiOsAsync(plan, document, "updated", cancellationToken);
        _logger.LogInformation(
            "The console on site {Site} is back on UniFi OS {Version}", _siteSlug, state.TargetVersion);
        return true;
    }

    /// <summary>
    /// Installed "5.1.28" against catalog "v5.1.28+baa7152": numeric parts only, because the
    /// hardware block never reports the build hash.
    /// </summary>
    internal static bool OsVersionMatches(string? installed, string? target)
    {
        if (string.IsNullOrWhiteSpace(installed) || string.IsNullOrWhiteSpace(target))
            return false;
        var t = target.Trim().TrimStart('v', 'V');
        var plus = t.IndexOf('+');
        if (plus >= 0) t = t[..plus];
        return string.Equals(installed.Trim().TrimStart('v', 'V'), t, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether the console reports the OS update still working. Only the known busy states count
    /// as busy - an unrecognized state is judged by version rather than waited on, so a vendor
    /// string we have never seen cannot hang the step until the budget's false alarm.
    /// </summary>
    private static bool UniFiOsUpdateInProgress(UniFiConsoleSystemInfo info)
    {
        return IsBusy(info.Firmware?.Progress?.State) || IsBusy(info.Firmware?.Update?.State);

        static bool IsBusy(string? state) =>
            state != null && BusyOsStates.Any(b => state.Contains(b, StringComparison.OrdinalIgnoreCase));
    }

    private static readonly string[] BusyOsStates = ["download", "install", "progress", "updating", "applying", "started"];

    private async Task<bool> StartUniFiOsUpdateAsync(
        FirmwareRolloutPlan plan, RolloutPlanDocument document, CancellationToken cancellationToken)
    {
        // Belt and braces on the scope rule: BeginAsync clears the flag on a self-hosted console,
        // and this refuses again at the only moment it would actually command one.
        if (await IsStandaloneConsoleAsync(cancellationToken))
        {
            _logger.LogWarning(
                "Refusing the UniFi OS update on site {Site}: the console is a self-hosted UniFi OS Server", _siteSlug);
            await SettleUniFiOsAsync(plan, document, "refused", cancellationToken);
            return true;
        }

        var pending = await _commands.GetPendingUniFiOsUpdateAsync(cancellationToken);
        if (pending?.Version == null)
        {
            await SettleUniFiOsAsync(plan, document, "nothing-to-update", cancellationToken);
            return true;
        }

        document.UniFiOsUpdate.TargetVersion = pending.Version;
        if (!await _commands.TriggerUniFiOsUpdateAsync(cancellationToken))
        {
            await SettleUniFiOsAsync(plan, document, "refused", cancellationToken);
            _logger.LogWarning("The console on site {Site} refused the UniFi OS update", _siteSlug);
            return true;
        }

        document.UniFiOsUpdate.Triggered = true;
        document.UniFiOsUpdate.TriggeredAt = Now;
        await PersistDocumentAsync(plan, document, cancellationToken);
        _logger.LogInformation(
            "Installing UniFi OS {Version} on the console for site {Site}; expect it to go dark",
            pending.Version, _siteSlug);
        return false;
    }

    private async Task SettleUniFiOsAsync(
        FirmwareRolloutPlan plan, RolloutPlanDocument document, string outcome, CancellationToken cancellationToken)
    {
        document.UniFiOsUpdate.Settled = true;
        document.UniFiOsUpdate.Outcome = outcome;
        await PersistDocumentAsync(plan, document, cancellationToken);
    }

    private async Task ProgressStepAsync(
        FirmwareRolloutPlan plan,
        RolloutPlanDocument document,
        List<FirmwareRolloutStep> steps,
        FirmwareRolloutStep step,
        RolloutDeviceObservation? observation,
        bool consoleDark,
        CancellationToken cancellationToken)
    {
        switch (step.State)
        {
            case FirmwareRolloutStepState.Commanded:
                await ProgressCommandedAsync(document, steps, step, observation, consoleDark, cancellationToken);
                break;

            case FirmwareRolloutStepState.Down:
                await ProgressDownAsync(document, steps, step, observation, cancellationToken);
                break;

            case FirmwareRolloutStepState.BackOnline:
                step.State = FirmwareRolloutStepState.CoolDown;
                await PersistStepAsync(step, cancellationToken);
                break;

            case FirmwareRolloutStepState.CoolDown:
                await ProgressCoolDownAsync(document, steps, step, cancellationToken);
                break;
        }
    }

    private async Task ProgressCommandedAsync(
        RolloutPlanDocument document,
        List<FirmwareRolloutStep> steps,
        FirmwareRolloutStep step,
        RolloutDeviceObservation? observation,
        bool consoleDark,
        CancellationToken cancellationToken)
    {
        if (observation == null)
        {
            // A console that answers nothing at all is expected while a gateway reboots - its own
            // upgrade takes the console with it - so that one step reads the silence as Down. For
            // every other device the silence is no information, and no information moves nothing.
            if (consoleDark && IsGatewayStep(step))
                await MarkDownAsync(step, cancellationToken);
            return;
        }

        var status = UniFiDeviceStateMap.ToStatus(observation.State);
        if (status.Kind is DeviceStatusKind.Transitional or DeviceStatusKind.Offline)
        {
            await MarkDownAsync(step, cancellationToken);
            return;
        }

        // A short cycle can fall entirely between two passes. Accept it only on the evidence that
        // matters: the reported version is now the target, and it was not the target before.
        if (VersionsMatch(observation.Firmware, step.ToVersion) && !VersionsMatch(step.FromVersion, step.ToVersion))
        {
            step.WentDownAt ??= step.CommandedAt;
            await MarkBackOnlineAsync(step, observation, cancellationToken);
            return;
        }

        var commandedAt = step.CommandedAt ?? Now;
        if (_escalatedAt.TryGetValue(step.Id, out var escalatedAt))
        {
            if (Now - escalatedAt >= CommandGraceWindow)
            {
                await FailStepAsync(document, steps, step,
                    "The device never started the upgrade, over the console or over SSH.", cancellationToken);
            }
            return;
        }

        if (Now - commandedAt < CommandGraceWindow)
            return;

        await EscalateToSshAsync(document, steps, step, observation, cancellationToken);
    }

    private async Task EscalateToSshAsync(
        RolloutPlanDocument document,
        List<FirmwareRolloutStep> steps,
        FirmwareRolloutStep step,
        RolloutDeviceObservation observation,
        CancellationToken cancellationToken)
    {
        var url = await ResolveImageUrlAsync(step.Model, cancellationToken);
        if (url == null)
        {
            await FailStepAsync(document, steps, step,
                "The upgrade command was accepted but nothing happened, and the console lists no image URL to retry over SSH.",
                cancellationToken);
            return;
        }

        if (string.IsNullOrWhiteSpace(observation.IpAddress))
        {
            await FailStepAsync(document, steps, step,
                "The upgrade command was accepted but nothing happened, and the device has no address to reach over SSH.",
                cancellationToken);
            return;
        }

        _logger.LogWarning(
            "{Device} on site {Site} did not act on its upgrade command; retrying over SSH",
            step.DeviceName, _siteSlug);

        var result = await _commands.TriggerSshUpgradeAsync(observation.IpAddress, url, cancellationToken);
        if (!result.IsOk)
        {
            await FailStepAsync(document, steps, step,
                $"The upgrade did not start and the SSH retry failed: {result.Message}", cancellationToken);
            return;
        }

        _escalatedAt[step.Id] = Now;
    }

    private async Task ProgressDownAsync(
        RolloutPlanDocument document,
        List<FirmwareRolloutStep> steps,
        FirmwareRolloutStep step,
        RolloutDeviceObservation? observation,
        CancellationToken cancellationToken)
    {
        if (observation != null && UniFiDeviceStateMap.ToStatus(observation.State).Kind == DeviceStatusKind.Online)
        {
            await MarkBackOnlineAsync(step, observation, cancellationToken);
            return;
        }

        var wentDownAt = step.WentDownAt ?? step.CommandedAt ?? Now;
        var budget = TimeSpan.FromSeconds(OfflineBudgetSecondsFor(document, step));
        if (Now - wentDownAt < budget)
            return;

        await FailStepAsync(document, steps, step,
            $"The device has been offline for over {budget.TotalMinutes:0} minutes and has not come back.",
            cancellationToken);

        await PublishAsync(
            RolloutAlerts.DeviceStuckOffline,
            AlertSeverity.Critical,
            $"Device Stuck Offline After Upgrade: {step.DeviceName}{_siteSuffix}",
            $"{step.DeviceName} ({step.Model}) went down for its firmware upgrade and has not returned within {budget.TotalMinutes:0} minutes. Remaining {step.Model} devices have been dropped from the rollout.",
            step.DeviceMac,
            step.DeviceName,
            cancellationToken);
    }

    private async Task ProgressCoolDownAsync(
        RolloutPlanDocument document,
        List<FirmwareRolloutStep> steps,
        FirmwareRolloutStep step,
        CancellationToken cancellationToken)
    {
        var backAt = step.BackAt ?? Now;
        if (Now - backAt < CoolDown)
            return;

        var preStats = ParseStats(step.PreStatsJson);
        var verdict = await _litmus.RunShortLitmusAsync(
            step.DeviceMac, preStats, backAt + ShortLitmusSettle, Now, cancellationToken);

        if (!verdict.Passed)
        {
            await FailStepAsync(document, steps, step, verdict.Reason ?? "The post-upgrade check failed.", cancellationToken);
            return;
        }

        step.State = FirmwareRolloutStepState.LitmusPassed;
        await PersistStepAsync(step, cancellationToken);
        _logger.LogInformation(
            "{Device} on site {Site} passed its post-upgrade checks on {Version}",
            step.DeviceName, _siteSlug, step.ToVersion);
    }

    private async Task MarkDownAsync(FirmwareRolloutStep step, CancellationToken cancellationToken)
    {
        step.State = FirmwareRolloutStepState.Down;
        step.WentDownAt ??= Now;
        await PersistStepAsync(step, cancellationToken);
    }

    private async Task MarkBackOnlineAsync(
        FirmwareRolloutStep step, RolloutDeviceObservation observation, CancellationToken cancellationToken)
    {
        step.BackAt = Now;
        if (step.WentDownAt is DateTime down)
            step.DowntimeSeconds = (int)Math.Max(0, (Now - down).TotalSeconds);

        // rc:ok and a full offline/online cycle both lie. The reported version is the only proof.
        if (!string.IsNullOrWhiteSpace(step.ToVersion) && !VersionsMatch(observation.Firmware, step.ToVersion))
        {
            step.State = FirmwareRolloutStepState.Failed;
            step.Error = $"The device cycled but came back on {observation.Firmware ?? "an unknown version"}, not {step.ToVersion}.";
            await PersistStepAsync(step, cancellationToken);
            _logger.LogError(
                "{Device} on site {Site} rebooted but is still on {Version}, not {Target}",
                step.DeviceName, _siteSlug, observation.Firmware, step.ToVersion);
            return;
        }

        step.State = FirmwareRolloutStepState.BackOnline;
        await PersistStepAsync(step, cancellationToken);

        // Only a verified upgrade feeds the timing store: a cycle that changed nothing is not a
        // measurement of how long this model takes to upgrade.
        if (step.DowntimeSeconds is int seconds && seconds > 0 && !string.IsNullOrWhiteSpace(step.Model))
        {
            await _repositories.UseAsync(
                (r, c) => r.RecordModelTimingAsync(step.Model, seconds, c), cancellationToken);
        }
    }

    private async Task FailStepAsync(
        RolloutPlanDocument document,
        List<FirmwareRolloutStep> steps,
        FirmwareRolloutStep step,
        string error,
        CancellationToken cancellationToken)
    {
        step.State = FirmwareRolloutStepState.Failed;
        step.Error = error;
        await PersistStepAsync(step, cancellationToken);
        _logger.LogError("{Device} on site {Site} failed its upgrade: {Error}", step.DeviceName, _siteSlug, error);

        await AbortSkuAsync(document, steps, step, error, cancellationToken);
    }

    /// <summary>
    /// Drops every device of a failed device's SKU that has not started. A failure that reproduces
    /// is a firmware problem with that model, and the rest of the fleet keeps rolling regardless.
    /// </summary>
    private async Task AbortSkuAsync(
        RolloutPlanDocument document,
        List<FirmwareRolloutStep> steps,
        FirmwareRolloutStep failed,
        string reason,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(failed.Model)) return;

        var peers = steps
            .Where(s => s.Id != failed.Id
                && string.Equals(s.Model, failed.Model, StringComparison.OrdinalIgnoreCase)
                && s.State is FirmwareRolloutStepState.Pending or FirmwareRolloutStepState.Held)
            .ToList();

        foreach (var peer in peers)
        {
            peer.State = FirmwareRolloutStepState.AbortedSku;
            peer.Error = $"Dropped after {failed.DeviceName} failed: {reason}";
            await PersistStepAsync(peer, cancellationToken);
        }

        if (!_skuAbortsPublished.Add(failed.Model))
            return;

        var isCanary = IsCanary(document, failed.DeviceMac);
        await PublishAsync(
            RolloutAlerts.SkuAborted,
            AlertSeverity.Warning,
            $"Firmware Rollout Dropped {failed.Model}{_siteSuffix}",
            $"{failed.DeviceName} ({failed.Model}{(isCanary ? ", the first of its model" : "")}) did not come through its upgrade to {failed.ToVersion ?? "the target version"}. {peers.Count} remaining {failed.Model} device{(peers.Count == 1 ? " was" : "s were")} dropped; other models keep rolling.",
            failed.DeviceMac,
            failed.DeviceName,
            cancellationToken);
    }

    /// <summary>
    /// Releases a model's held peers once its canary is through, which is the whole point of the
    /// hold: one device of each model proves the build before the rest follow it.
    /// </summary>
    private async Task PropagateCanaryOutcomesAsync(
        RolloutPlanDocument document, List<FirmwareRolloutStep> steps, CancellationToken cancellationToken)
    {
        var held = steps.Where(s => s.State == FirmwareRolloutStepState.Held).ToList();
        if (held.Count == 0) return;

        foreach (var group in held.GroupBy(s => s.Model, StringComparer.OrdinalIgnoreCase))
        {
            var canaries = steps
                .Where(s => string.Equals(s.Model, group.Key, StringComparison.OrdinalIgnoreCase)
                    && IsCanary(document, s.DeviceMac))
                .ToList();
            if (canaries.Count == 0) continue;

            if (canaries.Any(c => c.State is FirmwareRolloutStepState.LitmusPassed or FirmwareRolloutStepState.RegressionFlagged))
            {
                foreach (var step in group)
                {
                    step.State = FirmwareRolloutStepState.Pending;
                    await PersistStepAsync(step, cancellationToken);
                }

                _logger.LogInformation(
                    "The {Model} canary passed on site {Site}; releasing {Count} held device(s)",
                    group.Key, _siteSlug, group.Count());
            }
        }
    }

    private async Task RunDueResourceComparisonsAsync(List<FirmwareRolloutStep> steps, CancellationToken cancellationToken)
    {
        var due = steps.Where(s =>
            s.State is FirmwareRolloutStepState.LitmusPassed
            && s.PostStatsJson == null
            && s.BackAt is DateTime back
            && Now >= back + CoolDown + ResourceWindow);

        foreach (var step in due.ToList())
        {
            var from = step.BackAt!.Value + CoolDown;
            var post = await _litmus.CaptureStatsAsync(step.DeviceMac, from, from + ResourceWindow, cancellationToken);
            step.PostStatsJson = JsonSerializer.Serialize(post);

            var comparison = LitmusThresholds.Compare(ParseStats(step.PreStatsJson), post);
            if (comparison.Verdict == ResourceComparisonVerdict.Regression)
                step.State = FirmwareRolloutStepState.RegressionFlagged;

            await PersistStepAsync(step, cancellationToken);

            if (comparison.Verdict == ResourceComparisonVerdict.Regression)
            {
                await PublishAsync(
                    RolloutAlerts.ResourceRegression,
                    AlertSeverity.Warning,
                    $"Heavier After Upgrade: {step.DeviceName}{_siteSuffix}",
                    $"{step.DeviceName} ({step.Model}) went from {step.FromVersion ?? "its previous firmware"} to {step.ToVersion ?? "its new firmware"} and is working harder since. {comparison.Detail} Worth a look, or a roll back.",
                    step.DeviceMac, step.DeviceName, cancellationToken);
            }
            else if (comparison.Verdict == ResourceComparisonVerdict.Improvement)
            {
                await PublishAsync(
                    RolloutAlerts.ResourceImprovement,
                    AlertSeverity.Info,
                    $"Lighter After Upgrade: {step.DeviceName}{_siteSuffix}",
                    $"{step.DeviceName} ({step.Model}) went from {step.FromVersion ?? "its previous firmware"} to {step.ToVersion ?? "its new firmware"} and is working less hard since. {comparison.Detail}",
                    step.DeviceMac, step.DeviceName, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Queues a mesh pair's backhaul re-scan once BOTH halves are through. Re-scanning before the
    /// parent is upgraded would only re-pair to a link that is about to drop again.
    /// </summary>
    private void EnqueueDueMeshRepairs(RolloutPlanDocument document, List<FirmwareRolloutStep> steps)
    {
        foreach (var repair in document.MeshRepairs)
        {
            if (string.IsNullOrEmpty(repair.ChildMac) || _meshRepairsQueued.Contains(repair.ChildMac))
                continue;

            var child = steps.FirstOrDefault(s => s.DeviceMac == repair.ChildMac);
            var parent = repair.ParentMac == null ? null : steps.FirstOrDefault(s => s.DeviceMac == repair.ParentMac);

            var childDone = child == null || IsPassed(child);
            var parentDone = parent == null || IsPassed(parent);
            var waveDone = steps.Where(s => s.Wave <= repair.AfterWave).All(IsSettled);

            if (!((childDone && parentDone) || waveDone))
                continue;

            // A pair whose halves failed has nothing worth re-pairing.
            if (child != null && !IsPassed(child))
            {
                _meshRepairsQueued.Add(repair.ChildMac);
                continue;
            }

            if (_meshRepairs.Enqueue(repair.ChildIp, repair.Iface, repair.ChildName))
            {
                _logger.LogInformation(
                    "Queued a mesh backhaul re-pair for {Ap} on site {Site}", repair.ChildName, _siteSlug);
            }

            _meshRepairsQueued.Add(repair.ChildMac);
        }
    }

    private static bool IsPassed(FirmwareRolloutStep step) => step.State
        is FirmwareRolloutStepState.LitmusPassed or FirmwareRolloutStepState.RegressionFlagged;

    // --- Waves ---------------------------------------------------------------------------------

    private async Task OpenNextWaveAsync(
        FirmwareRolloutPlan plan,
        RolloutPlanDocument document,
        FirmwareRolloutSettings settings,
        List<FirmwareRolloutStep> steps,
        Dictionary<string, RolloutDeviceObservation> byMac,
        CancellationToken cancellationToken)
    {
        if (steps.Any(IsInFlight))
            return;

        var pending = steps.Where(s => !IsSettled(s)).ToList();
        if (pending.Count == 0)
            return;

        var wave = pending.Min(s => s.Wave);
        var waveSteps = pending.Where(s => s.Wave == wave).ToList();

        if (waveSteps.All(s => s.State == FirmwareRolloutStepState.Held))
        {
            // Nothing in this wave can move and no canary is left to release it. Rather than stall
            // forever, let it run: the canary that was meant to gate it is already gone.
            var stillGating = steps.Any(s => IsCanary(document, s.DeviceMac)
                && waveSteps.Any(w => string.Equals(w.Model, s.Model, StringComparison.OrdinalIgnoreCase))
                && !IsSettled(s));
            if (stillGating) return;

            foreach (var step in waveSteps)
            {
                step.State = FirmwareRolloutStepState.Pending;
                await PersistStepAsync(step, cancellationToken);
            }
        }

        if (settings.PerWaveApproval && document.ApprovedThroughWave < wave)
        {
            await PauseForApprovalAsync(plan, document, wave, cancellationToken);
            return;
        }

        if (_lastWaveSettledAt is DateTime settled && Now - settled < GapBefore(waveSteps, settings))
            return;

        var channel = waveSteps.Select(s => s.Channel).FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));
        if (!string.IsNullOrWhiteSpace(channel))
            await EnsureChannelAsync(plan, channel, cancellationToken);

        foreach (var step in waveSteps.Where(s => s.State == FirmwareRolloutStepState.Pending))
        {
            byMac.TryGetValue(step.DeviceMac, out var observation);
            await CommandStepAsync(document, steps, step, observation, settings, cancellationToken);
        }
    }

    private async Task PauseForApprovalAsync(
        FirmwareRolloutPlan plan, RolloutPlanDocument document, int wave, CancellationToken cancellationToken)
    {
        document.WaitingApprovalWave = wave;
        plan.PlanJson = JsonSerializer.Serialize(document);
        plan.Status = FirmwareRolloutStatus.Paused;
        await PersistPlanAsync(plan, cancellationToken);

        await PublishAsync(
            RolloutAlerts.WaveAwaitingApproval,
            AlertSeverity.Info,
            $"Firmware Rollout Waiting on You{_siteSuffix}",
            $"Wave {wave} is ready to go and this rollout approves every wave by hand. Open Firmware Rollout to let it run.",
            null, null, cancellationToken);

        _logger.LogInformation(
            "Firmware rollout {Id} on site {Site} is waiting for approval of wave {Wave}", plan.Id, _siteSlug, wave);
    }

    private async Task CommandStepAsync(
        RolloutPlanDocument document,
        List<FirmwareRolloutStep> steps,
        FirmwareRolloutStep step,
        RolloutDeviceObservation? observation,
        FirmwareRolloutSettings settings,
        CancellationToken cancellationToken)
    {
        if (observation == null)
            return;

        // The console has to have staged this device's build before there is anything to command.
        // After the wait it is commanded anyway: some models report nothing here even when ready.
        if (!string.IsNullOrWhiteSpace(step.ToVersion)
            && !VersionsMatch(observation.UpgradeToFirmware, step.ToVersion)
            && !VersionsMatch(observation.Firmware, step.ToVersion))
        {
            var waitingSince = _commandWaitSince.GetOrAdd(step.Id, _ => Now);
            if (Now - waitingSince < CatalogReflectWait)
                return;
        }

        step.PreStatsJson = JsonSerializer.Serialize(
            await _litmus.CaptureStatsAsync(step.DeviceMac, Now - ResourceWindow, Now, cancellationToken));

        var result = await _commands.TriggerUpgradeAsync(step.DeviceMac, cancellationToken);
        if (!result.IsOk)
        {
            var url = await ResolveImageUrlAsync(step.Model, cancellationToken);
            if (url != null)
                result = await _commands.TriggerExternalUpgradeAsync(step.DeviceMac, url, cancellationToken);

            if (!result.IsOk && url != null && !string.IsNullOrWhiteSpace(observation.IpAddress))
            {
                result = await _commands.TriggerSshUpgradeAsync(observation.IpAddress, url, cancellationToken);
                if (result.IsOk)
                    _escalatedAt[step.Id] = Now;
            }
        }

        if (!result.IsOk)
        {
            await FailStepAsync(document, steps, step,
                $"The upgrade could not be started: {result.Message ?? "no path accepted the command"}",
                cancellationToken);
            return;
        }

        step.State = FirmwareRolloutStepState.Commanded;
        step.CommandedAt = Now;
        await PersistStepAsync(step, cancellationToken);

        if (settings.SuppressStandardAlerts)
            _suppression.Refresh(_siteSlug, step.DeviceMac, Now);

        _logger.LogInformation(
            "Commanded {Device} ({Model}) on site {Site} to upgrade to {Version}",
            step.DeviceName, step.Model, _siteSlug, step.ToVersion);
    }

    private async Task CompleteAsync(
        FirmwareRolloutPlan plan,
        RolloutPlanDocument document,
        List<FirmwareRolloutStep> steps,
        CancellationToken cancellationToken)
    {
        await RestoreChannelsAsync(plan, cancellationToken);

        plan.Status = FirmwareRolloutStatus.SoakWait;
        plan.CompletedAt = Now;
        await PersistPlanAsync(plan, cancellationToken);
        _suppression.ClearSite(_siteSlug);

        var upgraded = steps.Count(IsPassed);
        var failed = steps.Count(s => s.State == FirmwareRolloutStepState.Failed);
        var dropped = steps.Count(s => s.State == FirmwareRolloutStepState.AbortedSku);

        await PublishAsync(
            RolloutAlerts.Completed,
            AlertSeverity.Info,
            $"Firmware Rollout Complete{_siteSuffix}",
            $"{upgraded} device{(upgraded == 1 ? "" : "s")} upgraded, {failed} failed, {dropped} dropped."
            + ConsoleUpdateSummary(document)
            + " The report follows after the soak.",
            null, null, cancellationToken);

        _logger.LogInformation(
            "Firmware rollout {Id} on site {Site} finished: {Upgraded} upgraded, {Failed} failed, {Dropped} dropped",
            plan.Id, _siteSlug, upgraded, failed, dropped);
    }

    // --- Channels and resume -------------------------------------------------------------------

    private async Task EnsureChannelAsync(FirmwareRolloutPlan plan, string channel, CancellationToken cancellationToken)
    {
        if (!await _channels.NeedsChangeAsync(channel, cancellationToken))
            return;

        if (plan.OriginalChannelSettingsJson == null)
        {
            // Persisted BEFORE the change: a crash between the two would otherwise leave the site
            // on a channel it never chose with nothing recording what to put back.
            plan.OriginalChannelSettingsJson = await _channels.CaptureOriginalAsync(cancellationToken);
            await PersistPlanAsync(plan, cancellationToken);
        }

        await _channels.ApplyAsync(channel, cancellationToken);
        await RefreshCatalogAsync(force: true, cancellationToken);
    }

    private async Task RestoreChannelsAsync(FirmwareRolloutPlan plan, CancellationToken cancellationToken)
    {
        if (plan.OriginalChannelSettingsJson == null)
            return;

        await _channels.RestoreAsync(plan.OriginalChannelSettingsJson, cancellationToken);
        plan.OriginalChannelSettingsJson = null;
        await PersistPlanAsync(plan, cancellationToken);
    }

    /// <summary>
    /// Puts the channels back for a rollout that ended while the server was down. The capture is
    /// only cleared once the restore has been made, so a leftover value is a restore that never ran.
    /// </summary>
    private async Task SweepPendingChannelRestoreAsync(CancellationToken cancellationToken)
    {
        if (_restoreSweepDone) return;
        _restoreSweepDone = true;

        var history = await _repositories.UseAsync((r, c) => r.GetPlanHistoryAsync(10, c), cancellationToken);
        foreach (var plan in history.Where(p => p.OriginalChannelSettingsJson != null
            && FirmwareRolloutStatuses.Terminal.Contains(p.Status)))
        {
            _logger.LogWarning(
                "Firmware rollout {Id} on site {Site} ended without putting the firmware channels back; restoring now",
                plan.Id, _siteSlug);
            await RestoreChannelsAsync(plan, cancellationToken);
        }
    }

    /// <summary>
    /// First look at a rollout that was already running when this instance started. The step
    /// machine reconciles itself against live device state on the normal pass, so all this adds is
    /// the record of what it found and a clean slate for the in-memory escalation timers.
    /// </summary>
    private async Task ReconcileOnResumeAsync(FirmwareRolloutPlan plan, CancellationToken cancellationToken)
    {
        if (_reconciledPlanId == plan.Id) return;
        _reconciledPlanId = plan.Id;

        _escalatedAt.Clear();
        _commandWaitSince.Clear();

        var steps = await _repositories.UseAsync((r, c) => r.GetStepsAsync(plan.Id, c), cancellationToken);
        var inFlight = steps.Where(IsInFlight).ToList();
        if (inFlight.Count == 0) return;

        _logger.LogInformation(
            "Resuming firmware rollout {Id} on site {Site} with {Count} device(s) mid-cycle: {Devices}",
            plan.Id, _siteSlug, inFlight.Count, string.Join(", ", inFlight.Select(s => $"{s.DeviceName} ({s.State})")));
    }

    // --- Helpers -------------------------------------------------------------------------------

    private async Task<string?> ResolveImageUrlAsync(string model, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;

        await RefreshCatalogAsync(force: false, cancellationToken);
        var entry = _catalog.FirstOrDefault(e =>
            string.Equals(e.BaseModel, model, StringComparison.OrdinalIgnoreCase)
            || string.Equals(e.Device, model, StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrWhiteSpace(entry?.Url) ? null : entry.Url;
    }

    private async Task RefreshCatalogAsync(bool force, CancellationToken cancellationToken)
    {
        if (!force && Now - _catalogReadAt < CatalogCacheTtl && _catalog.Count > 0)
            return;

        _catalog = await _commands.CheckForUpdatesAsync(cancellationToken);
        _catalogReadAt = Now;
    }

    private TimeSpan GapBefore(List<FirmwareRolloutStep> waveSteps, FirmwareRolloutSettings settings)
    {
        var spacing = ResolvedSpacing.For(settings.SpacingProfile, settings.AdvancedSpacingJson);
        var seconds = waveSteps
            .Select(s => FirmwareDeviceTypes.Parse(s.DeviceType) switch
            {
                DeviceType.Gateway => spacing.GatewayGapSeconds,
                DeviceType.Switch => spacing.SwitchGapSeconds,
                _ => spacing.ApGapSeconds,
            })
            .DefaultIfEmpty(spacing.ApGapSeconds)
            .Max();
        return TimeSpan.FromSeconds(seconds);
    }

    private static int OfflineBudgetSecondsFor(RolloutPlanDocument document, FirmwareRolloutStep step)
    {
        var planned = document.Waves
            .SelectMany(w => w.Steps)
            .FirstOrDefault(s => string.Equals(s.Mac, step.DeviceMac, StringComparison.OrdinalIgnoreCase));
        if (planned is { OfflineBudgetSeconds: > 0 })
            return planned.OfflineBudgetSeconds;

        var type = FirmwareDeviceTypes.Parse(step.DeviceType);
        return FirmwareTimingEstimator.OfflineBudgetSeconds(
            FirmwareTimingEstimator.Classify(type, step.Model, step.Model));
    }

    private static bool IsGatewayStep(FirmwareRolloutStep step) =>
        FirmwareDeviceTypes.Parse(step.DeviceType) == DeviceType.Gateway;

    private static bool IsCanary(RolloutPlanDocument document, string mac) =>
        document.Waves.SelectMany(w => w.Steps)
            .Any(s => string.Equals(s.Mac, mac, StringComparison.OrdinalIgnoreCase) && s.IsCanary);

    /// <summary>
    /// Version equality across the two spellings the console and the feed use. Whitespace and a
    /// leading "v" differ between sources for the same build.
    /// </summary>
    private static bool VersionsMatch(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;
        return string.Equals(Canonical(left), Canonical(right), StringComparison.OrdinalIgnoreCase);

        static string Canonical(string version) => version.Trim().TrimStart('v', 'V').Replace('+', '.');
    }

    private static RolloutPlanDocument ParseDocument(FirmwareRolloutPlan plan)
    {
        try
        {
            return JsonSerializer.Deserialize<RolloutPlanDocument>(plan.PlanJson) ?? new RolloutPlanDocument();
        }
        catch (JsonException)
        {
            return new RolloutPlanDocument();
        }
    }

    private static RolloutResourceStats? ParseStats(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<RolloutResourceStats>(json); }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// What the two console-level updates did, for the completion alert. Only outcomes a reader
    /// would act on or be surprised by earn a sentence.
    /// </summary>
    private static string ConsoleUpdateSummary(RolloutPlanDocument document)
    {
        var parts = new List<string>();

        switch (document.NetworkAppUpdate.Outcome)
        {
            case "updated": parts.Add("The UniFi Network application was updated."); break;
            case "stuck": parts.Add("The UniFi Network application did not come back after its update."); break;
        }

        switch (document.UniFiOsUpdate.Outcome)
        {
            case "updated":
                parts.Add($"The console was updated to UniFi OS {document.UniFiOsUpdate.TargetVersion ?? "its newest build"}.");
                break;
            case "unchanged":
                parts.Add($"The console accepted UniFi OS {document.UniFiOsUpdate.TargetVersion ?? "its newest build"} but is still offering it.");
                break;
            case "stuck":
                parts.Add("The console has not answered since its UniFi OS update.");
                break;
            case "refused":
                parts.Add("The console would not take its UniFi OS update.");
                break;
        }

        return parts.Count == 0 ? "" : " " + string.Join(" ", parts);
    }

    private async Task PersistDocumentAsync(
        FirmwareRolloutPlan plan, RolloutPlanDocument document, CancellationToken cancellationToken)
    {
        plan.PlanJson = JsonSerializer.Serialize(document);
        await PersistPlanAsync(plan, cancellationToken);
    }

    private Task PersistPlanAsync(FirmwareRolloutPlan plan, CancellationToken cancellationToken) =>
        _repositories.UseAsync((r, c) => r.UpdatePlanAsync(plan, c), cancellationToken);

    private Task PersistStepAsync(FirmwareRolloutStep step, CancellationToken cancellationToken) =>
        _repositories.UseAsync((r, c) => r.UpdateStepAsync(step, c), cancellationToken);

    private async Task PublishAsync(
        string eventType,
        AlertSeverity severity,
        string title,
        string message,
        string? deviceMac,
        string? deviceName,
        CancellationToken cancellationToken)
    {
        await _eventBus.PublishAsync(new AlertEvent
        {
            EventType = eventType,
            Source = RolloutAlerts.Source,
            Severity = severity,
            Title = title,
            Message = message,
            DeviceId = deviceMac,
            DeviceName = deviceName,
            SourceUrl = RolloutAlerts.SourceUrl,
        }, cancellationToken);
    }
}
