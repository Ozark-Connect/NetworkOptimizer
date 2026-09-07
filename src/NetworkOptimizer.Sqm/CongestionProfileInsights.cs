using NetworkOptimizer.Sqm.Models;

namespace NetworkOptimizer.Sqm;

/// <summary>
/// Reads a learned curve back in plain words: when the line is slow, by how much, and how that
/// compares with the connection type's built-in assumption. Everything is hours and percentages
/// of the best hour; nothing about the measuring.
/// </summary>
public static class CongestionProfileInsights
{
    /// <summary>Hours within this much of the worst hour count as part of the slow band.</summary>
    public const double BandTolerance = 0.10;

    /// <summary>A learned and default depth closer than this read as "about what the default expects".</summary>
    public const double ComparisonTolerance = 0.08;

    /// <summary>Below this much variation the line is flat enough that a schedule barely matters.</summary>
    public const double FlatThreshold = 0.08;

    /// <summary>How the learned dip compares with the built-in curve over the same hours.</summary>
    public enum Comparison { Deeper, Shallower, Similar, Flat }

    /// <summary>The slow band and its comparison, ready to render.</summary>
    /// <param name="StartHour">First hour of the slow band (0-23).</param>
    /// <param name="EndHour">Last hour of the slow band (0-23); the band can wrap midnight.</param>
    /// <param name="Depth">How far below the best hour the band sits (0.3 = 30% below).</param>
    /// <param name="DefaultDepth">The built-in curve's depth over the same hours.</param>
    public sealed record Insight(
        string SlowestLabel,
        int StartHour,
        int EndHour,
        double Depth,
        double DefaultDepth,
        Comparison Comparison)
    {
        /// <summary>"weekday evenings (19:00 to 22:00), about 70% of your best hour"</summary>
        public string SlowestSentence =>
            $"{SlowestLabel} ({StartHour:00}:00 to {EndHour:00}:00), about {Math.Round((1 - Depth) * 100):F0}% of your best hour";

        /// <summary>One sentence against the connection type's default curve.</summary>
        public string ComparisonSentence(string connectionTypeName) => Comparison switch
        {
            Comparison.Flat => $"your line barely varies, so the {connectionTypeName} default was shaping it harder than it needed",
            Comparison.Deeper => $"dips deeper than the {connectionTypeName} default expects (about {Pct(Depth)} vs {Pct(DefaultDepth)} below your best hour)",
            Comparison.Shallower => $"dips less than the {connectionTypeName} default expects (about {Pct(Depth)} vs {Pct(DefaultDepth)} below your best hour)",
            _ => $"close to what the {connectionTypeName} default expects (about {Pct(Depth)} below your best hour)",
        };

        private static string Pct(double depth) => $"{Math.Round(depth * 100):F0}%";
    }

    /// <summary>
    /// Describes the learned download curve. Null when the curve is flat and so is the default,
    /// which leaves nothing worth saying.
    /// </summary>
    public static Insight? Describe(LearnedCongestionProfile learned, ConnectionType type)
    {
        var hourly = LearnedCongestionProfile.HourOfDayAverages(learned.DownloadMultipliers);
        var worst = Array.IndexOf(hourly, hourly.Min());
        var variation = 1 - hourly[worst];

        // Grow the band outward from the worst hour while neighbours stay within tolerance.
        var limit = hourly[worst] + BandTolerance;
        var start = worst;
        var end = worst;
        while (hourly[(start + 23) % 24] <= limit && Span(start, end) < 23) start = (start + 23) % 24;
        while (hourly[(end + 1) % 24] <= limit && Span(start, end) < 23) end = (end + 1) % 24;

        var bandHours = BandHours(start, end).ToList();
        var depth = 1 - bandHours.Average(h => hourly[h]);

        var defaults = new ConnectionProfile { Type = type, NominalDownloadMbps = 100 }.GetBaselinePatternPublic();
        var defaultHourly = new double[24];
        for (var h = 0; h < 24; h++)
        {
            double sum = 0;
            for (var d = 0; d < 7; d++) sum += defaults[d, h];
            defaultHourly[h] = sum / 7.0;
        }
        var defaultBest = defaultHourly.Max();
        var defaultDepth = 1 - bandHours.Average(h => defaultHourly[h]) / defaultBest;
        var defaultVariation = 1 - defaultHourly.Min() / defaultBest;

        Comparison comparison;
        if (variation < FlatThreshold)
        {
            if (defaultVariation < FlatThreshold) return null;
            comparison = Comparison.Flat;
        }
        else if (depth > defaultDepth + ComparisonTolerance) comparison = Comparison.Deeper;
        else if (defaultDepth > depth + ComparisonTolerance) comparison = Comparison.Shallower;
        else comparison = Comparison.Similar;

        return new Insight(Label(learned, bandHours, start, end), start, end, depth, Math.Max(0, defaultDepth), comparison);
    }

    private static int Span(int start, int end) => ((end - start) % 24 + 24) % 24;

    private static IEnumerable<int> BandHours(int start, int end)
    {
        for (var h = start; ; h = (h + 1) % 24)
        {
            yield return h;
            if (h == end) yield break;
        }
    }

    /// <summary>"weekday evenings", "weekend afternoons", "overnight", from the band's centre and which days carry it.</summary>
    private static string Label(LearnedCongestionProfile learned, List<int> bandHours, int start, int end)
    {
        var centre = (start + Span(start, end) / 2) % 24;
        var timeOfDay = centre switch
        {
            >= 5 and <= 11 => "mornings",
            >= 12 and <= 16 => "afternoons",
            >= 17 and <= 22 => "evenings",
            _ => "overnight",
        };

        double weekday = 0, weekend = 0;
        foreach (var h in bandHours)
        {
            for (var d = 0; d < 5; d++) weekday += learned.DownloadMultipliers[LearnedCongestionProfile.SlotIndex(d, h)];
            for (var d = 5; d < 7; d++) weekend += learned.DownloadMultipliers[LearnedCongestionProfile.SlotIndex(d, h)];
        }
        weekday /= 5 * bandHours.Count;
        weekend /= 2 * bandHours.Count;

        var days = weekend < weekday - BandTolerance ? "weekend " : weekday < weekend - BandTolerance ? "weekday " : "";
        return timeOfDay == "overnight" ? $"{days}overnight".Trim() : $"{days}{timeOfDay}";
    }
}
