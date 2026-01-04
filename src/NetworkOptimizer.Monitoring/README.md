# NetworkOptimizer.Monitoring

> **Status: Future Project** - This library is planned but not yet fully implemented. The structure and interfaces below represent the intended design.

SNMP monitoring library for UniFi and network devices with metrics collection, aggregation, and alerting capabilities.

## Features

### SNMP Polling
- **Multi-version support**: SNMP v1, v2c, and v3
- **Secure authentication**: MD5, SHA-1, SHA-256, SHA-384, SHA-512
- **Privacy protocols**: DES, AES-128, AES-192, AES-256
- **High-capacity counters**: 64-bit counters for 10G+ interfaces
- **Interface filtering**: Automatic exclusion of virtual interfaces (br*, veth*, docker*, etc.)

### Metrics Collection
- **System metrics**: CPU, memory, uptime, temperature
- **Interface statistics**: In/out octets, packets, errors, discards
- **UniFi-specific**: Model, firmware version, MAC address
- **Resource monitoring**: CPU usage, memory usage, temperature sensors

### Metrics Aggregation
- **Multi-source support**: SNMP, agents, UniFi API
- **Normalized naming**: Consistent metric names across sources
- **Batching**: Efficient batch processing for storage
- **Tagging**: Rich metadata for filtering and analysis

### Alert Engine
- **Flexible thresholds**: Multiple comparison operators
- **Duration-based**: Trigger only after sustained conditions
- **Cooldown periods**: Prevent alert spam
- **Time windows**: Schedule alerts for specific times/days
- **Alert lifecycle**: Track from trigger to resolution
- **Default thresholds**: Pre-configured for common scenarios

## Installation

Add the NuGet package reference:

```xml
<PackageReference Include="Lextm.SharpSnmpLib" Version="12.5.7" />
```

## Quick Start

### Basic SNMP Polling

```csharp
using NetworkOptimizer.Monitoring;
using Microsoft.Extensions.Logging;

// Configure SNMP
var config = new SnmpConfiguration
{
    Version = SnmpVersion.V3,
    Username = "snmpuser",
    AuthenticationPassword = "authpass",
    PrivacyPassword = "privpass",
    AuthProtocol = AuthenticationProtocol.SHA256,
    PrivProtocol = PrivacyProtocol.AES,
    Timeout = 2000,
    EnableDebugLogging = true
};

// Create poller
var logger = LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<SnmpPoller>();
var poller = new SnmpPoller(config, logger);

// Get device metrics
var deviceIp = IPAddress.Parse("192.168.1.1");
var metrics = await poller.GetDeviceMetricsAsync(deviceIp, "gateway");

Console.WriteLine($"Device: {metrics.Hostname}");
Console.WriteLine($"CPU: {metrics.CpuUsage:F2}%");
Console.WriteLine($"Memory: {metrics.MemoryUsage:F2}%");
Console.WriteLine($"Uptime: {metrics.UptimeDays:F2} days");
Console.WriteLine($"Interfaces: {metrics.InterfaceCount}");
```

### Get Interface Metrics

```csharp
var interfaces = await poller.GetInterfaceMetricsAsync(deviceIp, "gateway");

foreach (var iface in interfaces.Where(i => i.IsUp))
{
    Console.WriteLine($"Interface: {iface.Description}");
    Console.WriteLine($"  Speed: {iface.SpeedGbps:F2} Gbps");
    Console.WriteLine($"  In: {iface.InOctets:N0} octets");
    Console.WriteLine($"  Out: {iface.OutOctets:N0} octets");
    Console.WriteLine($"  Errors: In={iface.InErrors}, Out={iface.OutErrors}");
}
```

### Metrics Aggregation

```csharp
var aggregatorLogger = LoggerFactory.Create(builder => builder.AddConsole())
    .CreateLogger<MetricsAggregator>();
var aggregator = new MetricsAggregator(aggregatorLogger);

// Add device metrics
aggregator.AddDeviceMetrics(metrics);

// Add interface metrics
aggregator.AddInterfaceMetrics(interfaces);

// Get batch for storage
var batch = aggregator.GetBatch();
Console.WriteLine($"Collected {batch.Count} metrics");

// Process metrics
foreach (var metric in batch.Metrics)
{
    Console.WriteLine($"{metric.Name}: {metric.Value} ({metric.DeviceHostname})");
}

// Clear batch
aggregator.ClearBatch();
```

