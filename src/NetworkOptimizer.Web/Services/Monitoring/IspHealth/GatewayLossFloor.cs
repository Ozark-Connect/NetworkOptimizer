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
    private readonly double _matchSeconds;

    private GatewayLossFloor(DateTime[] times, double[] lossPct, double stalenessSeconds, double matchSeconds)
    {
        _times = times;
        _lossPct = lossPct;
        _stalenessSeconds = stalenessSeconds;
        _matchSeconds = matchSeconds;
    }

    /// <summary>An empty floor: subtracts nothing. Used when no gateway target is monitored.</summary>
    public static GatewayLossFloor None { get; } =
        new(Array.Empty<DateTime>(), Array.Empty<double>(), 0, 0);

    /// <summary>True when any gateway loss was measured at all - i.e. when the floor can bite.</summary>
    public bool HasLoss { get; private set; }

    /// <summary>Highest gateway loss seen, for the finding that reports the local fault.</summary>
    public double PeakLossPct { get; private set; }

    /// <summary>Mean gateway loss across the samples that carried a reading.</summary>
    public double MeanLossPct { get; private set; }

    /// <summary>Gateway readings behind the floor, and the span they cover.</summary>
    public int ReadingCount => _times.Length;
    public DateTime? FirstReading => _times.Length > 0 ? _times[0] : null;
    public DateTime? LastReading => _times.Length > 0 ? _times[^1] : null;

    /// <summary>
    /// How the floor actually behaved this report: upstream loss readings seen, how many it reduced,
    /// and by how much in total. Counted rather than inferred, so "did it do anything" is answerable
    /// from a log line instead of from the shape of the input.
    /// </summary>
    public long SamplesSeen { get; private set; }
    public long SamplesReduced { get; private set; }
    public double TotalReductionPct { get; private set; }

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

        return new GatewayLossFloor(times, loss, options.GatewayFloorMaxStalenessSeconds, options.GatewayFloorMatchSeconds)
        {
            HasLoss = peak > 0,
            PeakLossPct = peak,
            MeanLossPct = sum / ordered.Count
        };
    }

    /// <summary>
    /// The floor at an instant: the worst gateway reading in the immediate neighborhood, looking both
    /// directions. An impairment that drops probes does so for as long as it lasts, so a probe landing
    /// between two gateway ticks was crossing the same impaired chain as the ticks either side of it.
    ///
    /// Where no reading is close enough - a series sampled far coarser than the match window - the
    /// last reading is carried forward instead, decaying to zero once stale, so an old value cannot
    /// keep suppressing loss indefinitely. Zero when nothing is known, leaving the measurement alone.
    /// </summary>
    public double FloorAt(DateTime time)
    {
        if (_times.Length == 0) return 0;

        // Worst gateway reading in the immediate neighborhood, looking both ways. An impairment that
        // drops probes does so continuously for as long as it lasts, while the gateway is sampled on
        // its own cadence - so which tick happens to precede a given probe is an artifact of timing,
        // not a fact about the network. Reading only backwards made one saturation burst subtract
        // between 0% and 46.7% across the same ten seconds, purely on sample alignment.
        var lo = LowerBound(time.AddSeconds(-_matchSeconds));
        var best = -1.0;
        var limit = time.AddSeconds(_matchSeconds);
        for (var i = lo; i < _times.Length && _times[i] <= limit; i++)
            if (_lossPct[i] > best) best = _lossPct[i];
        if (best >= 0) return best;

        // Nothing close by - fall back to carrying the last reading forward, which covers a series
        // sampled far coarser than the match window, and decays to zero once it is stale.
        var j = Array.BinarySearch(_times, time);
        if (j < 0)
        {
            j = ~j - 1;
            if (j < 0) return 0;
        }
        return (time - _times[j]).TotalSeconds <= _stalenessSeconds ? _lossPct[j] : 0;
    }

    /// <summary>Index of the first reading at or after <paramref name="from"/>.</summary>
    private int LowerBound(DateTime from)
    {
        var i = Array.BinarySearch(_times, from);
        return i >= 0 ? i : ~i;
    }

    /// <summary>
    /// One target's loss with the local chain's contribution removed. Never negative: a target
    /// measuring less loss than the gateway did is reporting a clean path, not a negative one.
    /// </summary>
    public double Apply(double lossPct, DateTime time)
    {
        if (_times.Length == 0) return lossPct;
        SamplesSeen++;
        var floor = FloorAt(time);
        if (floor <= 0) return lossPct;
        var reduced = Math.Max(0, lossPct - floor);
        if (reduced < lossPct)
        {
            SamplesReduced++;
            TotalReductionPct += lossPct - reduced;
        }
        return reduced;
    }
}
