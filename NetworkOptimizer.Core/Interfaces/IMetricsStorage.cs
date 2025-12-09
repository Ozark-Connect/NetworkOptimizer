using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Core.Models;

namespace NetworkOptimizer.Core.Interfaces;

/// <summary>
/// Interface for storing and retrieving time-series metrics data.
/// Provides methods for writing performance metrics, audit results, and agent data to persistent storage.
/// </summary>
public interface IMetricsStorage
{
    /// <summary>
    /// Writes general metrics to storage.
    /// </summary>
    /// <param name="measurementType">Type of measurement being stored.</param>
    /// <param name="deviceId">Device identifier (optional).</param>
    /// <param name="metrics">Dictionary of metric name-value pairs.</param>
    /// <param name="tags">Additional tags for filtering and grouping (optional).</param>
    /// <param name="timestamp">Timestamp for the metrics (defaults to current time).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task WriteMetricsAsync(
        MeasurementType measurementType,
        string? deviceId,
        Dictionary<string, object> metrics,
        Dictionary<string, string>? tags = null,
        DateTime? timestamp = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes device health metrics to storage.
    /// </summary>
    /// <param name="device">UniFi device with health metrics.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task WriteDeviceHealthAsync(UniFiDevice device, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes SQM performance metrics to storage.
    /// </summary>
    /// <param name="deviceId">Device identifier.</param>
    /// <param name="sqmMetrics">SQM performance metrics.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task WriteSqmMetricsAsync(string deviceId, PerformanceMetrics sqmMetrics, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes agent health and status metrics to storage.
    /// </summary>
    /// <param name="agentStatus">Agent status information.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task WriteAgentStatusAsync(AgentStatus agentStatus, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes audit results to storage.
    /// </summary>
    /// <param name="auditReport">Complete audit report.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task WriteAuditResultsAsync(AuditReport auditReport, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes network configuration change events to storage.
    /// </summary>
    /// <param name="siteId">Site identifier.</param>
    /// <param name="changeType">Type of configuration change.</param>
    /// <param name="changes">Dictionary of changed configuration values.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task WriteConfigurationChangeAsync(
        string siteId,
        string changeType,
        Dictionary<string, object> changes,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves historical metrics for a device.
    /// </summary>
    /// <param name="measurementType">Type of measurement to retrieve.</param>
    /// <param name="deviceId">Device identifier.</param>
    /// <param name="startTime">Start of time range.</param>
    /// <param name="endTime">End of time range.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of historical metric data points.</returns>
    Task<List<MetricDataPoint>> QueryMetricsAsync(
        MeasurementType measurementType,
        string deviceId,
        DateTime startTime,
        DateTime endTime,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves aggregated metrics (average, min, max) over a time range.
    /// </summary>
    /// <param name="measurementType">Type of measurement to aggregate.</param>
    /// <param name="deviceId">Device identifier.</param>
    /// <param name="startTime">Start of time range.</param>
    /// <param name="endTime">End of time range.</param>
    /// <param name="aggregationInterval">Interval for aggregation (e.g., "1h", "1d").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of aggregated metric data points.</returns>
    Task<List<AggregatedMetricDataPoint>> QueryAggregatedMetricsAsync(
        MeasurementType measurementType,
        string deviceId,
        DateTime startTime,
        DateTime endTime,
        string aggregationInterval,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves the latest metric value for a device.
    /// </summary>
    /// <param name="measurementType">Type of measurement to retrieve.</param>
    /// <param name="deviceId">Device identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Latest metric data point, or null if not found.</returns>
    Task<MetricDataPoint?> GetLatestMetricAsync(
        MeasurementType measurementType,
        string deviceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Performs a health check on the storage connection.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the storage is healthy and accessible.</returns>
    Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes metrics older than the specified retention period.
    /// </summary>
    /// <param name="retentionDays">Number of days to retain metrics.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Number of data points deleted.</returns>
    Task<long> CleanupOldMetricsAsync(int retentionDays, CancellationToken cancellationToken = default);
}

/// <summary>
/// Represents a single metric data point.
/// </summary>
public class MetricDataPoint
{
    /// <summary>
    /// Timestamp of the data point.
    /// </summary>
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// Metric values.
    /// </summary>
    public Dictionary<string, object> Values { get; set; } = new();

    /// <summary>
    /// Tags associated with the data point.
    /// </summary>
    public Dictionary<string, string> Tags { get; set; } = new();
}

/// <summary>
/// Represents an aggregated metric data point with statistical values.
/// </summary>
public class AggregatedMetricDataPoint
{
    /// <summary>
    /// Timestamp of the aggregation window.
    /// </summary>
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// Aggregated metric values (mean, min, max, etc.).
    /// </summary>
    public Dictionary<string, AggregatedValue> Values { get; set; } = new();

    /// <summary>
    /// Tags associated with the data point.
    /// </summary>
    public Dictionary<string, string> Tags { get; set; } = new();
}

/// <summary>
/// Represents aggregated statistical values for a metric.
/// </summary>
public class AggregatedValue
{
    /// <summary>
    /// Mean/average value.
    /// </summary>
    public double Mean { get; set; }

    /// <summary>
    /// Minimum value in the aggregation window.
    /// </summary>
    public double Min { get; set; }

    /// <summary>
    /// Maximum value in the aggregation window.
    /// </summary>
    public double Max { get; set; }

    /// <summary>
    /// Number of data points in the aggregation.
    /// </summary>
    public int Count { get; set; }

    /// <summary>
    /// Sum of all values in the aggregation window.
    /// </summary>
    public double Sum { get; set; }
}