### Alert Engine

```csharp
var alertLogger = LoggerFactory.Create(builder => builder.AddConsole())
    .CreateLogger<AlertEngine>();
var alertEngine = new AlertEngine(alertLogger);

// Add custom threshold
alertEngine.AddThreshold(new AlertThreshold
{
    Name = "High Interface Errors",
    Description = "Interface error rate exceeds 1000 errors",
    MetricType = "interface",
    MetricName = "InErrors",
    Value = 1000,
    Comparison = ThresholdComparison.GreaterThan,
    Severity = AlertSeverity.Warning,
    DurationSeconds = 300,  // 5 minutes
    CooldownSeconds = 900   // 15 minutes
});

// Evaluate metrics
var deviceAlerts = alertEngine.EvaluateDeviceMetrics(metrics);
var interfaceAlerts = alertEngine.EvaluateInterfaceMetrics(interfaces);

// Check for alerts
foreach (var alert in deviceAlerts.Concat(interfaceAlerts))
{
    Console.WriteLine($"[{alert.Severity}] {alert.Title}");
    Console.WriteLine($"  {alert.Message}");
}

// Get active alerts
var activeAlerts = alertEngine.GetActiveAlerts();
Console.WriteLine($"{activeAlerts.Count} active alerts");
```

## Configuration

### SNMP Configuration Options

```csharp
var config = new SnmpConfiguration
{
    // Connection
    Port = 161,
    Timeout = 2000,
    RetryCount = 2,

    // Version
    Version = SnmpVersion.V3,

    // v1/v2c
    Community = "public",

    // v3 Authentication
    Username = "snmpuser",
    AuthenticationPassword = "authpass",
    AuthProtocol = AuthenticationProtocol.SHA256,

    // v3 Privacy
    PrivacyPassword = "privpass",
    PrivProtocol = PrivacyProtocol.AES,

    // Advanced
    PollingIntervalSeconds = 60,
    UseHighCapacityCounters = true,
    HighCapacityThresholdMbps = 1000,
    EnableDebugLogging = false,
    MaxConcurrentRequests = 10,

    // Interface filtering
    ExcludeInterfacePatterns = new List<string>
    {
        "^lo$", "^br-", "^docker", "^veth", "^ifb"
    }
};
```

### Alert Threshold Configuration

```csharp
var threshold = new AlertThreshold
{
    Name = "Critical CPU Usage",
    Description = "CPU usage exceeds 95%",
    IsEnabled = true,

    // Metric
    MetricType = "device",
    MetricName = "CpuUsage",

    // Threshold
    Value = 95,
    Comparison = ThresholdComparison.GreaterThan,

    // Alert properties
    Severity = AlertSeverity.Critical,
    DurationSeconds = 180,    // Must persist for 3 minutes
    CooldownSeconds = 600,    // Wait 10 minutes between alerts

    // Targeting
    TargetDevices = new List<string> { "192.168.1.1" },
    TargetDeviceTypes = new List<DeviceType> { DeviceType.Gateway },

    // Time windows (optional)
    ActiveWindows = new List<TimeWindow>
    {
        new TimeWindow
        {
            DaysOfWeek = new List<DayOfWeek>
            {
                DayOfWeek.Monday,
                DayOfWeek.Tuesday,
                DayOfWeek.Wednesday,
                DayOfWeek.Thursday,
                DayOfWeek.Friday
            },
            StartTime = new TimeOnly(9, 0),
            EndTime = new TimeOnly(17, 0)
        }
    },

    Tags = new List<string> { "cpu", "performance", "critical" }
};
```

## OID Reference

### System OIDs
- `SysDescr`: System description
- `SysUpTime`: System uptime
- `SysName`: System name/hostname
- `SysLocation`: System location
- `SysContact`: System contact

### Interface OIDs
- `IfDescr`: Interface description
- `IfSpeed`: Interface speed
- `IfHighSpeed`: High-speed interface (Mbps)
- `IfOperStatus`: Operational status
- `IfInOctets/IfHCInOctets`: Inbound octets (32/64-bit)
- `IfOutOctets/IfHCOutOctets`: Outbound octets (32/64-bit)
- `IfInErrors`: Inbound errors
- `IfOutErrors`: Outbound errors

