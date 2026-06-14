using System.Collections.Concurrent;
using NetworkOptimizer.Alerts.Events;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Monitoring.Models;

namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>
/// Evaluates external ONT (Optical Network Terminal) readings and publishes alert
/// events on state transitions. Covers RX power degradation, PON link loss, and
/// FEC error spikes - the same failure modes as in-gateway SFP DDM but sourced
/// from the ISP-side device.
/// </summary>
public class OntAlertEvaluator
{
    private const double RxPowerLowDbm = PonThresholds.PonRxPowerLowDbm;
    private const double RxPowerHysteresisDbm = PonThresholds.PowerHysteresisDbm;
    private const long FecErrorDeltaThreshold = 1000;

    private readonly IAlertEventBus _eventBus;
    private readonly ILogger<OntAlertEvaluator> _logger;
    private readonly ConcurrentDictionary<int, OntAlertState> _states = new();

    public OntAlertEvaluator(IAlertEventBus eventBus, ILogger<OntAlertEvaluator> logger)
    {
        _eventBus = eventBus;
        _logger = logger;
    }

    public async ValueTask EvaluateAsync(
        int ontId, string ontName,
        double? rxPowerDbm,
        PonLinkState ponLinkStatus,
        long? fecErrors,
        CancellationToken ct = default)
    {
        var state = _states.GetOrAdd(ontId, _ => new OntAlertState());

        if (rxPowerDbm.HasValue)
        {
            await CheckRxPower(state, ontId, ontName, rxPowerDbm.Value, ct);
        }

        await CheckPonLink(state, ontId, ontName, ponLinkStatus, ct);

        if (fecErrors.HasValue)
        {
            await CheckFecErrors(state, ontId, ontName, fecErrors.Value, ct);
        }
    }

    private async ValueTask CheckRxPower(
        OntAlertState state, int ontId, string ontName, double rxPower, CancellationToken ct)
    {
        if (rxPower < RxPowerLowDbm && !state.RxPowerBreached)
        {
            state.RxPowerBreached = true;
            _logger.LogDebug("ONT {Name} RX power {Power} dBm below threshold {Threshold} dBm",
                ontName, rxPower, RxPowerLowDbm);

            await _eventBus.PublishAsync(new AlertEvent
            {
                EventType = "ont.rx_power_low",
                Source = "ont",
                Severity = AlertSeverity.Warning,
                Title = $"{ontName} RX power low",
                Message = $"ONT {ontName} optical RX power {rxPower:0.##} dBm is below {RxPowerLowDbm} dBm threshold.",
                DeviceName = ontName,
                MetricValue = rxPower,
                ThresholdValue = RxPowerLowDbm,
                SourceUrl = "/monitoring?tab=ont",
                Tags = ["ont", "rx_power"],
                Context = new Dictionary<string, string>
                {
                    ["ont_id"] = ontId.ToString(),
                    ["metric"] = "rx_power"
                }
            }, ct);
        }
        else if (rxPower >= RxPowerLowDbm + RxPowerHysteresisDbm && state.RxPowerBreached)
        {
            state.RxPowerBreached = false;
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
                Title = $"{ontName} PON link down",
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

    private async ValueTask CheckFecErrors(
        OntAlertState state, int ontId, string ontName, long fecErrors, CancellationToken ct)
    {
        if (state.PreviousFecErrors.HasValue)
        {
            var delta = fecErrors - state.PreviousFecErrors.Value;
            if (delta < 0) delta = 0; // counter reset

            if (delta > FecErrorDeltaThreshold)
            {
                _logger.LogDebug("ONT {Name} FEC error spike: {Delta} errors since last poll", ontName, delta);

                await _eventBus.PublishAsync(new AlertEvent
                {
                    EventType = "ont.fec_errors",
                    Source = "ont",
                    Severity = AlertSeverity.Warning,
                    Title = $"{ontName} FEC error spike",
                    Message = $"ONT {ontName} had {delta:N0} FEC errors since last poll (threshold: {FecErrorDeltaThreshold:N0}).",
                    DeviceName = ontName,
                    MetricValue = delta,
                    ThresholdValue = FecErrorDeltaThreshold,
                    SourceUrl = "/monitoring?tab=ont",
                    Tags = ["ont", "fec"],
                    Context = new Dictionary<string, string>
                    {
                        ["ont_id"] = ontId.ToString(),
                        ["metric"] = "fec_errors",
                        ["fec_delta"] = delta.ToString()
                    }
                }, ct);
            }
        }

        state.PreviousFecErrors = fecErrors;
    }

    private class OntAlertState
    {
        public bool RxPowerBreached;
        public bool PonLinkDown;
        public long? PreviousFecErrors;
    }
}
