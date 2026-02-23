using NetworkOptimizer.Alerts.Events;
using NetworkOptimizer.Alerts.Models;

namespace NetworkOptimizer.Alerts.Delivery;

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
    /// </summary>
    Task<bool> SendDigestAsync(IReadOnlyList<AlertHistoryEntry> alerts, DeliveryChannel channel, CancellationToken cancellationToken = default);

    /// <summary>
    /// Send a test notification to verify channel configuration.
    /// </summary>
    Task<(bool Success, string? Error)> TestAsync(DeliveryChannel channel, CancellationToken cancellationToken = default);
}
