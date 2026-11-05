using NetworkOptimizer.Core.Helpers;
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
    /// <summary>"Wi-Fi: Back Yard 5 GHz (avg 9 health points)", or null when nothing impaired the range.</summary>
    public string? TopIssue { get; init; }
    /// <summary>"18% of client satisfaction under 80": the latest hour's measure behind a Wi-Fi <see cref="TopIssue"/>; null otherwise.</summary>
    public string? TopDetail { get; init; }
    /// <summary>The hour <see cref="TopDetail"/> was measured in.</summary>
    public DateTime? TopDetailAt { get; init; }
    /// <summary>The area of <see cref="TopIssue"/>, for a short label.</summary>
    public string? TopArea { get; init; }
    /// <summary>"Wi-Fi: Back Yard 5 GHz": the issue up to its first comma, for tiles.</summary>
    public string? TopShort { get; init; }
    /// <summary>Where to investigate <see cref="TopIssue"/>, as a site-relative URL; null when there is none.</summary>
    public string? TopHref => SiteHealthSeriesBuilder.IssueHref(TopArea);
    /// <summary>The page <see cref="TopHref"/> opens, for its tooltip.</summary>
    public string? TopHrefLabel => SiteHealthSeriesBuilder.IssueHrefLabel(TopArea);

    /// <summary>
    /// <see cref="TopIssue"/> with its latest-hour measure, for hovers: "Wi-Fi: Back Yard 5 GHz (avg 9 health
    /// points). Latest, Nov 4, 22:00: 18% of client satisfaction under 80". The two carry different scopes, so the
    /// measure names its hour.
    /// </summary>
    public string? Describe(TimeZoneInfo zone, DisplayFormat format) =>
        TopIssue == null ? null
        : TopDetail is { } d && TopDetailAt is { } at ? $"{TopIssue}. Latest, {TimeFormatHelper.FormatLocalDateTime(at, zone, format)}: {d}"
        : TopIssue;
}

/// <summary>A site on the Dashboard Sites panel and the Sites page.</summary>
public sealed class SiteHealthSummary
{
    public string Slug { get; init; } = "";
    public string Name { get; init; } = "";
    public bool Online { get; init; }
    public bool AgentOffline { get; init; }
    /// <summary>"Console up", "Agent online", "Agent offline", "Console unreachable".</summary>
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

        var (area, text, cost, at) = TopIssue(inRange);
        // Wi-Fi text is "radio, measure": the card names the radio, the hover gives the measure. Other areas'
        // text is one phrase ("WAN outage, 12 min (ISP)") and is not split.
        var comma = area == "Wi-Fi" && text != null ? text.IndexOf(',') : -1;
        var subject = comma > 0 ? text![..comma] : text;
        return new SiteHealthSeries
        {
            Points = points,
            Average = Round(Mean(inRange.Select(h => h.Health))),
            PreviousAverage = previous == null ? null : Round(Mean(previous.Select(h => h.Health))),
            Latest = inRange.LastOrDefault(h => h.Health != null)?.Health,
            TopIssue = text == null ? null : $"{area}: {subject} ({Points("avg", cost)})",
            TopDetail = comma > 0 ? text![(comma + 1)..].Trim() : null,
            TopDetailAt = comma > 0 ? at : null,
            TopArea = area,
            TopShort = text == null ? null : $"{area}: {text.Split(',')[0]}",
        };
    }

    /// <summary>
    /// The page that shows an issue area's detail: ISP Health for Internet (outages, loss, latency,
    /// bufferbloat), the Wi-Fi Optimizer for Wi-Fi, Device Stats for devices offline, Network Performance on the LAN
    /// scope for LAN loss and port errors (each Fabric target's loss, side by side). Null for an unknown area.
    /// </summary>
    internal static string? IssueHref(string? area) => area switch
    {
        "Internet" => "/monitoring?tab=isp-health",
        "Wi-Fi" => "/wifi-optimizer",
        "Availability" => "/monitoring?tab=devices",
        "LAN" => "/monitoring?tab=performance&wan=lan",
        _ => null,
    };

    internal static string? IssueHrefLabel(string? area) => area switch
    {
        "Internet" => "Monitoring - ISP Health",
        "Wi-Fi" => "Wi-Fi Optimizer",
        "Availability" => "Monitoring - Device Stats",
        "LAN" => "Monitoring - Network Performance",
        _ => null,
    };

    /// <summary>"costs 9 health points", "costs &lt; 1 health point": the health an issue took in one hour.</summary>
    internal static string Costs(double points) => Points("costs", points);

    /// <summary>"<paramref name="verb"/> 9 health points", or "&lt; 1 health point" under one.</summary>
    internal static string Points(string verb, double points) =>
        points < 1 ? $"{verb} < 1 health point" : $"{verb} {points:0} health point{(Math.Round(points) == 1 ? "" : "s")}";

    /// <summary>An area is an issue when it averages this many points over the range...</summary>
    private const double IssueAveragePoints = 0.5;

    /// <summary>...or costs this many in any one hour, so a short sharp event (a WAN outage) is not averaged away.</summary>
    private const double IssuePeakHourPoints = 2;

    /// <summary>
    /// Of the areas that qualify as an issue, the one that cost the most over the range: its latest
    /// description, the average points it cost, and the hour of that description.
    /// </summary>
    internal static (string? Area, string? Text, double Points, DateTime At) TopIssue(IReadOnlyList<SiteHealthHour> hours)
    {
        var byArea = new Dictionary<string, (double Sum, double Peak, string Text, DateTime At)>();
        foreach (var h in hours)
            foreach (var c in SiteHealthScorer.Contributors(h))
            {
                var cur = byArea.GetValueOrDefault(c.Area);
                byArea[c.Area] = (cur.Sum + c.Impairment, Math.Max(cur.Peak, c.Impairment),
                    h.HourStart >= cur.At ? c.Text : cur.Text, h.HourStart >= cur.At ? h.HourStart : cur.At);
            }
        if (byArea.Count == 0 || hours.Count == 0) return (null, null, 0, default);
        var top = byArea
            .Where(kv => kv.Value.Sum / hours.Count * 100 >= IssueAveragePoints || kv.Value.Peak * 100 >= IssuePeakHourPoints)
            .OrderByDescending(kv => kv.Value.Sum)
            .FirstOrDefault();
        return top.Key == null ? (null, null, 0, default) : (top.Key, top.Value.Text, top.Value.Sum / hours.Count * 100, top.Value.At);
    }

    /// <summary>"Health 86. Wi-Fi: Back Yard 5 GHz, 18% of client satisfaction under 80 (costs 9 health points)" for one hour.</summary>
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
