using System.Collections.Concurrent;
using NetworkOptimizer.Alerts.Events;
using NetworkOptimizer.Core.Enums;

namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>
/// Evaluates gateway CPU and memory readings against thresholds and publishes
/// alert events on state transitions. CPU uses a sliding window of 5 samples
/// (~2.5-5 minutes at typical poll intervals) to avoid alerting on transient
/// spikes. Memory is evaluated per-sample since sustained high memory is
/// immediately actionable.
///
/// Temperature is evaluated for gateways and switches against a user-configurable
/// high threshold (per device type, falling back to <see cref="DefaultDeviceTempHighC"/>).
/// </summary>
public class DeviceHealthAlertEvaluator
{
    private const int CpuWindowSize = 5;
    private const double CpuHighThresholdPercent = 70.0;
    private const double CpuClearThresholdPercent = 55.0;
    private const double MemoryHighThresholdPercent = 95.0;
    private const double MemoryClearThresholdPercent = 85.0;

    /// <summary>Default high-temperature alert threshold (Celsius) when the user hasn't set one.</summary>
    public const double DefaultDeviceTempHighC = 85.0;

    // Temperature must drop this far below the threshold before the alert re-arms,
    // preventing flapping for devices hovering near their limit.
    private const double TempClearMarginC = 5.0;

    private readonly IAlertEventBus _eventBus;
    private readonly ILogger<DeviceHealthAlertEvaluator> _logger;
    private readonly ConcurrentDictionary<string, DeviceHealthState> _states = new();
    private readonly string _siteSuffix;

    /// <param name="siteSlug">
    /// Site this instance evaluates for (one instance per site, owned by
    /// <see cref="MonitoringAlertRegistry"/>). Non-default sites get their slug
    /// appended to alert titles; the default site reads exactly as before.
    /// </param>
    public DeviceHealthAlertEvaluator(IAlertEventBus eventBus, ILogger<DeviceHealthAlertEvaluator> logger,
        string siteSlug = SiteManagementService.DefaultSiteSlug)
    {
        _eventBus = eventBus;
        _logger = logger;
        _siteSuffix = string.IsNullOrEmpty(siteSlug) || siteSlug == SiteManagementService.DefaultSiteSlug
            ? "" : $" (site {siteSlug})";
    }

