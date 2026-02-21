using System.Threading.Channels;

namespace NetworkOptimizer.Alerts.Events;

/// <summary>
/// Channel-based in-process event bus for alert events.
/// Bounded to 1000 items; drops oldest on overflow.
/// </summary>
public class AlertEventBus : IAlertEventBus
{
    private readonly Channel<AlertEvent> _channel = Channel.CreateBounded<AlertEvent>(
        new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

    public ValueTask PublishAsync(AlertEvent alertEvent, CancellationToken cancellationToken = default)
    {
        return _channel.Writer.TryWrite(alertEvent) ? ValueTask.CompletedTask : SlowPublishAsync(alertEvent, cancellationToken);
    }

    private async ValueTask SlowPublishAsync(AlertEvent alertEvent, CancellationToken cancellationToken)
    {
        await _channel.Writer.WriteAsync(alertEvent, cancellationToken);
    }

    public async IAsyncEnumerable<AlertEvent> ConsumeAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var evt in _channel.Reader.ReadAllAsync(cancellationToken))
        {
            yield return evt;
        }
    }
}
