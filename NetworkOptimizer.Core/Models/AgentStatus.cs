using System.Net;
using NetworkOptimizer.Core.Enums;

namespace NetworkOptimizer.Core.Models;

/// <summary>
/// Represents the health and status of a deployed monitoring agent.
/// </summary>
public class AgentStatus
{
    /// <summary>
    /// Unique identifier for the agent.
    /// </summary>
    public string AgentId { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Human-readable name for the agent.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Type of the agent.
    /// </summary>
    public AgentType Type { get; set; } = AgentType.Unknown;

    /// <summary>
    /// Version of the agent software.
    /// </summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>
    /// IP address of the agent.
    /// </summary>
    public IPAddress? IpAddress { get; set; }

    /// <summary>
    /// Hostname of the agent machine.
    /// </summary>
    public string Hostname { get; set; } = string.Empty;

    /// <summary>
    /// Network segment or VLAN where the agent is deployed.
    /// </summary>
    public string NetworkSegment { get; set; } = string.Empty;

    /// <summary>
    /// VLAN ID where the agent is located.
    /// </summary>
    public int? VlanId { get; set; }

    /// <summary>
    /// Indicates whether the agent is currently online and reporting.
    /// </summary>
    public bool IsOnline { get; set; }

    /// <summary>
    /// Timestamp of the last successful check-in.
    /// </summary>
    public DateTime LastSeen { get; set; }

    /// <summary>
    /// Timestamp when the agent was first deployed.
    /// </summary>
    public DateTime DeployedAt { get; set; }

    /// <summary>
    /// Agent uptime in seconds.
    /// </summary>
    public long UptimeSeconds { get; set; }

    /// <summary>
    /// Current health status of the agent.
    /// </summary>
    public AgentHealthStatus HealthStatus { get; set; } = AgentHealthStatus.Unknown;

    /// <summary>
    /// CPU usage percentage on the agent machine (0-100).
    /// </summary>
    public double CpuUsage { get; set; }

    /// <summary>
    /// Memory usage percentage on the agent machine (0-100).
    /// </summary>
    public double MemoryUsage { get; set; }

    /// <summary>
    /// Disk usage percentage on the agent machine (0-100).
    /// </summary>
    public double DiskUsage { get; set; }

    /// <summary>
    /// Collected metrics from the agent.
    /// </summary>
    public AgentMetrics Metrics { get; set; } = new();

    /// <summary>
    /// Configuration settings for the agent.
    /// </summary>
    public AgentConfiguration Configuration { get; set; } = new();

    /// <summary>
    /// Recent errors or warnings from the agent.
    /// </summary>
    public List<AgentLog> RecentLogs { get; set; } = new();

    /// <summary>
    /// Additional metadata for the agent.
    /// </summary>
    public Dictionary<string, object> Metadata { get; set; } = new();

    /// <summary>
    /// Gets the agent uptime as a TimeSpan.
    /// </summary>
    public TimeSpan Uptime => TimeSpan.FromSeconds(UptimeSeconds);

    /// <summary>
    /// Gets the time elapsed since last check-in.
    /// </summary>
    public TimeSpan TimeSinceLastSeen => DateTime.UtcNow - LastSeen;

    /// <summary>
    /// Determines if the agent is considered stale (hasn't reported recently).
    /// </summary>
    public bool IsStale => TimeSinceLastSeen.TotalMinutes > 5;
}

/// <summary>
/// Represents the health status of an agent.
/// </summary>
public enum AgentHealthStatus
{
    /// <summary>
    /// Health status is unknown.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// Agent is healthy and operating normally.
    /// </summary>
    Healthy = 1,

    /// <summary>
    /// Agent is experiencing minor issues but still functional.
    /// </summary>
    Warning = 2,

    /// <summary>
    /// Agent is experiencing significant issues.
    /// </summary>
    Critical = 3,

    /// <summary>
    /// Agent is offline or unreachable.
    /// </summary>
    Offline = 4
}

/// <summary>
/// Represents metrics collected by a monitoring agent.
/// </summary>
public class AgentMetrics
{
    /// <summary>
    /// Timestamp when metrics were collected.
    /// </summary>
    public DateTime CollectedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Average latency to gateway in milliseconds.
    /// </summary>
    public double? LatencyToGatewayMs { get; set; }

    /// <summary>
    /// Average latency to internet in milliseconds.
    /// </summary>
    public double? LatencyToInternetMs { get; set; }

    /// <summary>
    /// Download bandwidth in Mbps.
    /// </summary>
    public double? DownloadBandwidthMbps { get; set; }

    /// <summary>
    /// Upload bandwidth in Mbps.
    /// </summary>
    public double? UploadBandwidthMbps { get; set; }

    /// <summary>
    /// Packet loss percentage (0-100).
    /// </summary>
    public double? PacketLossPercent { get; set; }

    /// <summary>
    /// Jitter in milliseconds.
    /// </summary>
    public double? JitterMs { get; set; }

    /// <summary>
    /// DNS resolution time in milliseconds.
    /// </summary>
    public double? DnsResolutionMs { get; set; }

    /// <summary>
    /// Number of active connections.
    /// </summary>
    public int? ActiveConnections { get; set; }

    /// <summary>
    /// Additional custom metrics.
    /// </summary>
    public Dictionary<string, double> CustomMetrics { get; set; } = new();
}

/// <summary>
/// Represents configuration settings for an agent.
/// </summary>
public class AgentConfiguration
{
    /// <summary>
    /// Polling interval in seconds.
    /// </summary>
    public int PollingIntervalSeconds { get; set; } = 60;

    /// <summary>
    /// Metrics storage endpoint URL.
    /// </summary>
    public string MetricsEndpoint { get; set; } = string.Empty;

    /// <summary>
    /// List of test targets for latency measurements.
    /// </summary>
    public List<string> TestTargets { get; set; } = new();

    /// <summary>
    /// Indicates whether bandwidth testing is enabled.
    /// </summary>
    public bool BandwidthTestEnabled { get; set; }

    /// <summary>
    /// Bandwidth test server URL.
    /// </summary>
    public string? BandwidthTestServer { get; set; }

    /// <summary>
    /// Additional configuration parameters.
    /// </summary>
    public Dictionary<string, object> AdditionalSettings { get; set; } = new();
}

/// <summary>
/// Represents a log entry from an agent.
/// </summary>
public class AgentLog
{
    /// <summary>
    /// Timestamp of the log entry.
    /// </summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Log level (Info, Warning, Error, etc.).
    /// </summary>
    public string Level { get; set; } = string.Empty;

    /// <summary>
    /// Log message.
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Exception details (if applicable).
    /// </summary>
    public string? Exception { get; set; }

    /// <summary>
    /// Additional context data.
    /// </summary>
    public Dictionary<string, object> Context { get; set; } = new();
}
