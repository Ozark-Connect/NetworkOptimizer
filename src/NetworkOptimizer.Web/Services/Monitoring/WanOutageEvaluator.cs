using System.Collections.Concurrent;
using System.Globalization;
using NetworkOptimizer.Alerts.Events;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.UniFi;

namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>
/// Turns per-target failure states into per-WAN outage alerts. The WAN-facing target categories
/// (access ISP, transit, internet, legacy WAN) are not independent - one access-layer outage
/// takes them all out at once - so instead of a pile of per-target notifications this publishes
/// one alert per WAN, classified by shape: <c>monitoring.wan_outage</c> (the connection is down),
/// <c>monitoring.wan_outage_partial</c> (part of the path beyond the access layer, while the WAN
/// still passes traffic), and <c>monitoring.wan_recovered</c> (closes either). One open alert per
/// (WAN, kind); a partial that becomes total is superseded, never stacked; when every WAN of a
/// multi-WAN site is out in the same evaluation, one site-level rollup replaces the per-WAN pile.
///
/// Fed by <see cref="MonitoringAlertEvaluator"/>, whose per-target offline state machine keeps
/// running underneath for every category (it just stops publishing per-target events for the WAN
/// categories). Evaluation is a throttled whole-site pass over the current target states, and a
/// verdict must hold across consecutive passes to open - the per-target
/// FailuresToDeclareOffline idea applied to the WAN - and to close, so a flapping WAN produces
/// one alert and one recovery. State is in memory only. At start-up the alerts still open are read
/// back (<see cref="OpenAlertSeed"/>): an outage spanning a restart keeps its one alert, and one that
/// ended during the restart is closed by the first confirmed recovery verdict rather than left open.
/// Nothing new is persisted, so the seeded state cannot outlive what the live probes say.
/// </summary>
public class WanOutageEvaluator
{
    /// <summary>AlertEvent.DeviceId of the site-level all-WANs rollup alert.</summary>
    internal const string RollupDeviceId = "all-wans";

    private const string OutageEventType = "monitoring.wan_outage";
    private const string PartialEventType = "monitoring.wan_outage_partial";
    private const string RecoveredEventType = "monitoring.wan_recovered";

    // Tuned to beat the console's own WAN-down push (~60-120 s): a target counts as failing
    // for WAN verdicts after two failed probes (each probe is multi-ping, and no WAN verdict
    // ever rests on one target - a total needs the whole cohort failing at once, which is the
    // real flap suppression), passes run at probe cadence, and two held passes open. Closing
    // stays at three so a flapping WAN still produces one alert and one recovery rather than
    // a stream. Net budget: ~25-40 s from first lost packet to alert. The per-target machine's
    // own 3-strikes threshold (Fabric/Custom alerts) is deliberately untouched.
    private const int EvaluationIntervalSeconds = 10;
    private const int ConfirmsToOpen = 2;
    private const int ConfirmsToClose = 3;
    private const int FailedProbesToCountFailing = 2;
    private const int TargetStalenessSeconds = 180;

    // A narrow partial (a few scattered destinations, no path hop) must hold this long before it
    // opens. Nothing is lost waiting: a partial that grows into a total opens on the total's clock.
    private const int NarrowPartialHoldSeconds = 180;

    /// <summary>How far apart the WANs' total-outage confirmations may sit and still read as one site outage.</summary>
    private const int RollupWindowSeconds = 90;
    private const int ContextTtlSeconds = 300;

    private readonly IAlertEventBus _eventBus;
    private readonly ILogger<WanOutageEvaluator> _logger;
    private readonly WanOutageContextSource _contextSource;
    private readonly Firmware.RolloutSuppressionRegistry? _rolloutWindows;
    private readonly TimeProvider _time;
    private readonly string _siteSlug;
    private readonly string _siteSuffix;

