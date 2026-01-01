using NetworkOptimizer.Core.Models;

namespace NetworkOptimizer.Core.Interfaces;

/// <summary>
/// Interface for managing Smart Queue Management (SQM) configurations and optimizations.
/// Provides methods for analyzing network performance and optimizing traffic shaping settings.
/// </summary>
public interface ISqmManager
{
    /// <summary>
    /// Analyzes current network performance to establish a baseline.
    /// </summary>
    /// <param name="deviceId">Device identifier (gateway/router).</param>
    /// <param name="interfaceName">Interface name to analyze.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Performance baseline metrics.</returns>
    Task<PerformanceBaseline> CaptureBaselineAsync(string deviceId, string interfaceName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates optimized SQM configuration based on network analysis.
    /// </summary>
    /// <param name="deviceId">Device identifier.</param>
    /// <param name="interfaceName">Interface name.</param>
    /// <param name="wanConfig">WAN configuration details.</param>
    /// <param name="baseline">Current performance baseline (optional).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Recommended SQM configuration.</returns>
    Task<SqmConfiguration> GenerateOptimalConfigurationAsync(
        string deviceId,
        string interfaceName,
        WanConfiguration wanConfig,
        PerformanceBaseline? baseline = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies SQM configuration to a device.
    /// </summary>
    /// <param name="siteId">Site identifier.</param>
    /// <param name="configuration">SQM configuration to apply.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the configuration was successfully applied.</returns>
    Task<bool> ApplySqmConfigurationAsync(string siteId, SqmConfiguration configuration, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tests SQM configuration by measuring performance metrics.
    /// </summary>
    /// <param name="deviceId">Device identifier.</param>
    /// <param name="testDurationSeconds">Duration of the test in seconds.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Performance metrics collected during the test.</returns>
    Task<PerformanceMetrics> TestSqmPerformanceAsync(string deviceId, int testDurationSeconds = 60, CancellationToken cancellationToken = default);

    /// <summary>
    /// Compares baseline performance with current SQM-enabled performance.
    /// </summary>
    /// <param name="baseline">Baseline performance metrics.</param>
    /// <param name="current">Current performance metrics.</param>
    /// <returns>Performance comparison analysis.</returns>
    PerformanceComparison ComparePerformance(PerformanceBaseline baseline, PerformanceMetrics current);

    /// <summary>
    /// Automatically tunes SQM settings based on real-time performance data.
    /// </summary>
    /// <param name="deviceId">Device identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Tuning result with updated configuration.</returns>
    Task<SqmTuningResult> AutoTuneSqmAsync(string deviceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates SQM configuration for common issues.
    /// </summary>
    /// <param name="configuration">SQM configuration to validate.</param>
    /// <returns>Validation result with any issues found.</returns>
    Task<SqmValidationResult> ValidateConfigurationAsync(SqmConfiguration configuration);

    /// <summary>
    /// Retrieves historical SQM performance trends.
    /// </summary>
    /// <param name="deviceId">Device identifier.</param>
    /// <param name="startTime">Start of time range.</param>
    /// <param name="endTime">End of time range.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of historical performance metrics.</returns>
    Task<List<PerformanceMetrics>> GetPerformanceHistoryAsync(
        string deviceId,
        DateTime startTime,
        DateTime endTime,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Performs a bufferbloat test to measure latency under load.
    /// </summary>
    /// <param name="deviceId">Device identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Bufferbloat test results.</returns>
    Task<BufferbloatTestResult> PerformBufferbloatTestAsync(string deviceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Recommends traffic priority rules based on network usage patterns.
    /// </summary>
    /// <param name="deviceId">Device identifier.</param>
    /// <param name="analysisDurationHours">Number of hours of traffic to analyze.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Recommended priority rules.</returns>
    Task<List<TrafficPriorityRule>> RecommendPriorityRulesAsync(
        string deviceId,
        int analysisDurationHours = 24,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Represents a comparison between baseline and current performance.
/// </summary>
public class PerformanceComparison
{
    /// <summary>
    /// Baseline performance metrics.
    /// </summary>
    public PerformanceBaseline Baseline { get; set; } = new();

    /// <summary>
    /// Current performance metrics.
    /// </summary>
    public PerformanceMetrics Current { get; set; } = new();

    /// <summary>
    /// Change in average latency (negative is improvement).
    /// </summary>
    public double LatencyChangePct { get; set; }

    /// <summary>
    /// Change in jitter (negative is improvement).
    /// </summary>
    public double JitterChangePct { get; set; }

    /// <summary>
    /// Change in packet loss (negative is improvement).
    /// </summary>
    public double PacketLossChangePct { get; set; }

    /// <summary>
    /// Change in download throughput (positive is improvement).
    /// </summary>
    public double DownloadThroughputChangePct { get; set; }

    /// <summary>
    /// Change in upload throughput (positive is improvement).
    /// </summary>
    public double UploadThroughputChangePct { get; set; }

    /// <summary>
    /// Overall improvement score (0-100, where higher is better).
    /// </summary>
    public int ImprovementScore { get; set; }

    /// <summary>
    /// Summary of improvements and degradations.
    /// </summary>
    public string Summary { get; set; } = string.Empty;
}

/// <summary>
/// Represents the result of SQM auto-tuning.
/// </summary>
public class SqmTuningResult
{
    /// <summary>
    /// Indicates whether tuning was successful.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Original SQM configuration.
    /// </summary>
    public SqmConfiguration? OriginalConfiguration { get; set; }

    /// <summary>
    /// Tuned SQM configuration.
    /// </summary>
    public SqmConfiguration? TunedConfiguration { get; set; }

    /// <summary>
    /// Performance comparison before and after tuning.
    /// </summary>
    public PerformanceComparison? Comparison { get; set; }

    /// <summary>
    /// List of tuning adjustments made.
    /// </summary>
    public List<TuningAdjustment> Adjustments { get; set; } = new();

    /// <summary>
    /// Tuning recommendations and notes.
    /// </summary>
    public string Recommendations { get; set; } = string.Empty;
}

/// <summary>
/// Represents a single tuning adjustment.
/// </summary>
public class TuningAdjustment
{
    /// <summary>
    /// Parameter that was adjusted.
    /// </summary>
    public string Parameter { get; set; } = string.Empty;

    /// <summary>
    /// Original value.
    /// </summary>
    public string OriginalValue { get; set; } = string.Empty;

    /// <summary>
    /// New value.
    /// </summary>
    public string NewValue { get; set; } = string.Empty;

    /// <summary>
    /// Reason for the adjustment.
    /// </summary>
    public string Reason { get; set; } = string.Empty;
}

/// <summary>
/// Represents SQM configuration validation result.
/// </summary>
public class SqmValidationResult
{
    /// <summary>
    /// Indicates whether the configuration is valid.
    /// </summary>
    public bool IsValid { get; set; }

    /// <summary>
    /// List of validation errors.
    /// </summary>
    public List<string> Errors { get; set; } = new();

    /// <summary>
    /// List of validation warnings.
    /// </summary>
    public List<string> Warnings { get; set; } = new();

    /// <summary>
    /// List of optimization suggestions.
    /// </summary>
    public List<string> Suggestions { get; set; } = new();
}

/// <summary>
/// Represents bufferbloat test results.
/// </summary>
public class BufferbloatTestResult
{
    /// <summary>
    /// Test timestamp.
    /// </summary>
    public DateTime TestTime { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Idle latency (no load) in milliseconds.
    /// </summary>
    public double IdleLatencyMs { get; set; }

    /// <summary>
    /// Latency under download load in milliseconds.
    /// </summary>
    public double DownloadLoadLatencyMs { get; set; }

    /// <summary>
    /// Latency under upload load in milliseconds.
    /// </summary>
    public double UploadLoadLatencyMs { get; set; }

    /// <summary>
    /// Latency under full load (download + upload) in milliseconds.
    /// </summary>
    public double FullLoadLatencyMs { get; set; }

    /// <summary>
    /// Bufferbloat grade (A+ to F).
    /// </summary>
    public string BufferbloatGrade { get; set; } = string.Empty;

    /// <summary>
    /// Download speed during test in Mbps.
    /// </summary>
    public double DownloadSpeedMbps { get; set; }

    /// <summary>
    /// Upload speed during test in Mbps.
    /// </summary>
    public double UploadSpeedMbps { get; set; }

    /// <summary>
    /// Detailed test results and observations.
    /// </summary>
    public string Details { get; set; } = string.Empty;
}
