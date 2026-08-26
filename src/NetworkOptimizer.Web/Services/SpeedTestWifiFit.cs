namespace NetworkOptimizer.Web.Services;

/// <summary>One access point's association with the client during the test window.</summary>
/// <param name="ApMac">Access point that reported it.</param>
/// <param name="Band">Band token from the series.</param>
/// <param name="TxRateKbps">PHY the access point transmits at - the ceiling on To Device.</param>
/// <param name="RxRateKbps">PHY the access point receives at - the ceiling on From Device.</param>
/// <param name="SignalDbm">Signal over the window.</param>
/// <param name="NoiseDbm">Noise floor over the window.</param>
/// <param name="Channel">Channel the association was on.</param>
/// <param name="ChannelWidth">Channel width in MHz.</param>
/// <param name="Points">How many series points backed this candidate.</param>
/// <param name="ObservedThroughputBps">Most traffic seen on this association during the window, in
/// either direction. Zero means it carried nothing, which is evidence it did not serve the test -
/// but a roam mid-test resets the byte counters, so absence is not proof.</param>
public sealed record WifiFitCandidate(
    string ApMac,
    string? Band,
    long? TxRateKbps,
    long? RxRateKbps,
    double? SignalDbm,
    int Points,
    double? NoiseDbm = null,
    int? Channel = null,
    int? ChannelWidth = null,
    double ObservedThroughputBps = 0);

/// <summary>A scored candidate. Efficiencies are measured throughput over the PHY that bounds it.</summary>
/// <param name="Candidate">The association scored.</param>
/// <param name="FromDeviceEfficiency">Download over RX PHY, or null when either is unknown.</param>
/// <param name="ToDeviceEfficiency">Upload over TX PHY, or null when either is unknown.</param>
/// <param name="Score">Mean of the known efficiencies; higher explains the measurement more tightly.</param>
/// <param name="Rejected">Why it cannot be the association, or null if it is a candidate.</param>
public sealed record WifiFitScore(
    WifiFitCandidate Candidate,
    double? FromDeviceEfficiency,
    double? ToDeviceEfficiency,
    double Score,
    string? Rejected)
{
    public bool IsViable => Rejected == null;
}

/// <summary>
/// Picks which access point actually served a speed test. Time decides: points are stamped when the
/// access point read them, so an association present through the test window is the one that held
/// the client. Efficiency only breaks ties between access points that genuinely overlap a roam, and
/// never disqualifies - an implausible ratio means the PHY sample is unrepresentative.
///
/// Direction mapping, confirmed with TJ and load-bearing (see CLAUDE.md):
///   From Device (client to server) = DownloadBitsPerSecond, bounded by RX PHY (access point receives)
///   To Device   (server to client) = UploadBitsPerSecond,   bounded by TX PHY (access point transmits)
/// </summary>
public static class SpeedTestWifiFit
{
    /// <summary>
    /// Scores every candidate. Order is by score descending, rejected ones last, so a caller can log
    /// the whole comparison rather than only what won.
    /// </summary>
    public static IReadOnlyList<WifiFitScore> Score(
        IEnumerable<WifiFitCandidate> candidates,
        double? fromDeviceBps,
        double? toDeviceBps)
    {
        var scored = new List<WifiFitScore>();

        foreach (var c in candidates)
        {
            var from = Efficiency(fromDeviceBps, c.RxRateKbps);
            var to = Efficiency(toDeviceBps, c.TxRateKbps);

            var known = new[] { from, to }.Where(e => e.HasValue).Select(e => e!.Value).ToList();
            if (known.Count == 0)
            {
                scored.Add(new WifiFitScore(c, from, to, 0, "no PHY for either direction"));
                continue;
            }

            scored.Add(new WifiFitScore(c, from, to, known.Average(), null));
        }

        // Time first. Points are stamped when the access point read them, so an association present
        // through the window IS the one that held the client - that is evidence, where efficiency is
        // only a proxy for it. Coverage, then whether it carried traffic, then efficiency last as a
        // tiebreak between access points that genuinely overlap a roam.
        return scored
            .OrderBy(s => s.IsViable ? 0 : 1)
            .ThenByDescending(s => s.Candidate.Points)
            .ThenByDescending(s => s.Candidate.ObservedThroughputBps > 0 ? 1 : 0)
            .ThenByDescending(s => s.Score)
            .ToList();
    }

    /// <summary>The best-fitting association, or null when none of them explains the measurement.</summary>
    public static WifiFitScore? Best(
        IEnumerable<WifiFitCandidate> candidates,
        double? fromDeviceBps,
        double? toDeviceBps)
        => Score(candidates, fromDeviceBps, toDeviceBps).FirstOrDefault(s => s.IsViable);

    private static double? Efficiency(double? measuredBps, long? phyKbps)
    {
        if (measuredBps is not > 0 || phyKbps is not > 0) return null;
        return measuredBps.Value / (phyKbps.Value * 1000.0);
    }
}
