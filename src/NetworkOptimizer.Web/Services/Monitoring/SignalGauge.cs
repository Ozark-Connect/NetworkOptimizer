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
/// falls off from green in BOTH directions. Never reuse the rising track for optical.
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
    /// Cellular signal quality, 0 to 100, in hard steps at the thresholds where the hero's signal
    /// bars light one more bar. The lit bars take one flat grade color, so a sparkline on this
    /// scale has to change color at the same bounds, not blend across them, to match them.
    /// </summary>
    /// <param name="floor">The quality at the bottom of the scale; the hero rescales to its day's range.</param>
    public static IReadOnlyList<GaugeStop> QualityStops(double floor = 0)
    {
        double P(double q) => Position(q, floor, 100);
        return
        [
            new(0, Poor), new(P(20), Poor),
            new(P(20), Weak), new(P(40), Weak),
            new(P(40), Fair), new(P(60), Fair),
            new(P(60), Good), new(P(80), Good),
            new(P(80), Excellent), new(100, Excellent),
        ];
    }
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
    /// Band track for optical Rx power, peaking green at the peak and easing out through
    /// good and fair either side of it, so a hot link shows the color turning above the peak
    /// as a weak one does below it.
    ///
    /// Green peaks at a point rather than holding flat across the whole excellent
    /// band: the eye reads the taper as "how much room is left", which a plateau
    /// hides.
    /// </summary>
    public static string OpticalTrack(OpticalBands bands) => Css(OpticalStops(bands));

    /// <summary>
    /// The stops of <see cref="OpticalTrack"/>, for a sparkline drawn on the same scale. Each sits
    /// at its own dBm bound; a bound past the track's ends is dropped rather than piled onto the
    /// edge, where it would paint a hairline of its color, and the edge keeps the last color inside.
    /// </summary>
    public static IReadOnlyList<GaugeStop> OpticalStops(OpticalBands bands)
    {
        var span = bands.DomainHigh - bands.DomainLow;
        double Raw(double v) => span > 0 ? (v - bands.DomainLow) / span * 100 : 0;

        var inner = new[]
        {
            new GaugeStop(Raw(bands.FairLow), Weak),
            new GaugeStop(Raw(bands.GoodLow), Fair),
            new GaugeStop(Raw(bands.ExcellentLow), Good),
            new GaugeStop(Raw(bands.Peak), Excellent),
            new GaugeStop(Raw(bands.ExcellentHigh), Good),
            new GaugeStop(Raw(bands.GoodHigh), Fair),
            new GaugeStop(Raw(bands.FairHigh), Weak),
        }.Where(s => s.Position >= 0 && s.Position <= 100).ToList();

        var stops = new List<GaugeStop>();
        if (inner.Count == 0 || inner[0].Position > 0) stops.Add(new(0, Poor));
        stops.AddRange(inner);
        if (inner.Count == 0 || inner[^1].Position < 100) stops.Add(new(100, Poor));
        return stops;
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
    /// Bottom of the track: the fair floor (-28 dBm for PON). The track spans only the readings a
    /// working link shows, so a day's movement fills the gauge and its sparkline; a reading past
    /// either end pins there, which is a fault worth seeing at a glance. It used to be mirrored
    /// about <see cref="Peak"/>, which put the PON floor at -34 dBm and its ceiling at -2. Every
    /// color stop sits at its dBm bound, so the colors do not move with the ends.
    /// </summary>
    public double DomainLow => FairLow;

    /// <summary>Top of the track: the good ceiling (-6 dBm for PON); hotter pins to the top.</summary>
    public double DomainHigh => GoodHigh;

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
