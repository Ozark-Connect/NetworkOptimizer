namespace NetworkOptimizer.Alerts.Events;

/// <summary>
/// In-process event bus for alert events. Publishers push events, the processing service consumes them.
/// </summary>
public interface IAlertEventBus
{
    /// <summary>
    /// Publish an alert event. Non-blocking; drops oldest if buffer full.
    /// </summary>
    ValueTask PublishAsync(AlertEvent alertEvent, CancellationToken cancellationToken = default);

    /// <summary>
    /// Consume alert events (used by AlertProcessingService).
    /// </summary>
    IAsyncEnumerable<AlertEvent> ConsumeAsync(CancellationToken cancellationToken);
}
