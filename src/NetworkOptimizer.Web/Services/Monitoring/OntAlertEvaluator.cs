using System.Collections.Concurrent;
using NetworkOptimizer.Alerts.Events;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Monitoring.Models;

namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>
/// Evaluates external ONT (Optical Network Terminal) readings and publishes alert
/// events on state transitions. Covers RX power degradation, PON link loss, FEC
/// error spikes, and high temperature - the same failure modes as in-gateway SFP
/// DDM but sourced from the ISP-side device. RX-power and temperature thresholds
/// are the caller-supplied effective values (the shared PON settings SFP uses),
/// defaulting to the built-in <see cref="PonThresholds"/> constants.
/// </summary>
public class OntAlertEvaluator
{
    private const double RxPowerHysteresisDbm = PonThresholds.PowerHysteresisDbm;
    private const double TempHysteresisC = PonThresholds.TempHysteresisC;
    private const long FecErrorDeltaThreshold = PonThresholds.PonFecErrorSpikePerPoll;
    private const long BipErrorStrictThreshold = PonThresholds.PonBipErrorSpikePerPoll;
    private const long BipErrorRelaxedThreshold = PonThresholds.PonBipErrorSpikeFecOnPerPoll;
    private const long HecErrorDeltaThreshold = PonThresholds.PonHecErrorSpikePerPoll;
    private const string DefaultSourceUrl = "/monitoring?tab=ont";

    private readonly IAlertEventBus _eventBus;
    private readonly ILogger<OntAlertEvaluator> _logger;
    private readonly ConcurrentDictionary<int, OntAlertState> _states = new();
    private readonly string _siteSuffix;

    /// <param name="siteSlug">
    /// Site this instance evaluates for (one instance per site, owned by
    /// <see cref="MonitoringAlertRegistry"/> - ONT ids are per-site database
    /// sequences, so state must not be shared). Non-default sites get their
    /// slug appended to alert titles.
    /// </param>
    public OntAlertEvaluator(IAlertEventBus eventBus, ILogger<OntAlertEvaluator> logger,
        string siteSlug = SiteManagementService.DefaultSiteSlug)
    {
        _eventBus = eventBus;
        _logger = logger;
        _siteSuffix = string.IsNullOrEmpty(siteSlug) || siteSlug == SiteManagementService.DefaultSiteSlug
            ? "" : $" (site {siteSlug})";
    }

    public async ValueTask EvaluateAsync(
        int ontId, string ontName,
        double? rxPowerDbm,
        PonLinkState ponLinkStatus,
        long? fecErrors,
        double? temperatureC = null,
        double rxPowerLowDbm = PonThresholds.PonRxPowerLowDbm,
        double tempHighC = PonThresholds.PonTempHighC,
        long? bipErrors = null,
        long? hecErrors = null,
        bool? fecEnabled = null,
        string? sourceUrl = null,
        long? uptimeSeconds = null,
        CancellationToken ct = default)
    {
        var state = _states.GetOrAdd(ontId, _ => new OntAlertState());
        // Where a raised alert links to. Attached SFP ONTs surface on SFP Stats, standalone
        // ONTs on ONT Stats; the caller passes the deep link to the triggering module.
        state.SourceUrl = string.IsNullOrEmpty(sourceUrl) ? DefaultSourceUrl : sourceUrl;

        if (uptimeSeconds.HasValue)
        {
            await CheckReboot(state, ontId, ontName, uptimeSeconds.Value, ct);
        }

        if (rxPowerDbm.HasValue)
        {
            await CheckRxPower(state, ontId, ontName, rxPowerDbm.Value, rxPowerLowDbm, ct);
        }

        await CheckPonLink(state, ontId, ontName, ponLinkStatus, ct);

        // BIP is always-on regardless of FEC, so it's evaluated whenever reported. Read the
        // uncorrectable counter for the same interval first: BIP counts line errors, and whether
        // any of them reached the data is what separates a degrading link from a lossy one. Safe
        // to read here because the FEC/HEC checks below have not consumed their previous values yet.
        if (bipErrors.HasValue)
        {
            var lost = fecEnabled == false
                ? SpikeDelta(hecErrors, state.PreviousHecErrors)
                : SpikeDelta(fecErrors, state.PreviousFecErrors);
            await CheckBipErrors(state, ontId, ontName, bipErrors.Value, fecEnabled, lost, ct);
        }

        // The uncorrectable-codeword signal adapts to whether payload FEC is running:
        // FEC codewords when it's enabled (or unknown - the standalone-ONT default),
        // HEC header errors when it's explicitly disabled and FEC counters stay flat.
        // Mirrors the SFP ONT card's adaptive FEC/HEC display.
        if (fecEnabled == false)
        {
            if (hecErrors.HasValue)
                await CheckHecErrors(state, ontId, ontName, hecErrors.Value, ct);
        }
        else if (fecErrors.HasValue)
        {
            await CheckFecErrors(state, ontId, ontName, fecErrors.Value, ct);
        }

        if (temperatureC.HasValue)
        {
            await CheckTemperature(state, ontId, ontName, temperatureC.Value, tempHighC, ct);
        }
    }

