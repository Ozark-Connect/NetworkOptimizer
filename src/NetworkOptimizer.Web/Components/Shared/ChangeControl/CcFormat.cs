using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services.ChangeControl;

namespace NetworkOptimizer.Web.Components.Shared.ChangeControl;

/// <summary>Display helpers shared by the Change Control components.</summary>
public static class CcFormat
{
    public static DateTime Local(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), TimeZoneInfo.Local);

    /// <summary>"Oct 20, 4:42 PM".</summary>
    public static string Time(DateTime utc) => Local(utc).ToString("MMM d, h:mm tt");

    /// <summary>
    /// An option's text with room after it: Chrome ignores padding on a select's open list, which
    /// sizes to its longest option and would otherwise end that text at the list's edge.
    /// </summary>
    public static string Option(string text) => text + "   ";

    /// <summary>"Oct 20".</summary>
    public static string Day(DateTime utc) => Local(utc).ToString("MMM d");

    /// <summary>What "Unattributed" means, for its tooltip.</summary>
    public const string UnattributedTip = "UniFi Network's audit log names no admin for this change.";

    /// <summary>The actor dot class for a change.</summary>
    public static string Dot(ConfigChangeActorKind kind) => kind switch
    {
        ConfigChangeActorKind.Person => "person",
        ConfigChangeActorKind.NetworkOptimizer => "no",
        ConfigChangeActorKind.Firmware => "firmware",
        _ => "unattributed",
    };

    /// <summary>The marker class on the timeline: firmware steps and app upgrades draw as diamonds.</summary>
    public static string Marker(ChangeRowView c) => c.Kind != ConfigChangeKind.Config ? "firmware" : Dot(c.ActorKind);

    public static string ActionClass(ConfigChangeAction a) => a.ToString().ToLowerInvariant();

    public static string ActionText(ConfigChangeAction a, bool pastTense) => (a, pastTense) switch
    {
        (ConfigChangeAction.Created, true) => "Created",
        (ConfigChangeAction.Deleted, true) => "Deleted",
        (ConfigChangeAction.Updated, true) => "Updated",
        (ConfigChangeAction.Created, false) => "Create",
        (ConfigChangeAction.Deleted, false) => "Delete",
        _ => "Update",
    };

    public static string PlanActionClass(PlanAction a) => a switch
    {
        PlanAction.Create => "created",
        PlanAction.Delete => "deleted",
        _ => "updated",
    };

    public static string PlanStatusBadge(ConfigPlanStatus s) => s switch
    {
        ConfigPlanStatus.PendingApproval => "status-info",
        ConfigPlanStatus.Watching => "status-warning",
        ConfigPlanStatus.Applied => "status-connected",
        ConfigPlanStatus.Failed or ConfigPlanStatus.Reverted => "status-error",
        _ => "badge-muted",
    };

    public static string PlanStatusText(ConfigPlanStatus s) => s switch
    {
        ConfigPlanStatus.PendingApproval => "Pending approval",
        ConfigPlanStatus.Watching => "Watching",
        _ => s.ToString(),
    };

    public static string TierBadge(int tier) => tier switch
    {
        PlanTiers.Lockout => "status-error",
        PlanTiers.SecurityRegression => "status-error",
        PlanTiers.Acknowledge => "status-warning",
        _ => "badge-muted",
    };

    public static string Plural(int n, string one, string many) => n == 1 ? $"{n} {one}" : $"{n} {many}";

    public static string Bytes(long b) => b switch
    {
        >= 1L << 30 => $"{b / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{b / (double)(1L << 20):0} MB",
        >= 1L << 10 => $"{b / (double)(1L << 10):0} KB",
        _ => $"{b} B",
    };

    /// <summary>Position (0-100) of a time on a range, for the timeline.</summary>
    public static double Pct(DateTime t, DateTime from, DateTime to) =>
        Math.Clamp((t - from).TotalSeconds / Math.Max(1, (to - from).TotalSeconds) * 100.0, 0, 100);

    /// <summary>An SVG polyline "points" string for a series on a 0..100 by 0..100 box; gaps break the line.</summary>
    public static List<string> Polylines(IReadOnlyList<(DateTime Time, double? Value)> series, DateTime from, DateTime to, double max)
    {
        var lines = new List<string>();
        var current = new List<string>();
        var scale = max <= 0 ? 1 : max;
        foreach (var (time, value) in series)
        {
            if (value == null)
            {
                if (current.Count > 1) lines.Add(string.Join(" ", current));
                current = [];
                continue;
            }
            var x = Pct(time, from, to);
            var y = 96 - Math.Clamp(value.Value / scale, 0, 1) * 88;
            current.Add($"{x.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)},{y.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}");
        }
        if (current.Count > 1) lines.Add(string.Join(" ", current));
        return lines;
    }

    /// <summary>An SVG polygon "points" string filling between a layer's lower and upper edges, on the same box as <see cref="Polylines"/>.</summary>
    public static string Area(IReadOnlyList<(DateTime Time, double Lower, double Upper)> points, DateTime from, DateTime to, double max)
    {
        var scale = max <= 0 ? 1 : max;
        string P(DateTime t, double v) =>
            $"{Pct(t, from, to).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)},{(96 - Math.Clamp(v / scale, 0, 1) * 88).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}";
        return string.Join(" ", points.Select(p => P(p.Time, p.Upper)).Concat(points.Reverse().Select(p => P(p.Time, p.Lower))));
    }

    /// <summary>
    /// One SVG polygon per drawn run of <paramref name="series"/>: the line itself, closed down to the lane's
    /// baseline, for shading under it. Same box and same runs as <see cref="Polylines"/>.
    /// </summary>
    public static List<string> AreasUnder(IReadOnlyList<(DateTime Time, double? Value)> series, DateTime from, DateTime to, double max)
    {
        const string Baseline = "96";
        var areas = new List<string>();
        foreach (var line in Polylines(series, from, to, max))
        {
            var points = line.Split(' ');
            var firstX = points[0].Split(',')[0];
            var lastX = points[^1].Split(',')[0];
            areas.Add($"{line} {lastX},{Baseline} {firstX},{Baseline}");
        }
        return areas;
    }

    /// <summary>
    /// <paramref name="b"/> with the <paramref name="a"/> point either side of each of its runs added, for two
    /// series that split one measurement (idle vs loaded): the B line then meets the A line instead of floating
    /// apart, and a one-point B run still draws.
    /// </summary>
    public static List<(DateTime Time, double? Value)> Bridged(
        IReadOnlyList<(DateTime Time, double? Value)> a, IReadOnlyList<(DateTime Time, double? Value)> b)
    {
        var aByTime = new Dictionary<DateTime, double?>();
        foreach (var p in a) aByTime[p.Time] = p.Value;
        var result = b.ToList();
        for (var i = 0; i < b.Count; i++)
        {
            if (b[i].Value != null) continue;
            var nextStartsRun = i + 1 < b.Count && b[i + 1].Value != null;
            var prevEndsRun = i > 0 && b[i - 1].Value != null;
            if ((nextStartsRun || prevEndsRun) && aByTime.TryGetValue(b[i].Time, out var av) && av != null)
                result[i] = (b[i].Time, av);
        }
        return result;
    }

    /// <summary>The max of two series, for a shared axis.</summary>
    public static double Max(IEnumerable<(DateTime, double?)> a, IEnumerable<(DateTime, double?)>? b = null)
    {
        var values = a.Concat(b ?? []).Where(p => p.Item2 != null).Select(p => p.Item2!.Value).ToList();
        return values.Count == 0 ? 1 : Math.Max(values.Max() * 1.1, 0.5);
    }
}
