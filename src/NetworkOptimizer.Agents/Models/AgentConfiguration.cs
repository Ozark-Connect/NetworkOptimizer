namespace NetworkOptimizer.Agents.Models;

/// <summary>
/// Configuration for a deployed agent
/// </summary>
public class AgentConfiguration
{
    /// <summary>
    /// Unique identifier for the agent
    /// </summary>
    public required string AgentId { get; set; }

    /// <summary>
    /// Friendly name for the device
    /// </summary>
    public required string DeviceName { get; set; }

    /// <summary>
    /// Type of agent being deployed
    /// </summary>
    public required AgentType AgentType { get; set; }

    /// <summary>
    /// InfluxDB endpoint URL
    /// </summary>
    public required string InfluxDbUrl { get; set; }

    /// <summary>
    /// InfluxDB organization
    /// </summary>
    public required string InfluxDbOrg { get; set; }

    /// <summary>
    /// InfluxDB bucket name
    /// </summary>
    public required string InfluxDbBucket { get; set; }

    /// <summary>
    /// InfluxDB authentication token
    /// </summary>
    public required string InfluxDbToken { get; set; }

    /// <summary>
    /// Metric collection interval in seconds
    /// </summary>
    public int CollectionIntervalSeconds { get; set; } = 30;

    /// <summary>
    /// Speedtest interval in minutes (UDM/UCG only)
    /// </summary>
    public int SpeedtestIntervalMinutes { get; set; } = 60;

    /// <summary>
    /// Enable Docker metrics collection (Linux agent only)
    /// </summary>
    public bool EnableDockerMetrics { get; set; } = false;

    /// <summary>
    /// Additional tags to apply to all metrics
    /// </summary>
    public Dictionary<string, string> Tags { get; set; } = new();

    /// <summary>
    /// SSH credentials for deployment
    /// </summary>
    public required SshCredentials SshCredentials { get; set; }
}

public enum AgentType
{
    /// <summary>
    /// UniFi Dream Machine (UDM/UDM-Pro/UDM-SE)
    /// </summary>
    UDM,

    /// <summary>
    /// UniFi Cloud Gateway (UCG-Ultra/UCG-Max)
    /// </summary>
    UCG,

    /// <summary>
    /// Generic Linux system
    /// </summary>
    Linux
}
