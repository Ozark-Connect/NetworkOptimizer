namespace NetworkOptimizer.Core.Models;

/// <summary>
/// Represents Smart Queue Management (SQM) configuration for traffic shaping and QoS.
/// </summary>
public class SqmConfiguration
{
    /// <summary>
    /// Unique identifier for the SQM configuration.
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Device identifier where this SQM configuration is applied.
    /// </summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>
    /// Interface name where SQM is configured (e.g., "WAN", "eth0").
    /// </summary>
    public string InterfaceName { get; set; } = string.Empty;

    /// <summary>
    /// Indicates whether SQM is enabled on this interface.
    /// </summary>
    public bool IsEnabled { get; set; }

    /// <summary>
    /// Download bandwidth limit in Mbps.
    /// </summary>
    public int DownloadBandwidthMbps { get; set; }

    /// <summary>
    /// Upload bandwidth limit in Mbps.
    /// </summary>
    public int UploadBandwidthMbps { get; set; }

    /// <summary>
    /// SQM algorithm/discipline (e.g., "fq_codel", "cake").
    /// </summary>
    public string QueueDiscipline { get; set; } = "fq_codel";

    /// <summary>
    /// Link layer adaptation type (e.g., "none", "atm", "ethernet").
    /// </summary>
    public string LinkLayerAdaptation { get; set; } = "ethernet";

    /// <summary>
    /// Overhead bytes to account for in bandwidth calculations.
    /// </summary>
    public int OverheadBytes { get; set; }

    /// <summary>
    /// WAN configuration associated with this SQM setup.
    /// </summary>
    public WanConfiguration WanConfig { get; set; } = new();

    /// <summary>
    /// Baseline performance metrics before SQM was applied.
    /// </summary>
    public PerformanceBaseline? Baseline { get; set; }

    /// <summary>
    /// Current performance metrics with SQM enabled.
    /// </summary>
    public PerformanceMetrics? CurrentMetrics { get; set; }

    /// <summary>
    /// Timestamp when the configuration was created.
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Timestamp when the configuration was last modified.
    /// </summary>
    public DateTime LastModified { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Additional configuration parameters.
    /// </summary>
    public Dictionary<string, object> AdditionalSettings { get; set; } = new();

    /// <summary>
    /// Priority rules for traffic classification.
    /// </summary>
    public List<TrafficPriorityRule> PriorityRules { get; set; } = new();
}

/// <summary>
/// Represents WAN connection configuration details.
/// </summary>
public class WanConfiguration
{
    /// <summary>
    /// WAN connection type (e.g., "PPPoE", "DHCP", "Static").
    /// </summary>
    public string ConnectionType { get; set; } = string.Empty;

    /// <summary>
    /// ISP name.
    /// </summary>
    public string IspName { get; set; } = string.Empty;

    /// <summary>
    /// Provisioned download speed from ISP (in Mbps).
    /// </summary>
    public int ProvisionedDownloadMbps { get; set; }

    /// <summary>
    /// Provisioned upload speed from ISP (in Mbps).
    /// </summary>
    public int ProvisionedUploadMbps { get; set; }

    /// <summary>
    /// Actual measured download speed (in Mbps).
    /// </summary>
    public double? MeasuredDownloadMbps { get; set; }

    /// <summary>
    /// Actual measured upload speed (in Mbps).
    /// </summary>
    public double? MeasuredUploadMbps { get; set; }

    /// <summary>
    /// MTU size for the WAN interface.
    /// </summary>
    public int MtuSize { get; set; } = 1500;

    /// <summary>
    /// VLAN ID for the WAN interface (if applicable).
    /// </summary>
    public int? VlanId { get; set; }

    /// <summary>
    /// IPv6 enabled status.
    /// </summary>
    public bool Ipv6Enabled { get; set; }
}

/// <summary>
/// Represents baseline performance metrics before optimization.
/// </summary>
public class PerformanceBaseline
{
    /// <summary>
    /// Timestamp when the baseline was captured.
    /// </summary>
    public DateTime CapturedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Average latency in milliseconds.
    /// </summary>
    public double AverageLatencyMs { get; set; }

    /// <summary>
    /// Jitter in milliseconds.
    /// </summary>
    public double JitterMs { get; set; }

    /// <summary>
    /// Packet loss percentage (0-100).
    /// </summary>
    public double PacketLossPercent { get; set; }

    /// <summary>
    /// Download throughput in Mbps.
    /// </summary>
    public double DownloadThroughputMbps { get; set; }

    /// <summary>
    /// Upload throughput in Mbps.
    /// </summary>
    public double UploadThroughputMbps { get; set; }

    /// <summary>
    /// Bufferbloat score (A+ to F).
    /// </summary>
    public string BufferbloatGrade { get; set; } = string.Empty;
}

/// <summary>
/// Represents current performance metrics.
/// </summary>
public class PerformanceMetrics
{
    /// <summary>
    /// Timestamp when the metrics were captured.
    /// </summary>
    public DateTime CapturedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Average latency in milliseconds.
    /// </summary>
    public double AverageLatencyMs { get; set; }

    /// <summary>
    /// Jitter in milliseconds.
    /// </summary>
    public double JitterMs { get; set; }

    /// <summary>
    /// Packet loss percentage (0-100).
    /// </summary>
    public double PacketLossPercent { get; set; }

    /// <summary>
    /// Download throughput in Mbps.
    /// </summary>
    public double DownloadThroughputMbps { get; set; }

    /// <summary>
    /// Upload throughput in Mbps.
    /// </summary>
    public double UploadThroughputMbps { get; set; }

    /// <summary>
    /// Bufferbloat score (A+ to F).
    /// </summary>
    public string BufferbloatGrade { get; set; } = string.Empty;

    /// <summary>
    /// Quality of Service score (0-100).
    /// </summary>
    public int QosScore { get; set; }
}

/// <summary>
/// Represents a traffic priority rule for QoS classification.
/// </summary>
public class TrafficPriorityRule
{
    /// <summary>
    /// Rule identifier.
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Rule name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Rule description.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Priority level (1-8, where 1 is highest).
    /// </summary>
    public int Priority { get; set; }

    /// <summary>
    /// Traffic matching criteria (IP, port, protocol, etc.).
    /// </summary>
    public string MatchCriteria { get; set; } = string.Empty;

    /// <summary>
    /// DSCP marking to apply.
    /// </summary>
    public int? DscpMarking { get; set; }

    /// <summary>
    /// Indicates whether the rule is enabled.
    /// </summary>
    public bool IsEnabled { get; set; } = true;
}
