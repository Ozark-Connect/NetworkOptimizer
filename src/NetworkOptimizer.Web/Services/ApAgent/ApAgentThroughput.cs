namespace NetworkOptimizer.Web.Services.ApAgent;

/// <summary>
/// Turns two readings of an access point's cumulative per-client byte counters into a rate.
///
/// One definition on purpose. The counters are consumed twice - once per write window by the fold
/// that produces the stored point, and once per poll by the collector that feeds the live cache -
/// and the rules that make the result trustworthy (what counts as a usable gap, what a counter
/// going backwards means) have to be identical in both, or the number shown and the number stored
/// disagree about the same traffic.
/// </summary>
internal static class ApAgentThroughput
{
    /// <summary>
    /// Shortest gap that yields a usable rate. Below this the division amplifies the reading's own
    /// timing error into a rate far larger than any traffic that occurred.
    /// </summary>
    private const double MinElapsedSeconds = 0.5;

    /// <summary>
    /// Bits per second in each direction, or null when the pair cannot support a rate: too little
    /// time between readings, or a counter that went backwards, which is an association reset
    /// rather than negative traffic.
    /// </summary>
    /// <remarks>
    /// Direction is the access point's: <paramref name="txBytes"/> is what it sent to the client
    /// (a download), <paramref name="rxBytes"/> what it received. That matches
    /// <c>TxThroughputBps</c>/<c>RxThroughputBps</c> everywhere else, and it is inherited from the
    /// counters themselves rather than chosen here.
    /// </remarks>
    public static (double? Tx, double? Rx) FromCounters(
        long txBytes, long rxBytes, DateTime at,
        long priorTxBytes, long priorRxBytes, DateTime priorAt)
    {
        var elapsed = (at - priorAt).TotalSeconds;
        var deltaTx = txBytes - priorTxBytes;
        var deltaRx = rxBytes - priorRxBytes;

        if (elapsed <= MinElapsedSeconds || deltaTx < 0 || deltaRx < 0) return (null, null);

        return (deltaTx * 8.0 / elapsed, deltaRx * 8.0 / elapsed);
    }
}