### UniFi-Specific OIDs
- `UniFiModel`: Device model
- `UniFiFirmwareVersion`: Firmware version
- `UniFiMacAddress`: MAC address
- `UniFiTemperature`: Device temperature

See `UniFiOids.cs` for complete OID reference.

## Models

### DeviceMetrics
System-level metrics for network devices:
- CPU usage, memory usage, temperature
- Uptime, firmware version, model
- Interface count and list
- Device type classification

### InterfaceMetrics
Interface-level statistics:
- Traffic counters (octets, packets)
- Error counters (errors, discards)
- Status (admin/operational)
- Speed and MTU
- Interface filtering

### Alert
Alert instance with lifecycle tracking:
- Severity levels (Info, Warning, Error, Critical)
- Status (Active, Acknowledged, Resolved, Suppressed)
- Trigger/update/resolve timestamps
- Acknowledgment tracking

### AlertThreshold
Threshold configuration:
- Metric targeting
- Comparison operators
- Duration and cooldown
- Time windows
- Device/interface targeting

## Advanced Usage

### Custom Metric Sources

```csharp
// Add custom metric from external source
aggregator.AddCustomMetric(
    name: "custom.bandwidth.utilization",
    value: 85.5,
    deviceIp: "192.168.1.1",
    tags: new Dictionary<string, string>
    {
        { "source", "external_monitor" },
        { "type", "bandwidth" }
    }
);
```

### Alert Management

```csharp
// Acknowledge alert
alertEngine.AcknowledgeAlert(alertId, "admin@example.com");

// Resolve alert manually
alertEngine.ResolveAlert(alertId);

// Clean up old alerts
alertEngine.ClearOldAlerts(TimeSpan.FromDays(30));

// Get alert history
var history = alertEngine.GetAlertHistory(maxCount: 100);
```

## Best Practices

1. **Use SNMP v3** for security with strong authentication and privacy
2. **Enable high-capacity counters** for 10G+ interfaces to prevent counter wraps
3. **Set appropriate timeouts** based on network latency
4. **Filter virtual interfaces** to reduce noise
5. **Use duration-based alerts** to avoid flapping
6. **Set cooldown periods** to prevent alert spam
7. **Batch metrics** for efficient storage
8. **Monitor device temperature** to prevent hardware issues

## Integration Example

```csharp
// Complete monitoring loop
public async Task MonitorDevicesAsync(List<string> deviceIps, CancellationToken ct)
{
    var poller = new SnmpPoller(config, pollerLogger);
    var aggregator = new MetricsAggregator(aggregatorLogger);
    var alertEngine = new AlertEngine(alertLogger);

    while (!ct.IsCancellationRequested)
    {
        foreach (var ip in deviceIps)
        {
            try
            {
                // Collect metrics
                var deviceMetrics = await poller.GetDeviceMetricsAsync(
                    IPAddress.Parse(ip));

                // Aggregate
                aggregator.AddDeviceMetrics(deviceMetrics);
                aggregator.AddInterfaceMetrics(deviceMetrics.Interfaces);

                // Check alerts
                var alerts = alertEngine.EvaluateDeviceMetrics(deviceMetrics);
                alerts.AddRange(alertEngine.EvaluateInterfaceMetrics(
                    deviceMetrics.Interfaces));

                // Process alerts
                foreach (var alert in alerts)
                {
                    await SendAlertNotificationAsync(alert);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to monitor device {Ip}", ip);
            }
        }

        // Store metrics batch
        var batch = aggregator.GetBatch();
        await StoreMetricsBatchAsync(batch);
        aggregator.ClearBatch();

        // Wait for next interval
        await Task.Delay(TimeSpan.FromSeconds(config.PollingIntervalSeconds), ct);
    }
}
```

## License

Business Source License 1.1. See [LICENSE](../../LICENSE) in the repository root.

This component uses [Lextm.SharpSnmpLib](https://github.com/lextm/sharpsnmplib) (MIT License) for SNMP operations.

© 2026 Ozark Connect
