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
    /// <summary>"Wi-Fi: Back Yard 5 GHz, 18% of samples under 80 (costs 9 points)", or null when nothing impaired the range.</summary>
    public string? TopIssue { get; init; }
    /// <summary>The area of <see cref="TopIssue"/>, for a short label.</summary>
    public string? TopArea { get; init; }
    /// <summary>"Wi-Fi: Back Yard 5 GHz": the issue up to its first comma, for tiles.</summary>
    public string? TopShort { get; init; }
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
    /// <summary>A rollout scheduled, running, paused, soaking, or reported in the last 24 hours; null when none.</summary>
    public Firmware.SiteRolloutStatus? Rollout { get; init; }
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
            // Clamped to the range end: the provisional current hour ends at now, not at the top of the next hour.
            var full = start.AddHours(span);
            var end = full > to ? to : full;
            points.Add(new HealthPoint(start, end, Mean(list.Select(h => h.Health)),
                Mean(list.Select(h => (double?)Isp(h))) ?? 0, Mean(list.Select(h => (double?)Local(h))) ?? 0,
                Mean(list.Select(h => (double?)WifiWeighted(h))) ?? 0, Mean(list.Select(h => h.Availability)) ?? 0,
                Mean(list.Select(h => h.Lan)) ?? 0, worst == null ? null : Tip(worst)));
        }

        var (area, text, cost) = TopIssue(inRange);
        return new SiteHealthSeries
        {
            Points = points,
            Average = Round(Mean(inRange.Select(h => h.Health))),
            PreviousAverage = previous == null ? null : Round(Mean(previous.Select(h => h.Health))),
            Latest = inRange.LastOrDefault(h => h.Health != null)?.Health,
            TopIssue = text == null ? null : $"{area}: {text} ({Costs(cost)})",
            TopArea = area,
            TopShort = text == null ? null : $"{area}: {text.Split(',')[0]}",
        };
    }

    /// <summary>"costs 9 points", "costs under 1 point": the health an issue took.</summary>
    internal static string Costs(double points) =>
        points < 1 ? "costs under 1 point" : $"costs {points:0} point{(Math.Round(points) == 1 ? "" : "s")}";

    /// <summary>An area is an issue when it averages this many points over the range...</summary>
    private const double IssueAveragePoints = 0.5;

    /// <summary>...or costs this many in any one hour, so a short sharp event (a WAN outage) is not averaged away.</summary>
    private const double IssuePeakHourPoints = 2;

    /// <summary>
    /// Of the areas that qualify as an issue, the one that cost the most over the range: its latest
    /// description and the average points it cost.
    /// </summary>
    internal static (string? Area, string? Text, double Points) TopIssue(IReadOnlyList<SiteHealthHour> hours)
    {
        var byArea = new Dictionary<string, (double Sum, double Peak, string Text, DateTime At)>();
        foreach (var h in hours)
            foreach (var c in SiteHealthScorer.Contributors(h))
            {
                var cur = byArea.GetValueOrDefault(c.Area);
                byArea[c.Area] = (cur.Sum + c.Impairment, Math.Max(cur.Peak, c.Impairment),
                    h.HourStart >= cur.At ? c.Text : cur.Text, h.HourStart >= cur.At ? h.HourStart : cur.At);
            }
        if (byArea.Count == 0 || hours.Count == 0) return (null, null, 0);
        var top = byArea
            .Where(kv => kv.Value.Sum / hours.Count * 100 >= IssueAveragePoints || kv.Value.Peak * 100 >= IssuePeakHourPoints)
            .OrderByDescending(kv => kv.Value.Sum)
            .FirstOrDefault();
        return top.Key == null ? (null, null, 0) : (top.Key, top.Value.Text, top.Value.Sum / hours.Count * 100);
    }

    /// <summary>"Health 86. Wi-Fi: Back Yard 5 GHz, 18% of samples under 80 (costs 9 points)" for one hour.</summary>
    public static string Tip(SiteHealthHour h)
    {
        var parts = SiteHealthScorer.Contributors(h).Select(c => $"{c.Area}: {c.Text} ({Costs(c.Impairment * 100)})");
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
