using System.Collections.Concurrent;
using NetworkOptimizer.Alerts.Events;
using NetworkOptimizer.Core.Enums;

namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>
/// Evaluates SFP DDM readings against thresholds and publishes alert events on
/// state transitions (normal→breached). PON modules have tighter thresholds
/// than generic SFP+ since their operating envelope is well-defined by ITU-T.
/// Non-PON modules get a catch-all temperature threshold only.
/// </summary>
public class SfpAlertEvaluator
{
    private const double PonRxPowerLowDbm = -25.0;
    private const double PonTxPowerHighDbm = 4.0;
    private const double PonTempHighC = 75.0;
    private const double SfpTempHighC = 87.0;

    private const double TempHysteresisC = 5.0;
    private const double PowerHysteresisDbm = 2.0;

    private readonly IAlertEventBus _eventBus;
    private readonly ILogger<SfpAlertEvaluator> _logger;
    private readonly ConcurrentDictionary<string, SfpAlertState> _states = new();

    public SfpAlertEvaluator(IAlertEventBus eventBus, ILogger<SfpAlertEvaluator> logger)
    {
        _eventBus = eventBus;
        _logger = logger;
    }

    public async ValueTask EvaluateAsync(
        string deviceMac, string portName, string? deviceName,
        bool isPon,
        double? rxPowerDbm, double? txPowerDbm, double? temperatureC,
        CancellationToken ct = default)
    {
        var key = $"{deviceMac}:{portName}";
        var state = _states.GetOrAdd(key, _ => new SfpAlertState());
        var portLabel = deviceName != null ? $"{deviceName} port {portName}" : $"port {portName}";

        if (temperatureC.HasValue)
        {
            var threshold = isPon ? PonTempHighC : SfpTempHighC;
            await CheckHighThreshold(state, "temp", temperatureC.Value, threshold, TempHysteresisC,
                "monitoring.sfp_temperature",
                $"SFP temperature on {portLabel}",
                $"SFP temperature {temperatureC.Value:0.#} C exceeds {threshold} C threshold on {portLabel}",
                deviceMac, portName, deviceName, isPon, ct);
        }

        if (isPon && rxPowerDbm.HasValue)
        {
            await CheckLowThreshold(state, "rx", rxPowerDbm.Value, PonRxPowerLowDbm, PowerHysteresisDbm,
                "monitoring.sfp_rx_power",
                $"PON RX power on {portLabel}",
                $"PON RX power {rxPowerDbm.Value:0.##} dBm is below {PonRxPowerLowDbm} dBm on {portLabel}",
                deviceMac, portName, deviceName, isPon, ct);
        }

        if (isPon && txPowerDbm.HasValue)
        {
            await CheckHighThreshold(state, "tx", txPowerDbm.Value, PonTxPowerHighDbm, PowerHysteresisDbm,
                "monitoring.sfp_tx_power",
                $"PON TX power on {portLabel}",
                $"PON TX power {txPowerDbm.Value:0.##} dBm exceeds {PonTxPowerHighDbm} dBm on {portLabel}",
                deviceMac, portName, deviceName, isPon, ct);
        }
    }

    private async ValueTask CheckHighThreshold(
        SfpAlertState state, string metric, double value, double threshold, double hysteresis,
        string eventType, string title, string message,
        string deviceMac, string portName, string? deviceName, bool isPon,
        CancellationToken ct)
    {
        var wasBreached = state.Breached.Contains(metric);

        if (value > threshold && !wasBreached)
        {
            state.Breached.Add(metric);
            await PublishEvent(eventType, title, message, deviceMac, portName, deviceName, isPon, value, threshold, metric, ct);
        }
        else if (value <= threshold - hysteresis && wasBreached)
        {
            state.Breached.Remove(metric);
        }
    }

    private async ValueTask CheckLowThreshold(
        SfpAlertState state, string metric, double value, double threshold, double hysteresis,
        string eventType, string title, string message,
        string deviceMac, string portName, string? deviceName, bool isPon,
        CancellationToken ct)
    {
        var wasBreached = state.Breached.Contains(metric);

        if (value < threshold && !wasBreached)
        {
            state.Breached.Add(metric);
            await PublishEvent(eventType, title, message, deviceMac, portName, deviceName, isPon, value, threshold, metric, ct);
        }
        else if (value >= threshold + hysteresis && wasBreached)
        {
            state.Breached.Remove(metric);
        }
    }

    private async ValueTask PublishEvent(
        string eventType, string title, string message,
        string deviceMac, string portName, string? deviceName, bool isPon,
        double value, double threshold, string metric,
        CancellationToken ct)
    {
        _logger.LogDebug("SFP threshold breached: {EventType} on {DeviceMac} port {Port} ({Metric}={Value})",
            eventType, deviceMac, portName, metric, value);

        await _eventBus.PublishAsync(new AlertEvent
        {
            EventType = eventType,
            Source = "monitoring",
            Severity = AlertSeverity.Warning,
            Title = title,
            Message = message,
            DeviceId = deviceMac,
            DeviceName = deviceName,
            MetricValue = value,
            ThresholdValue = threshold,
            SourceUrl = "/monitoring?tab=sfp",
            Tags = ["monitoring", "sfp", isPon ? "pon" : "sfp"],
            Context = new Dictionary<string, string>
            {
                ["device_mac"] = deviceMac,
                ["port_name"] = portName,
                ["metric"] = metric,
                ["is_pon"] = isPon.ToString()
            }
        }, ct);
    }

    private class SfpAlertState
    {
        public HashSet<string> Breached { get; } = new();
    }
}