    private readonly ConcurrentDictionary<string, TargetLiveState> _targets = new();
    private readonly SemaphoreSlim _passGate = new(1, 1);
    private readonly Dictionary<string, WanState> _wanStates = new(StringComparer.OrdinalIgnoreCase);
    private WanOutageContext _context = WanOutageContext.Empty;
    private DateTime _contextLoadedUtc = DateTime.MinValue;
    private DateTime _lastPassUtc = DateTime.MinValue;
    private bool _rollupOpen;
    private DateTime? _rollupSince;
    // The rollup's link category. A WAN first seen under a rollup seeded at start-up reuses it for its recovery.
    private string? _rollupCategory;
    private readonly OpenAlertSeed _openAtStart;
    private OpenAlerts _open = OpenAlerts.None;
    private bool _seedApplied;
    // The rollup was open before a restart, so WANs first seen now belong to it.
    private bool _rollupFromSeed;

    /// <param name="siteSlug">
    /// Site this instance evaluates for (one instance per site, owned by
    /// <see cref="MonitoringAlertRegistry"/>, same pattern as <see cref="MonitoringAlertEvaluator"/>).
    /// </param>
    /// <param name="scopeFactory">Reads WAN outages still open before a restart, so their recovery is published.</param>
    public WanOutageEvaluator(IAlertEventBus eventBus, ILogger<WanOutageEvaluator> logger,
        WanOutageContextSource contextSource,
        string siteSlug = SiteManagementService.DefaultSiteSlug,
        Firmware.RolloutSuppressionRegistry? rolloutWindows = null,
        TimeProvider? timeProvider = null,
        IServiceScopeFactory? scopeFactory = null)
    {
        _openAtStart = new OpenAlertSeed(scopeFactory, string.IsNullOrEmpty(siteSlug) ? SiteManagementService.DefaultSiteSlug : siteSlug,
            [OutageEventType, PartialEventType], a => WanSubject(a.DeviceId), logger);
        _eventBus = eventBus;
        _logger = logger;
        _contextSource = contextSource;
        _rolloutWindows = rolloutWindows;
        _time = timeProvider ?? TimeProvider.System;
        _siteSlug = string.IsNullOrEmpty(siteSlug) ? SiteManagementService.DefaultSiteSlug : siteSlug;
        _siteSuffix = _siteSlug == SiteManagementService.DefaultSiteSlug ? "" : $" (site {_siteSlug})";
    }

    /// <summary>
    /// Whether a WAN is in an outage right now: its own outage open or confirming, the site-level
    /// rollup, or the gateway cycling through a UniFi OS update. Null names the primary. Refreshed
    /// after every evaluation pass; false until the first. Path Localization stands down while it is
    /// true - an outage is the outage detectors' to explain - but only on that WAN, so an unplugged
    /// backup never stops the primary from being traced.
    /// </summary>
    public bool IsOutageOpen(string? wanKey)
    {
        var snapshot = _outageSnapshot;
        if (snapshot.SiteWide) return true;
        var key = string.IsNullOrEmpty(wanKey) ? snapshot.PrimaryWanKey : GatewayWanHelper.WanInterfaceKeyFromKey(wanKey);
        return key != null && snapshot.Wans.Contains(key);
    }

    private sealed record OutageSnapshot(bool SiteWide, string? PrimaryWanKey, IReadOnlySet<string> Wans);

    private volatile OutageSnapshot _outageSnapshot = new(false, null, new HashSet<string>());

    /// <summary>Whether a target type is alerted per WAN here rather than per target.</summary>
    public static bool CoversTargetType(MonitoringTargetType type) => type is
        MonitoringTargetType.Wan or
        MonitoringTargetType.AccessIsp or
        MonitoringTargetType.Transit or
        MonitoringTargetType.InternetService;

