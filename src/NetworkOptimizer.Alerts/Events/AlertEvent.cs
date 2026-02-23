using NetworkOptimizer.Core.Enums;

namespace NetworkOptimizer.Alerts.Events;

/// <summary>
/// An event published by any module that may trigger an alert.
/// Consumed by AlertProcessingService via the event bus.
/// </summary>
public record AlertEvent
{
    /// <summary>
    /// Dot-delimited event type for pattern matching (e.g., "audit.score_dropped", "device.offline").
    /// </summary>
    public required string EventType { get; init; }

    /// <summary>
    /// Severity of the event.
    /// </summary>
    public AlertSeverity Severity { get; init; } = AlertSeverity.Info;

    /// <summary>
    /// Source module that produced this event (e.g., "audit", "speedtest", "device", "wan").
    /// </summary>
    public required string Source { get; init; }

    /// <summary>
    /// Human-readable alert title.
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    /// Detailed alert message.
    /// </summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>
    /// Device identifier (MAC or IP) if applicable.
    /// </summary>
    public string? DeviceId { get; init; }

    /// <summary>
    /// Device display name if applicable.
    /// </summary>
    public string? DeviceName { get; init; }

    /// <summary>
    /// Device IP address if applicable.
    /// </summary>
    public string? DeviceIp { get; init; }

    /// <summary>
    /// Current metric value that triggered the event (if threshold-based).
    /// </summary>
    public double? MetricValue { get; init; }

    /// <summary>
    /// Threshold value that was breached (if threshold-based).
    /// </summary>
    public double? ThresholdValue { get; init; }

    /// <summary>
    /// Additional contextual data (score delta, previous average, related findings, etc.).
    /// </summary>
    public Dictionary<string, string> Context { get; init; } = new();

    /// <summary>
    /// Tags for categorization and filtering.
    /// </summary>
    public List<string> Tags { get; init; } = [];

    /// <summary>
    /// When the event occurred.
    /// </summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}
