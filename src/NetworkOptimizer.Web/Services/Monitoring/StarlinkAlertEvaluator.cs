using System.Collections.Concurrent;
using System.Globalization;
using NetworkOptimizer.Alerts.Events;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Monitoring.Models;

namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>
/// Turns a Starlink dish's own reporting into alerts. Everything here hangs off the dish poll
/// rather than off <see cref="MonitoringAlertEvaluator"/>, because Starlink is usually a backup
/// WAN with no vantage, no agent and no monitored targets at all: the dish is the only sensor on
/// that link, so these must fire for a WAN nothing else watches.
///
/// <para>
/// Nothing here correlates with per-WAN outage alerting, deliberately. A dish outage that also
/// darkens monitored targets should raise both: they carry different evidence, and "the dish says
/// it is obstructed" alongside "the WAN is down" is a better story than either alone.
/// </para>
///
/// <para>
/// Severity does NOT follow the per-WAN outage table, which rates a backup's troubles lower
/// because service is unaffected. The opposite applies to a dish: a degraded primary announces
/// itself, while a degraded backup is silent by construction and is discovered at the moment it
/// is needed. Knowing the backup is unhealthy before the primary drops is the point of watching
/// it, so backup dish problems keep real severity.
/// </para>
///
/// <para>
/// Every rule is written against a VALUE, never against "the field is set". On the reference dish
/// (fixed tilt, permanently rate-restricted subscription) <c>disablement_code</c> reads
/// <c>Okay</c>, <c>alerts</c> carries <c>install_pending</c> continuously, <c>hardware_self_test</c>
/// reads <c>Failed</c> continuously, and both restriction reasons are permanently populated -
/// all while nothing is wrong. Any "has a value" test would fire on day one and never stop.
/// </para>
///
/// <para>
/// State is in memory only, so a restart re-arms every rule: an already-open condition raises its
/// alert once more, and the windowed rules (alignment, outage burst) stay quiet until they have
/// gathered enough samples again. That is the accepted cost of never persisting a verdict that
/// could go stale against a dish that has since recovered.
/// </para>
/// </summary>
public class StarlinkAlertEvaluator
{
    // --- Event types -------------------------------------------------------------------------

    internal const string DishAlertEvent = "starlink.dish_alert";
    internal const string ObstructedEvent = "starlink.obstructed";
    internal const string AlignmentDriftEvent = "starlink.alignment_drift";
    internal const string EthSpeedDegradedEvent = "starlink.eth_speed_degraded";
    internal const string OutageBurstEvent = "starlink.outage_burst";
    internal const string ServiceRestrictedEvent = "starlink.service_restricted";
    internal const string RecoveredEvent = "starlink.recovered";

    /// <summary>
    /// Context key on a <see cref="RecoveredEvent"/> naming the event type it closes. Matched by
    /// <c>AlertProcessingService.StarlinkRecoveredTypeKey</c>, which cannot reference this project;
    /// the two must stay in step, exactly as the WAN outage family's rollup device id does.
    /// </summary>
    internal const string RecoveredTypeKey = "recovered_type";

    /// <summary>Prefix of the AlertEvent.DeviceId these alerts carry, so one dish's alerts close only its own.</summary>
    internal const string DeviceIdPrefix = "starlink:";

    // --- Tuning ------------------------------------------------------------------------------

    /// <summary>
    /// How long a condition must hold before an obstruction alert opens, and how long its clear
    /// condition must hold before one closes. Obstruction is momentary by design - the dish loses
    /// a satellite behind a branch and picks up another - so a window is mandatory here, not a
    /// nicety. It costs little on <c>FractionObstructed</c>, which is already a long rolling
    /// average and cannot spike and recover inside the window, and does the real work on
    /// <c>IsSnrPersistentlyLow</c>, which is a bare boolean that can.
    ///
    /// <para>
    /// The same rolling average means RECOVERY lags: when the branch finally comes down the
    /// fraction decays over hours, so the alert closes long after the sky cleared. That is the
    /// metric's nature rather than a bug, and shortening the window would not change it.
    /// </para>
    /// </summary>
    private static readonly TimeSpan ObstructionSustain = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Obstruction fraction an open alert has to fall back under to close. Set below the raise
    /// bar so a dish sitting right at 2% does not alternate between alert and recovery. There is
    /// room for both: the reference dish runs at a median 0.06% obstructed and never exceeded
    /// 0.1% in 30 days, so the raise bar sits some twenty times above anything healthy.
    /// </summary>
    private const double ObstructionClearFraction = StarlinkHealthThresholds.ObstructionFractionPoor * 0.75;

    /// <summary>
    /// How far the dish's current alignment may sit from its own baseline before it needs
    /// re-aiming. Measured on the reference dish over 30 days, 98% of readings fall within 0.27
    /// degrees of the median, so 2 degrees is about seven times the healthy band: nothing but real
    /// movement reaches it. This is the operator's bar and should not be moved to accommodate a
    /// noisier install - lengthen <see cref="AlignmentSustain"/> instead.
    /// </summary>
    private const double AlignmentDriftDeg = 2.0;