    /// <summary>
    /// Records a WAN-scoped target's current per-target state machine verdict. Called by
    /// <see cref="MonitoringAlertEvaluator"/> on every probe result for a covered target,
    /// from both the local collection loop and the agent result sink.
    /// </summary>
    /// <param name="target">The target.</param>
    /// <param name="vantageId">Where the probe ran from, as the probe result names it ("server", "agent-{id}").</param>
    /// <param name="isOffline">The per-target machine's offline verdict.</param>
    /// <param name="isLossy">The per-target machine's lossy verdict.</param>
    /// <param name="consecutiveFailures">Failed probes in a row.</param>
    internal void RecordTargetState(MonitoringTarget target, string vantageId, bool isOffline, bool isLossy, int consecutiveFailures)
    {
        if (!CoversTargetType(target.TargetType)) return;
        var state = _targets.GetOrAdd(target.TargetId, _ => new TargetLiveState());
        state.Target = target;
        state.Vantage = vantageId;
        state.Offline = isOffline || consecutiveFailures >= FailedProbesToCountFailing;
        state.Lossy = isLossy;
        state.LastResultUtc = _time.GetUtcNow().UtcDateTime;
    }

    /// <summary>
    /// Runs one whole-site evaluation pass when due. Throttled to one pass per
    /// <see cref="EvaluationIntervalSeconds"/> and single-flight, so the per-probe call sites
    /// can invoke it unconditionally.
    /// </summary>
    internal async ValueTask EvaluateAsync(CancellationToken ct = default)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        if (now - _lastPassUtc < TimeSpan.FromSeconds(EvaluationIntervalSeconds)) return;
        if (!await _passGate.WaitAsync(0, ct)) return;
        try
        {
            if (now - _lastPassUtc < TimeSpan.FromSeconds(EvaluationIntervalSeconds)) return;
            _lastPassUtc = now;
            try
            {
                await RunPassAsync(now, ct);
            }
            finally
            {
                // Whatever the pass got through is the state now, even when it threw part-way.
                RefreshOutageSnapshot(now);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WAN outage evaluation pass failed for site {Site}", _siteSlug);
        }
        finally
        {
            _passGate.Release();
        }
    }

    /// <summary>Publishes what <see cref="IsOutageOpen"/> reads. Caller holds the pass gate.</summary>
    private void RefreshOutageSnapshot(DateTime now) =>
        _outageSnapshot = new OutageSnapshot(
            _rollupOpen || _rolloutWindows?.IsOsCycling(_siteSlug, now) == true,
            _context.PrimaryWanKey,
            _wanStates
                .Where(w => w.Value.OpenKind != WanVerdictKind.None || w.Value.LastVerdict?.Kind is WanVerdictKind.Total or WanVerdictKind.Partial)
                .Select(w => w.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase));

