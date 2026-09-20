using System.Collections.Concurrent;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Runs one scheduled WAN speed test per site at a time. The scheduler starts every due task at
/// once, and tests sharing a gateway both refuse each other and skew each other's throughput, so a
/// second test queues behind the first instead.
/// </summary>
internal sealed class SiteWanTestGate
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Waits for the site's turn. <c>Queued</c> is true when another test held the gate first.
    /// Dispose the lease to release it.
    /// </summary>
    public async Task<(IDisposable Lease, bool Queued)> EnterAsync(string siteKey, CancellationToken ct)
    {
        var gate = _gates.GetOrAdd(siteKey, _ => new SemaphoreSlim(1, 1));
        var queued = !gate.Wait(0);
        if (queued)
            await gate.WaitAsync(ct);
        return (new Lease(gate), queued);
    }

    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                gate.Release();
        }
    }
}