    /// <summary>Drift an open alignment alert has to fall back under to close, below the raise bar so it cannot flap.</summary>
    private const double AlignmentClearDeg = AlignmentDriftDeg * 0.75;

    /// <summary>How long the drift has to hold, both to open the alert and to close it.</summary>
    private static readonly TimeSpan AlignmentSustain = TimeSpan.FromMinutes(30);

    /// <summary>
    /// The current alignment is the median of this window rather than the latest sample: single
    /// samples wander up to ~1.6 degrees from the median on a perfectly healthy dish, which would
    /// put a spurious trigger within reach of the 2 degree bar. Comparing medians does not.
    /// </summary>
    private static readonly TimeSpan AlignmentSampleWindow = TimeSpan.FromHours(1);

    /// <summary>
    /// Samples needed in the window before a median means anything. The reference dish lands about
    /// 730 points a day, one every two minutes, so an hour holds around thirty and this floor is
    /// only ever reached while the window is filling or after a gap in polling.
    /// </summary>
    private const int MinAlignmentSamples = 5;

    /// <summary>
    /// Attitude uncertainty above which the dish does not know where it is pointing, so a computed
    /// drift is measuring its confusion rather than its aim.
    ///
    /// <para>
    /// Set from the reference dish's own 30 day distribution, because the intuitive value is badly
    /// wrong. A healthy dish is nowhere near certain of its attitude: p50 0.70, p95 1.49, p99 1.83,
    /// max 2.71 degrees. A bar anywhere near the 2 degree drift trigger would gate out most healthy
    /// samples, and since a gated poll stalls the sustain, the drift alert would never survive its
    /// 30 minute window - it would be dead rather than quiet. Four degrees sits about 1.5x the
    /// observed maximum, so ordinary operation never gates and only genuine confusion does.
    /// </para>
    ///
    /// <para>
    /// The gate earns its place: mean uncertainty rises monotonically with how far the offset has
    /// strayed from its median (0.77 within 0.15 degrees, 0.90 to 0.3, 1.10 to 0.6, 1.19 beyond),
    /// so the excursions this rule must not mistake for movement do come with the dish saying it is
    /// less sure. Note that uncertainty is NOT the noise on the computed offset, which is far
    /// tighter (98% of readings within 0.27 degrees of the median) - it is the dish's own stated
    /// confidence, and it is conservative. Sustained high uncertainty is a GPS or IMU problem in
    /// its own right; it is not alerted here.
    /// </para>
    /// </summary>
    private const double AttitudeUncertaintyMaxDeg = 4.0;

    /// <summary>
    /// Rolling window the outage-burst rule sums the dish's own outage seconds over. The bar it is
    /// summed against has room: the reference dish logged between 1 and 31 seconds of outage a day
    /// over 30 days, a median of 13, against a 300 second bar.
    /// </summary>
    private static readonly TimeSpan OutageWindow = TimeSpan.FromDays(1);

    /// <summary>Outage seconds per day an open burst alert has to fall back under to close.</summary>
    private const double OutageClearSecondsPerDay = StarlinkHealthThresholds.OutageSecondsPerDayPoor * 0.5;

    /// <summary>
    /// How long a downshifted Ethernet link has to hold before it alerts. A renegotiation during a
    /// reboot or a cable reseat settles well inside this, and the rule is about a link that stays
    /// capped.
    /// </summary>
    private static readonly TimeSpan EthSpeedSustain = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Dish alert codes that describe a state rather than a fault, and so must never raise one.
    /// <list type="bullet">
    /// <item>
    /// <c>install_pending</c> - MEASURED. The only code the reference dish raised in 30 days, and
    /// it raised it continuously while nothing was wrong. Without this entry the product would
    /// alert on day one and never stop, which is the failure this whole list exists to prevent.
    /// </item>
    /// <item><c>is_heating</c> - the dish heating itself in cold weather, which is it working.</item>
    /// <item><c>is_power_save_idle</c> - a power-save setting the owner chose.</item>
    /// <item><c>roaming</c> - a service mode, and mobility is deliberately never treated as a fault here.</item>
    /// <item><c>obstruction_map_reset</c> - housekeeping after a move or reboot, not damage.</item>
    /// </list>
    /// Only the first is measured; the other four are judged on what the code means, since nothing
    /// has been observed raising them. Everything else the dish reports is passed through verbatim:
    /// these are SpaceX's own judgment about its own hardware, and translating them would only lose
    /// what a search for the exact string turns up.
    ///
    /// <para>
    /// The reference dish is fixed-tilt, motorless and permanently rate restricted, and raised
    /// NOTHING else across 30 days - which is real evidence that codes like
    /// <c>mast_not_near_vertical</c>, <c>motors_stuck</c> and <c>low_motor_current</c> are not
    /// simply always-on for that class of install, and so belong outside this list. If some other
    /// hardware or firmware does report a code continuously while healthy, the symptom is one
    /// alert per app restart on that install, and the fix is an entry here.
    /// </para>
    /// </summary>
    private static readonly HashSet<string> BenignDishAlerts = new(StringComparer.OrdinalIgnoreCase)
    {
        "install_pending",
        "is_heating",
        "is_power_save_idle",
        "roaming",
        "obstruction_map_reset",
    };

