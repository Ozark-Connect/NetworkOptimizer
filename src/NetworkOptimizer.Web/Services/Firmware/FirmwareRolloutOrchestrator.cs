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
///
/// Deadlines are measured in time the executor could actually SEE the site, never in wall time:
/// a dark console, a dropped agent tunnel or a process that was not running says nothing about the
/// device it would otherwise condemn, so blind time stalls a rollout rather than failing it.
/// </summary>
public class FirmwareRolloutOrchestrator : BackgroundService
{
    /// <summary>How often the executor polls device state.</summary>
    public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long after a command a device has to enter Upgrading or go offline before the command is
    /// treated as not taken. The same window applies again after the SSH escalation.
    /// </summary>
    public static readonly TimeSpan CommandGraceWindow = TimeSpan.FromMinutes(5);

    /// <summary>Settling time for APs and switches after a reboot.</summary>
    public static readonly TimeSpan CoolDown = TimeSpan.FromMinutes(5);

    /// <summary>Settling time for gateways - heavier restart, but always the last step so no wave waits on it.</summary>
    public static readonly TimeSpan GatewayCoolDown = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How much of the cool-down the short litmus ignores. The first minutes after a boot are all
    /// spike, so the canary is judged on the quiet tail of the cool-down.
    /// </summary>
    public static readonly TimeSpan ShortLitmusSettle = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Length of the before and after windows the resource comparison averages over, when a site
    /// has no soak configured. The soak IS this window: it is how much settled running sits on each
    /// side of the comparison, not a wait bolted on after the numbers are already in.
    /// </summary>
    public static readonly TimeSpan ResourceWindow = TimeSpan.FromHours(2);

    /// <summary>The configured soak, as the window the comparison reads on both sides.</summary>
    private static TimeSpan ResourceWindowFor(FirmwareRolloutSettings settings) =>
        settings.SoakHours > 0 ? TimeSpan.FromHours(settings.SoakHours) : ResourceWindow;

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

    /// <summary>
    /// How long the site has to be out of sight before the rollout says so. A device reboot takes
    /// the console with it for a minute or two, which is the run working rather than stalling.
    /// </summary>
    public static readonly TimeSpan VisibilityLostAfter = TimeSpan.FromMinutes(5);

    /// <summary>How long a firmware catalog read is reused before the console is asked again.</summary>
    private static readonly TimeSpan CatalogCacheTtl = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Blind stretches kept on the plan. A flapping tunnel adds one every other pass, and anything
    /// this far back is long past every deadline still in flight.
    /// </summary>
    private const int MaxBlindIntervals = 500;

    private readonly IFirmwareRolloutRepositoryAccessor _repositories;
    private readonly IFirmwareCommandClient _commands;
    private readonly IRolloutDeviceObserver _observer;
    private readonly IRolloutLitmusService _litmus;
    private readonly IRolloutHealthGate _health;
    private readonly IMeshRepairQueue _meshRepairs;
    private readonly RolloutChannelManager _channels;
    private readonly RolloutSuppressionRegistry _suppression;
    private readonly IRolloutAutopilot _autopilot;
    private readonly IReleaseMetadataSource _releases;
    private readonly IAlertEventBus _eventBus;
    private readonly SiteTunnelRouting? _tunnelRouting;
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
    private bool _resumeGapCharged;

    // This pass's copy of the plan's visibility record, so the deadline helpers do not each have to
    // be handed the document.
    private RolloutVisibility _visibility = new();

    /// <param name="repositories">Site-pinned rollout repository access.</param>
    /// <param name="commands">Firmware command surface.</param>
    /// <param name="observer">Live device state.</param>
    /// <param name="litmus">Post-upgrade checks.</param>
    /// <param name="health">Start-time health gate.</param>
    /// <param name="meshRepairs">Background mesh re-pair queue.</param>
    /// <param name="channels">Console channel set and restore.</param>
    /// <param name="suppression">Standard-alert suppression windows.</param>
    /// <param name="autopilot">Unattended plan builder, driven by the registry's tick.</param>
    /// <param name="releases">Changelog links for the post-soak report.</param>
    /// <param name="eventBus">Site-stamped alert bus.</param>
    /// <param name="time">Clock.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="siteSlug">Site this instance executes for.</param>
    /// <param name="tunnelRouting">
    /// Agent tunnel state, where the app has it. Null falls back to console silence alone as the
    /// blindness signal, which is what a build without the routing service can tell.
    /// </param>
    public FirmwareRolloutOrchestrator(
        IFirmwareRolloutRepositoryAccessor repositories,
        IFirmwareCommandClient commands,
        IRolloutDeviceObserver observer,
        IRolloutLitmusService litmus,
        IRolloutHealthGate health,
        IMeshRepairQueue meshRepairs,
        RolloutChannelManager channels,
        RolloutSuppressionRegistry suppression,
        IRolloutAutopilot autopilot,
        IReleaseMetadataSource releases,
        IAlertEventBus eventBus,
        TimeProvider time,
        ILogger<FirmwareRolloutOrchestrator> logger,
        string siteSlug = SiteManagementService.DefaultSiteSlug,
        SiteTunnelRouting? tunnelRouting = null)
    {
        _tunnelRouting = tunnelRouting;
        _repositories = repositories;
        _commands = commands;
        _observer = observer;
        _litmus = litmus;
        _health = health;
        _meshRepairs = meshRepairs;
        _channels = channels;
        _suppression = suppression;
        _autopilot = autopilot;
        _releases = releases;
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
                await WatchRollbacksAsync(plan, soaking, cancellationToken);
                var soakDoc = ParseDocument(plan);
                await RunDueResourceComparisonsAsync(soaking, plan, soakDoc, cancellationToken);
                await BuildSoakReportIfDueAsync(plan, soaking, cancellationToken);
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
    /// Gives autopilot its hourly chance to build the next unattended rollout. The executor still
    /// only runs whatever plan exists; this is the one place that decides one should.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task CreateAutopilotPlanIfDueAsync(CancellationToken cancellationToken = default) =>
        _autopilot.CreatePlanIfDueAsync(cancellationToken);

    /// <summary>Holds a running rollout. In-flight devices are still watched to the end of their cycle.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task PauseAsync(CancellationToken cancellationToken = default)
    {
        // The tick lock keeps this off a pass in flight, whose stale plan copy would otherwise be
        // persisted over the status change. Same for every mutator below.
        await _tickLock.WaitAsync(cancellationToken);
        try
        {
            var plan = await _repositories.UseAsync((r, c) => r.GetActivePlanAsync(c), cancellationToken);
            if (plan is not { Status: FirmwareRolloutStatus.Running }) return;

            plan.Status = FirmwareRolloutStatus.Paused;
            await PersistPlanAsync(plan, cancellationToken);
            _logger.LogInformation("Firmware rollout {Id} on site {Site} paused", plan.Id, _siteSlug);
        }
        finally
        {
            _tickLock.Release();
        }
    }

    /// <summary>
    /// Releases a hold. When the plan was paused at a wave boundary for approval, this is the
    /// approval: the waiting wave is recorded as approved so the boundary is not hit again.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        await _tickLock.WaitAsync(cancellationToken);
        try
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
        finally
        {
            _tickLock.Release();
        }
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
        await _tickLock.WaitAsync(cancellationToken);
        try
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
        finally
        {
            _tickLock.Release();
        }
    }

