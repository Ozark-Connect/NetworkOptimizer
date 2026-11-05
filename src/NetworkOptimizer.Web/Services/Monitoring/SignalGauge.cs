using System.Globalization;

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

    /// <summary>
    /// How much of a band track to dim above and below the reading, leaving a lit
    /// window on it.
    ///
    /// A band track cannot be lit from the bottom the way the rising one is. Doing
    /// that lights everything the reading has passed, so a perfectly good -22 dBm
    /// shows a bar full of the red and orange below it, and an overdriven -5 shows
    /// a bar full of green. Only the reading's own stretch of the track says
    /// anything true about the reading.
    /// </summary>
    public static (double Above, double Below) WindowEdges(double position, double halfHeight = 9)
    {
        var above = Math.Clamp(100 - position - halfHeight, 0, 100);
        var below = Math.Clamp(position - halfHeight, 0, 100);
        return (above, below);
    }

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
    public static string SnrTrack(double domainLow, double domainHigh, double fair, double good, double excellent) =>
        Css(SnrStops(domainLow, domainHigh, fair, good, excellent));

    /// <summary>The stops of <see cref="SnrTrack"/>, for a sparkline drawn on the same scale.</summary>
    public static IReadOnlyList<GaugeStop> SnrStops(double domainLow, double domainHigh, double fair, double good, double excellent)
    {
        var f = Position(fair, domainLow, domainHigh);
        var g = Position(good, domainLow, domainHigh);
        var e = Position(excellent, domainLow, domainHigh);

        return [new(0, Poor), new(f / 2, Weak), new(f, Fair), new(g, Good), new(e, Excellent), new(100, Excellent)];
    }

    /// <summary>
    /// Cellular signal quality, 0 to 100, colored at the thresholds where the hero's signal bars
    /// light one more bar, so a sparkline on that scale changes color where the bars change count.
    /// </summary>
    public static readonly IReadOnlyList<GaugeStop> QualityStops =
        [new(0, Poor), new(20, Weak), new(40, Fair), new(60, Good), new(80, Excellent), new(100, Excellent)];

    /// <summary>
    /// Rising track for cellular RSRP: poor below <paramref name="fair"/>, fair up to
    /// <paramref name="good"/>, good above it, matching the grades the 5G NR and LTE boxes color by.
    /// </summary>
    public static IReadOnlyList<GaugeStop> RsrpStops(double domainLow, double domainHigh, double fair, double good) =>
        [new(0, Poor), new(Position(fair, domainLow, domainHigh), Fair), new(Position(good, domainLow, domainHigh), Good), new(100, Excellent)];

    private static string Css(IEnumerable<GaugeStop> stops) =>
        "linear-gradient(to top, " +
        string.Join(", ", stops.Select(s => $"{s.Color} {s.Position.ToString("0.#", CultureInfo.InvariantCulture)}%")) + ")";

    /// <summary>
    /// Band track for optical Rx power: red at both ends, peaking green at the
    /// middle of the excellent band and easing out through good and fair either
    /// side of it. The upper red is the point of the whole thing - it is what
    /// tells a user their receiver is being overdriven rather than doing well.
    ///
    /// Green peaks at a point rather than holding flat across the whole excellent
    /// band: the eye reads the taper as "how much room is left", which a plateau
    /// hides.
    /// </summary>
    public static string OpticalTrack(OpticalBands bands) => Css(OpticalStops(bands));

    /// <summary>The stops of <see cref="OpticalTrack"/>, for a sparkline drawn on the same scale.</summary>
    public static IReadOnlyList<GaugeStop> OpticalStops(OpticalBands bands)
    {
        double P(double v) => Position(v, bands.DomainLow, bands.DomainHigh);

        return
        [
            new(0, Poor),
            new(P(bands.FairLow), Weak),
            new(P(bands.GoodLow), Fair),
            new(P(bands.ExcellentLow), Good),
            new(P(bands.Peak), Excellent),
            new(P(bands.ExcellentHigh), Good),
            new(P(bands.GoodHigh), Fair),
            new(P(bands.FairHigh), Weak),
            new(100, Poor),
        ];
    }
}

/// <summary>One color stop on a gauge track: a CSS color at a position from 0 (bottom) to 100 (top).</summary>
public readonly record struct GaugeStop(double Position, string Color);

/// <summary>
/// Optical Rx power grading bounds, in dBm. Every grade is a range with a floor
/// and a ceiling: too much light is a fault in its own right.
/// </summary>
/// <param name="Peak">
/// Where the gauge shows its best reading. Stated outright rather than taken as the
/// middle of the excellent band: that middle is -15 dBm for PON, which would only be
/// seen on an unusually low split ratio, so the gauge would have peaked well hotter
/// than a normal link ever runs.
/// </param>
public readonly record struct OpticalBands(
    double ExcellentLow, double ExcellentHigh,
    double GoodLow, double GoodHigh,
    double FairLow, double FairHigh,
    double Peak)
{
    /// <summary>PON and generic optics.</summary>
    public static readonly OpticalBands Pon = new(-22, -8, -25, -6, -28, -4, Peak: -18);

    /// <summary>Active Ethernet optics, which run far hotter than PON - no split loss to pay for.</summary>
    public static readonly OpticalBands ActiveEthernet = new(-8, -1, -10, 0, -14, 1, Peak: -4.5);

    /// <summary>
    /// External ONT devices, which report against slightly tighter bounds than the
    /// gateway-attached optics. Kept separate so the track's color boundaries land
    /// exactly where OntStatsPanel.ExternalRxClass changes grade.
    /// </summary>
    public static readonly OpticalBands ExternalOnt = new(-22.5, -8, -25, -6, -27, -4, Peak: -18);

    /// <summary>
    /// Half the track's span. Taken as the wider of the two sides so the domain is
    /// symmetric about <see cref="Peak"/> and neither graded end gets clipped.
    /// </summary>
    private double HalfSpan => Math.Max(Peak - (FairLow - 2), (FairHigh + 2) - Peak);

    /// <summary>
    /// Bottom of the track. Placed so the peak sits at dead center: the graded bounds
    /// are not symmetric around it (PON runs -28 to -4 either side of a -18 peak), and
    /// deriving the ends from those bounds alone put the best reading below the middle
    /// of the bar.
    /// </summary>
    public double DomainLow => Peak - HalfSpan;

    /// <summary>Top of the track, the same distance past the peak as <see cref="DomainLow"/> is below it.</summary>
    public double DomainHigh => Peak + HalfSpan;

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
