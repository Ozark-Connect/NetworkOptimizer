using System.Collections.Concurrent;
using NetworkOptimizer.Alerts;
using NetworkOptimizer.Alerts.Events;
using NetworkOptimizer.Alerts.Interfaces;
using NetworkOptimizer.Alerts.Models;
using NetworkOptimizer.Core.Enums;

namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>
/// Evaluates gateway CPU and memory readings against thresholds and publishes
/// alert events on state transitions. CPU uses a sliding window of 5 samples
/// (~2.5-5 minutes at typical poll intervals) to avoid alerting on transient
/// spikes. Memory is evaluated per-sample since sustained high memory is
/// immediately actionable. Both thresholds come from the Threshold % on the site's
/// Gateway: High CPU / Gateway: High Memory rules.
///
/// Temperature is evaluated for gateways and switches against a user-configurable
/// high threshold (per device type, falling back to <see cref="DefaultDeviceTempHighC"/>).
///
/// WARNING: the Message format strings below are parsed by
/// <c>DeviceHealthChartEndpoints.AlertReading</c> to extract the metric value for the
/// collapsed chart tooltip. If you change a Message format or add a new health alert type,
/// update AlertReading to match.
/// </summary>
public class DeviceHealthAlertEvaluator
{
    internal const string HighCpuEventType = "device.gateway_high_cpu";
    internal const string HighMemoryEventType = "device.gateway_high_memory";
    internal const string HighTemperatureEventType = "device.high_temperature";

    private const int CpuWindowSize = 5;

    // Defaults for when the site has no enabled rule with a Threshold % for the event type.
    // The rule's threshold wins otherwise; the clear level sits a fixed margin below it.
    private const double DefaultCpuHighThresholdPercent = 70.0;
    private const double CpuClearMarginPercent = 15.0;
    private const double DefaultMemoryHighThresholdPercent = 95.0;
    private const double MemoryClearMarginPercent = 10.0;

    private static readonly TimeSpan RuleCacheDuration = TimeSpan.FromSeconds(60);

    /// <summary>Default high-temperature alert threshold (Celsius) when the user hasn't set one.</summary>
    public const double DefaultDeviceTempHighC = 85.0;

    // Temperature must drop this far below the threshold before the alert re-arms,
    // preventing flapping for devices hovering near their limit.
    private const double TempClearMarginC = 5.0;

    private readonly IAlertEventBus _eventBus;
    private readonly ILogger<DeviceHealthAlertEvaluator> _logger;
    private readonly ConcurrentDictionary<string, DeviceHealthState> _states = new();
    private readonly string _siteSuffix;
    private readonly string _siteSlug;
    private readonly IServiceScopeFactory? _scopeFactory;
    private RuleSnapshot? _ruleCache;

