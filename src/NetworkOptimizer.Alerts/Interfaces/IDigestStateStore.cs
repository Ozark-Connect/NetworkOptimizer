namespace NetworkOptimizer.Alerts.Interfaces;

/// <summary>
/// Persists digest "last sent" timestamps so they survive app restarts.
/// </summary>
public interface IDigestStateStore
{
    Task<DateTime?> GetLastSentAsync(int channelId, CancellationToken cancellationToken = default);
    Task SetLastSentAsync(int channelId, DateTime sentAt, CancellationToken cancellationToken = default);
}
