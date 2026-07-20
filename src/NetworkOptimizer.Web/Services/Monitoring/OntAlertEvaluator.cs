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
        CancellationToken ct = default)
    {
        var state = _states.GetOrAdd(ontId, _ => new OntAlertState());

        if (rxPowerDbm.HasValue)
        {
            await CheckRxPower(state, ontId, ontName, rxPowerDbm.Value, rxPowerLowDbm, ct);
        }

        await CheckPonLink(state, ontId, ontName, ponLinkStatus, ct);

        // BIP is always-on regardless of FEC, so it's evaluated whenever reported.
        if (bipErrors.HasValue)
        {
            await CheckBipErrors(state, ontId, ontName, bipErrors.Value, fecEnabled, ct);
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
                SourceUrl = "/monitoring?tab=ont",
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
                Message = $"ONT {ontName} temperature {tempC:0.#} C is above {tempHighC:0} C threshold.",
                DeviceName = ontName,
                MetricValue = tempC,
                ThresholdValue = tempHighC,
                SourceUrl = "/monitoring?tab=ont",
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
                SourceUrl = "/monitoring?tab=ont",
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
            v => state.PreviousFecErrors = v, ct);

    private ValueTask CheckBipErrors(
        OntAlertState state, int ontId, string ontName, long bipErrors, bool? fecEnabled, CancellationToken ct)
    {
        // BIP is uncorrected data loss only when payload FEC is off; with FEC on (or unknown,
        // the standalone-ONT default) it counts pre-FEC line errors FEC corrects, so relax the
        // threshold to avoid flagging a healthy FEC-enabled link at its normal operating point.
        var threshold = fecEnabled == false ? BipErrorStrictThreshold : BipErrorRelaxedThreshold;
        return CheckErrorSpike(ontId, ontName, bipErrors, state.PreviousBipErrors, threshold,
            "ont.bip_errors", "BIP error", "BIP errors", "bip", "bip_delta",
            v => state.PreviousBipErrors = v, ct);
    }

    private ValueTask CheckHecErrors(
        OntAlertState state, int ontId, string ontName, long hecErrors, CancellationToken ct) =>
        CheckErrorSpike(ontId, ontName, hecErrors, state.PreviousHecErrors, HecErrorDeltaThreshold,
            "ont.hec_errors", "HEC error", "HEC errors", "hec", "hec_delta",
            v => state.PreviousHecErrors = v, ct);

    /// <summary>
    /// Shared per-poll error-counter spike check. Fires when the increase since the last
    /// poll exceeds the threshold; a negative step (ONT counter reset) counts as zero, so
    /// a reboot never fakes a spike. The first reading only establishes the baseline.
    /// </summary>
    private async ValueTask CheckErrorSpike(
        int ontId, string ontName, long current, long? previous, long threshold,
        string eventType, string spikeLabel, string countLabel, string metricTag, string deltaKey,
        Action<long> setPrevious, CancellationToken ct)
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
                    Severity = AlertSeverity.Warning,
                    Title = $"{ontName} {spikeLabel} spike{_siteSuffix}",
                    Message = $"ONT {ontName} had {delta:N0} {countLabel} since last poll (threshold: {threshold:N0}).",
                    DeviceName = ontName,
                    MetricValue = delta,
                    ThresholdValue = threshold,
                    SourceUrl = "/monitoring?tab=ont",
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
    }
}
