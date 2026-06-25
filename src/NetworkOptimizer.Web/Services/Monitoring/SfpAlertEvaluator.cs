using System.Collections.Concurrent;
using NetworkOptimizer.Alerts.Events;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>
/// Evaluates SFP DDM readings against thresholds and publishes alert events on
/// state transitions (normal->breached). PON and AE modules have tighter thresholds
/// than generic SFP+ since their operating envelopes are well-defined.
/// </summary>
public class SfpAlertEvaluator
{
    private const double TempHysteresisC = PonThresholds.TempHysteresisC;
    private const double PowerHysteresisDbm = PonThresholds.PowerHysteresisDbm;

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
        SfpCategory category,
        double? rxPowerDbm, double? txPowerDbm, double? temperatureC,
        SfpDdmThresholds thresholds,
        CancellationToken ct = default)
    {
        var key = $"{deviceMac}:{portName}";
        var state = _states.GetOrAdd(key, _ => new SfpAlertState());
        var portLabel = deviceName != null ? $"{deviceName} port {portName}" : $"port {portName}";
        var catLabel = category switch { SfpCategory.Pon => "PON", SfpCategory.ActiveEthernet => "AE", _ => "SFP" };

        if (temperatureC.HasValue)
        {
            var threshold = category switch
            {
                SfpCategory.Pon => thresholds.PonTempHighC,
                SfpCategory.ActiveEthernet => thresholds.AeTempHighC,
                _ => thresholds.SfpTempHighGenericC
            };
            await CheckHighThreshold(state, "temp", temperatureC.Value, threshold, TempHysteresisC,
                "monitoring.sfp_temperature",
                $"SFP temperature on {portLabel}",
                $"SFP temperature {temperatureC.Value:0.#} C exceeds {threshold} C threshold on {portLabel}",
                deviceMac, portName, deviceName, category, ct);
        }

        if (category != SfpCategory.Standard && rxPowerDbm.HasValue)
        {
            var rxThreshold = category == SfpCategory.Pon ? thresholds.PonRxPowerLowDbm : thresholds.AeRxPowerLowDbm;
            await CheckLowThreshold(state, "rx", rxPowerDbm.Value, rxThreshold, PowerHysteresisDbm,
                "monitoring.sfp_rx_power",
                $"{catLabel} RX power on {portLabel}",
                $"{catLabel} RX power {rxPowerDbm.Value:0.##} dBm is below {rxThreshold} dBm on {portLabel}",
                deviceMac, portName, deviceName, category, ct);
        }

        if (category != SfpCategory.Standard && txPowerDbm.HasValue)
        {
            var txThreshold = category == SfpCategory.Pon ? thresholds.PonTxPowerHighDbm : thresholds.AeTxPowerHighDbm;
            await CheckHighThreshold(state, "tx", txPowerDbm.Value, txThreshold, PowerHysteresisDbm,
                "monitoring.sfp_tx_power",
                $"{catLabel} TX power on {portLabel}",
                $"{catLabel} TX power {txPowerDbm.Value:0.##} dBm exceeds {txThreshold} dBm on {portLabel}",
                deviceMac, portName, deviceName, category, ct);
        }
    }

    private async ValueTask CheckHighThreshold(
        SfpAlertState state, string metric, double value, double threshold, double hysteresis,
        string eventType, string title, string message,
        string deviceMac, string portName, string? deviceName, SfpCategory category,
        CancellationToken ct)
    {
        var wasBreached = state.Breached.Contains(metric);

        if (value > threshold && !wasBreached)
        {
            state.Breached.Add(metric);
            await PublishEvent(eventType, title, message, deviceMac, portName, deviceName, category, value, threshold, metric, ct);
        }
        else if (value <= threshold - hysteresis && wasBreached)
        {
            state.Breached.Remove(metric);
        }
    }

    private async ValueTask CheckLowThreshold(
        SfpAlertState state, string metric, double value, double threshold, double hysteresis,
        string eventType, string title, string message,
        string deviceMac, string portName, string? deviceName, SfpCategory category,
        CancellationToken ct)
    {
        var wasBreached = state.Breached.Contains(metric);

        if (value < threshold && !wasBreached)
        {
            state.Breached.Add(metric);
            await PublishEvent(eventType, title, message, deviceMac, portName, deviceName, category, value, threshold, metric, ct);
        }
        else if (value >= threshold + hysteresis && wasBreached)
        {
            state.Breached.Remove(metric);
        }
    }

    private async ValueTask PublishEvent(
        string eventType, string title, string message,
        string deviceMac, string portName, string? deviceName, SfpCategory category,
        double value, double threshold, string metric,
        CancellationToken ct)
    {
        _logger.LogDebug("SFP threshold breached: {EventType} on {DeviceMac} port {Port} ({Metric}={Value})",
            eventType, deviceMac, portName, metric, value);

        var catTag = category switch { SfpCategory.Pon => "pon", SfpCategory.ActiveEthernet => "ae", _ => "sfp" };

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
            Tags = ["monitoring", "sfp", catTag],
            Context = new Dictionary<string, string>
            {
                ["device_mac"] = deviceMac,
                ["port_name"] = portName,
                ["metric"] = metric,
                ["sfp_category"] = category.ToString()
            }
        }, ct);
    }

    private class SfpAlertState
    {
        public HashSet<string> Breached { get; } = new();
    }
}
