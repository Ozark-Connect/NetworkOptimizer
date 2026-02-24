using NetworkOptimizer.Alerts.Events;
using NetworkOptimizer.Alerts.Models;
using NetworkOptimizer.Core.Enums;

namespace NetworkOptimizer.Alerts.Delivery;

/// <summary>
/// Pre-collapse summary counts so digest channels can show accurate totals.
/// </summary>
public record DigestSummary(int TotalCount, int CriticalCount, int ErrorCount, int WarningCount, int InfoCount)
{
    public static DigestSummary FromAlerts(IReadOnlyList<AlertHistoryEntry> alerts) => new(
        alerts.Count,
        alerts.Count(a => a.Severity == AlertSeverity.Critical),
        alerts.Count(a => a.Severity == AlertSeverity.Error),
        alerts.Count(a => a.Severity == AlertSeverity.Warning),
        alerts.Count(a => a.Severity == AlertSeverity.Info));
}

/// <summary>
/// Interface for delivering alerts to an external channel (email, webhook, Slack, etc.).
/// </summary>
public interface IAlertDeliveryChannel
{
    /// <summary>
    /// The channel type this implementation handles.
    /// </summary>
    DeliveryChannelType ChannelType { get; }

    /// <summary>
    /// Send a single alert notification.
    /// </summary>
    Task<bool> SendAsync(AlertEvent alertEvent, AlertHistoryEntry historyEntry, DeliveryChannel channel, CancellationToken cancellationToken = default);

    /// <summary>
    /// Send a digest summary of multiple alerts.
    /// <paramref name="summary"/> contains pre-collapse counts for accurate totals.
    /// </summary>
    Task<bool> SendDigestAsync(IReadOnlyList<AlertHistoryEntry> alerts, DeliveryChannel channel, DigestSummary summary, CancellationToken cancellationToken = default);

    /// <summary>
    /// Send a test notification to verify channel configuration.
    /// </summary>
    Task<(bool Success, string? Error)> TestAsync(DeliveryChannel channel, CancellationToken cancellationToken = default);
}