    /// <param name="siteSlug">
    /// Site this instance evaluates for (one instance per site, owned by
    /// <see cref="MonitoringAlertRegistry"/>). Non-default sites get their slug
    /// appended to alert titles; the default site reads exactly as before.
    /// </param>
    /// <param name="scopeFactory">
    /// Reads the site's alert rules for the CPU and memory thresholds. Null uses the defaults.
    /// </param>
    public DeviceHealthAlertEvaluator(IAlertEventBus eventBus, ILogger<DeviceHealthAlertEvaluator> logger,
        string siteSlug = SiteManagementService.DefaultSiteSlug, IServiceScopeFactory? scopeFactory = null)
    {
        _eventBus = eventBus;
        _logger = logger;
        _siteSlug = siteSlug;
        _scopeFactory = scopeFactory;
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
                var cpuThreshold = await GetThresholdAsync(HighCpuEventType, DefaultCpuHighThresholdPercent, ct);

                if (!state.CpuBreached && avg >= cpuThreshold)
                {
                    state.CpuBreached = true;
                    _logger.LogDebug("Gateway CPU threshold breached: {DeviceMac} avg={Avg:0.#}%", deviceMac, avg);

                    await _eventBus.PublishAsync(new AlertEvent
                    {
                        EventType = HighCpuEventType,
                        Source = "device",
                        Severity = AlertSeverity.Warning,
                        Title = $"{label} CPU usage high{_siteSuffix}",
                        Message = $"Gateway {label} CPU averaged {avg:0.#}% over the last {CpuWindowSize} samples, exceeding the {cpuThreshold:0.#}% threshold.",
                        DeviceId = deviceMac,
                        DeviceName = deviceName,
                        MetricValue = avg,
                        ThresholdValue = cpuThreshold,
                        SourceUrl = MonitoringLinks.DeviceStats(deviceMac, MonitoringLinks.NowMs()),
                        Tags = ["device", "gateway", "cpu"],
                        Context = new Dictionary<string, string>
                        {
                            ["device_mac"] = deviceMac,
                            ["device_type"] = deviceType,
                            ["metric"] = "cpu_percent",
                            [AlertRuleEvaluator.ValuePercentContextKey] = avg.ToString("0.###")
                        }
                    }, ct);
                }
                else if (state.CpuBreached && avg <= cpuThreshold - CpuClearMarginPercent)
                {
                    state.CpuBreached = false;
                }
            }
        }

        if (isGateway && memoryUsedPercent.HasValue)
        {
            var memoryThreshold = await GetThresholdAsync(HighMemoryEventType, DefaultMemoryHighThresholdPercent, ct);

            if (!state.MemoryBreached && memoryUsedPercent.Value >= memoryThreshold)
            {
                state.MemoryBreached = true;
                _logger.LogDebug("Gateway memory threshold breached: {DeviceMac} mem={Mem:0.#}%", deviceMac, memoryUsedPercent.Value);

                await _eventBus.PublishAsync(new AlertEvent
                {
                    EventType = HighMemoryEventType,
                    Source = "device",
                    Severity = AlertSeverity.Warning,
                    Title = $"{label} memory usage high{_siteSuffix}",
                    Message = $"Gateway {label} memory usage at {memoryUsedPercent.Value:0.#}%, exceeding the {memoryThreshold:0.#}% threshold.",
                    DeviceId = deviceMac,
                    DeviceName = deviceName,
                    MetricValue = memoryUsedPercent.Value,
                    ThresholdValue = memoryThreshold,
                    SourceUrl = MonitoringLinks.DeviceStats(deviceMac, MonitoringLinks.NowMs()),
                    Tags = ["device", "gateway", "memory"],
                    Context = new Dictionary<string, string>
                    {
                        ["device_mac"] = deviceMac,
                        ["device_type"] = deviceType,
                        ["metric"] = "memory_used_percent",
                        [AlertRuleEvaluator.ValuePercentContextKey] = memoryUsedPercent.Value.ToString("0.###")
                    }
                }, ct);
            }
            else if (state.MemoryBreached && memoryUsedPercent.Value <= memoryThreshold - MemoryClearMarginPercent)
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
                    EventType = HighTemperatureEventType,
                    Source = "device",
                    Severity = AlertSeverity.Warning,
                    Title = $"{label} temperature high{_siteSuffix}",
                    Message = $"{typeLabel} {label} temperature at {temperatureC.Value:0.#} °C, exceeding the {threshold:0.#} °C threshold.",
                    DeviceId = deviceMac,
                    DeviceName = deviceName,
                    MetricValue = temperatureC.Value,
                    ThresholdValue = threshold,
                    SourceUrl = MonitoringLinks.DeviceStats(deviceMac, MonitoringLinks.NowMs()),
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

    /// <summary>
    /// The lowest Threshold % among the site's enabled rules for the event type, so every such
    /// rule gets an event it can check; each rule then filters on the reading in the event context.
    /// </summary>
    private async ValueTask<double> GetThresholdAsync(string eventType, double defaultPercent, CancellationToken ct)
    {
        var thresholds = (await GetRulesAsync(ct))
            .Where(r => r.IsEnabled
                && r.ThresholdPercent is > 0
                && AlertRuleEvaluator.MatchesEventType(eventType, r.EventTypePattern)
                && (string.IsNullOrEmpty(r.Source) || string.Equals(r.Source, "device", StringComparison.OrdinalIgnoreCase)))
            .Select(r => r.ThresholdPercent!.Value)
            .ToList();
        return thresholds.Count > 0 ? thresholds.Min() : defaultPercent;
    }

    private async ValueTask<IReadOnlyList<AlertRule>> GetRulesAsync(CancellationToken ct)
    {
        if (_scopeFactory == null)
            return [];

        var cached = _ruleCache;
        if (cached != null && DateTime.UtcNow - cached.CachedAt < RuleCacheDuration)
            return cached.Rules;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            scope.ServiceProvider.GetRequiredService<IAlertSiteScope>().UseSite(_siteSlug);
            var rules = await scope.ServiceProvider.GetRequiredService<IAlertRepository>().GetEnabledRulesAsync(ct);
            _ruleCache = new RuleSnapshot(rules, DateTime.UtcNow);
            return rules;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to read alert rules for gateway health thresholds; using {Source}",
                cached != null ? "the last read" : "defaults");
            return cached?.Rules ?? [];
        }
    }

    private sealed record RuleSnapshot(IReadOnlyList<AlertRule> Rules, DateTime CachedAt);

    private class DeviceHealthState
    {
        public Queue<double> CpuWindow { get; } = new();
        public bool CpuBreached;
        public bool MemoryBreached;
        public bool TempBreached;
    }
}