    public async ValueTask EvaluateAsync(
        string deviceMac, string? deviceName, string deviceType,
        double? cpuPercent, double? memoryUsedPercent,
        double? temperatureC = null, double? tempHighThresholdC = null,
        CancellationToken ct = default)
    {
        var isGateway = string.Equals(deviceType, "gateway", StringComparison.OrdinalIgnoreCase);
        var isSwitch = string.Equals(deviceType, "switch", StringComparison.OrdinalIgnoreCase);

        // CPU and memory alerting is gateway-only; temperature covers gateways and switches.
        if (!isGateway && !isSwitch)
            return;

        var state = _states.GetOrAdd(deviceMac, _ => new DeviceHealthState());
        var label = deviceName ?? deviceMac;

        if (isGateway && cpuPercent.HasValue)
        {
            state.CpuWindow.Enqueue(cpuPercent.Value);
            while (state.CpuWindow.Count > CpuWindowSize) state.CpuWindow.Dequeue();

            if (state.CpuWindow.Count >= CpuWindowSize)
            {
                var avg = state.CpuWindow.Average();

                if (!state.CpuBreached && avg >= CpuHighThresholdPercent)
                {
                    state.CpuBreached = true;
                    _logger.LogDebug("Gateway CPU threshold breached: {DeviceMac} avg={Avg:0.#}%", deviceMac, avg);

                    await _eventBus.PublishAsync(new AlertEvent
                    {
                        EventType = "device.gateway_high_cpu",
                        Source = "device",
                        Severity = AlertSeverity.Warning,
                        Title = $"{label} CPU usage high{_siteSuffix}",
                        Message = $"Gateway {label} CPU averaged {avg:0.#}% over the last {CpuWindowSize} samples, exceeding the {CpuHighThresholdPercent}% threshold.",
                        DeviceId = deviceMac,
                        DeviceName = deviceName,
                        MetricValue = avg,
                        ThresholdValue = CpuHighThresholdPercent,
                        SourceUrl = "/monitoring?tab=devices",
                        Tags = ["device", "gateway", "cpu"],
                        Context = new Dictionary<string, string>
                        {
                            ["device_mac"] = deviceMac,
                            ["device_type"] = deviceType,
                            ["metric"] = "cpu_percent"
                        }
                    }, ct);
                }
                else if (state.CpuBreached && avg <= CpuClearThresholdPercent)
                {
                    state.CpuBreached = false;
                }
            }
        }

        if (isGateway && memoryUsedPercent.HasValue)
        {
            if (!state.MemoryBreached && memoryUsedPercent.Value >= MemoryHighThresholdPercent)
            {
                state.MemoryBreached = true;
                _logger.LogDebug("Gateway memory threshold breached: {DeviceMac} mem={Mem:0.#}%", deviceMac, memoryUsedPercent.Value);

                await _eventBus.PublishAsync(new AlertEvent
                {
                    EventType = "device.gateway_high_memory",
                    Source = "device",
                    Severity = AlertSeverity.Warning,
                    Title = $"{label} memory usage high{_siteSuffix}",
                    Message = $"Gateway {label} memory usage at {memoryUsedPercent.Value:0.#}%, exceeding the {MemoryHighThresholdPercent}% threshold.",
                    DeviceId = deviceMac,
                    DeviceName = deviceName,
                    MetricValue = memoryUsedPercent.Value,
                    ThresholdValue = MemoryHighThresholdPercent,
                    SourceUrl = "/monitoring?tab=devices",
                    Tags = ["device", "gateway", "memory"],
                    Context = new Dictionary<string, string>
                    {
                        ["device_mac"] = deviceMac,
                        ["device_type"] = deviceType,
                        ["metric"] = "memory_used_percent"
                    }
                }, ct);
            }
            else if (state.MemoryBreached && memoryUsedPercent.Value <= MemoryClearThresholdPercent)
            {
                state.MemoryBreached = false;
            }
        }

        if (temperatureC.HasValue)
        {
            var threshold = tempHighThresholdC ?? DefaultDeviceTempHighC;
            var typeLabel = isGateway ? "Gateway" : "Switch";

            if (!state.TempBreached && temperatureC.Value >= threshold)
            {
                state.TempBreached = true;
                _logger.LogDebug("Device temperature threshold breached: {DeviceMac} temp={Temp:0.#}C threshold={Threshold:0.#}C",
                    deviceMac, temperatureC.Value, threshold);

                await _eventBus.PublishAsync(new AlertEvent
                {
                    EventType = "device.high_temperature",
                    Source = "device",
                    Severity = AlertSeverity.Warning,
                    Title = $"{label} temperature high{_siteSuffix}",
                    Message = $"{typeLabel} {label} temperature at {temperatureC.Value:0.#} C, exceeding the {threshold:0.#} C threshold.",
                    DeviceId = deviceMac,
                    DeviceName = deviceName,
                    MetricValue = temperatureC.Value,
                    ThresholdValue = threshold,
                    SourceUrl = "/monitoring?tab=devices",
                    Tags = ["device", deviceType, "temperature"],
                    Context = new Dictionary<string, string>
                    {
                        ["device_mac"] = deviceMac,
                        ["device_type"] = deviceType,
                        ["metric"] = "temperature_c"
                    }
                }, ct);
            }
            else if (state.TempBreached && temperatureC.Value <= threshold - TempClearMarginC)
            {
                state.TempBreached = false;
            }
        }
    }

    private class DeviceHealthState
    {
        public Queue<double> CpuWindow { get; } = new();
        public bool CpuBreached;
        public bool MemoryBreached;
        public bool TempBreached;
    }
}