    private readonly IAlertEventBus _eventBus;
    private readonly ILogger<StarlinkAlertEvaluator> _logger;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<int, DishState> _states = new();
    private readonly string _siteSuffix;

    /// <param name="siteSlug">
    /// Site this instance evaluates for (one instance per site, owned by
    /// <see cref="MonitoringAlertRegistry"/> - Starlink configuration ids are per-site database
    /// sequences, so state must not be shared). Non-default sites get their slug appended to
    /// alert titles.
    /// </param>
    /// <param name="timeProvider">Injected in tests so the sustain windows can be driven without waiting.</param>
    public StarlinkAlertEvaluator(IAlertEventBus eventBus, ILogger<StarlinkAlertEvaluator> logger,
        string siteSlug = SiteManagementService.DefaultSiteSlug,
        TimeProvider? timeProvider = null)
    {
        _eventBus = eventBus;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
        _siteSuffix = string.IsNullOrEmpty(siteSlug) || siteSlug == SiteManagementService.DefaultSiteSlug
            ? "" : $" (site {siteSlug})";
    }

    /// <summary>
    /// Evaluates one poll of one dish and publishes whatever changed.
    /// </summary>
    /// <param name="starlinkId">Configuration id of the dish (per-site database sequence).</param>
    /// <param name="dishName">The dish's configured name, used when no WAN could be bound to it.</param>
    /// <param name="stats">This poll's reading.</param>
    /// <param name="alignmentOffsetDeg">
    /// This poll's boresight offset from desired, as
    /// <see cref="StarlinkMonitorService.ComputeAlignmentOffsetDeg"/> computes it. Null when the
    /// dish did not report the geometry.
    /// </param>
    /// <param name="alignmentBaselineDeg">
    /// The dish's own long-run median offset. Alignment is judged against this rather than against
    /// zero: a hand-aimed fixed dish sits wherever it was mounted, several degrees off ideal from
    /// day one, and works perfectly there. Null when there is not enough history yet, which
    /// disables the drift rule rather than guessing.
    /// </param>
    /// <param name="ethCapableMbps">
    /// The fastest Ethernet speed this dish has been seen to negotiate, which is the only evidence
    /// available for what it is capable of. Null disables the degraded-speed rule.
    /// </param>
    /// <param name="wanLabel">
    /// The WAN the dish was bound to, already formatted by <c>GatewayWanHelper.FormatWanLabel</c>.
    /// Null when the binding is unknown, in which case alerts name the dish and still fire.
    /// </param>
    public async ValueTask EvaluateAsync(
        int starlinkId,
        string dishName,
        StarlinkStats stats,
        double? alignmentOffsetDeg = null,
        double? alignmentBaselineDeg = null,
        int? ethCapableMbps = null,
        string? wanLabel = null,
        CancellationToken ct = default)
    {
        var state = _states.GetOrAdd(starlinkId, _ => new DishState());
        var now = _time.GetUtcNow().UtcDateTime;
        var subject = new DishSubject(starlinkId, dishName, wanLabel);

        // One evaluation per dish at a time. The timer poll is single-flighted against itself, but
        // the Starlink Stats panel calls PollStarlinkAsync directly (Refresh, moving between
        // terminals, first paint on an empty cache) and that path has no such guard - so two polls
        // of the SAME dish can be in flight together. This state is not thread-safe: the sample
        // windows are plain Queues, whose concurrent Enqueue/Dequeue corrupts their internal
        // indices rather than merely racing. A lock cannot span the awaits, hence the semaphore.
        await state.Gate.WaitAsync(ct);
        try
        {
            await CheckDishAlerts(state, subject, stats, ct);
            await CheckObstruction(state, subject, stats, now, ct);
            await CheckAlignmentDrift(state, subject, stats, alignmentOffsetDeg, alignmentBaselineDeg, now, ct);
            await CheckEthSpeed(state, subject, stats, ethCapableMbps, now, ct);
            await CheckOutageBurst(state, subject, stats, now, ct);
            await CheckServiceRestriction(state, subject, stats, ct);
        }
        finally
        {
            state.Gate.Release();
        }
    }

    // --- starlink.dish_alert -----------------------------------------------------------------

    /// <summary>
    /// The dish's own verdict on itself, folded into one event: the alert codes it raises, a
    /// self-test that has started failing, and a disablement code other than <c>Okay</c>. Three
    /// separate types would all have meant the same thing to an operator ("go look at the dish"),
    /// so they share one, and the alert names whichever of them is live.
    ///
    /// <para>
    /// The self-test is a TRANSITION from passing to failing, never a standing state. The
    /// reference dish reports <c>Failed</c> continuously while entirely healthy, so what a healthy
    /// dish of a given hardware revision and firmware reports is not knowable in general - only
    /// that a dish which was passing and is now failing has changed. A dish that has always failed
    /// its self-test therefore never raises this on that account alone.
    /// </para>
    /// </summary>
    private async ValueTask CheckDishAlerts(DishState state, DishSubject subject, StarlinkStats stats,
        CancellationToken ct)
    {
        var selfTest = Normalize(stats.HardwareSelfTest);
        if (selfTest == "passed")
        {
            state.SelfTestHasPassed = true;
            state.SelfTestRegressed = false;
        }
        else if (selfTest == "failed" && state.SelfTestHasPassed)
        {
            state.SelfTestRegressed = true;
        }

        var disablement = stats.DisablementCode;
        var disabled = IsDisabled(disablement);

        // The dish's codes verbatim - they are SpaceX's own vocabulary for its own hardware, and
        // paraphrasing them would only lose what a search for the exact string turns up.
        var codes = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var code in stats.ActiveAlerts)
        {
            if (!string.IsNullOrWhiteSpace(code) && !BenignDishAlerts.Contains(code))
                codes.Add(code);
        }

