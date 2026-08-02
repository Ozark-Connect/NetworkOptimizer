namespace NetworkOptimizer.Web.Services.Monitoring.IspHealth;

/// <summary>
/// Loss measured to the LAN gateway, used as a floor under every upstream loss measurement.
///
/// Every probe this host sends crosses its own NIC, its cable, the switching fabric and the gateway
/// before it can reach anything beyond - so loss to the gateway is the common-mode noise floor of the
/// whole measurement chain. A healthy UniFi gateway does not drop ICMP addressed to itself; when it
/// does, the cause is on this side of the WAN (gateway CPU or forwarding path, switching overload or
/// errors, cabling, or the monitoring host's own NIC), and a gateway dropping under load is dropping
/// the packets it routes to the WAN too. None of it is the ISP's, and all of it lands on every
/// upstream target at once.
///
/// Subtractive only: it caps what may be attributed upstream and can never invent upstream health,
/// nor make a target read worse than it measured.
///
/// LOSS ONLY. Gateway RTT inflates under CPU pressure with no forwarding fault at all, so the same
/// treatment applied to latency would absolve real upstream latency. Drops are the clean signal
/// precisely because a gateway only drops when forwarding or switching is genuinely in trouble.
/// </summary>
public sealed class GatewayLossFloor
{
    private readonly DateTime[] _times;
    private readonly double[] _lossPct;
    private readonly double _stalenessSeconds;

    private GatewayLossFloor(DateTime[] times, double[] lossPct, double stalenessSeconds)
    {
        _times = times;
        _lossPct = lossPct;
        _stalenessSeconds = stalenessSeconds;
    }

    /// <summary>An empty floor: subtracts nothing. Used when no gateway target is monitored.</summary>
    public static GatewayLossFloor None { get; } =
        new(Array.Empty<DateTime>(), Array.Empty<double>(), 0);

    /// <summary>True when any gateway loss was measured at all - i.e. when the floor can bite.</summary>
    public bool HasLoss { get; private set; }

    /// <summary>Highest gateway loss seen, for the finding that reports the local fault.</summary>
    public double PeakLossPct { get; private set; }

    /// <summary>Mean gateway loss across the samples that carried a reading.</summary>
    public double MeanLossPct { get; private set; }

    /// <summary>
    /// Builds the floor from the gateway target's series. Samples without a loss reading are dropped
    /// rather than treated as zero: an absent measurement is not evidence the chain was clean.
    /// </summary>
    public static GatewayLossFloor Build(IReadOnlyList<LatencySample> gatewaySamples, IspHealthOptions options)
    {
        if (gatewaySamples.Count == 0) return None;

        var ordered = gatewaySamples
            .Where(s => s.LossPercent.HasValue)
            .OrderBy(s => s.Time)
            .ToList();
        if (ordered.Count == 0) return None;

        var times = new DateTime[ordered.Count];
        var loss = new double[ordered.Count];
        var sum = 0.0;
        var peak = 0.0;
        for (var i = 0; i < ordered.Count; i++)
        {
            times[i] = ordered[i].Time;
            var v = Math.Clamp(ordered[i].LossPercent!.Value, 0, 100);
            loss[i] = v;
            sum += v;
            if (v > peak) peak = v;
        }

        return new GatewayLossFloor(times, loss, options.GatewayFloorMaxStalenessSeconds)
        {
            HasLoss = peak > 0,
            PeakLossPct = peak,
            MeanLossPct = sum / ordered.Count
        };
    }

    /// <summary>
    /// The floor at an instant: the most recent gateway reading at or before it, provided that reading
    /// is not stale. Carried forward rather than re-interpolated because the floor reflects a physical
    /// condition that persists across a poll or two; past the staleness bound it decays to zero, so an
    /// old reading cannot keep suppressing loss indefinitely. Zero when nothing is known, which leaves
    /// the measurement untouched.
    /// </summary>
    public double FloorAt(DateTime time)
    {
        if (_times.Length == 0) return 0;

        var i = Array.BinarySearch(_times, time);
        if (i < 0)
        {
            i = ~i - 1;          // the entry immediately before the insertion point
            if (i < 0) return 0; // before the first reading
        }
        return (time - _times[i]).TotalSeconds <= _stalenessSeconds ? _lossPct[i] : 0;
    }

    /// <summary>
    /// One target's loss with the local chain's contribution removed. Never negative: a target
    /// measuring less loss than the gateway did is reporting a clean path, not a negative one.
    /// </summary>
    public double Apply(double lossPct, DateTime time)
    {
        if (_times.Length == 0) return lossPct;
        var floor = FloorAt(time);
        return floor <= 0 ? lossPct : Math.Max(0, lossPct - floor);
    }
}
