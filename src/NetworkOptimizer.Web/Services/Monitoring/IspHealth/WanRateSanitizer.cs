namespace NetworkOptimizer.Web.Services.Monitoring.IspHealth;

/// <summary>
/// Drops physically impossible WAN throughput samples before they reach load classification.
///
/// Interface counters reset when a link flaps or the gateway restarts, and the poller's next delta
/// spans that discontinuity - it reports a rate that never happened, several times the line's
/// capacity. One such sample is enough to mark its window fully loaded, and because these artifacts
/// land exactly at a flap, the outage's own 100%-loss samples then get averaged in as LOADED loss.
/// A real observed case read 1.09 Gbps on a plan a third of that, at the same second as an outage.
///
/// Clamping utilization does not help: the clamp happens after the sample has already been believed.
/// The sample has to be rejected, not squashed.
/// </summary>
public static class WanRateSanitizer
{
    /// <summary>Filtered samples plus how many were dropped, for the caller to log.</summary>
    public record Result(List<ThroughputSample> Samples, int Dropped);

    /// <summary>
    /// Rejects any sample whose download or upload exceeds its configured plan speed by more than
    /// <see cref="IspHealthOptions.WanRateImplausibleMultiple"/>. ISPs over-provision, and a burst can
    /// legitimately beat the plan, so the multiple is well above 1 - this is only meant to catch
    /// counter artifacts, never a fast line.
    ///
    /// A direction with no configured plan is not judged: with nothing to compare against, guessing a
    /// ceiling would risk discarding real traffic, and a missing plan already disables the load-based
    /// factors anyway. The whole sample is dropped when either direction is implausible, because a
    /// counter discontinuity corrupts the reading rather than one field of it.
    /// </summary>
    public static Result Filter(
        IReadOnlyList<ThroughputSample> samples,
        double? expectedDownloadMbps,
        double? expectedUploadMbps,
        IspHealthOptions options)
    {
        var downCeiling = expectedDownloadMbps is > 0
            ? expectedDownloadMbps.Value * 1_000_000 * options.WanRateImplausibleMultiple
            : (double?)null;
        var upCeiling = expectedUploadMbps is > 0
            ? expectedUploadMbps.Value * 1_000_000 * options.WanRateImplausibleMultiple
            : (double?)null;

        if (downCeiling is null && upCeiling is null)
            return new Result(samples.ToList(), 0);

        var kept = new List<ThroughputSample>(samples.Count);
        var dropped = 0;
        foreach (var s in samples)
        {
            var bad = (downCeiling.HasValue && s.DownloadBps > downCeiling.Value)
                || (upCeiling.HasValue && s.UploadBps > upCeiling.Value);
            if (bad) dropped++;
            else kept.Add(s);
        }
        return new Result(kept, dropped);
    }
}