    private async Task RunPassAsync(DateTime now, CancellationToken ct)
    {
        if (!_seedApplied)
        {
            _open = await _openAtStart.OpenAsync();
            if (_open.Get(OutageEventType, RollupDeviceId) is { } rollup)
            {
                _rollupOpen = _rollupFromSeed = true;
                _rollupSince = OpenAlertSeed.ContextTime(rollup, "since");
                _rollupCategory = LinkCategory(rollup.SourceUrl);
            }
            _seedApplied = true;
        }

        // Group fresh target states by WAN. A target with no recent result says nothing about
        // the WAN: when probing stops entirely (agent disconnected, monitoring off), every
        // target goes stale and no verdict is reached - a monitoring gap is not an outage,
        // and not a recovery either.
        // A vantage a firmware rollout has cut off from the gateway (a switch on its path is
        // rebooting) says nothing either: its probes fail whatever the WAN is doing. Its targets
        // sit out the pass, and a vantage still connected keeps reporting a real outage.
        var staleness = TimeSpan.FromSeconds(TargetStalenessSeconds);
        var fresh = _targets.Values
            .Where(t => now - t.LastResultUtc <= staleness)
            .Where(t => _rolloutWindows?.IsWanDark(_siteSlug, t.Vantage, now) != true)
            .ToList();

        await RefreshContextAsync(now, fresh, ct);

        var byWan = fresh
            .GroupBy(t => MonitoringTarget.IsUnpinned(t.Target.WanInterface)
                ? _context.PrimaryWanKey
                : GatewayWanHelper.WanInterfaceKeyFromKey(t.Target.WanInterface!),
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        // Verdict and confirmation counting per WAN.
        var confirmedThisPass = new Dictionary<string, WanVerdictKind>(StringComparer.OrdinalIgnoreCase);
        foreach (var (wanKey, targets) in byWan)
        {
            var verdict = WanOutageClassifier.Classify(targets.Select(ToSnapshot).ToList());
            var state = GetWanState(wanKey);
            if (verdict.Kind == state.PendingKind) state.PendingCount++;
            else
            {
                state.PendingKind = verdict.Kind;
                state.PendingCount = 1;
                state.PendingSince = now;
            }
            if (verdict.Kind != WanVerdictKind.None && state.EpisodeStart == null)
                state.EpisodeStart = now;
            state.LastVerdict = verdict;

            var needed = verdict.Kind == WanVerdictKind.None ? ConfirmsToClose : ConfirmsToOpen;
            var held = verdict.Kind != WanVerdictKind.Partial || verdict.Broad
                || now - state.PendingSince >= TimeSpan.FromSeconds(NarrowPartialHoldSeconds);
            if (state.PendingCount >= needed && held) confirmedThisPass[wanKey] = verdict.Kind;

            // When this WAN's total outage was first confirmed, for the rollup's window. Cleared
            // the moment it is no longer verdicted total, so a recovered WAN cannot hold a stale
            // confirmation and let a later rollup claim the site was wholly down.
            if (verdict.Kind == WanVerdictKind.Total && state.PendingCount >= ConfirmsToOpen)
                state.TotalConfirmedAt ??= now;
            else if (verdict.Kind != WanVerdictKind.Total)
                state.TotalConfirmedAt = null;
        }

        // Site rollup: every WAN of a multi-WAN site down together collapses into ONE site-level
        // Critical - N notifications for one event is the spam this alert class exists to remove.
        // "Together" is a window rather than a single evaluation: WANs are polled at their own
        // intervals (10 s on one, 60 s on another is ordinary), so a site that loses everything
        // at once still confirms its WANs a minute apart, and a same-pass test would almost never
        // fire. A WAN that already opened its own alert is folded in - the rollup event resolves
        // the per-WAN alerts it supersedes - so the worst case is one alert then the rollup,
        // rather than one per WAN.
        // A UniFi OS update reboots the gateway, taking WAN with it. The outage is expected
        // and the rollout's own alerts cover it, so no WAN alert opens while it is in progress.
        // Network app updates do NOT suppress: the gateway stays up, so a WAN outage during
        // one is real. Recovery always flows so a pre-existing alert can close.
        var osCycling = _rolloutWindows?.IsOsCycling(_siteSlug, now) == true;

        var totalEverywhere = byWan.Keys.All(k => GetWanState(k).TotalConfirmedAt != null);
        if (!_rollupOpen
            && !osCycling
            && byWan.Count >= 2
            && totalEverywhere
            && now - byWan.Keys.Min(k => GetWanState(k).TotalConfirmedAt!.Value)
                <= TimeSpan.FromSeconds(RollupWindowSeconds))
        {
            _rollupOpen = true;
            _rollupSince = byWan.Keys.Select(k => GetWanState(k).EpisodeStart).Min() ?? now;
            foreach (var k in byWan.Keys)
            {
                var s = GetWanState(k);
                s.CoveredByRollup = true;
                s.OpenKind = WanVerdictKind.None;
                s.OpenCategory = s.LastVerdict?.ChartCategory;
            }
            await _eventBus.PublishAsync(BuildRollupEvent(byWan.Keys.ToList(), now), ct);
            return;
        }

        foreach (var (wanKey, kind) in confirmedThisPass)
        {
            var state = GetWanState(wanKey);
            var info = WanInfo(wanKey);
            switch (kind)
            {
                case WanVerdictKind.Total when !state.CoveredByRollup && !osCycling && state.OpenKind != WanVerdictKind.Total:
                    // Opens fresh, or supersedes an open partial: publishing the total closes
                    // the partial downstream (AlertProcessingService resolves it), so the two
                    // never stack.
                    state.OpenKind = WanVerdictKind.Total;
                    await _eventBus.PublishAsync(BuildOutageEvent(info, state, now), ct);
                    break;

                case WanVerdictKind.Partial when !state.CoveredByRollup && !osCycling && state.OpenKind == WanVerdictKind.None:
                    // A partial never downgrades an open total; the total stays open until
                    // recovery closes it.
                    state.OpenKind = WanVerdictKind.Partial;
                    await _eventBus.PublishAsync(BuildOutageEvent(info, state, now), ct);
                    break;

                case WanVerdictKind.None:
                    await HandleRecoveryAsync(wanKey, state, info, now, ct);
                    break;
            }
        }
    }

    /// <summary>
    /// Recovery confirmed for one WAN. Under an open rollup the first recovery closes the rollup
    /// (its "every WAN is out" premise is gone) and any still-out WAN opens its own alert, so
    /// the picture goes back to per-WAN as soon as the WANs differ again.
    /// </summary>
    private async Task HandleRecoveryAsync(string wanKey, WanState state, WanOutageWanInfo info,
        DateTime now, CancellationToken ct)
    {
        if (state.CoveredByRollup && _rollupOpen)
        {
            _rollupOpen = _rollupFromSeed = false;
            state.CoveredByRollup = false;
            await _eventBus.PublishAsync(BuildRecoveredEvent(info, state, now), ct);
            state.EpisodeStart = null;
            foreach (var (otherKey, other) in _wanStates)
            {
                if (!other.CoveredByRollup) continue;
                other.CoveredByRollup = false;
                var otherKind = other.LastVerdict?.Kind ?? WanVerdictKind.None;
                if (otherKind == WanVerdictKind.None)
                {
                    // Recovering alongside this WAN (possibly in the very same pass): the
                    // rollup's close already says the site is back, so nothing reopens and
                    // its own None-confirmation has nothing left to announce.
                    other.OpenKind = WanVerdictKind.None;
                    other.EpisodeStart = null;
                    continue;
                }
                other.OpenKind = otherKind;
                await _eventBus.PublishAsync(BuildOutageEvent(WanInfo(otherKey), other, now), ct);
            }
            _rollupSince = null;
            return;
        }

        if (state.OpenKind != WanVerdictKind.None)
        {
            state.OpenKind = WanVerdictKind.None;
            await _eventBus.PublishAsync(BuildRecoveredEvent(info, state, now), ct);
        }
        state.EpisodeStart = null;
    }

    private async Task RefreshContextAsync(DateTime now, List<TargetLiveState> fresh, CancellationToken ct)
    {
        if (now - _contextLoadedUtc < TimeSpan.FromSeconds(ContextTtlSeconds)) return;
        try
        {
            var wanKeys = fresh
                .Select(t => t.Target.WanInterface)
                .Where(w => !string.IsNullOrEmpty(w))
                .Select(w => w!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            _context = await _contextSource.LoadAsync(_siteSlug, wanKeys, ct);
            _contextLoadedUtc = now;
        }
        catch (Exception ex)
        {
            // Keep the previous snapshot: classification still works from the targets alone,
            // it just loses trace-map attribution until the next successful load.
            _contextLoadedUtc = now;
            _logger.LogDebug(ex, "WAN outage context load failed for site {Site}; keeping previous snapshot", _siteSlug);
        }
    }

    private WanTargetSnapshot ToSnapshot(TargetLiveState t)
    {
        var hop = _context.HopsByTargetId.TryGetValue(t.Target.TargetId, out var h)
            ? h
            : new WanOutageHopInfo(int.MaxValue, new HashSet<string>());
        return new WanTargetSnapshot(
            t.Target.TargetId,
            t.Target.TargetType,
            t.Target.Name,
            t.Target.Address,
            Failing: t.Offline,
            Degraded: t.Offline || t.Lossy,
            Depth: hop.Depth,
            KnownPosition: hop.Depth != int.MaxValue,
            IsInternet: t.Target.TargetType is MonitoringTargetType.InternetService or MonitoringTargetType.Wan,
            AsnLabel: string.IsNullOrEmpty(t.Target.AsnName) ? null : t.Target.AsnName,
            AsnNumber: t.Target.AsnNumber ?? 0,
            AncestorIps: hop.AncestorIps);
    }

    private WanState GetWanState(string wanKey)
    {
        if (!_wanStates.TryGetValue(wanKey, out var state))
            _wanStates[wanKey] = state = NewWanState(wanKey);
        return state;
    }

    /// <summary>A WAN first seen by this process, carrying any outage open on it at start-up.</summary>
    private WanState NewWanState(string wanKey)
    {
        var covered = _rollupFromSeed && _rollupOpen;
        var state = new WanState
        {
            CoveredByRollup = covered,
            EpisodeStart = covered ? _rollupSince : null,
            OpenCategory = covered ? _rollupCategory : null
        };
        var key = WanSubject(wanKey);
        if (_open.Take(OutageEventType, key) is { } total)
        {
            state.OpenKind = WanVerdictKind.Total;
            state.EpisodeStart = OpenAlertSeed.ContextTime(total, "since");
            state.OpenCategory = LinkCategory(total.SourceUrl);
        }
        else if (_open.Take(PartialEventType, key) is { } partial)
        {
            state.OpenKind = WanVerdictKind.Partial;
            state.EpisodeStart = OpenAlertSeed.ContextTime(partial, "since");
            state.OpenCategory = LinkCategory(partial.SourceUrl);
        }
        return state;
    }

    // WAN keys are compared case-insensitively everywhere else in this class.
    private static string? WanSubject(string? wanKey) =>
        string.IsNullOrWhiteSpace(wanKey) ? null : wanKey.Trim().ToLowerInvariant();

    private WanOutageWanInfo WanInfo(string wanKey) =>
        _context.Wans.TryGetValue(wanKey, out var info)
            ? info
            : new WanOutageWanInfo(wanKey,
                GatewayWanHelper.FormatWanLabel(null, GatewayWanHelper.WanIndexFromKey(wanKey), null, null),
                TreatAsPrimary: true, CarriesTraffic: true, ConsoleUp: null);

    private AlertEvent BuildOutageEvent(WanOutageWanInfo info, WanState state, DateTime now)
    {
        var verdict = state.LastVerdict!;
        var total = verdict.Kind == WanVerdictKind.Total;
        var duration = Humanize(now - (state.EpisodeStart ?? now));
        // Kept for the recovery, which links to the same view as the outage it closes.
        state.OpenCategory = verdict.ChartCategory;
        var breakAt = total
            ? verdict.LastReachableHop ?? verdict.BrokenNetwork
            : verdict.BranchLabel;

        string message;
        if (total && verdict.AccessDown && info.ConsoleUp == false)
            message = $"The console reports the {info.Label} link down. All {verdict.TotalCount} monitored targets on it have been failing for {duration}.";
        else if (total && verdict.AccessDown)
            message = $"All {verdict.TotalCount} monitored targets on {info.Label} have been failing for {duration}, including your ISP's first hop. This looks like the connection itself.";
        else if (total && verdict.LastReachableHop == null && verdict.BrokenNetwork == null)
            message = $"All {verdict.TotalCount} monitored targets on {info.Label} have been failing for {duration}. This looks like the connection itself.";
        else if (total)
        {
            message = $"Your ISP's first hop on {info.Label} still answers, but the {verdict.FailingCount} targets beyond it have been failing for {duration}.";
            if (verdict.LastReachableHop != null)
                message += $" The path is fine up to {verdict.LastReachableHop}; past that, nothing responds.";
            else if (verdict.BrokenNetwork != null)
                message += $" The break looks like it sits in {verdict.BrokenNetwork}.";
        }
        // No reassurance about the connection itself: a partial is often a total still arriving,
        // with the rest of the targets a probe cycle behind. State the evidence, not a verdict
        // on the WAN that the next evaluation may overturn.
        else if (verdict.BranchLabel != null)
            message = $"{verdict.FailingCount} of {verdict.TotalCount} monitored targets on {info.Label} have been failing or degraded for {duration}, all behind {verdict.BranchLabel}. Other destinations are still reachable.";
        else
            message = $"{verdict.FailingCount} of {verdict.TotalCount} monitored targets on {info.Label} have been failing or degraded for {duration}, across unrelated networks.";

        return new AlertEvent
        {
            EventType = total ? OutageEventType : PartialEventType,
            Source = "monitoring",
            // Severity tracks user impact, not which link failed: a WAN that is carrying traffic
            // (the primary, or any WAN on a load-balancing site) taking a total outage is a real
            // service loss, while an idle failover backup dropping costs redundancy only.
            Severity = total
                ? info.CarriesTraffic ? AlertSeverity.Critical : AlertSeverity.Warning
                : info.CarriesTraffic ? AlertSeverity.Warning : AlertSeverity.Info,
            Title = total
                ? $"Internet down on {info.Label}{_siteSuffix}"
                : $"Partial internet outage on {info.Label}{_siteSuffix}",
            Message = message,
            DeviceId = info.WanKey,
            DeviceName = info.Label,
            // Opens on this WAN's own chart at the moment the outage started, rather than the
            // tab's default view of now: by the time anyone follows the link the window that
            // shows what happened has usually scrolled off the live view.
            SourceUrl = WanSourceUrl(info.WanKey, verdict.ChartCategory, state.EpisodeStart ?? now),
            Tags = ["monitoring", "wan-outage"],
            Context = BuildContext(info, verdict, state.EpisodeStart, breakAt)
        };
    }

    private AlertEvent BuildRecoveredEvent(WanOutageWanInfo info, WanState state, DateTime now)
    {
        var duration = Humanize(now - (state.EpisodeStart ?? now));
        return new AlertEvent
        {
            EventType = RecoveredEventType,
            Source = "monitoring",
            Severity = AlertSeverity.Info,
            Title = $"{info.Label} is back{_siteSuffix}",
            Message = $"Targets on {info.Label} are answering again. The outage lasted {duration}.",
            DeviceId = info.WanKey,
            DeviceName = info.Label,
            // Parked at the START of the outage, not the recovery: what someone following a
            // recovery wants to see is the episode that just ended.
            SourceUrl = WanSourceUrl(info.WanKey, state.OpenCategory ?? MonitoringLinks.AccessIspCategory,
                state.EpisodeStart ?? now),
            Tags = ["monitoring", "wan-outage"],
            Context = new Dictionary<string, string>
            {
                ["wan"] = info.WanKey,
                ["wan_label"] = info.Label,
                ["verdict"] = "recovered",
                ["since"] = (state.EpisodeStart ?? now).ToString("o", CultureInfo.InvariantCulture)
            }
        };
    }

    private AlertEvent BuildRollupEvent(IReadOnlyList<string> wanKeys, DateTime now)
    {
        var labels = wanKeys.Select(k => WanInfo(k).Label).ToList();
        var failing = wanKeys.Sum(k => GetWanState(k).LastVerdict?.FailingCount ?? 0);
        var totalTargets = wanKeys.Sum(k => GetWanState(k).LastVerdict?.TotalCount ?? 0);
        var duration = Humanize(now - (_rollupSince ?? now));
        _rollupCategory = WanOutageClassifier.ShallowestCategory(
            wanKeys.Select(k => GetWanState(k).LastVerdict?.ChartCategory));
        return new AlertEvent
        {
            EventType = OutageEventType,
            Source = "monitoring",
            Severity = AlertSeverity.Critical,
            Title = $"Internet down on all WANs{_siteSuffix}",
            Message = $"Every WAN ({string.Join(", ", labels)}) has been failing all of its monitored targets for {duration}. The site looks offline.",
            DeviceId = RollupDeviceId,
            DeviceName = "All WANs",
            // Every WAN is out, so this one spans them all rather than naming one.
            SourceUrl = $"/monitoring?tab=performance&category={_rollupCategory}&at={new DateTimeOffset(DateTime.SpecifyKind(_rollupSince ?? now, DateTimeKind.Utc)).ToUnixTimeMilliseconds()}&wan={Services.Monitoring.LiveWanScope.AllWansToken}",
            Tags = ["monitoring", "wan-outage"],
            Context = new Dictionary<string, string>
            {
                ["verdict"] = "all_wans_down",
                ["targets_failing"] = failing.ToString(CultureInfo.InvariantCulture),
                ["targets_total"] = totalTargets.ToString(CultureInfo.InvariantCulture),
                ["since"] = (_rollupSince ?? now).ToString("o", CultureInfo.InvariantCulture)
            }
        };
    }

    private Dictionary<string, string> BuildContext(WanOutageWanInfo info, WanVerdict verdict,
        DateTime? since, string? breakAt)
    {
        var context = new Dictionary<string, string>
        {
            ["wan"] = info.WanKey,
            ["wan_label"] = info.Label,
            ["verdict"] = verdict.Kind == WanVerdictKind.Total
                ? verdict.AccessDown ? "access_down" : "upstream"
                : verdict.BranchLabel != null ? "partial_branch" : "partial_independent",
            ["targets_failing"] = verdict.FailingCount.ToString(CultureInfo.InvariantCulture),
            ["targets_total"] = verdict.TotalCount.ToString(CultureInfo.InvariantCulture)
        };
        if (breakAt != null) context["break_at"] = breakAt;
        if (since != null) context["since"] = since.Value.ToString("o", CultureInfo.InvariantCulture);
        return context;
    }

    /// <summary>
    /// The Network Performance chart, scoped to one WAN and parked at an instant. The analysis
    /// page reads the category, the WAN and the timestamp from the link, so following an alert
    /// lands on the evidence rather than on whatever the tab happens to show now.
    /// </summary>
    private static string WanSourceUrl(string wanKey, string category, DateTime at)
    {
        var ms = new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        return $"/monitoring?tab=performance&category={category}&at={ms}&wan={Uri.EscapeDataString(wanKey)}";
    }

    /// <summary>The <c>category</c> a stored alert's link opens on, so a restart keeps linking its recovery there.</summary>
    internal static string? LinkCategory(string? sourceUrl)
    {
        var query = sourceUrl?.Split('?', 2) is [_, var q] ? q : null;
        if (query == null) return null;
        return Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(query).TryGetValue("category", out var category)
            && !string.IsNullOrEmpty(category) ? category.ToString() : null;
    }

    private static string Humanize(TimeSpan duration)
    {
        if (duration.TotalMinutes < 1) return "under a minute";
        if (duration.TotalHours < 1)
        {
            var minutes = (int)duration.TotalMinutes;
            return minutes == 1 ? "1 minute" : $"{minutes} minutes";
        }
        var hours = (int)duration.TotalHours;
        var rest = (int)duration.Subtract(TimeSpan.FromHours(hours)).TotalMinutes;
        var hourPart = hours == 1 ? "1 hour" : $"{hours} hours";
        return rest == 0 ? hourPart : $"{hourPart} {rest} minutes";
    }

    private sealed class TargetLiveState
    {
        public MonitoringTarget Target = null!;
        public string Vantage = "server";
        public bool Offline;
        public bool Lossy;
        public DateTime LastResultUtc;
    }

    private sealed class WanState
    {
        /// <summary>Verdict kind observed on recent passes, awaiting confirmation.</summary>
        public WanVerdictKind PendingKind;

        /// <summary>Consecutive passes the pending kind has held.</summary>
        public int PendingCount;

        /// <summary>When the pending kind was first observed, for the narrow-partial hold.</summary>
        public DateTime PendingSince;

        /// <summary>When the current outage episode was first observed (pre-confirmation), for the notification body.</summary>
        public DateTime? EpisodeStart;

        /// <summary>Which alert is currently open for this WAN, so a partial is superseded rather than stacked.</summary>
        public WanVerdictKind OpenKind;

        /// <summary>The open alert's link category, which its recovery reuses.</summary>
        public string? OpenCategory;

        /// <summary>Whether this WAN's outage is represented by the site-level rollup alert.</summary>
        public bool CoveredByRollup;

        /// <summary>When this WAN's total outage was confirmed, for the site rollup's window.</summary>
        public DateTime? TotalConfirmedAt;

        /// <summary>The most recent classification, carried into the event bodies.</summary>
        public WanVerdict? LastVerdict;
    }
}
