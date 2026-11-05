using System.Globalization;
using System.Text;

namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>
/// SVG paths for a signal hero sparkline, in a 0-100 box drawn with preserveAspectRatio="none".
/// Y uses the hero gauge's own scale (<see cref="SignalGauge.Position"/>), so a point drawn level
/// with the gauge's reading is the same reading. X runs from the start of the window to now.
/// </summary>
public static class SparklinePath
{
    /// <summary>The line and area paths, and where the line ends; empty paths when nothing draws.</summary>
    public readonly record struct Result(string Line, string Area, double? EndX, double? EndY);

    /// <summary>Where, within the landing, the line has reached the live reading and holds it (0-1).</summary>
    public const double LandingReach = 0.15;

    /// <summary>Where along the taper (0-1) it reaches full size and holds it to the end.</summary>
    public const double TaperFull = 0.85;

    /// <summary>The taper element's overflow above and below the chart; keep in step with .signal-spark-taper.</summary>
    public const string TaperPad = "16px";

    /// <summary>The transform-origin height of the taper: the line's own level inside the padded element.</summary>
    public static string TaperOrigin(double endY) => $"calc({TaperPad} + {F(endY / 100)} * (100% - 2 * {TaperPad}))";

    /// <param name="current">The live reading, appended at <paramref name="now"/> so the line ends on the value beside it.</param>
    /// <param name="maxGap">A longer gap between samples breaks the line instead of bridging an outage.</param>
    /// <param name="landing">
    /// Share of the width (0-100) kept for the reach from the last sample to the live reading. History
    /// is 15-minute means, so without it a change since the last one is squeezed into a few pixels
    /// and the line meets the gauge still in the old reading's color.
    /// </param>
    public static Result Build(
        IReadOnlyList<SparkPoint>? points, double? current, DateTime now, TimeSpan window,
        double domainLow, double domainHigh, TimeSpan maxGap, double landing = 0)
    {
        var hasCurrent = current is double cur && double.IsFinite(cur);
        var historyWidth = hasCurrent ? 100 - Math.Clamp(landing, 0, 50) : 100;
        var start = now - window;
        var all = (points ?? []).Where(p => p.Time >= start && p.Time < now).ToList();
        if (current is double c && double.IsFinite(c)) all.Add(new SparkPoint(now, c));
        if (all.Count == 0) return new Result("", "", null, null);

        var span = window.TotalSeconds;
        var segments = new List<List<(double X, double Y)>> { new() };
        DateTime? prev = null;
        foreach (var p in all)
        {
            if (prev is DateTime t && p.Time - t > maxGap && segments[^1].Count > 0)
                segments.Add(new());
            var x = p.Time >= now ? 100 : Math.Clamp((p.Time - start).TotalSeconds / span * historyWidth, 0, historyWidth);
            var y = 100 - SignalGauge.Position(p.Value, domainLow, domainHigh);
            segments[^1].Add((x, y));
            prev = p.Time;
        }

        // Reach the live reading early in the landing and hold it, so the line meets the gauge flat
        // and in the live reading's color.
        var last = segments[^1];
        if (hasCurrent && historyWidth < 100 && last.Count >= 2)
            last.Insert(last.Count - 1, (historyWidth + (100 - historyWidth) * LandingReach, last[^1].Y));

        var line = new StringBuilder();
        var area = new StringBuilder();
        foreach (var seg in segments.Where(s => s.Count >= 2))
        {
            var curve = MonotoneCurve(seg);
            line.Append('M').Append(F(seg[0].X)).Append(',').Append(F(seg[0].Y)).Append(curve);
            area.Append('M').Append(F(seg[0].X)).Append(",100L").Append(F(seg[0].X)).Append(',').Append(F(seg[0].Y))
                .Append(curve).Append('L').Append(F(seg[^1].X)).Append(",100Z");
        }

        var end = segments[^1][^1];
        return new Result(line.ToString(), area.ToString(), end.X, end.Y);
    }

    /// <summary>
    /// Cubic segments through every point with monotone tangents (Fritsch-Carlson, as d3's
    /// curveMonotoneX): the curve never overshoots a sample, so it never draws a reading that was
    /// not there, and never leaves the 0-100 box.
    /// </summary>
    private static string MonotoneCurve(IReadOnlyList<(double X, double Y)> p)
    {
        var n = p.Count;
        var m = new double[n];
        for (var i = 1; i < n - 1; i++)
        {
            var h0 = p[i].X - p[i - 1].X;
            var h1 = p[i + 1].X - p[i].X;
            if (h0 <= 0 || h1 <= 0) continue;
            var s0 = (p[i].Y - p[i - 1].Y) / h0;
            var s1 = (p[i + 1].Y - p[i].Y) / h1;
            var mid = (s0 * h1 + s1 * h0) / (h0 + h1);
            m[i] = (Math.Sign(s0) + Math.Sign(s1)) * Math.Min(Math.Min(Math.Abs(s0), Math.Abs(s1)), 0.5 * Math.Abs(mid));
        }
        m[0] = Secant(p[0], p[1]);
        m[n - 1] = Secant(p[n - 2], p[n - 1]);

        var sb = new StringBuilder();
        for (var i = 0; i < n - 1; i++)
        {
            var h = (p[i + 1].X - p[i].X) / 3;
            sb.Append('C')
              .Append(F(p[i].X + h)).Append(',').Append(F(Math.Clamp(p[i].Y + m[i] * h, 0, 100))).Append(' ')
              .Append(F(p[i + 1].X - h)).Append(',').Append(F(Math.Clamp(p[i + 1].Y - m[i + 1] * h, 0, 100))).Append(' ')
              .Append(F(p[i + 1].X)).Append(',').Append(F(p[i + 1].Y));
        }
        return sb.ToString();
    }

    /// <summary>
    /// A CSS polygon() for the tail that thickens the line where it meets its gauge: from the stroke
    /// (<paramref name="halfStroke"/> either side of <paramref name="endY"/>) to <paramref name="up"/>
    /// above and <paramref name="down"/> below it. Quadratic in the distance along the tail: the edges
    /// leave the line tangentially and flare mostly in the last stretch, reaching full size at
    /// <see cref="TaperFull"/> and holding it, so the end is as tall as what it meets for its last pixels.
    /// The lengths are CSS (px, %, or calc), since the gauge's size is only known in the browser.
    /// </summary>
    /// <param name="pad">Room the taper element has above and below the chart (it reaches past the chart
    /// at the scale's ends); the chart's own 0-100 is the element's height less twice this.</param>
    public static string TaperPolygon(double endY, string up, string down, string halfStroke = "0.875px", int steps = 12, string pad = TaperPad)
    {
        var y = $"{pad} + {F(endY / 100)} * (100% - 2 * {pad})";
        var top = new List<string>();
        var bottom = new List<string>();
        for (var i = 0; i <= steps; i++)
        {
            var s = (double)i / steps;
            var r = Math.Min(1, s / TaperFull);
            var t = F(r * r);
            var x = F(s * 100);
            top.Add($"{x}% calc({y} - ({halfStroke} + {t} * ({up} - {halfStroke})))");
            bottom.Add($"{x}% calc({y} + ({halfStroke} + {t} * ({down} - {halfStroke})))");
        }
        bottom.Reverse();
        return $"polygon({string.Join(", ", top.Concat(bottom))})";
    }

    private static double Secant((double X, double Y) a, (double X, double Y) b) =>
        b.X > a.X ? (b.Y - a.Y) / (b.X - a.X) : 0;

    private static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}
