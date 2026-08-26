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
public sealed record WifiFitCandidate(
    string ApMac,
    string? Band,
    long? TxRateKbps,
    long? RxRateKbps,
    double? SignalDbm,
    int Points,
    double? NoiseDbm = null,
    int? Channel = null,
    int? ChannelWidth = null);

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
/// Picks which access point actually served a speed test, by asking which association's PHY explains
/// the throughput that was measured.
///
/// A client that roams mid-test leaves the topology snapshot describing an access point it is no
/// longer on, and on a meshed access point that snapshot can carry the mesh uplink's PHY instead of
/// the client's - measured once at 1921/1441 Mbps against 10.9/21.3 Mbps of actual throughput.
/// Absurdity is the signal: 0.6% efficiency is not a link, it is the wrong record.
///
/// Direction mapping, confirmed with TJ and load-bearing (see CLAUDE.md):
///   From Device (client to server) = DownloadBitsPerSecond, bounded by RX PHY (access point receives)
///   To Device   (server to client) = UploadBitsPerSecond,   bounded by TX PHY (access point transmits)
/// </summary>
public static class SpeedTestWifiFit
{
    /// <summary>
    /// Measured throughput may exceed the reported PHY slightly - they are sampled on different
    /// clocks and the rate moves during a test. Past this the candidate cannot be what carried it.
    /// </summary>
    public const double MaxEfficiency = 1.05;

    /// <summary>
    /// Below this the PHY does not describe the link at all. Deliberately far under any real link:
    /// a genuinely bad one - interference, retries, a distant client - runs at low efficiency and
    /// must not be discarded as impossible.
    /// </summary>
    public const double MinEfficiency = 0.02;

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

            var rejected =
                known.Any(e => e > MaxEfficiency) ? "throughput exceeds this PHY"
                : known.Any(e => e < MinEfficiency) ? "PHY far too high to explain the throughput"
                : null;

            scored.Add(new WifiFitScore(c, from, to, known.Average(), rejected));
        }

        return scored
            .OrderBy(s => s.IsViable ? 0 : 1)
            .ThenByDescending(s => s.Score)
            .ThenByDescending(s => s.Candidate.Points)
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