    /// <summary>A path loss this far above the learned level raises the rise alert.</summary>
    internal const double PathLossRiseDb = 2.0;
    /// <summary>Less than this much left to the class limit raises the budget alert as a warning.</summary>
    internal const double PathLossReserveDb = 3.0;
    /// <summary>Both alerts clear this far back inside their threshold, so a reading at the edge does not flap.</summary>
    internal const double PathLossHysteresisDb = 0.5;
    /// <summary>Readings that make up "now": one outlier poll does not raise or clear anything.</summary>
    private const int PathLossRecentReadings = 3;
    /// <summary>Readings before there is a level to compare against.</summary>
    internal const int PathLossBaselineReadings = 6;
    /// <summary>How slowly the learned level follows the loss: the seasons move it, a fault does not.</summary>
    private static readonly TimeSpan PathLossBaselineTime = TimeSpan.FromDays(1);

    /// <summary>The upper loss limit of each ODN class the PON-ID message can name (G.984.2), in dB.</summary>
    /// <summary>How far back the stored history is read for the usual path loss after a restart.</summary>
    internal static readonly TimeSpan PathLossPriorSpan = TimeSpan.FromDays(7);
    /// <summary>Hourly means the history must hold before its median counts as the usual level.</summary>
    internal const int PathLossPriorHours = 6;