        // The signature the open alert was raised for. It carries the disablement code and the
        // self-test regression alongside the dish's codes so that either one arriving counts as
        // new evidence, but neither is presented to the reader as a dish alert code.
        var signature = new SortedSet<string>(codes, StringComparer.OrdinalIgnoreCase);
        if (state.SelfTestRegressed) signature.Add("hardware_self_test:failed");
        if (disabled) signature.Add($"disablement:{disablement}");

        if (signature.Count == 0)
        {
            if (state.OpenDishAlertCodes.Count > 0)
            {
                state.OpenDishAlertCodes.Clear();
                await PublishRecovered(subject, DishAlertEvent, "dish fault",
                    "The dish is no longer reporting a fault.", ct);
            }
            return;
        }

        // Only new evidence republishes: a standing set stays as the one open alert it already
        // raised. Something appearing on top of the open set, or the dish going from "complaining"
        // to "out of service", is new evidence and supersedes it.
        var severity = disabled ? AlertSeverity.Critical : AlertSeverity.Warning;
        if (state.OpenDishAlertCodes.Count > 0
            && signature.IsSubsetOf(state.OpenDishAlertCodes)
            && severity == state.OpenDishAlertSeverity)
        {
            return;
        }

        state.OpenDishAlertCodes = new HashSet<string>(signature, StringComparer.OrdinalIgnoreCase);
        state.OpenDishAlertSeverity = severity;

        var codeList = string.Join(", ", codes);
        var sentences = new List<string>();
        if (disabled)
            sentences.Add($"Starlink has taken {subject.Label} out of service (disablement code {disablement}).");
        if (codes.Count > 0)
            sentences.Add($"The dish reports: {codeList}.");
        if (state.SelfTestRegressed)
            sentences.Add("Its hardware self-test was passing and is now failing.");
        var message = string.Join(" ", sentences);

        _logger.LogDebug("Starlink dish {Name} reporting {Codes} (disablement {Disablement})",
            subject.DishName, codeList, disablement);