    /// <summary>
    /// Stops a rollout for good, puts the console channels back, and drops every device that had
    /// not started. Devices already mid-cycle are left to finish - nothing can call them back.
    /// </summary>
    /// <param name="reason">Why it was stopped, recorded on the dropped steps.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task AbortAsync(string reason, CancellationToken cancellationToken = default)
    {
        await _tickLock.WaitAsync(cancellationToken);
        try
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

            // Only clear suppression for steps that were dropped. Devices mid-cycle are left to
            // finish, and their suppression window must stay open until the tick loop sees them
            // settle (no active plan → ClearSite in the normal sweep).
            var inFlight = steps.Where(s => !IsSettled(s)).Select(s => s.DeviceMac).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (inFlight.Count == 0)
                _suppression.ClearSite(_siteSlug);
            else
                foreach (var step in steps.Where(s => IsSettled(s)))
                    _suppression.Clear(_siteSlug, step.DeviceMac);

            _logger.LogWarning("Firmware rollout {Id} on site {Site} aborted: {Reason}", plan.Id, _siteSlug, reason);
        }
        finally
        {
            _tickLock.Release();
        }
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
        await _tickLock.WaitAsync(cancellationToken);
        try
        {
            return await RollbackStepCoreAsync(stepId, cancellationToken);
        }
        finally
        {
            _tickLock.Release();
        }
    }

    private async Task<bool> RollbackStepCoreAsync(int stepId, CancellationToken cancellationToken)
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
        // The SSH path is already spent: a device that never acts must fail after the grace window,
        // not be "escalated" to the catalog URL - which carries the NEW build and would undo this.
        _escalatedAt[step.Id] = Now;
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

    /// <summary>
    /// One start at a time per plan: the scheduled-start check reads the status and only writes it
    /// several awaits later, so two callers can both pass it and double-run the pre-flight. Keyed
    /// by site and plan so it holds even where more than one executor resolves to the same site.
    /// </summary>
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> StartGates = new();

    private async Task<bool> BeginAsync(FirmwareRolloutPlan plan, bool overrideHealthGate, CancellationToken cancellationToken)
    {
        var gate = StartGates.GetOrAdd($"{_siteSlug}:{plan.Id}", _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            // Re-read inside the gate: whoever held it first may have started this already.
            var current = await _repositories.UseAsync((r, c) => r.GetPlanAsync(plan.Id, c), cancellationToken);
            if (current is null or { Status: not (FirmwareRolloutStatus.Scheduled or FirmwareRolloutStatus.Announced or FirmwareRolloutStatus.Draft) })
                return current is { Status: FirmwareRolloutStatus.Running };

            return await BeginCoreAsync(current, overrideHealthGate, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<bool> BeginCoreAsync(FirmwareRolloutPlan plan, bool overrideHealthGate, CancellationToken cancellationToken)
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
        var document = ParseDocument(plan);

        if (document.IncludesUniFiNetworkUpdate || document.IncludesUniFiOsUpdate)
            await RunPreFlightBackupAsync(plan, document, cancellationToken);
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

        await ApplyNetworkAppChannelAsync(plan, document, settings, cancellationToken);
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
            $"Upgrading {RolloutScopeCopy.Scope(document, upgrading, document.Waves.Count)}.",
            null, null, cancellationToken);

        _logger.LogInformation("Firmware rollout {Id} started on site {Site}", plan.Id, _siteSlug);
        return true;
    }

    private async Task RunPreFlightBackupAsync(
        FirmwareRolloutPlan plan, RolloutPlanDocument document, CancellationToken cancellationToken)
    {
        var console = await _commands.GetConsoleSystemInfoAsync(cancellationToken);
        if (!RolloutPlanComposer.ConsoleReachable(console))
        {
            await AddBackupNoteAsync(plan, document,
                "No console backup was taken: the console API is not reachable (API-key connection).",
                cancellationToken);
            return;
        }

        var backup = await _commands.TriggerBackupAsync(cancellationToken);
        if (backup.IsOk)
        {
            _logger.LogInformation("Pre-flight console backup succeeded on site {Site}", _siteSlug);
            return;
        }

        _logger.LogWarning(
            "Pre-flight console backup failed on site {Site}: {Reason} - proceeding anyway",
            _siteSlug, backup.Message);
        // UniFi backs itself up before applying an update through its own API, whatever the account
        // is, so our backup failing is not the exposure the old wording implied. It still matters on
        // the SSH path, which is why the unreachable-console note above is left as it is.
        // The reason is left out on purpose: in practice it is always the account, and the console's
        // own wording for it ("did not answer the backup request") reads as a timeout instead.
        await AddBackupNoteAsync(plan, document,
            "Our Console backup didn't run, usually due to the service account role or permissions. "
            + "UniFi takes its own before applying an update through its API, so you do have a backup "
            + "available if you need it.",
            cancellationToken);
    }

    private async Task AddBackupNoteAsync(
        FirmwareRolloutPlan plan, RolloutPlanDocument document, string note, CancellationToken cancellationToken)
    {
        if (!document.Notes.Contains(note))
        {
            document.Notes.Add(note);
            plan.PlanJson = JsonSerializer.Serialize(document);
            await PersistPlanAsync(plan, cancellationToken);
        }
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
        // No early-out on zero steps: a console-only plan (every device current, only the UniFi
        // Network or UniFi OS update in scope) has none and must still advance to completion.
        var steps = await _repositories.UseAsync((r, c) => r.GetStepsAsync(plan.Id, c), cancellationToken);

        var observations = await _observer.ObserveAsync(cancellationToken);
        var byMac = observations.ToDictionary(o => o.Mac, StringComparer.OrdinalIgnoreCase);
        var consoleDark = observations.Count == 0;

        // Before anything is judged: a deadline must never count time this pass could not watch.
        // A device missing from a console that answered is NOT this - that is the device's own news.
        await TrackVisibilityAsync(plan, document, consoleDark, await IsTunnelDownAsync(), cancellationToken);

        var inFlightSteps = steps.Where(IsInFlight).ToList();
        if (inFlightSteps.Count > 0 && settings.SuppressStandardAlerts)
            _suppression.RefreshSiteActive(_siteSlug, Now);

        foreach (var step in inFlightSteps)
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
        await RunDueResourceComparisonsAsync(steps, plan, document, cancellationToken);
        EnqueueDueMeshRepairs(document, steps);

        // Wave 0. The UniFi Network application update aligns the firmware catalog every device
        // step then works from, so no device is commanded until it has been through.
        if (document.NetworkAppUpdate is { Triggered: true, Settled: false } && settings.SuppressStandardAlerts)
            _suppression.RefreshConsoleCycle(_siteSlug, Now);

        var networkAppSettled = await AdvanceNetworkAppUpdateAsync(plan, document, consoleDark, cancellationToken);

        if (networkAppSettled && document.NetworkAppUpdate.Triggered)
            _suppression.ClearConsoleCycle(_siteSlug);

        if (networkAppSettled && plan.Status == FirmwareRolloutStatus.Running)
            await OpenNextWaveAsync(plan, document, settings, steps, byMac, cancellationToken);

        // The Running check holds the console's own update while the plan is paused - in-flight
        // devices are watched to the end of their cycle above, but nothing new may start.
        if (plan.Status != FirmwareRolloutStatus.Running || !steps.All(IsSettled))
            return;

        // The planner forces the gateway's channel group last, so every device step being settled
        // is exactly "the gateway step has settled" - and is also the right point on a plan that
        // has no gateway step at all.
        if (document.UniFiOsUpdate is { Triggered: true, Settled: false } && settings.SuppressStandardAlerts)
            _suppression.RefreshConsoleCycle(_siteSlug, Now);

        if (!await AdvanceUniFiOsUpdateAsync(plan, document, steps, cancellationToken))
            return;

        _suppression.ClearConsoleCycle(_siteSlug);

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

        // The console only knows what its channel offers once it has looked, so the check comes
        // first; updateAvailable is then the answer, and its absence means nothing to install.
        await _commands.CheckForApplicationUpdatesAsync(cancellationToken);
        var application = (await _commands.GetConsoleSystemInfoAsync(cancellationToken))?.NetworkApplication;

        var apiPathAvailable = application is not { HasUpdate: false }
            && !string.IsNullOrWhiteSpace(application?.Version)
            && NetworkOptimizer.Core.Helpers.FirmwareVersionFormat.IsNewer(application.UpdateAvailable, application.Version);

        if (apiPathAvailable)
        {
            state.TargetVersion = application!.UpdateAvailable;

            if (await _commands.TriggerNetworkApplicationUpdateAsync(cancellationToken))
            {
                state.Triggered = true;
                state.TriggeredAt = Now;
                _logger.LogInformation("Installing the UniFi Network application update on site {Site}", _siteSlug);
                return;
            }

            _logger.LogWarning("API trigger refused the Network app update on site {Site}", _siteSlug);
        }
        else
        {
            _logger.LogInformation(
                "The console on site {Site} does not see a Network app update (installed {Version}); "
                + "checking the SSH fallback", _siteSlug, application?.Version ?? "unknown");
        }

        // The console may not see an update because the channel switch failed, but the plan
        // captured the URL at planning time when the channel was still right.
        var installedApp = application?.Version;
        var plannedApp = state.TargetVersion;
        if (!string.IsNullOrWhiteSpace(state.Url)
            && !string.IsNullOrWhiteSpace(plannedApp)
            && !string.IsNullOrWhiteSpace(installedApp)
            && NetworkOptimizer.Core.Helpers.FirmwareVersionFormat.IsNewer(plannedApp, installedApp))
        {
            _logger.LogInformation(
                "Falling back to SSH for the Network app update on site {Site} ({Url})", _siteSlug, state.Url);
            var ssh = await _commands.TriggerSshNetworkAppUpdateAsync(state.Url, cancellationToken);
            if (ssh.IsOk)
            {
                state.Triggered = true;
                state.TriggeredAt = Now;
                _logger.LogInformation("SSH Network app update accepted on site {Site}", _siteSlug);
                return;
            }
            _logger.LogWarning(
                "SSH Network app update also failed on site {Site}: {Reason}", _siteSlug, ssh.Message);
        }

        state.Settled = true;
        state.Outcome = "nothing-to-update";
        _logger.LogInformation(
            "No UniFi Network application update to install on site {Site}; going straight to the devices", _siteSlug);
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

        if (ElapsedReachable(triggeredAt) < NetworkAppUpdateBudget)
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
            if (state.WentDownAt == null)
            {
                state.WentDownAt = Now;
                await PersistDocumentAsync(plan, document, cancellationToken);
            }

            if (ElapsedReachable(triggeredAt) < UniFiOsUpdateBudget)
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

        // The installed version is the definitive answer. Check it before progress state,
        // which can report stale "started"/"updating" after the install has already finished.
        var installed = info.InstalledOsVersion;

        // Downtime is observed, not judged, so it is recorded outside the judge delay below.
        // Answering on the old version means the reboot is still ahead: a null we saw earlier was
        // a blip during the download, not the console going away, so the clock restarts.
        var onTarget = installed != null && OsVersionMatches(installed, state.TargetVersion);
        if (!onTarget && state.WentDownAt != null && state.BackAt == null)
        {
            state.WentDownAt = null;
            await PersistDocumentAsync(plan, document, cancellationToken);
        }
        else if (onTarget && state.WentDownAt != null && state.BackAt == null)
        {
            state.BackAt = Now;
            await PersistDocumentAsync(plan, document, cancellationToken);
        }

        if (installed != null && ElapsedReachable(triggeredAt) >= UniFiOsJudgeDelay)
        {
            if (OsVersionMatches(installed, state.TargetVersion))
            {
                await SettleUniFiOsAsync(plan, document, "updated", cancellationToken);
                _logger.LogInformation(
                    "The console on site {Site} is back on UniFi OS {Version}", _siteSlug, installed);
                return true;
            }

            if (!UniFiOsUpdateInProgress(info))
            {
                await SettleUniFiOsAsync(plan, document, "unchanged", cancellationToken);
                _logger.LogError(
                    "The console on site {Site} came back on UniFi OS {Installed}, not {Target}, so the update did not take",
                    _siteSlug, installed, state.TargetVersion);
                return true;
            }
        }

        if (ElapsedReachable(triggeredAt) < UniFiOsUpdateBudget)
            return false;

        await SettleUniFiOsAsync(plan, document, installed != null && OsVersionMatches(installed, state.TargetVersion) ? "updated" : "stuck", cancellationToken);
        if (installed == null || !OsVersionMatches(installed, state.TargetVersion))
        {
            await PublishAsync(
                RolloutAlerts.DeviceStuckOffline,
                AlertSeverity.Critical,
                $"Console Stuck Updating UniFi OS{_siteSuffix}",
                $"The UniFi OS {state.TargetVersion ?? "update"} install has been running for {UniFiOsUpdateBudget.TotalMinutes:0} minutes without finishing.",
                steps.FirstOrDefault(IsGatewayStep)?.DeviceMac,
                steps.FirstOrDefault(IsGatewayStep)?.DeviceName,
                cancellationToken);
        }
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
        var console = await _commands.GetConsoleSystemInfoAsync(cancellationToken);
        if (console?.IsStandaloneConsole == true)
        {
            _logger.LogWarning(
                "Refusing the UniFi OS update on site {Site}: the console is a self-hosted UniFi OS Server", _siteSlug);
            await SettleUniFiOsAsync(plan, document, "refused", cancellationToken);
            return true;
        }

        // The channel decides which build is on offer, so it goes on before the offer is read.
        await ApplyUniFiOsChannelAsync(plan, document, console, cancellationToken);

        var pending = await _commands.GetPendingUniFiOsUpdateAsync(cancellationToken);
        var installedOs = (await _commands.GetConsoleSystemInfoAsync(cancellationToken))?.InstalledOsVersion;

        var apiPathAvailable = pending?.Version != null
            && !string.IsNullOrWhiteSpace(installedOs)
            && NetworkOptimizer.Core.Helpers.FirmwareVersionFormat.IsNewer(pending.Version, installedOs);

        // Capture the gateway's pre-update stats for the report.
        if (!string.IsNullOrWhiteSpace(document.ConsoleMac) && document.UniFiOsUpdate.PreStatsJson == null)
        {
            var settings = await _repositories.UseAsync((r, c) => r.GetSettingsAsync(c), cancellationToken);
            var preWindow = ResourceWindowFor(settings);
            document.UniFiOsUpdate.PreStatsJson = JsonSerializer.Serialize(
                await _litmus.CaptureStatsAsync(document.ConsoleMac, Now - preWindow, Now, cancellationToken));
        }

        if (apiPathAvailable)
        {
            document.UniFiOsUpdate.TargetVersion = pending!.Version;

            if (await _commands.TriggerUniFiOsUpdateAsync(cancellationToken))
            {
                document.UniFiOsUpdate.Triggered = true;
                document.UniFiOsUpdate.TriggeredAt = Now;
                await PersistDocumentAsync(plan, document, cancellationToken);
                _logger.LogInformation(
                    "Installing UniFi OS {Version} on the console for site {Site}; expect it to go dark",
                    pending.Version, _siteSlug);
                return false;
            }

            _logger.LogWarning("API trigger refused the UniFi OS update on site {Site}", _siteSlug);
        }
        else
        {
            _logger.LogInformation(
                "The console on site {Site} does not see a UniFi OS update (installed {Installed}, pending {Pending}); "
                + "checking the SSH fallback",
                _siteSlug, installedOs ?? "unknown", pending?.Version ?? "none");
        }

        // The console may not see the build because the channel switch failed, but the plan
        // captured the firmware URL at planning time when the channel was still right.
        var plannedOs = document.UniFiOsUpdate.TargetVersion;
        if (!string.IsNullOrWhiteSpace(document.UniFiOsUpdate.Url)
            && !string.IsNullOrWhiteSpace(plannedOs)
            && !string.IsNullOrWhiteSpace(installedOs)
            && NetworkOptimizer.Core.Helpers.FirmwareVersionFormat.IsNewer(plannedOs, installedOs))
        {
            _logger.LogInformation(
                "Falling back to SSH for the UniFi OS update on site {Site} ({Url})", _siteSlug, document.UniFiOsUpdate.Url);
            var ssh = await _commands.TriggerSshUniFiOsUpdateAsync(document.UniFiOsUpdate.Url, cancellationToken);
            if (ssh.IsOk)
            {
                document.UniFiOsUpdate.Triggered = true;
                document.UniFiOsUpdate.TriggeredAt = Now;
                await PersistDocumentAsync(plan, document, cancellationToken);
                _logger.LogInformation(
                    "SSH UniFi OS update accepted on site {Site}; expect it to go dark", _siteSlug);
                return false;
            }
            _logger.LogWarning(
                "SSH UniFi OS update also failed on site {Site}: {Reason}", _siteSlug, ssh.Message);
        }

        var outcome = apiPathAvailable ? "refused" : "nothing-to-update";
        await SettleUniFiOsAsync(plan, document, outcome, cancellationToken);
        return true;
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
            await MarkBackOnlineAsync(document, steps, step, observation, cancellationToken);
            return;
        }

        var commandedAt = step.CommandedAt ?? Now;
        if (_escalatedAt.TryGetValue(step.Id, out var escalatedAt))
        {
            if (ElapsedObserved(escalatedAt) >= CommandGraceWindow)
            {
                await FailStepAsync(document, steps, step,
                    "The device never started the upgrade, over the console or over SSH.", cancellationToken);
            }
            return;
        }

        if (ElapsedObserved(commandedAt) < CommandGraceWindow)
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
            await MarkBackOnlineAsync(document, steps, step, observation, cancellationToken);
            return;
        }

        var wentDownAt = step.WentDownAt ?? step.CommandedAt ?? Now;
        var budget = TimeSpan.FromSeconds(OfflineBudgetSecondsFor(document, step));
        // A quiet console is this device's own doing when the device IS the console, so that time
        // counts against it; a gateway that never comes back must still reach Critical rather than
        // hiding behind a visibility warning. Vantage blindness is never charged to any device.
        var elapsed = IsGatewayStep(step) ? ElapsedReachable(wentDownAt) : ElapsedObserved(wentDownAt);
        if (elapsed < budget)
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
        var cooldown = IsGatewayStep(step) ? GatewayCoolDown : CoolDown;
        if (ElapsedObserved(backAt) < cooldown)
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
        RolloutPlanDocument document,
        List<FirmwareRolloutStep> steps,
        FirmwareRolloutStep step,
        RolloutDeviceObservation observation,
        CancellationToken cancellationToken)
    {
        step.BackAt = Now;
        if (step.WentDownAt is DateTime down)
            step.DowntimeSeconds = (int)Math.Max(0, (Now - down).TotalSeconds);

        // rc:ok and a full offline/online cycle both lie. The reported version is the only proof.
        // Through FailStepAsync, not inline: a device that came back on the old firmware is the
        // clearest evidence the build is bad, so its model must stop here like any other failure.
        if (!string.IsNullOrWhiteSpace(step.ToVersion) && !VersionsMatch(observation.Firmware, step.ToVersion))
        {
            _logger.LogError(
                "{Device} on site {Site} rebooted but is still on {Version}, not {Target}",
                step.DeviceName, _siteSlug, observation.Firmware, step.ToVersion);
            await FailStepAsync(
                document, steps, step,
                $"The device cycled but came back on {observation.Firmware ?? "an unknown version"}, not {step.ToVersion}.",
                cancellationToken);
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

    private async Task RunDueResourceComparisonsAsync(List<FirmwareRolloutStep> steps,
        FirmwareRolloutPlan? plan, RolloutPlanDocument? document, CancellationToken cancellationToken)
    {
        var settings = await _repositories.UseAsync((r, c) => r.GetSettingsAsync(c), cancellationToken);
        var window = ResourceWindowFor(settings);

        var due = steps.Where(s =>
            s.State is FirmwareRolloutStepState.LitmusPassed
            && s.PostStatsJson == null
            && s.BackAt is DateTime back
            && Now >= back + CoolDownFor(s) + window);

        foreach (var step in due.ToList())
        {
            var from = step.BackAt!.Value + CoolDownFor(step);
            var post = await _litmus.CaptureStatsAsync(step.DeviceMac, from, from + window, cancellationToken);
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

        if (plan != null && document != null
            && document.UniFiOsUpdate is { Outcome: "updated", PostStatsJson: null }
            && !string.IsNullOrWhiteSpace(document.ConsoleMac)
            && document.UniFiOsUpdate.TriggeredAt is DateTime osTriggered
            && Now >= osTriggered + GatewayCoolDown + window)
        {
            try
            {
                var from = osTriggered + GatewayCoolDown;
                var post = await _litmus.CaptureStatsAsync(document.ConsoleMac, from, from + window, cancellationToken);
                document.UniFiOsUpdate.PostStatsJson = JsonSerializer.Serialize(post);
                await PersistDocumentAsync(plan, document, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Could not capture post-OS-update stats for site {Site}", _siteSlug);
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
            if (ElapsedObserved(waitingSince) < CatalogReflectWait)
                return;
        }

        // Historical, so the window costs nothing to widen: it is read out of what the site already
        // recorded before the command, and has to match the after-window for the two to compare.
        var preWindow = ResourceWindowFor(settings);
        step.PreStatsJson = JsonSerializer.Serialize(
            await _litmus.CaptureStatsAsync(step.DeviceMac, Now - preWindow, Now, cancellationToken));

        // Last gate before this device reboots. The plan can be hours old and the console restages
        // on its own, so what it runs NOW decides - a target that is not ahead of it is a downgrade
        // whatever the plan says, and firmware does not come back on its own.
        // TODO: a deliberate downgrade is the separate opt-in mode, as for the console.
        if (!NetworkOptimizer.Core.Helpers.FirmwareVersionFormat.IsNewer(step.ToVersion, observation.Firmware))
        {
            _logger.LogWarning(
                "Refusing to command {Device} on site {Site}: {Target} is not newer than the installed {Installed}",
                step.DeviceName, _siteSlug, step.ToVersion ?? "no target", observation.Firmware ?? "unknown");
            step.State = FirmwareRolloutStepState.SkippedExcluded;
            step.Error = "Nothing newer to install on the planned channel.";
            await PersistStepAsync(step, cancellationToken);
            return;
        }

        // The image this plan committed to, captured on this device's own channel. Only used when it
        // names the version this step is for - the catalog is matched by model code, so a
        // disagreement means the entry is not this step's build and the URL cannot be trusted.
        var image = document.TargetImages
            .FirstOrDefault(i => string.Equals(i.Mac, step.DeviceMac, StringComparison.OrdinalIgnoreCase));
        var planned = image != null
            && NetworkOptimizer.Core.Helpers.FirmwareVersionFormat.SameBuild(image.Version, step.ToVersion)
            ? image.Url
            : null;

        var result = string.IsNullOrWhiteSpace(planned)
            ? await _commands.TriggerUpgradeAsync(step.DeviceMac, cancellationToken)
            : await _commands.TriggerExternalUpgradeAsync(step.DeviceMac, planned, cancellationToken);

        // A build Ubiquiti has since pulled 404s, so the console's own catalog is still the fallback.
        if (!result.IsOk && !string.IsNullOrWhiteSpace(planned))
            result = await _commands.TriggerUpgradeAsync(step.DeviceMac, cancellationToken);

        if (!result.IsOk)
        {
            var url = planned ?? await ResolveImageUrlAsync(step.Model, cancellationToken);
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
        _commandWaitSince.TryRemove(step.Id, out _);
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
            // A console-only rollout has no device tallies to report, and printing three zeros
            // reads as a rollout that did nothing.
            (steps.Count == 0 && RolloutScopeCopy.IncludesConsole(document)
                ? ConsoleUpdateSummary(document).TrimStart()
                : $"{upgraded} device{(upgraded == 1 ? "" : "s")} upgraded, {failed} failed, {dropped} dropped."
                  + ConsoleUpdateSummary(document))
            + " The report follows after the soak.",
            null, null, cancellationToken);

        _logger.LogInformation(
            "Firmware rollout {Id} on site {Site} finished: {Upgraded} upgraded, {Failed} failed, {Dropped} dropped",
            plan.Id, _siteSlug, upgraded, failed, dropped);
    }

    // --- Report --------------------------------------------------------------------------------

    /// <summary>
    /// Keeps watching steps a rollback put back in flight during the soak. The normal step pass
    /// only runs for Running plans, so without this a mid-soak rollback would sit in Commanded
    /// forever.
    /// </summary>
    private async Task WatchRollbacksAsync(
        FirmwareRolloutPlan plan, List<FirmwareRolloutStep> steps, CancellationToken cancellationToken)
    {
        if (!steps.Any(IsInFlight)) return;

        var document = ParseDocument(plan);
        var settings = await _repositories.UseAsync((r, c) => r.GetSettingsAsync(c), cancellationToken);
        var observations = await _observer.ObserveAsync(cancellationToken);
        var byMac = observations.ToDictionary(o => o.Mac, StringComparer.OrdinalIgnoreCase);
        var consoleDark = observations.Count == 0;
        await TrackVisibilityAsync(plan, document, consoleDark, await IsTunnelDownAsync(), cancellationToken);

        foreach (var step in steps.Where(IsInFlight).ToList())
        {
            if (settings.SuppressStandardAlerts)
                _suppression.Refresh(_siteSlug, step.DeviceMac, Now);

            byMac.TryGetValue(step.DeviceMac, out var observation);
            await ProgressStepAsync(plan, document, steps, step, observation, consoleDark, cancellationToken);

            if (IsSettled(step))
                _suppression.Clear(_siteSlug, step.DeviceMac);
        }
    }

    /// <summary>
    /// Builds the post-soak report once the plan has sat in SoakWait for the site's soak window and
    /// moves it to Reported. The wait is what makes the report worth reading: every device's
    /// before/after resource window has closed by then, so nothing in it is still provisional.
    /// </summary>
    /// <summary>
    /// Whether the report must wait for the console's own before/after numbers. Bounded: a capture
    /// that never lands (no telemetry, Influx down) stops holding the report one window past due.
    /// </summary>
    private bool AwaitingGatewayPostStats(RolloutPlanDocument document, TimeSpan window)
    {
        var os = document.UniFiOsUpdate;
        if (os is not { Outcome: "updated", PostStatsJson: null } || string.IsNullOrWhiteSpace(document.ConsoleMac))
            return false;
        if (os.TriggeredAt is not DateTime triggered)
            return false;

        return Now < triggered + GatewayCoolDown + window + window;
    }

    /// <summary>
    /// Names the console row from the live device list on a plan built before the planner captured
    /// it. Cosmetic, so a console that will not answer leaves the report's generic labels in place.
    /// </summary>
    /// <returns>True when the document gained a console name and is worth persisting.</returns>
    private async Task<bool> NameConsoleFromSiteAsync(RolloutPlanDocument document, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(document.ConsoleMac) || !string.IsNullOrWhiteSpace(document.ConsoleName))
            return false;

        var observations = await _observer.ObserveAsync(cancellationToken);
        var console = observations.FirstOrDefault(o =>
            string.Equals(o.Mac, document.ConsoleMac, StringComparison.OrdinalIgnoreCase));
        if (console == null)
            return false;

        document.ConsoleName = console.Name;
        document.ConsoleModel = console.Model;
        return true;
    }

    private async Task BuildSoakReportIfDueAsync(
        FirmwareRolloutPlan plan, List<FirmwareRolloutStep> steps, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(plan.ReportJson))
            return;

        // A mid-soak rollback is still cycling; reporting now would make the plan terminal and
        // stop the tick from ever finishing that step.
        if (steps.Any(IsInFlight))
            return;

        var settings = await _repositories.UseAsync((r, c) => r.GetSettingsAsync(c), cancellationToken);

        // Due when the comparisons are in, not on a clock of its own: every upgraded device has
        // been measured over its own soak window, so there is nothing left to wait for. Waiting
        // past that only delayed a report whose numbers were already final.
        var measured = steps.Where(s => s.State is FirmwareRolloutStepState.LitmusPassed
            or FirmwareRolloutStepState.RegressionFlagged).ToList();
        if (measured.Count > 0 && measured.Any(s => s.PostStatsJson == null))
            return;

        // The console's window opens a gateway cool-down after ITS trigger, which is always after
        // the last device wave - so the device comparisons finishing is not enough to report.
        if (AwaitingGatewayPostStats(ParseDocument(plan), ResourceWindowFor(settings)))
            return;

        var completedAt = plan.CompletedAt ?? Now;
        if (measured.Count == 0 && Now - completedAt < ResourceWindowFor(settings))
            return;

        var document = ParseDocument(plan);
        if (await NameConsoleFromSiteAsync(document, cancellationToken))
            plan.PlanJson = JsonSerializer.Serialize(document);
        var changelogs = await ResolveChangelogsAsync(steps, cancellationToken);
        var report = RolloutReportBuilder.Build(plan, document, steps, Now, changelogs);

        plan.ReportJson = JsonSerializer.Serialize(report);
        plan.Status = FirmwareRolloutStatus.Reported;
        await PersistPlanAsync(plan, cancellationToken);

        var issues = report.Issues.Count switch
        {
            0 => "Nothing regressed.",
            1 => "1 thing is worth a look.",
            var count => $"{count} things are worth a look.",
        };

        var consoleOnly = steps.Count == 0 && RolloutScopeCopy.IncludesConsole(document);
        var subject = consoleOnly
            ? $"{(string.IsNullOrWhiteSpace(document.ConsoleName) ? "The console" : document.ConsoleName)} has"
            : $"{report.DevicesUpgraded} device{(report.DevicesUpgraded == 1 ? " has" : "s have")}";

        await PublishAsync(
            RolloutAlerts.ReportReady,
            AlertSeverity.Info,
            $"Firmware Rollout Report Ready{_siteSuffix}",
            $"{subject} been running {(consoleOnly || report.DevicesUpgraded == 1 ? "its" : "their")} new firmware "
            + $"for {TimeFormatHelper.Pluralize(settings.SoakHours, "hour")}. {issues} "
            + "Open Firmware Rollout for the before-and-after.",
            null, null, cancellationToken);

        _logger.LogInformation(
            "Firmware rollout {Id} on site {Site} reported: {Upgraded} upgraded, {Failed} failed, {Skipped} skipped",
            plan.Id, _siteSlug, report.DevicesUpgraded, report.DevicesFailed, report.DevicesSkipped);
    }

    /// <summary>
    /// Changelog links for the versions the plan installed, one lookup per model and version. A
    /// feed that will not answer costs the links, not the report.
    /// </summary>
    private async Task<Dictionary<string, string?>> ResolveChangelogsAsync(
        List<FirmwareRolloutStep> steps, CancellationToken cancellationToken)
    {
        var urls = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var step in steps)
        {
            if (string.IsNullOrWhiteSpace(step.Model) || string.IsNullOrWhiteSpace(step.ToVersion))
                continue;

            var key = RolloutReportBuilder.ChangelogKey(step.Model, step.ToVersion);
            if (urls.ContainsKey(key)) continue;

            try
            {
                urls[key] = (await _releases.GetAsync(step.Model, step.ToVersion, cancellationToken))?.ChangelogUrl;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "No changelog for {Model} {Version} on site {Site}",
                    step.Model, step.ToVersion, _siteSlug);
                urls[key] = null;
            }
        }

        return urls;
    }

    // --- Visibility ----------------------------------------------------------------------------

    /// <summary>
    /// Records what this pass could see and announces a spell that has gone on long enough to be
    /// worth knowing about. Runs before any deadline is judged, so time this pass could not watch
    /// is already excluded when the deadlines are read.
    /// </summary>
    private async Task TrackVisibilityAsync(
        FirmwareRolloutPlan plan,
        RolloutPlanDocument document,
        bool consoleDark,
        bool tunnelDown,
        CancellationToken cancellationToken)
    {
        var visibility = document.Visibility;
        _visibility = visibility;
        var blind = consoleDark || tunnelDown;

        // A process that was not running watched nothing, so the gap it left is ours. Charged only
        // on the first pass: a long gap later is this executor running slowly, which still had its
        // own eyes on the site either side of it.
        if (!_resumeGapCharged)
        {
            _resumeGapCharged = true;
            if (visibility.LastTickAt is DateTime last && Now - last > TickInterval)
                AddBlind(visibility, last, Now, vantage: true);
        }

        var spell = TimeSpan.Zero;
        if (blind)
        {
            // From the last pass that could see, not from now: the site went quiet somewhere in
            // between, and charging the whole gap is the direction that never blames a device.
            visibility.BlindSince ??= visibility.LastTickAt ?? Now;
            visibility.BlindIsVantage |= tunnelDown;
            spell = Now - visibility.BlindSince.Value;
        }
        else if (visibility.BlindSince is DateTime since)
        {
            spell = Now - since;
            AddBlind(visibility, since, Now, visibility.BlindIsVantage);
            visibility.BlindSince = null;
            visibility.BlindIsVantage = false;
        }

        visibility.LastTickAt = Now;

        await AnnounceVisibilityAsync(visibility, blind, spell, cancellationToken);
        await PersistDocumentAsync(plan, document, cancellationToken);
    }

    /// <summary>
    /// Says once that the site has gone out of sight, and once that it is back. Without this a
    /// rollout that has stopped counting time is indistinguishable from one that is just slow.
    /// </summary>
    private async Task AnnounceVisibilityAsync(
        RolloutVisibility visibility, bool blind, TimeSpan spell, CancellationToken cancellationToken)
    {
        if (blind)
        {
            if (visibility.LostAnnounced || spell < VisibilityLostAfter)
                return;

            visibility.LostAnnounced = true;
            _logger.LogWarning(
                "Firmware rollout on site {Site} has had no sight of the site for {Minutes:0} minutes; every deadline is holding",
                _siteSlug, spell.TotalMinutes);

            await PublishAsync(
                RolloutAlerts.VisibilityLost,
                AlertSeverity.Warning,
                $"Firmware Rollout Cannot See The Site{_siteSuffix}",
                $"Nothing has answered for {spell.TotalMinutes:0} minutes, so the rollout is holding where it is. Time we cannot watch is not counted against any device.",
                null, null, cancellationToken);
            return;
        }

        if (!visibility.LostAnnounced)
            return;

        visibility.LostAnnounced = false;
        _logger.LogInformation(
            "Firmware rollout on site {Site} can see the site again after {Minutes:0} minutes", _siteSlug, spell.TotalMinutes);

        await PublishAsync(
            RolloutAlerts.VisibilityRestored,
            AlertSeverity.Info,
            $"Firmware Rollout Can See The Site Again{_siteSuffix}",
            $"The site is answering again after {spell.TotalMinutes:0} minutes and the rollout has picked up where it left off.",
            null, null, cancellationToken);
    }

    /// <summary>
    /// Whether an agent-served site's tunnel is down. That is our own way in failing rather than
    /// anything about the site, so it never counts against a device or against the console.
    /// </summary>
    private async Task<bool> IsTunnelDownAsync()
    {
        if (_tunnelRouting == null)
            return false;

        try
        {
            return await _tunnelRouting.IsViaAgentAsync(_siteSlug) && !_tunnelRouting.IsAgentOnline(_siteSlug);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not read the agent tunnel state for site {Site}", _siteSlug);
            return false;
        }
    }

    /// <summary>
    /// Adds a blind stretch, extending the last one where they touch and dropping the oldest once
    /// the list is long enough to bloat the plan document.
    /// </summary>
    private static void AddBlind(RolloutVisibility visibility, DateTime from, DateTime to, bool vantage)
    {
        if (to <= from) return;

        var last = visibility.Blind.Count > 0 ? visibility.Blind[^1] : null;
        if (last != null && from <= last.To && vantage == last.Vantage)
        {
            if (to > last.To) last.To = to;
            return;
        }

        visibility.Blind.Add(new RolloutBlindInterval { From = from, To = to, Vantage = vantage });
        if (visibility.Blind.Count > MaxBlindIntervals)
            visibility.Blind.RemoveAt(0);
    }

    /// <summary>
    /// How much of the time since <paramref name="from"/> the rollout could see the site. Every
    /// device deadline is measured with this: a dark console, a dropped tunnel or a server that was
    /// not running says nothing about the device it would otherwise condemn.
    /// </summary>
    private TimeSpan ElapsedObserved(DateTime from) => ObservedBetween(_visibility, from, Now, vantageOnly: false);

    /// <summary>
    /// How much of that time this server could have reached the site at all - our own outages taken
    /// out, the console's silence left in.
    /// <para>
    /// The two console-level budgets use this rather than <see cref="ElapsedObserved"/> because for
    /// them a quiet console IS the measurement: an application or a UniFi OS that never comes back
    /// is exactly what they exist to catch, and excluding that time would make them unreachable.
    /// </para>
    /// </summary>
    private TimeSpan ElapsedReachable(DateTime from) => ObservedBetween(_visibility, from, Now, vantageOnly: true);

    /// <summary>Time between two points with the blind stretches taken out.</summary>
    /// <param name="visibility">The plan's visibility record.</param>
    /// <param name="from">Start of the interval.</param>
    /// <param name="to">End of the interval.</param>
    /// <param name="vantageOnly">Subtract only the stretches that were this server's own fault.</param>
    internal static TimeSpan ObservedBetween(RolloutVisibility visibility, DateTime from, DateTime to, bool vantageOnly)
    {
        if (to <= from) return TimeSpan.Zero;

        var spells = visibility.Blind
            .Where(b => !vantageOnly || b.Vantage)
            .Select(b => (b.From, b.To))
            .ToList();

        if (visibility.BlindSince is DateTime open && (!vantageOnly || visibility.BlindIsVantage))
            spells.Add((open, to));

        var blind = TimeSpan.Zero;
        var counted = from;
        foreach (var spell in spells
            .Select(s => (Start: s.From < from ? from : s.From, End: s.To > to ? to : s.To))
            .Where(s => s.End > s.Start)
            .OrderBy(s => s.Start))
        {
            // Overlapping stretches (a resume gap inside a blind spell) must not be counted twice.
            var start = spell.Start < counted ? counted : spell.Start;
            if (spell.End <= start) continue;

            blind += spell.End - start;
            counted = spell.End;
        }

        var observed = to - from - blind;
        return observed > TimeSpan.Zero ? observed : TimeSpan.Zero;
    }

    // --- Channels and resume -------------------------------------------------------------------

    private async Task EnsureChannelAsync(FirmwareRolloutPlan plan, string channel, CancellationToken cancellationToken)
    {
        if (!await _channels.NeedsChangeAsync(channel, cancellationToken))
            return;

        if (OriginalChannelSettings.Parse(plan.OriginalChannelSettingsJson)?.DeviceChannel == null)
        {
            // Persisted BEFORE the change: a crash between the two would otherwise leave the site
            // on a channel it never chose with nothing recording what to put back.
            plan.OriginalChannelSettingsJson = await _channels.CaptureAsync(
                plan.OriginalChannelSettingsJson, device: true, cancellationToken: cancellationToken);
            await PersistPlanAsync(plan, cancellationToken);
        }

        await _channels.ApplyAsync(channel, cancellationToken);
        await RefreshCatalogAsync(force: true, cancellationToken);
    }

    /// <summary>
    /// Puts the UniFi Network application on the channel this rollout wants, before the wave-0
    /// update reads what that channel is offering. A surface the rollout does not update is a
    /// surface it does not re-channel, so this only runs when the application is included.
    /// </summary>
    private async Task ApplyNetworkAppChannelAsync(
        FirmwareRolloutPlan plan,
        RolloutPlanDocument document,
        FirmwareRolloutSettings settings,
        CancellationToken cancellationToken)
    {
        if (!document.IncludesUniFiNetworkUpdate || document.ConsoleChannels.NetworkAppChannel != null)
            return;

        var channel = settings.EffectiveNetworkAppChannel;
        if (string.IsNullOrWhiteSpace(channel))
            return;

        var console = await _commands.GetConsoleSystemInfoAsync(cancellationToken);
        var current = console?.NetworkApplication?.ReleaseChannel;
        if (RolloutChannelManager.AlreadyOn(current, channel))
            return;

        if (string.IsNullOrWhiteSpace(current))
        {
            // Never change a channel that cannot be read back: without the original there is
            // nothing to restore, and the site would be left on a channel it never chose.
            _logger.LogWarning(
                "Leaving the UniFi Network application channel alone on site {Site}: the console does not report the one it is on",
                _siteSlug);
            return;
        }

        plan.OriginalChannelSettingsJson = await _channels.CaptureAsync(
            plan.OriginalChannelSettingsJson, networkApp: true, console: console, cancellationToken: cancellationToken);
        await PersistDocumentAsync(plan, document, cancellationToken);

        if (!await _channels.ApplyNetworkAppChannelAsync(channel, cancellationToken))
            return;

        document.ConsoleChannels.NetworkAppChannel = channel;
        await PersistDocumentAsync(plan, document, cancellationToken);
    }

    /// <summary>
    /// Puts the console's UniFi OS on the channel this rollout wants, before the pending build is
    /// read. Cloud Gateways only - the caller has already refused a self-hosted console.
    /// </summary>
    private async Task ApplyUniFiOsChannelAsync(
        FirmwareRolloutPlan plan,
        RolloutPlanDocument document,
        UniFiConsoleSystemInfo? console,
        CancellationToken cancellationToken)
    {
        if (document.ConsoleChannels.UniFiOsChannel != null)
            return;

        var settings = await _repositories.UseAsync((r, c) => r.GetSettingsAsync(c), cancellationToken);
        var channel = settings.EffectiveUniFiOsChannel;
        if (string.IsNullOrWhiteSpace(channel))
            return;

        var current = console?.Firmware?.ReleaseChannel;
        if (RolloutChannelManager.AlreadyOn(current, channel))
            return;

        if (string.IsNullOrWhiteSpace(current))
        {
            _logger.LogWarning(
                "Leaving the UniFi OS channel alone on site {Site}: the console does not report the one it is on", _siteSlug);
            return;
        }

        plan.OriginalChannelSettingsJson = await _channels.CaptureAsync(
            plan.OriginalChannelSettingsJson, unifiOs: true, console: console, cancellationToken: cancellationToken);
        await PersistDocumentAsync(plan, document, cancellationToken);

        if (!await _channels.ApplyUniFiOsChannelAsync(channel, console, cancellationToken))
            return;

        document.ConsoleChannels.UniFiOsChannel = channel;
        await PersistDocumentAsync(plan, document, cancellationToken);
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

        var document = ParseDocument(plan);
        var settings = await _repositories.UseAsync((r, c) => r.GetSettingsAsync(c), cancellationToken);
        var spacing = ResolvedSpacing.For(settings.SpacingProfile, settings.AdvancedSpacingJson);
        RolloutPlanner.ComputeTimeline(document, spacing);
        plan.PlanJson = JsonSerializer.Serialize(document);
        await PersistPlanAsync(plan, cancellationToken);

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

    private static TimeSpan CoolDownFor(FirmwareRolloutStep step) =>
        IsGatewayStep(step) ? GatewayCoolDown : CoolDown;

    private static bool IsCanary(RolloutPlanDocument document, string mac) =>
        document.Waves.SelectMany(w => w.Steps)
            .Any(s => string.Equals(s.Mac, mac, StringComparison.OrdinalIgnoreCase) && s.IsCanary);

    /// <summary>
    /// Version equality across the two spellings the console and the feed use. Whitespace and a
    /// leading "v" differ between sources for the same build.
    /// </summary>
    private static bool VersionsMatch(string? left, string? right) =>
        NetworkOptimizer.Core.Helpers.FirmwareVersionFormat.SameBuild(left, right);

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