    /// <summary>
    /// The usual path loss from stored hourly means: their median, so a fault of the last days
    /// does not pull it up and still stands out against it. Null when the history is too short.
    /// </summary>
    internal static double? PriorPathLoss(IReadOnlyList<double> hourlyMeans)
    {
        if (hourlyMeans.Count < PathLossPriorHours) return null;
        var sorted = hourlyMeans.OrderBy(v => v).ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    /// <summary>The <c>priorLevel</c> source for <see cref="EvaluatePathLossAsync"/>: one query of the longterm history.</summary>
    public static Func<CancellationToken, Task<double?>> PriorPathLossFrom(
        NetworkOptimizer.Storage.Services.MonitoringInfluxClient influx, string measurement,
        params (string Tag, string Value)[] tags) =>
        async ct =>
        {
            var to = DateTime.UtcNow;
            var series = await influx.QueryFieldSeriesAsync(
                measurement, tags, "path_loss_db", to - PathLossPriorSpan, to, TimeSpan.FromHours(1), ct: ct);
            return PriorPathLoss(series.Select(p => p.Value).ToList());
        };

    private static readonly (string Name, double MaxDb)[] OdnClassLimits =
    {
        ("A", 20), ("B", 25), ("B+", 28), ("C", 30), ("C+", 32),
    };

    /// <summary>
    /// Path loss (the OLT's announced launch power less the ONT's RX reading) against two things:
    /// the budget of the ODN class the OLT names, and the level this ONT usually runs at. The
    /// first is an absolute limit and needs the class; the second needs no class and catches a
    /// connector or splice going bad long before the budget is spent. The usual level is learned
    /// from the first readings after a start and then follows slowly, a few readings behind, and it stands still while
    /// the rise alert is raised, so a fault cannot become the new normal. It is kept in memory,
    /// so after a restart of the application <paramref name="priorLevel"/> is asked once for the
    /// level the stored history shows; without it a fault that is already there is learned as the level.
    /// Only ONTs whose OLT announces its launch power reach here.
    /// </summary>
    public async ValueTask EvaluatePathLossAsync(
        int ontId, string ontName, double? pathLossDb, long? odnClass,
        string? sourceUrl = null, DateTime? now = null, CancellationToken ct = default,
        Func<CancellationToken, Task<double?>>? priorLevel = null)
    {
        if (pathLossDb is not double loss || !double.IsFinite(loss)) return;
        var state = _states.GetOrAdd(ontId, _ => new OntAlertState());
        if (!string.IsNullOrEmpty(sourceUrl)) state.SourceUrl = sourceUrl;
        var at = now ?? DateTime.UtcNow;

        // One reading at a time per ONT: the timer and a manual refresh can both arrive here, and
        // the recent readings and the level are not safe to move from two sides at once.
        await state.PathLossGate.WaitAsync(ct);
        try
        {
            if (priorLevel is not null && !state.PathLossPriorAsked)
            {
                state.PathLossPriorAsked = true;
                try
                {
                    if (state.PathLossBaseline is null && await priorLevel(ct) is double prior && double.IsFinite(prior))
                    {
                        state.PathLossBaseline = prior;
                        state.PathLossBaselineCount = PathLossBaselineReadings;
                        state.PathLossBaselineAt = at;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogDebug(ex, "No stored path loss level for ONT {Name}; learning it from new readings", ontName);
                }
            }

            state.RecentPathLoss.Enqueue(loss);
            double? leaving = null;
            while (state.RecentPathLoss.Count > PathLossRecentReadings) leaving = state.RecentPathLoss.Dequeue();
            var settled = state.RecentPathLoss.Count == PathLossRecentReadings;
            // The lowest of the recent readings for raising, the highest for clearing: every one of
            // them has to agree before the state changes.
            var low = state.RecentPathLoss.Min();
            var high = state.RecentPathLoss.Max();

            if (odnClass is >= 0 and <= 4 && settled)
            {
                var (className, max) = OdnClassLimits[odnClass.Value];
                var level = low > max ? 2 : low > max - PathLossReserveDb ? 1 : 0;
                var holds = high > max - PathLossHysteresisDb ? 2 : high > max - PathLossReserveDb - PathLossHysteresisDb ? 1 : 0;
                if (level > state.PathLossBudgetLevel)
                {
                    state.PathLossBudgetLevel = level;
                    var over = level == 2;
                    await _eventBus.PublishAsync(new AlertEvent
                    {
                        EventType = "ont.path_loss_high",
                        Source = "ont",
                        Severity = over ? AlertSeverity.Error : AlertSeverity.Warning,
                        Title = $"{ontName} path loss {(over ? "over" : "near")} the class limit{_siteSuffix}",
                        Message = over
                            ? $"ONT {ontName} path loss {loss:0.0} dB is over the {max:0} dB limit of ODN class {className}: the link runs outside its budget."
                            : $"ONT {ontName} path loss {loss:0.0} dB leaves {max - loss:0.0} dB to the {max:0} dB limit of ODN class {className}.",
                        DeviceName = ontName,
                        MetricValue = loss,
                        ThresholdValue = over ? max : max - PathLossReserveDb,
                        SourceUrl = state.SourceUrl,
                        Tags = ["ont", "path_loss"],
                        Context = new Dictionary<string, string>
                        {
                            ["ont_id"] = ontId.ToString(),
                            ["metric"] = "path_loss",
                            ["odn_class"] = className
                        }
                    }, ct);
                }
                else if (holds < state.PathLossBudgetLevel)
                {
                    state.PathLossBudgetLevel = holds;
                }
            }

            if (state.PathLossBaseline is double baseline && state.PathLossBaselineCount >= PathLossBaselineReadings && settled)
            {
                if (!state.PathLossRisen && low - baseline >= PathLossRiseDb)
                {
                    state.PathLossRisen = true;
                    await _eventBus.PublishAsync(new AlertEvent
                    {
                        EventType = "ont.path_loss_rise",
                        Source = "ont",
                        Severity = AlertSeverity.Warning,
                        Title = $"{ontName} path loss rose{_siteSuffix}",
                        Message = $"ONT {ontName} path loss {loss:0.0} dB is {loss - baseline:0.0} dB above its usual {baseline:0.0} dB. " +
                            "Check connectors, the patch cable and bends first; a loss that rose with the launch power unchanged is on the fibre path.",
                        DeviceName = ontName,
                        MetricValue = loss,
                        ThresholdValue = Math.Round(baseline + PathLossRiseDb, 1),
                        SourceUrl = state.SourceUrl,
                        Tags = ["ont", "path_loss"],
                        Context = new Dictionary<string, string>
                        {
                            ["ont_id"] = ontId.ToString(),
                            ["metric"] = "path_loss"
                        }
                    }, ct);
                }
                else if (state.PathLossRisen && high - baseline < PathLossRiseDb - PathLossHysteresisDb)
                {
                    state.PathLossRisen = false;
                }
            }

            // Learn from the reading that just left the recent ones, not from the newest: the level
            // must not hold any reading it is being compared with, or a step of exactly the threshold
            // pulls it up while it is still being confirmed and never raises.
            if (!state.PathLossRisen && leaving is double learned)
            {
                if (state.PathLossBaseline is not double level || state.PathLossBaselineCount < PathLossBaselineReadings)
                {
                    var n = state.PathLossBaselineCount;
                    state.PathLossBaseline = ((state.PathLossBaseline ?? 0) * n + learned) / (n + 1);
                    state.PathLossBaselineCount = n + 1;
                }
                else
                {
                    var elapsed = at - state.PathLossBaselineAt;
                    var weight = elapsed > TimeSpan.Zero ? 1 - Math.Exp(-elapsed / PathLossBaselineTime) : 0;
                    state.PathLossBaseline = level + (learned - level) * weight;
                }
                state.PathLossBaselineAt = at;
            }
        }
        finally
        {
            state.PathLossGate.Release();
        }
    }

    /// <summary>
    /// Flags an attached ONT whose own page stops answering while its SFP module's DDM still reads
    /// light: the optics are fine and the management side has hung. A DDM read below the RX low
    /// threshold (or none) is a fiber or optic fault, which the RX and PON link alerts own.
    /// </summary>
    public async ValueTask EvaluateManagementAsync(
        int ontId, string ontName, bool pollSucceeded, double? ddmRxPowerDbm, double rxPowerLowDbm,
        string? sourceUrl = null, CancellationToken ct = default)
    {
        var state = _states.GetOrAdd(ontId, _ => new OntAlertState());
        if (pollSucceeded)
        {
            state.FailedPolls = 0;
            state.ManagementDown = false;
            return;
        }

        var hasLight = ddmRxPowerDbm is double rx && rx >= rxPowerLowDbm;
        state.FailedPolls = hasLight ? state.FailedPolls + 1 : 0;
        if (state.ManagementDown || state.FailedPolls < PonThresholds.OntManagementFailedPolls) return;

        state.ManagementDown = true;
        _logger.LogDebug("ONT {Name} unresponsive for {Polls} polls with DDM RX {Rx} dBm", ontName, state.FailedPolls, ddmRxPowerDbm);

        await _eventBus.PublishAsync(new AlertEvent
        {
            EventType = "ont.management_unresponsive",
            Source = "ont",
            Severity = AlertSeverity.Warning,
            Title = $"{ontName} not responding{_siteSuffix}",
            Message = $"ONT {ontName} has not answered {state.FailedPolls} polls in a row while its SFP module reads " +
                $"{ddmRxPowerDbm:0.##} dBm RX. The optics are fine; the module's management side may be hung.",
            DeviceName = ontName,
            MetricValue = state.FailedPolls,
            ThresholdValue = PonThresholds.OntManagementFailedPolls,
            SourceUrl = string.IsNullOrEmpty(sourceUrl) ? state.SourceUrl : sourceUrl,
            Tags = ["ont", "management"],
            Context = new Dictionary<string, string>
            {
                ["ont_id"] = ontId.ToString(),
                ["metric"] = "management"
            }
        }, ct);
    }

    /// <summary>
    /// Flags an ONT whose uptime went backwards since the last poll, which only a reboot causes.
    /// The first reading only sets the baseline.
    /// </summary>
    private async ValueTask CheckReboot(
        OntAlertState state, int ontId, string ontName, long uptimeSeconds, CancellationToken ct)
    {
        var previous = state.PreviousUptimeSeconds;
        state.PreviousUptimeSeconds = uptimeSeconds;
        if (previous is not long prev || uptimeSeconds >= prev) return;

        _logger.LogDebug("ONT {Name} uptime went from {Prev} s to {Now} s", ontName, prev, uptimeSeconds);

        await _eventBus.PublishAsync(new AlertEvent
        {
            EventType = "ont.rebooted",
            Source = "ont",
            Severity = AlertSeverity.Warning,
            Title = $"{ontName} rebooted{_siteSuffix}",
            Message = $"ONT {ontName} restarted: its uptime went from {TimeSpan.FromSeconds(prev):d\\.hh\\:mm\\:ss} " +
                $"to {TimeSpan.FromSeconds(uptimeSeconds):d\\.hh\\:mm\\:ss}.",
            DeviceName = ontName,
            MetricValue = uptimeSeconds,
            SourceUrl = state.SourceUrl,
            Tags = ["ont", "reboot"],
            Context = new Dictionary<string, string>
            {
                ["ont_id"] = ontId.ToString(),
                ["metric"] = "uptime"
            }
        }, ct);
    }

    private async ValueTask CheckRxPower(
        OntAlertState state, int ontId, string ontName, double rxPower, double rxPowerLowDbm, CancellationToken ct)
    {
        if (rxPower < rxPowerLowDbm && !state.RxPowerBreached)
        {
            state.RxPowerBreached = true;
            _logger.LogDebug("ONT {Name} RX power {Power} dBm below threshold {Threshold} dBm",
                ontName, rxPower, rxPowerLowDbm);

            await _eventBus.PublishAsync(new AlertEvent
            {
                EventType = "ont.rx_power_low",
                Source = "ont",
                Severity = AlertSeverity.Warning,
                Title = $"{ontName} RX power low{_siteSuffix}",
                Message = $"ONT {ontName} optical RX power {rxPower:0.##} dBm is below {rxPowerLowDbm:0.##} dBm threshold.",
                DeviceName = ontName,
                MetricValue = rxPower,
                ThresholdValue = rxPowerLowDbm,
                SourceUrl = state.SourceUrl,
                Tags = ["ont", "rx_power"],
                Context = new Dictionary<string, string>
                {
                    ["ont_id"] = ontId.ToString(),
                    ["metric"] = "rx_power"
                }
            }, ct);
        }
        else if (rxPower >= rxPowerLowDbm + RxPowerHysteresisDbm && state.RxPowerBreached)
        {
            state.RxPowerBreached = false;
        }
    }

    /// <summary>
    /// Flags a sustained high transceiver temperature. Only ONTs whose provider reports a
    /// temperature reach here; the poll passes a null reading otherwise and no alert is
    /// possible. Clears with hysteresis so a reading hovering at the threshold doesn't flap.
    /// </summary>
    private async ValueTask CheckTemperature(
        OntAlertState state, int ontId, string ontName, double tempC, double tempHighC, CancellationToken ct)
    {
        if (tempC > tempHighC && !state.TempBreached)
        {
            state.TempBreached = true;
            _logger.LogDebug("ONT {Name} temperature {Temp} C above threshold {Threshold} C",
                ontName, tempC, tempHighC);

            await _eventBus.PublishAsync(new AlertEvent
            {
                EventType = "ont.high_temperature",
                Source = "ont",
                Severity = AlertSeverity.Warning,
                Title = $"{ontName} temperature high{_siteSuffix}",
                Message = $"ONT {ontName} temperature {tempC:0.#} °C is above {tempHighC:0} °C threshold.",
                DeviceName = ontName,
                MetricValue = tempC,
                ThresholdValue = tempHighC,
                SourceUrl = state.SourceUrl,
                Tags = ["ont", "temperature"],
                Context = new Dictionary<string, string>
                {
                    ["ont_id"] = ontId.ToString(),
                    ["metric"] = "temperature"
                }
            }, ct);
        }
        else if (tempC <= tempHighC - TempHysteresisC && state.TempBreached)
        {
            state.TempBreached = false;
        }
    }

    private async ValueTask CheckPonLink(
        OntAlertState state, int ontId, string ontName, PonLinkState ponLinkStatus, CancellationToken ct)
    {
        var isDown = ponLinkStatus != PonLinkState.Operation && ponLinkStatus != PonLinkState.Unknown;

        if (isDown && !state.PonLinkDown)
        {
            state.PonLinkDown = true;
            _logger.LogDebug("ONT {Name} PON link down (state: {State})", ontName, ponLinkStatus);

            await _eventBus.PublishAsync(new AlertEvent
            {
                EventType = "ont.pon_link_down",
                Source = "ont",
                Severity = AlertSeverity.Error,
                Title = $"{ontName} PON link down{_siteSuffix}",
                Message = $"ONT {ontName} PON link is down (state: {ponLinkStatus}).",
                DeviceName = ontName,
                SourceUrl = state.SourceUrl,
                Tags = ["ont", "pon_link"],
                Context = new Dictionary<string, string>
                {
                    ["ont_id"] = ontId.ToString(),
                    ["pon_link_state"] = ponLinkStatus.ToString()
                }
            }, ct);
        }
        else if (!isDown && state.PonLinkDown)
        {
            state.PonLinkDown = false;
        }
    }

    private ValueTask CheckFecErrors(
        OntAlertState state, int ontId, string ontName, long fecErrors, CancellationToken ct) =>
        CheckErrorSpike(ontId, ontName, fecErrors, state.PreviousFecErrors, FecErrorDeltaThreshold,
            "ont.fec_errors", "FEC error", "FEC errors", "fec", "fec_delta",
            v => state.PreviousFecErrors = v, state.SourceUrl, ct);

    private ValueTask CheckBipErrors(
        OntAlertState state, int ontId, string ontName, long bipErrors, bool? fecEnabled,
        long? uncorrectableDelta, CancellationToken ct)
    {
        // BIP is uncorrected data loss only when payload FEC is off; with FEC on (or unknown,
        // the standalone-ONT default) it counts pre-FEC line errors FEC corrects, so relax the
        // threshold to avoid flagging a healthy FEC-enabled link at its normal operating point.
        var threshold = fecEnabled == false ? BipErrorStrictThreshold : BipErrorRelaxedThreshold;

        // A BIP spike the correction layers absorbed entirely is a link degrading, not one losing
        // data, so it drops to Info and leaves Warning to mean something reached the payload. Where
        // the ONT reports no uncorrectable counter there is nothing to check it against, and the
        // spike keeps its full severity rather than being quietly discounted.
        // Reads with the pre-FEC assumption above: if BIP ever proves to be measured post-FEC on
        // some hardware, this and that threshold relaxation are wrong together, not separately.
        var severity = uncorrectableDelta == 0 ? AlertSeverity.Info : AlertSeverity.Warning;

        return CheckErrorSpike(ontId, ontName, bipErrors, state.PreviousBipErrors, threshold,
            "ont.bip_errors", "BIP error", "BIP errors", "bip", "bip_delta",
            v => state.PreviousBipErrors = v, state.SourceUrl, ct, severity);
    }

    /// <summary>Per-poll increase, or null with no baseline. A counter reset counts as zero.</summary>
    private static long? SpikeDelta(long? current, long? previous) =>
        current is long c && previous is long p ? Math.Max(0, c - p) : null;

    private ValueTask CheckHecErrors(
        OntAlertState state, int ontId, string ontName, long hecErrors, CancellationToken ct) =>
        CheckErrorSpike(ontId, ontName, hecErrors, state.PreviousHecErrors, HecErrorDeltaThreshold,
            "ont.hec_errors", "HEC error", "HEC errors", "hec", "hec_delta",
            v => state.PreviousHecErrors = v, state.SourceUrl, ct);

    /// <summary>
    /// Shared per-poll error-counter spike check. Fires when the increase since the last
    /// poll exceeds the threshold; a negative step (ONT counter reset) counts as zero, so
    /// a reboot never fakes a spike. The first reading only establishes the baseline.
    /// </summary>
    private async ValueTask CheckErrorSpike(
        int ontId, string ontName, long current, long? previous, long threshold,
        string eventType, string spikeLabel, string countLabel, string metricTag, string deltaKey,
        Action<long> setPrevious, string sourceUrl, CancellationToken ct,
        AlertSeverity severity = AlertSeverity.Warning)
    {
        if (previous.HasValue)
        {
            var delta = current - previous.Value;
            if (delta < 0) delta = 0; // counter reset

            if (delta > threshold)
            {
                _logger.LogDebug("ONT {Name} {Label} spike: {Delta} since last poll", ontName, spikeLabel, delta);

                await _eventBus.PublishAsync(new AlertEvent
                {
                    EventType = eventType,
                    Source = "ont",
                    Severity = severity,
                    Title = $"{ontName} {spikeLabel} spike{_siteSuffix}",
                    Message = $"ONT {ontName} had {delta:N0} {countLabel} since last poll (threshold: {threshold:N0}).",
                    DeviceName = ontName,
                    MetricValue = delta,
                    ThresholdValue = threshold,
                    SourceUrl = sourceUrl,
                    Tags = ["ont", metricTag],
                    Context = new Dictionary<string, string>
                    {
                        ["ont_id"] = ontId.ToString(),
                        ["metric"] = $"{metricTag}_errors",
                        [deltaKey] = delta.ToString()
                    }
                }, ct);
            }
        }

        setPrevious(current);
    }

    private class OntAlertState
    {
        public bool RxPowerBreached;
        public bool PonLinkDown;
        public bool TempBreached;
        public long? PreviousFecErrors;
        public long? PreviousBipErrors;
        public long? PreviousHecErrors;
        public long? PreviousUptimeSeconds;
        public int FailedPolls;
        public bool ManagementDown;
        public string SourceUrl = DefaultSourceUrl;
        public readonly Queue<double> RecentPathLoss = new();
        /// <summary>0 inside the budget, 1 within the reserve of the limit, 2 over it.</summary>
        public int PathLossBudgetLevel;
        public bool PathLossRisen;
        public bool PathLossPriorAsked;
        public readonly SemaphoreSlim PathLossGate = new(1, 1);
        public double? PathLossBaseline;
        public int PathLossBaselineCount;
        public DateTime PathLossBaselineAt;
    }
}
