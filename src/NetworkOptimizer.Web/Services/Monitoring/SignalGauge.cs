namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>
/// Gradient tracks for the dashboard signal gauges. The gradient stops and the
/// health thresholds come from one place here so a threshold change cannot leave
/// the bar showing green where the reading is called poor.
///
/// Two shapes, because the two readings are not the same kind of scale. DOCSIS
/// SNR only rises: more is better, all the way up. Optical Rx power is a band -
/// a receiver above the top of it is overloaded, which is a fault, so the track
/// has to run back to red at BOTH ends. Never reuse the rising track for optical.
/// </summary>
public static class SignalGauge
{
    private const string Poor = "var(--signal-poor)";
    private const string Weak = "var(--signal-weak)";
    private const string Fair = "var(--signal-fair)";
    private const string Good = "var(--signal-good)";
    private const string Excellent = "var(--signal-excellent)";

    /// <summary>Where a reading sits on its track, 0 (bottom) to 100 (top).</summary>
    public static double Position(double value, double domainLow, double domainHigh)
    {
        if (domainHigh <= domainLow) return 0;
        var pct = (value - domainLow) / (domainHigh - domainLow) * 100.0;
        return Math.Clamp(pct, 0, 100);
    }

    /// <summary>
    /// Rising track for DOCSIS downstream SNR: red at the bottom through to green
    /// at the top. Thresholds are the lower bounds for each grade.
    /// </summary>
    public static string SnrTrack(double domainLow, double domainHigh, double fair, double good, double excellent)
    {
        var f = Position(fair, domainLow, domainHigh);
        var g = Position(good, domainLow, domainHigh);
        var e = Position(excellent, domainLow, domainHigh);

        return "linear-gradient(to top, " +
               $"{Poor} 0%, {Weak} {f / 2:0.#}%, {Fair} {f:0.#}%, {Good} {g:0.#}%, {Excellent} {e:0.#}%, {Excellent} 100%)";
    }

    /// <summary>
    /// Band track for optical Rx power: red at both ends, green through the good
    /// band. The upper red is the point of the whole thing - it is what tells a
    /// user their receiver is being overdriven rather than that it is doing well.
    /// </summary>
    public static string OpticalTrack(OpticalBands bands)
    {
        var lo = bands.DomainLow;
        var hi = bands.DomainHigh;

        double P(double v) => Position(v, lo, hi);

        return "linear-gradient(to top, " +
               $"{Poor} 0%, " +
               $"{Weak} {P(bands.FairLow):0.#}%, " +
               $"{Fair} {P(bands.GoodLow):0.#}%, " +
               $"{Excellent} {P(bands.ExcellentLow):0.#}%, " +
               $"{Excellent} {P(bands.ExcellentHigh):0.#}%, " +
               $"{Good} {P(bands.GoodHigh):0.#}%, " +
               $"{Fair} {P(bands.FairHigh):0.#}%, " +
               $"{Poor} 100%)";
    }
}

/// <summary>
/// Optical Rx power grading bounds, in dBm. Every grade is a range with a floor
/// and a ceiling: too much light is a fault in its own right.
/// </summary>
public readonly record struct OpticalBands(
    double ExcellentLow, double ExcellentHigh,
    double GoodLow, double GoodHigh,
    double FairLow, double FairHigh)
{
    /// <summary>PON and generic optics.</summary>
    public static readonly OpticalBands Pon = new(-22, -8, -25, -6, -28, -4);

    /// <summary>Active Ethernet optics, which run far hotter than PON.</summary>
    public static readonly OpticalBands ActiveEthernet = new(-8, -1, -10, 0, -14, 1);

    /// <summary>
    /// External ONT devices, which report against slightly tighter bounds than the
    /// gateway-attached optics. Kept separate so the track's color boundaries land
    /// exactly where OntStatsPanel.ExternalRxClass changes grade.
    /// </summary>
    public static readonly OpticalBands ExternalOnt = new(-22.5, -8, -25, -6, -27, -4);

    /// <summary>A little headroom past the last graded bound, so the ends are visible.</summary>
    public double DomainLow => FairLow - 2;

    /// <summary>A little headroom past the last graded bound, so the ends are visible.</summary>
    public double DomainHigh => FairHigh + 2;

    /// <summary>The signal-* class for a reading, or empty when there is none.</summary>
    public string ClassFor(double? rxDbm)
    {
        if (!rxDbm.HasValue) return "";
        var v = rxDbm.Value;
        if (v >= ExcellentLow && v <= ExcellentHigh) return "signal-excellent";
        if (v >= GoodLow && v <= GoodHigh) return "signal-good";
        if (v >= FairLow && v <= FairHigh) return "signal-fair";
        return "signal-poor";
    }
}
