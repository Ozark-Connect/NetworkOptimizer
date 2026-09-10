using System.Collections.Concurrent;
using NetworkOptimizer.Alerts.Events;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Core.Helpers;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>
/// Evaluates SFP DDM readings against thresholds and publishes alert events on
/// state transitions (normal->breached). PON and AE modules have tighter thresholds
/// than generic SFP+ since their operating envelopes are well-defined.
/// </summary>
public class SfpAlertEvaluator
{
    /// <summary>Where to turn this off, named as it reads on screen.</summary>
    private const string DdmSpikeHint =
        "If these are chronic, SFP Stats - SFP Alert Thresholds has a setting to ignore single-poll DDM spikes.";

    private const double TempHysteresisC = PonThresholds.TempHysteresisC;
    private const double PowerHysteresisDbm = PonThresholds.PowerHysteresisDbm;

    private readonly IAlertEventBus _eventBus;
    private readonly ILogger<SfpAlertEvaluator> _logger;
    private readonly ConcurrentDictionary<string, SfpAlertState> _states = new();
    private readonly string _siteSuffix;

    /// <param name="siteSlug">
    /// Site this instance evaluates for (one instance per site, owned by
    /// <see cref="MonitoringAlertRegistry"/>). Non-default sites get their slug
    /// appended to alert titles; the default site reads exactly as before.
    /// </param>
    public SfpAlertEvaluator(IAlertEventBus eventBus, ILogger<SfpAlertEvaluator> logger,
        string siteSlug = SiteManagementService.DefaultSiteSlug)
    {
        _eventBus = eventBus;
        _logger = logger;
        _siteSuffix = string.IsNullOrEmpty(siteSlug) || siteSlug == SiteManagementService.DefaultSiteSlug
            ? "" : $" (site {siteSlug})";
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

        var samples = ResolveSamples(state, category, temperatureC, rxPowerDbm, portLabel, thresholds.IgnoreDdmSpikes);
        foreach (var (evalTemp, evalRx, looksLikeArtifact) in samples)
            await EvaluateTempAndRxAsync(state, deviceMac, portName, deviceName, category, catLabel, portLabel,
                evalTemp, evalRx, looksLikeArtifact, thresholds, ct);

        // TX is never deferred: the PON artifact leaves TX power alone - under 0.08 dB across all
        // 19 measured events - so holding it would delay a real TX fault to guard against something
        // that does not touch it.
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

    private async ValueTask EvaluateTempAndRxAsync(
        SfpAlertState state, string deviceMac, string portName, string? deviceName,
        SfpCategory category, string catLabel, string portLabel,
        double? temperatureC, double? rxPowerDbm, bool looksLikeArtifact,
        SfpDdmThresholds thresholds, CancellationToken ct)
    {
        // Only on a reading that matches the pattern, and only while the setting is off - once it
        // is on, a released alert held for two polls and the setting would not have helped.
        var hint = looksLikeArtifact ? " " + DdmSpikeHint : "";
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
                $"SFP temperature {temperatureC.Value:0.#} °C exceeds {threshold} °C threshold on {portLabel}.{hint}",
                deviceMac, portName, deviceName, category, ct);
        }

        if (category != SfpCategory.Standard && rxPowerDbm.HasValue)
        {
            var rxThreshold = category == SfpCategory.Pon ? thresholds.PonRxPowerLowDbm : thresholds.AeRxPowerLowDbm;
            await CheckLowThreshold(state, "rx", rxPowerDbm.Value, rxThreshold, PowerHysteresisDbm,
                "monitoring.sfp_rx_power",
                $"{catLabel} RX power on {portLabel}",
                $"{catLabel} RX power {rxPowerDbm.Value:0.##} dBm is below {rxThreshold} dBm on {portLabel}.{hint}",
                deviceMac, portName, deviceName, category, ct);
        }
    }

    /// <summary>
    /// Which temperature/RX readings this poll evaluates. For anything but a PON stick carrying
    /// both readings, that is simply this poll's own pair.
    ///
    /// On a PON stick a joint temperature+RX jump is held back one poll, because the only thing
    /// separating the module's DDM read artifact from a real fault is whether the next reading
    /// comes back. A confirmed artifact is dropped and never reaches a threshold or an event; a
    /// jump that holds is released and evaluated one poll late. Readings that did not jump are
    /// evaluated immediately, so ordinary alerts are never delayed.
    ///
    /// Never widen this to Active Ethernet or standard optics. The artifact is specific to PON ONT
    /// sticks, and on anything else this only delays real alerts.
    /// </summary>
    private List<(double? Temp, double? Rx, bool LooksLikeArtifact)> ResolveSamples(
        SfpAlertState state, SfpCategory category, double? temperatureC, double? rxPowerDbm,
        string portLabel, bool ignoreSpikes)
    {
        if (category != SfpCategory.Pon || temperatureC is not { } curTemp || rxPowerDbm is not { } curRx)
            return [(temperatureC, rxPowerDbm, false)];

        // Off: nothing is held, but a reading that jumped jointly still carries the pointer to the
        // setting. Whether it returns is unknowable now, and the alert has to go out either way.
        if (!ignoreSpikes)
        {
            var jumped = state.LastTemp is { } lt0 && state.LastRx is { } lr0
                && SfpDdmSpikeFilter.IsJointJump(lt0, lr0, curTemp, curRx);
            state.LastTemp = curTemp;
            state.LastRx = curRx;
            state.PendingTemp = null;
            state.PendingRx = null;
            return [(curTemp, curRx, jumped)];
        }

        var release = new List<(double? Temp, double? Rx, bool LooksLikeArtifact)>();

        if (state.PendingTemp is { } pendTemp && state.PendingRx is { } pendRx
            && state.LastTemp is { } lastTemp && state.LastRx is { } lastRx)
        {
            state.PendingTemp = null;
            state.PendingRx = null;
            if (SfpDdmSpikeFilter.IsArtifact(lastTemp, lastRx, pendTemp, pendRx, curTemp, curRx))
            {
                _logger.LogDebug(
                    "SFP DDM artifact discarded on {Port}: temp {Temp:0.#} C, RX {Rx:0.##} dBm returned to {BackTemp:0.#} C / {BackRx:0.##} dBm",
                    portLabel, pendTemp, pendRx, curTemp, curRx);
            }
            else
            {
                release.Add((pendTemp, pendRx, false));
                state.LastTemp = pendTemp;
                state.LastRx = pendRx;
            }
        }

        if (state.LastTemp is { } prevTemp && state.LastRx is { } prevRx
            && SfpDdmSpikeFilter.IsJointJump(prevTemp, prevRx, curTemp, curRx))
        {
            state.PendingTemp = curTemp;
            state.PendingRx = curRx;
            return release;
        }

        state.LastTemp = curTemp;
        state.LastRx = curRx;
        release.Add((curTemp, curRx, false));
        return release;
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
            Title = $"{title}{_siteSuffix}",
            Message = message,
            DeviceId = deviceMac,
            DeviceName = deviceName,
            MetricValue = value,
            ThresholdValue = threshold,
            SourceUrl = MonitoringLinks.HardwareStats("sfp", DateTime.UtcNow),
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

        /// <summary>Last temperature/RX pair accepted as real, for the PON artifact check.</summary>
        public double? LastTemp { get; set; }
        public double? LastRx { get; set; }

        /// <summary>A joint jump held back until the next poll says whether it returned.</summary>
        public double? PendingTemp { get; set; }
        public double? PendingRx { get; set; }
    }
}