        await _eventBus.PublishAsync(new AlertEvent
        {
            EventType = DishAlertEvent,
            Source = "starlink",
            Severity = severity,
            Title = disabled
                ? $"{subject.Label} is out of service{_siteSuffix}"
                : $"{subject.Label} dish reports a fault{_siteSuffix}",
            Message = message,
            DeviceId = subject.DeviceId,
            DeviceName = subject.Label,
            SourceUrl = subject.SourceUrl,
            Tags = ["starlink", "dish"],
            Context = subject.Context(new Dictionary<string, string>
            {
                ["dish_alerts"] = codeList,
                ["disablement_code"] = disablement ?? "",
                ["hardware_self_test"] = stats.HardwareSelfTest ?? "",
            })
        }, ct);
    }

    // --- starlink.obstructed -----------------------------------------------------------------

    /// <summary>
    /// One type for both ways the dish can lose its view of the sky, because the remedy is the
    /// same either way: it cannot see enough of it. A sustained obstruction fraction is the
    /// measured version; the dish's own persistently-low-SNR flag is its version.
    /// </summary>
    private async ValueTask CheckObstruction(DishState state, DishSubject subject, StarlinkStats stats,
        DateTime now, CancellationToken ct)
    {
        var fraction = stats.FractionObstructed;
        var snrLow = stats.IsSnrPersistentlyLow == true;

        // A poll that reported neither signal says nothing either way, so it neither advances a
        // pending run nor counts against one - otherwise a run interrupted by a gap in reporting
        // would confirm on the far side of it as if it had held throughout.
        if (fraction is null && stats.IsSnrPersistentlyLow is null)
        {
            state.Obstruction.Stall();
            return;
        }

        var raise = snrLow || fraction >= StarlinkHealthThresholds.ObstructionFractionPoor;
        var clear = !snrLow && (fraction is null || fraction < ObstructionClearFraction);
        var critical = fraction >= StarlinkHealthThresholds.ObstructionFractionCritical;

        var transition = state.Obstruction.Observe(raise, clear, now, ObstructionSustain);
        var escalated = state.Obstruction.Open && critical && !state.ObstructionCritical;

        if (transition == GateTransition.Closed)
        {
            state.ObstructionCritical = false;
            await PublishRecovered(subject, ObstructedEvent, "obstruction",
                "The dish has a clear enough view of the sky again.", ct);
            return;
        }

        if (transition != GateTransition.Opened && !escalated) return;

        state.ObstructionCritical = critical;

        var fractionTripped = fraction >= StarlinkHealthThresholds.ObstructionFractionPoor;
        var reason = snrLow && fractionTripped
            ? $"The dish has been {FormatPercent(fraction)} obstructed and reports persistently low signal"
            : snrLow
                ? "The dish reports persistently low signal"
                : $"The dish has been {FormatPercent(fraction)} obstructed";

        _logger.LogDebug("Starlink dish {Name} obstructed: fraction={Fraction} snrLow={SnrLow}",
            subject.DishName, fraction, snrLow);

        await _eventBus.PublishAsync(new AlertEvent
        {
            EventType = ObstructedEvent,
            Source = "starlink",
            Severity = critical ? AlertSeverity.Critical : AlertSeverity.Warning,
            Title = $"{subject.Label} dish is obstructed{_siteSuffix}",
            Message = $"{reason} for at least {FormatDuration(ObstructionSustain)}. " +
                      "Something in its view of the sky is cutting satellites off; the obstruction map on Starlink Stats shows where.",
            DeviceId = subject.DeviceId,
            DeviceName = subject.Label,
            // Only when the fraction is what tripped it. On an SNR-only alert the obstruction
            // fraction is healthy, and pairing that number with the poor-obstruction threshold
            // would render as "0.0006 against 0.02" beside a message about low signal.
            MetricValue = fractionTripped ? fraction : null,
            ThresholdValue = fractionTripped ? StarlinkHealthThresholds.ObstructionFractionPoor : null,
            SourceUrl = subject.SourceUrl,
            Tags = ["starlink", "obstruction"],
            Context = subject.Context(new Dictionary<string, string>
            {
                ["fraction_obstructed"] = Format(fraction),
                ["snr_persistently_low"] = snrLow ? "true" : "false",
            })
        }, ct);
    }

    // --- starlink.alignment_drift ------------------------------------------------------------

    /// <summary>
    /// A phased array steers electronically well off boresight, so a misaligned dish does not fail
    /// loudly - it loses margin, and drops concentrate at the times of day when satellites transit
    /// the part of the corridor now beyond its steering range. Nobody diagnoses that, because at
    /// any given moment everything looks fine. A fixed dish stays wrong until a human climbs up to
    /// it, so this alert is the only way anyone learns the mount slipped after wind, snow load or
    /// a knock.
    ///
    /// <para>
    /// Judged against the dish's OWN baseline, never against desired: the reference dish sits at a
    /// steady 3.69 degrees off desired and works fine there, so an absolute threshold would flag a
    /// healthy install permanently.
    /// </para>
    ///
    /// <para>
    /// Deliberately NOT gated on mobility class. The obvious design suppresses drift on a roaming
    /// dish, but the reference dish is fixed-tilt, bolted down, and reports <c>Mobile</c> - that
    /// gate would have silenced the alert on exactly the installation it was written for. A
    /// genuinely moving dish is handled by the baseline instead: one whose attitude changes
    /// constantly has a baseline that moves with it and never accumulates a sustained departure.
    /// </para>
    /// </summary>
    private async ValueTask CheckAlignmentDrift(DishState state, DishSubject subject, StarlinkStats stats,
        double? offsetDeg, double? baselineDeg, DateTime now, CancellationToken ct)
    {
        if (offsetDeg is not double offset)
        {
            state.Alignment.Stall();
            return;
        }

        // Above the uncertainty bar the dish does not know where it is pointing, so any drift
        // computed from it measures its confusion. Neither open nor close while that holds: the
        // sample is dropped and the sustain stalls, and an already-open alert stays open because
        // uncertainty says nothing about whether the dish moved back.
        if (stats.AttitudeUncertaintyDeg > AttitudeUncertaintyMaxDeg)
        {
            state.Alignment.Stall();
            return;
        }

        state.AlignmentSamples.Enqueue((now, offset));
        while (state.AlignmentSamples.Count > 0 && now - state.AlignmentSamples.Peek().At > AlignmentSampleWindow)
            state.AlignmentSamples.Dequeue();

        // No baseline (too little history, or Influx unreachable) and too few samples in the
        // window are both "cannot judge", not "judged fine": the pending run is discarded so it
        // cannot confirm across the gap on the strength of readings taken before it.
        if (baselineDeg is not double baseline || state.AlignmentSamples.Count < MinAlignmentSamples)
        {
            state.Alignment.Stall();
            return;
        }

        var current = Median(state.AlignmentSamples.Select(s => s.Offset));
        var drift = Math.Abs(current - baseline);

        var transition = state.Alignment.Observe(
            drift > AlignmentDriftDeg, drift <= AlignmentClearDeg, now, AlignmentSustain);

        if (transition == GateTransition.Closed)
        {
            await PublishRecovered(subject, AlignmentDriftEvent, "alignment drift",
                $"The dish is pointing within {AlignmentClearDeg:0.#} degrees of where it normally sits again.", ct);
            return;
        }

        if (transition != GateTransition.Opened) return;

        _logger.LogDebug("Starlink dish {Name} alignment drifted: current={Current} baseline={Baseline}",
            subject.DishName, current, baseline);

        await _eventBus.PublishAsync(new AlertEvent
        {
            EventType = AlignmentDriftEvent,
            Source = "starlink",
            Severity = AlertSeverity.Warning,
            Title = $"{subject.Label} dish alignment has drifted{_siteSuffix}",
            Message = $"The dish is pointing {drift:0.#} degrees further from its ideal aim than it normally does " +
                      $"({current:0.#} degrees off ideal now, against a long-run {baseline:0.#}), and has held there " +
                      $"for {FormatDuration(AlignmentSustain)}. A fixed mount does not correct itself, so this needs re-aiming by hand.",
            DeviceId = subject.DeviceId,
            DeviceName = subject.Label,
            MetricValue = drift,
            ThresholdValue = AlignmentDriftDeg,
            SourceUrl = subject.SourceUrl,
            Tags = ["starlink", "alignment"],
            Context = subject.Context(new Dictionary<string, string>
            {
                ["alignment_offset_deg"] = Format(current),
                ["alignment_baseline_deg"] = Format(baseline),
                ["alignment_drift_deg"] = Format(drift),
            })
        }, ct);
    }

    // --- starlink.eth_speed_degraded ---------------------------------------------------------

    /// <summary>
    /// A bad cable or a bad port silently capping the service. The dish negotiates a clean
    /// constant (1000 on the reference dish), so a drop below what it has been seen to reach is
    /// unambiguous - and it is the only evidence available for what the dish is capable of, since
    /// nothing reports a nameplate rate.
    /// </summary>
    private async ValueTask CheckEthSpeed(DishState state, DishSubject subject, StarlinkStats stats,
        int? capableMbps, DateTime now, CancellationToken ct)
    {
        // A poll with no negotiated speed, or no known capable rate to compare it against, is not
        // evidence in either direction, so the pending run is discarded rather than carried over.
        if (stats.EthSpeedMbps is not int current || capableMbps is not int capable || capable <= 0)
        {
            state.EthSpeed.Stall();
            return;
        }

        var transition = state.EthSpeed.Observe(current < capable, current >= capable, now, EthSpeedSustain);

        if (transition == GateTransition.Closed)
        {
            await PublishRecovered(subject, EthSpeedDegradedEvent, "Ethernet speed",
                $"The dish is negotiating {capable} Mbps again.", ct);
            return;
        }

        if (transition != GateTransition.Opened) return;

        _logger.LogDebug("Starlink dish {Name} Ethernet negotiated {Current} Mbps against {Capable} Mbps",
            subject.DishName, current, capable);

        await _eventBus.PublishAsync(new AlertEvent
        {
            EventType = EthSpeedDegradedEvent,
            Source = "starlink",
            Severity = AlertSeverity.Warning,
            Title = $"{subject.Label} dish Ethernet link degraded{_siteSuffix}",
            Message = $"The dish has negotiated {current} Mbps, where it normally reaches {capable} Mbps. " +
                      "A cable or a port is capping the connection below what the service can deliver.",
            DeviceId = subject.DeviceId,
            DeviceName = subject.Label,
            MetricValue = current,
            ThresholdValue = capable,
            SourceUrl = subject.SourceUrl,
            Tags = ["starlink", "ethernet"],
            Context = subject.Context(new Dictionary<string, string>
            {
                ["eth_speed_mbps"] = current.ToString(CultureInfo.InvariantCulture),
                ["eth_capable_mbps"] = capable.ToString(CultureInfo.InvariantCulture),
            })
        }, ct);
    }

    // --- starlink.outage_burst ---------------------------------------------------------------

    /// <summary>
    /// The dish's own outage log, summed over a rolling day. Individual dish outages are short and
    /// routine; what an operator can act on is the day they stop being routine. The most recent
    /// cause travels with the alert, which is what makes it self-explaining.
    /// </summary>
    private async ValueTask CheckOutageBurst(DishState state, DishSubject subject, StarlinkStats stats,
        DateTime now, CancellationToken ct)
    {
        if (stats.OutageSecondsDelta > 0)
            state.OutageSamples.Enqueue((now, stats.OutageSecondsDelta));
        while (state.OutageSamples.Count > 0 && now - state.OutageSamples.Peek().At > OutageWindow)
            state.OutageSamples.Dequeue();

        var total = state.OutageSamples.Sum(s => s.Seconds);

        if (state.OutageBurstOpen)
        {
            if (total >= OutageClearSecondsPerDay) return;
            state.OutageBurstOpen = false;
            await PublishRecovered(subject, OutageBurstEvent, "outages",
                "The dish is back under a normal amount of downtime for the day.", ct);
            return;
        }

        if (total < StarlinkHealthThresholds.OutageSecondsPerDayPoor) return;
        state.OutageBurstOpen = true;

        var cause = string.IsNullOrWhiteSpace(stats.LastOutageCause) ? null : stats.LastOutageCause;
        _logger.LogDebug("Starlink dish {Name} outage burst: {Seconds}s in the last day (last cause {Cause})",
            subject.DishName, total, cause);

        await _eventBus.PublishAsync(new AlertEvent
        {
            EventType = OutageBurstEvent,
            Source = "starlink",
            Severity = AlertSeverity.Warning,
            Title = $"{subject.Label} dish keeps dropping out{_siteSuffix}",
            Message = $"The dish has logged {total:0} seconds of outage in the last day, past the " +
                      $"{StarlinkHealthThresholds.OutageSecondsPerDayPoor:0} second mark." +
                      (cause == null ? "" : $" It gave {cause} as the reason for the most recent one."),
            DeviceId = subject.DeviceId,
            DeviceName = subject.Label,
            MetricValue = total,
            ThresholdValue = StarlinkHealthThresholds.OutageSecondsPerDayPoor,
            SourceUrl = subject.SourceUrl,
            Tags = ["starlink", "outage"],
            Context = subject.Context(new Dictionary<string, string>
            {
                ["outage_seconds_per_day"] = Format(total),
                ["last_outage_cause"] = cause ?? "",
            })
        }, ct);
    }

    // --- starlink.service_restricted ---------------------------------------------------------

    /// <summary>
    /// Reported as a TRANSITION, never as a state. Some subscriptions are throttled by design and
    /// report the restriction permanently - the reference dish sits at <c>LowSpeedPolicyLimit</c>
    /// downstream and <c>PolicyLimit</c> upstream continuously, with nothing wrong - so an alert
    /// on the standing condition would fire forever at someone who bought exactly that service.
    /// Class of service does not separate the two cases either: that same permanently restricted
    /// dish reads <c>Consumer</c>.
    ///
    /// <para>
    /// Watching the edge instead serves both. For anyone on a plan with a data allotment, crossing
    /// from unrestricted into restricted is the moment the allotment ran out and everything slowed
    /// down, which is exactly when they would want to know. For a dish that is always restricted,
    /// nothing ever transitions and nothing is ever sent.
    /// </para>
    /// </summary>
    private async ValueTask CheckServiceRestriction(DishState state, DishSubject subject, StarlinkStats stats,
        CancellationToken ct)
    {
        var down = RestrictionReason(stats.DownlinkRestrictedReason);
        var up = RestrictionReason(stats.UplinkRestrictedReason);
        var restricted = down != null || up != null;

        var previous = state.WasRestricted;
        state.WasRestricted = restricted;

        // The first reading only establishes what normal looks like for this dish. Without it, a
        // permanently restricted dish would announce its own subscription on every restart.
        if (previous is null) return;

        if (restricted && previous == false)
        {
            var reasons = string.Join(", ", new[]
            {
                down == null ? null : $"downlink {down}",
                up == null ? null : $"uplink {up}",
            }.Where(r => r != null));

            _logger.LogDebug("Starlink dish {Name} entered a restricted state: {Reasons}", subject.DishName, reasons);

            await _eventBus.PublishAsync(new AlertEvent
            {
                EventType = ServiceRestrictedEvent,
                Source = "starlink",
                Severity = AlertSeverity.Info,
                Title = $"{subject.Label} service is now rate limited{_siteSuffix}",
                Message = $"Starlink started limiting this connection ({reasons}). On a plan with a data " +
                          "allotment this is the moment it ran out.",
                DeviceId = subject.DeviceId,
                DeviceName = subject.Label,
                SourceUrl = subject.SourceUrl,
                Tags = ["starlink", "restriction"],
                Context = subject.Context(new Dictionary<string, string>
                {
                    ["dl_restricted_reason"] = stats.DownlinkRestrictedReason ?? "",
                    ["ul_restricted_reason"] = stats.UplinkRestrictedReason ?? "",
                })
            }, ct);
        }
        else if (!restricted && previous == true)
        {
            await PublishRecovered(subject, ServiceRestrictedEvent, "rate limit",
                "Starlink is no longer limiting this connection.", ct);
        }
    }

    // --- starlink.recovered ------------------------------------------------------------------

    /// <summary>
    /// Closes exactly one open alert, named in <see cref="RecoveredTypeKey"/> so the processor can
    /// resolve that dish's alert of that type and leave its other conditions alone.
    /// </summary>
    private ValueTask PublishRecovered(DishSubject subject, string recoveredType, string what, string detail,
        CancellationToken ct)
    {
        _logger.LogDebug("Starlink dish {Name} recovered from {Type}", subject.DishName, recoveredType);

        return _eventBus.PublishAsync(new AlertEvent
        {
            EventType = RecoveredEvent,
            Source = "starlink",
            Severity = AlertSeverity.Info,
            Title = $"{subject.Label} {what} cleared{_siteSuffix}",
            Message = detail,
            DeviceId = subject.DeviceId,
            DeviceName = subject.Label,
            SourceUrl = subject.SourceUrl,
            Tags = ["starlink", "recovered"],
            Context = subject.Context(new Dictionary<string, string>
            {
                [RecoveredTypeKey] = recoveredType,
            })
        }, ct);
    }

    // --- Helpers ---------------------------------------------------------------------------

    /// <summary>
    /// Whether the terminal has been taken out of service. Tested against the value, not against
    /// "is set": a healthy dish reports <c>Okay</c> here on every poll. An unknown or absent code
    /// is no signal at all rather than a fault.
    /// </summary>
    private static bool IsDisabled(string? disablementCode)
    {
        var value = Normalize(disablementCode);
        return value.Length > 0 && value != "okay" && value != "unknownstate" && value != "unknown";
    }

    /// <summary>
    /// The restriction reason when the dish is actually being limited, null when it is not.
    /// <c>NoLimit</c> is the healthy value and an unknown reason carries no information, so
    /// neither counts as restricted.
    /// </summary>
    private static string? RestrictionReason(string? reason)
    {
        var value = Normalize(reason);
        return value.Length == 0 || value == "nolimit" || value == "unknown" ? null : reason;
    }

    /// <summary>
    /// Folds a protobuf enum name to a comparable form, so <c>LOW_SPEED_POLICY_LIMIT</c> and
    /// <c>LowSpeedPolicyLimit</c> are the same value however a provider chose to render it.
    /// </summary>
    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "" : value.Replace("_", "").Trim().ToLowerInvariant();

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        if (sorted.Count == 0) return 0;
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    private static string Format(double? value) =>
        value?.ToString("0.####", CultureInfo.InvariantCulture) ?? "";

    private static string FormatPercent(double? fraction) =>
        fraction is null ? "" : (fraction.Value * 100).ToString("0.##", CultureInfo.InvariantCulture) + "%";

    private static string FormatDuration(TimeSpan span) =>
        span.TotalMinutes < 60
            ? $"{span.TotalMinutes:0} minutes"
            : span.TotalHours == 1 ? "an hour" : $"{span.TotalHours:0} hours";

    /// <summary>
    /// How one dish is named and linked in its alerts. The WAN label wins when the dish could be
    /// bound to one, because that is the name the rest of the product uses for a connection; the
    /// dish's own name is the fallback, and the alert fires either way.
    /// </summary>
    private readonly record struct DishSubject(int Id, string DishName, string? WanLabel)
    {
        public string Label => string.IsNullOrWhiteSpace(WanLabel) ? DishName : WanLabel!;

        public string DeviceId => $"{DeviceIdPrefix}{Id}";

        public string SourceUrl => $"/monitoring?tab=starlink&starlink={Id}";

        public Dictionary<string, string> Context(Dictionary<string, string> extra)
        {
            extra["starlink_id"] = Id.ToString(CultureInfo.InvariantCulture);
            extra["dish_name"] = DishName;
            if (!string.IsNullOrWhiteSpace(WanLabel)) extra["wan_label"] = WanLabel!;
            return extra;
        }
    }

    private enum GateTransition { None, Opened, Closed }

    /// <summary>
    /// Two-sided sustain. A condition must hold for the window to open an alert, and its clear
    /// condition must hold just as long to close one, so a value hovering at the bar produces one
    /// alert and one recovery rather than a stream of both.
    /// </summary>
    private sealed class SustainGate
    {
        private DateTime? _raiseSince;
        private DateTime? _clearSince;

        public bool Open { get; private set; }

        public GateTransition Observe(bool raise, bool clear, DateTime now, TimeSpan window)
        {
            _raiseSince = raise ? _raiseSince ?? now : null;
            _clearSince = clear ? _clearSince ?? now : null;

            if (!Open && _raiseSince is { } raiseSince && now - raiseSince >= window)
            {
                Open = true;
                _clearSince = null;
                return GateTransition.Opened;
            }

            if (Open && _clearSince is { } clearSince && now - clearSince >= window)
            {
                Open = false;
                _raiseSince = null;
                return GateTransition.Closed;
            }

            return GateTransition.None;
        }

        /// <summary>
        /// Discards both pending runs without changing whether the alert is open, for a poll whose
        /// reading says nothing either way.
        /// </summary>
        public void Stall()
        {
            _raiseSince = null;
            _clearSince = null;
        }
    }

    private sealed class DishState
    {
        /// <summary>
        /// Serializes evaluation of this dish, since nothing below is thread-safe and a UI-driven
        /// poll can overlap the timer's. Never disposed: a dish's state lives as long as the
        /// evaluator, and the semaphore is uncontended in the ordinary single-poll case.
        /// </summary>
        public readonly SemaphoreSlim Gate = new(1, 1);

        /// <summary>Codes the currently open dish_alert was raised for, so a standing set does not republish.</summary>
        public HashSet<string> OpenDishAlertCodes = new(StringComparer.OrdinalIgnoreCase);

        public AlertSeverity OpenDishAlertSeverity;

        /// <summary>Whether this dish has ever been seen to pass its self-test, which is what makes a later failure a change.</summary>
        public bool SelfTestHasPassed;

        public bool SelfTestRegressed;

        public readonly SustainGate Obstruction = new();
        public bool ObstructionCritical;

        public readonly SustainGate Alignment = new();
        public readonly Queue<(DateTime At, double Offset)> AlignmentSamples = new();

        public readonly SustainGate EthSpeed = new();

        public readonly Queue<(DateTime At, double Seconds)> OutageSamples = new();
        public bool OutageBurstOpen;

        /// <summary>Null until the first poll: the first reading only establishes what normal looks like.</summary>
        public bool? WasRestricted;
    }
}
