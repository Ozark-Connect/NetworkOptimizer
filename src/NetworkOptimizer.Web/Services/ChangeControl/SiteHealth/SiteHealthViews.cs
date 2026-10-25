using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services.ChangeControl.SiteHealth;

/// <summary>One bucket of a health line (an hour, or several on long ranges), with its hover text.</summary>
public sealed record HealthPoint(DateTime Start, DateTime End, double? Health, double Isp, double Local, double Wifi, double Availability, double Lan, string? Tip);

/// <summary>A site's health over a range: the line, its average, the period before, and the top issue.</summary>
public sealed class SiteHealthSeries
{
    public List<HealthPoint> Points { get; init; } = [];
    public double? Average { get; init; }
    public double? PreviousAverage { get; init; }
    /// <summary>The most recent hour's health.</summary>
    public double? Latest { get; init; }
    /// <summary>"Wi-Fi 9: Back Yard 5 GHz, 18% of samples under 80", or null when nothing impaired the range.</summary>
    public string? TopIssue { get; init; }
    /// <summary>The area of <see cref="TopIssue"/>, for a short label.</summary>
    public string? TopArea { get; init; }
}

/// <summary>A site on the Dashboard Sites panel and the Sites page.</summary>
public sealed class SiteHealthSummary
{
    public string Slug { get; init; } = "";
    public string Name { get; init; } = "";
    public bool Online { get; init; }
    public bool AgentOffline { get; init; }
    /// <summary>"Console connected", "Agent online", "Agent offline", "Console unreachable".</summary>
    public string Connection { get; init; } = "";
    public DateTime? UnreachableSince { get; init; }
    public SiteHealthSeries Health { get; init; } = new();
    public int Incidents24h { get; init; }
    public int Changes24h { get; init; }
    public int Regressions24h { get; init; }
    public int PendingApprovals { get; init; }
}

/// <summary>Turns stored hours into what pages draw.</summary>
public static class SiteHealthSeriesBuilder
{
    /// <summary>Most points a line gets; longer ranges average hours into buckets.</summary>
    private const int MaxPoints = 200;

    /// <summary>Healthy at or above this, degraded down to <see cref="DegradedFloor"/>, below that down.</summary>
    public const double HealthyFloor = 95;
    public const double DegradedFloor = 85;

    public static SiteHealthSeries Build(IReadOnlyList<SiteHealthHour> hours, DateTime from, DateTime to, IReadOnlyList<SiteHealthHour>? previous = null)
    {
        var inRange = hours.Where(h => h.HourStart >= from && h.HourStart < to).OrderBy(h => h.HourStart).ToList();
        var span = Math.Max(1, (int)Math.Ceiling((to - from).TotalHours / MaxPoints));
        var points = new List<HealthPoint>();
        foreach (var bucket in inRange.GroupBy(h => (int)((h.HourStart - from).TotalHours / span)))
        {
            var list = bucket.ToList();
            var start = from.AddHours(bucket.Key * span);
            var worst = list.Where(h => h.Health != null).OrderBy(h => h.Health).FirstOrDefault();
            points.Add(new HealthPoint(start, start.AddHours(span), Mean(list.Select(h => h.Health)),
                Mean(list.Select(h => (double?)Isp(h))) ?? 0, Mean(list.Select(h => (double?)Local(h))) ?? 0,
                Mean(list.Select(h => (double?)WifiWeighted(h))) ?? 0, Mean(list.Select(h => h.Availability)) ?? 0,
                Mean(list.Select(h => h.Lan)) ?? 0, worst == null ? null : Tip(worst)));
        }

        var (area, issue) = TopIssue(inRange);
        return new SiteHealthSeries
        {
            Points = points,
            Average = Round(Mean(inRange.Select(h => h.Health))),
            PreviousAverage = previous == null ? null : Round(Mean(previous.Select(h => h.Health))),
            Latest = inRange.LastOrDefault(h => h.Health != null)?.Health,
            TopIssue = issue,
            TopArea = area,
        };
    }

    /// <summary>The area that cost the most over the range, named with its latest description.</summary>
    internal static (string? Area, string? Text) TopIssue(IReadOnlyList<SiteHealthHour> hours)
    {
        var byArea = new Dictionary<string, (double Sum, string Text, DateTime At)>();
        foreach (var h in hours)
            foreach (var c in SiteHealthScorer.Contributors(h))
            {
                var cur = byArea.GetValueOrDefault(c.Area);
                byArea[c.Area] = (cur.Sum + c.Impairment, h.HourStart >= cur.At ? c.Text : cur.Text, h.HourStart >= cur.At ? h.HourStart : cur.At);
            }
        if (byArea.Count == 0 || hours.Count == 0) return (null, null);
        var top = byArea.OrderByDescending(kv => kv.Value.Sum).First();
        var points = top.Value.Sum / hours.Count * 100;
        return points < 0.5 ? (null, null) : (top.Key, $"{top.Key} {points:0}: {top.Value.Text}");
    }

    /// <summary>"Health 86 at 2 PM. Wi-Fi 9: Back Yard 5 GHz, ..." for one hour.</summary>
    public static string Tip(SiteHealthHour h)
    {
        var parts = SiteHealthScorer.Contributors(h).Select(c => $"{c.Area} {c.Impairment * 100:0}: {c.Text}");
        var head = h.Health is { } v ? $"Health {v:0.#}" : "No data";
        return string.Join(". ", new[] { head }.Concat(parts));
    }

    /// <summary>Health-point losses by area for the stacked lane.</summary>
    public static double Isp(SiteHealthHour h) => h.InternetIsp ?? 0;

    public static double Local(SiteHealthHour h) =>
        h.Internet is { } i ? Math.Max(0, 1 - (1 - i) / Math.Max(0.0001, 1 - (h.InternetIsp ?? 0))) : 0;

    public static double WifiWeighted(SiteHealthHour h) => (h.Wifi ?? 0) * (h.WirelessShare ?? 1);

    /// <summary>"healthy", "degraded", "down", or "unknown", for color.</summary>
    public static string Level(double? health) => health switch
    {
        null => "unknown",
        >= HealthyFloor => "healthy",
        >= DegradedFloor => "degraded",
        _ => "down",
    };

    private static double? Mean(IEnumerable<double?> values)
    {
        var list = values.OfType<double>().ToList();
        return list.Count > 0 ? list.Average() : null;
    }

    private static double? Round(double? v) => v is { } x ? Math.Round(x, 1) : null;
}
