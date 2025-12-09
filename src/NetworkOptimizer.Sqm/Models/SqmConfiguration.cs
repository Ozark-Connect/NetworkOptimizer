namespace NetworkOptimizer.Sqm.Models;

/// <summary>
/// Configuration for SQM (Smart Queue Management) on a WAN interface
/// </summary>
public class SqmConfiguration
{
    /// <summary>
    /// WAN interface name (e.g., "eth2", "eth4")
    /// </summary>
    public string Interface { get; set; } = "eth2";

    /// <summary>
    /// Maximum download speed in Mbps (ceiling)
    /// </summary>
    public int MaxDownloadSpeed { get; set; } = 285;

    /// <summary>
    /// Minimum download speed floor in Mbps
    /// </summary>
    public int MinDownloadSpeed { get; set; } = 190;

    /// <summary>
    /// Absolute maximum achievable download speed in Mbps
    /// </summary>
    public int AbsoluteMaxDownloadSpeed { get; set; } = 280;

    /// <summary>
    /// Overhead multiplier for speedtest results (1.05 = 5% overhead)
    /// </summary>
    public double OverheadMultiplier { get; set; } = 1.05;

    /// <summary>
    /// Ping target host for latency monitoring
    /// </summary>
    public string PingHost { get; set; } = "40.134.217.121";

    /// <summary>
    /// Baseline latency in milliseconds (unloaded optimal ping)
    /// </summary>
    public double BaselineLatency { get; set; } = 17.9;

    /// <summary>
    /// Latency threshold in milliseconds (trigger adjustment when exceeded)
    /// </summary>
    public double LatencyThreshold { get; set; } = 2.2;

    /// <summary>
    /// Rate decrease multiplier when high latency detected (0.97 = 3% decrease per deviation)
    /// </summary>
    public double LatencyDecrease { get; set; } = 0.97;

    /// <summary>
    /// Rate increase multiplier when latency normalizes (1.04 = 4% increase)
    /// </summary>
    public double LatencyIncrease { get; set; } = 1.04;

    /// <summary>
    /// InfluxDB endpoint for metrics collection (optional)
    /// </summary>
    public string? InfluxDbEndpoint { get; set; }

    /// <summary>
    /// InfluxDB token for authentication (optional)
    /// </summary>
    public string? InfluxDbToken { get; set; }

    /// <summary>
    /// InfluxDB organization (optional)
    /// </summary>
    public string? InfluxDbOrg { get; set; }

    /// <summary>
    /// InfluxDB bucket name (optional)
    /// </summary>
    public string? InfluxDbBucket { get; set; }

    /// <summary>
    /// Speedtest schedule (cron format) - default: 6 AM and 6:30 PM
    /// </summary>
    public List<string> SpeedtestSchedule { get; set; } = new() { "0 6 * * *", "30 18 * * *" };

    /// <summary>
    /// Ping adjustment interval in minutes (default: 5)
    /// </summary>
    public int PingAdjustmentInterval { get; set; } = 5;

    /// <summary>
    /// Learning mode enabled - collect baseline data without aggressive adjustments
    /// </summary>
    public bool LearningMode { get; set; } = false;

    /// <summary>
    /// Learning mode start timestamp
    /// </summary>
    public DateTime? LearningModeStarted { get; set; }
}
