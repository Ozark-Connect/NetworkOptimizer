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
/// </summary>
public class DeviceHealthAlertEvaluator
{
    private const int CpuWindowSize = 5;
    private const double CpuHighThresholdPercent = 70.0;
    private const double CpuClearThresholdPercent = 55.0;
    private const double MemoryHighThresholdPercent = 95.0;
    private const double MemoryClearThresholdPercent = 85.0;

    private readonly IAlertEventBus _eventBus;
    private readonly ILogger<DeviceHealthAlertEvaluator> _logger;
    private readonly ConcurrentDictionary<string, DeviceHealthState> _states = new();

    public DeviceHealthAlertEvaluator(IAlertEventBus eventBus, ILogger<DeviceHealthAlertEvaluator> logger)
    {
        _eventBus = eventBus;
        _logger = logger;
    }

    public async ValueTask EvaluateAsync(
        string deviceMac, string? deviceName, string deviceType,
        double? cpuPercent, double? memoryUsedPercent,
        CancellationToken ct = default)
    {
        if (!string.Equals(deviceType, "gateway", StringComparison.OrdinalIgnoreCase))
            return;

        var state = _states.GetOrAdd(deviceMac, _ => new DeviceHealthState());
        var label = deviceName ?? deviceMac;

        if (cpuPercent.HasValue)
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
                        Title = $"{label} CPU usage high",
                        Message = $"Gateway {label} CPU averaged {avg:0.#}% over the last {CpuWindowSize} samples, exceeding the {CpuHighThresholdPercent}% threshold.",
                        DeviceId = deviceMac,
                        DeviceName = deviceName,
                        MetricValue = avg,
                        ThresholdValue = CpuHighThresholdPercent,
                        SourceUrl = "/monitoring?tab=health",
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

        if (memoryUsedPercent.HasValue)
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
                    Title = $"{label} memory usage high",
                    Message = $"Gateway {label} memory usage at {memoryUsedPercent.Value:0.#}%, exceeding the {MemoryHighThresholdPercent}% threshold.",
                    DeviceId = deviceMac,
                    DeviceName = deviceName,
                    MetricValue = memoryUsedPercent.Value,
                    ThresholdValue = MemoryHighThresholdPercent,
                    SourceUrl = "/monitoring?tab=health",
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
    }

    private class DeviceHealthState
    {
        public Queue<double> CpuWindow { get; } = new();
        public bool CpuBreached;
        public bool MemoryBreached;
    }
}
