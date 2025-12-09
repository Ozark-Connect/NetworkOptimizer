namespace NetworkOptimizer.Core.Enums;

/// <summary>
/// Represents the types of measurements that can be collected and stored in time-series storage.
/// Each measurement type corresponds to a specific metric category for network optimization.
/// </summary>
public enum MeasurementType
{
    /// <summary>
    /// UniFi device health and status metrics.
    /// </summary>
    DeviceHealth = 0,

    /// <summary>
    /// Network interface statistics (throughput, errors, drops).
    /// </summary>
    InterfaceMetrics = 1,

    /// <summary>
    /// Smart Queue Management (SQM) performance metrics.
    /// </summary>
    SqmPerformance = 2,

    /// <summary>
    /// Network latency and jitter measurements.
    /// </summary>
    LatencyMetrics = 3,

    /// <summary>
    /// Bandwidth utilization and throughput measurements.
    /// </summary>
    BandwidthMetrics = 4,

    /// <summary>
    /// Agent health and connectivity status.
    /// </summary>
    AgentHealth = 5,

    /// <summary>
    /// Audit findings and security compliance scores.
    /// </summary>
    AuditResults = 6,

    /// <summary>
    /// Network configuration change events.
    /// </summary>
    ConfigurationEvents = 7,

    /// <summary>
    /// Client device performance metrics.
    /// </summary>
    ClientMetrics = 8,

    /// <summary>
    /// Wireless network quality metrics.
    /// </summary>
    WirelessQuality = 9,

    /// <summary>
    /// Traffic shaping and QoS rule performance.
    /// </summary>
    QosMetrics = 10,

    /// <summary>
    /// VLAN and network segmentation metrics.
    /// </summary>
    VlanMetrics = 11
}

/// <summary>
/// Extension methods for MeasurementType enum.
/// </summary>
public static class MeasurementTypeExtensions
{
    /// <summary>
    /// Converts MeasurementType enum to storage measurement name string.
    /// </summary>
    public static string ToMeasurementName(this MeasurementType measurementType)
    {
        return measurementType switch
        {
            MeasurementType.DeviceHealth => "device_health",
            MeasurementType.InterfaceMetrics => "interface_metrics",
            MeasurementType.SqmPerformance => "sqm_performance",
            MeasurementType.LatencyMetrics => "latency_metrics",
            MeasurementType.BandwidthMetrics => "bandwidth_metrics",
            MeasurementType.AgentHealth => "agent_health",
            MeasurementType.AuditResults => "audit_results",
            MeasurementType.ConfigurationEvents => "configuration_events",
            MeasurementType.ClientMetrics => "client_metrics",
            MeasurementType.WirelessQuality => "wireless_quality",
            MeasurementType.QosMetrics => "qos_metrics",
            MeasurementType.VlanMetrics => "vlan_metrics",
            _ => throw new ArgumentOutOfRangeException(nameof(measurementType), measurementType, "Unknown measurement type")
        };
    }

    /// <summary>
    /// Parses a measurement name string to MeasurementType enum.
    /// </summary>
    public static MeasurementType ParseMeasurement(string measurementName)
    {
        return measurementName switch
        {
            "device_health" => MeasurementType.DeviceHealth,
            "interface_metrics" => MeasurementType.InterfaceMetrics,
            "sqm_performance" => MeasurementType.SqmPerformance,
            "latency_metrics" => MeasurementType.LatencyMetrics,
            "bandwidth_metrics" => MeasurementType.BandwidthMetrics,
            "agent_health" => MeasurementType.AgentHealth,
            "audit_results" => MeasurementType.AuditResults,
            "configuration_events" => MeasurementType.ConfigurationEvents,
            "client_metrics" => MeasurementType.ClientMetrics,
            "wireless_quality" => MeasurementType.WirelessQuality,
            "qos_metrics" => MeasurementType.QosMetrics,
            "vlan_metrics" => MeasurementType.VlanMetrics,
            _ => throw new ArgumentException($"Unknown measurement name: {measurementName}", nameof(measurementName))
        };
    }

    /// <summary>
    /// Determines if the measurement type requires agent deployment.
    /// </summary>
    public static bool RequiresAgent(this MeasurementType measurementType)
    {
        return measurementType switch
        {
            MeasurementType.LatencyMetrics => true,
            MeasurementType.BandwidthMetrics => true,
            MeasurementType.AgentHealth => true,
            MeasurementType.ClientMetrics => true,
            _ => false
        };
    }
}
