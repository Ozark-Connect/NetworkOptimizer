using NetworkOptimizer.Web.Services.Monitoring.IspHealth;

namespace NetworkOptimizer.Web.Services.Monitoring.PathLocalization;

/// <summary>
/// How Path Localization episodes read on ISP Health: their Path &amp; Congestion Events rows and the
/// findings they raise. Like <see cref="IspHealthPresentation"/>, wording only - nothing here scores.
///
/// The words are careful about one thing: a traceroute sees the forward path, so an episode says where
/// the degradation first appears, never which link broke, and "the return path or the far side" when
/// nothing forward explains it.
/// </summary>
public static class PathEpisodePresentation
{
    /// <summary>One event row per episode.</summary>
    public static IEnumerable<TimelineEntry> Entries(IEnumerable<PathEpisodeView> episodes) =>
        episodes.Select(e =>
        {
            var names = Names(e.Degraded);
            var end = e.ClosedAt ?? DateTime.UtcNow;
            var label = string.IsNullOrEmpty(e.Label) ? "no trace has completed yet" : e.Label;
            var lead = e.Tracing
                ? $"{names} degraded ({Magnitude(e)}), tracing from the vantage that measures {(e.Degraded.Count == 1 ? "it" : "them")}."
                : $"{IspHealthPresentation.FormatDuration(end - e.OpenedAt)} of loss or added latency on {names} while the rest of the connection stayed clean ({Magnitude(e)}).";
            var confirmed = e.Disposition == CongestionDisposition.Confirmed;
            return new TimelineEntry(e.OpenedAt,
                e.Tracing ? "Tracing now" : "Path trace",
                confirmed ? "isp-event-badge-congestion" : "isp-event-badge-congestion-soft",
                $"{lead} {Sentence(label)}",
                e.ClosedAt,
                EventCategory.Congestion,
                confirmed
                    ? "Traceroutes during the degradation place where it first appears."
                    : "Traceroutes during the degradation found no forward hop that explains it.",
                TargetIds: e.Degraded.Select(t => t.TargetId).Where(id => id.Length > 0).ToList(),
                EpisodeId: e.Id);
        });

    /// <summary>
    /// Findings: a Confirmed episode is worth telling the user about with its localization attached, and
    /// so is an Unverifiable one that keeps coming back - the same targets and the same reading on
    /// <see cref="IspHealthOptions.PathRepeatFindingDays"/> or more days, which is a pattern (a peak-hour
    /// return-path squeeze reads exactly like this).
    /// </summary>
    public static IEnumerable<IspHealthIssue> Findings(IReadOnlyList<PathEpisodeView> episodes, IspHealthOptions options)
    {
        var groups = episodes
            .Where(e => !e.Tracing && !string.IsNullOrEmpty(e.Label))
            .GroupBy(e => (Targets: string.Join("|", e.Degraded.Select(t => t.TargetId).OrderBy(x => x)), e.Label, e.Disposition));
        foreach (var g in groups.OrderByDescending(g => g.Max(e => e.OpenedAt)))
        {
            var list = g.OrderBy(e => e.OpenedAt).ToList();
            var days = list.Select(e => e.OpenedAt.ToLocalTime().Date).Distinct().Count();
            var latest = list[^1];
            var names = Names(latest.Degraded);
            var when = list.Count == 1
                ? IspHealthPresentation.EventTimeLabel(latest.OpenedAt)
                : $"{list.Count} times on {days} days, last {IspHealthPresentation.EventTimeLabel(latest.OpenedAt)}";
            if (g.Key.Disposition == CongestionDisposition.Confirmed)
            {
                yield return new IspHealthIssue
                {
                    Severity = IspIssueSeverity.Warning,
                    Title = $"Path degradation localized: {latest.Label}",
                    Description = $"{names} degraded while the rest of your connection stayed clean ({when}). {latest.Summary}",
                    Recommendation = "This is where the degradation first appears on the forward path, which is the hop table to hand your ISP.",
                    LinkUrl = "#isp-events",
                    LinkText = "See the trace"
                };
            }
            else if (days >= options.PathRepeatFindingDays)
            {
                yield return new IspHealthIssue
                {
                    Severity = IspIssueSeverity.Info,
                    Title = $"Recurring path degradation on {names}",
                    Description = $"{names} degraded{(latest.Degraded.Count > 1 ? " together" : "")} while the rest of your connection stayed clean, {when}. {latest.Label}.",
                    Recommendation = "A traceroute cannot see the return path, so this cannot be pinned on a forward hop. A pattern that repeats at the same hours is worth raising with your ISP with these times.",
                    LinkUrl = "#isp-events",
                    LinkText = "See the traces"
                };
            }
        }
    }

    /// <summary>"AWS us-east-1, AWS us-east-2 and Speedtest ORD".</summary>
    internal static string Names(IReadOnlyList<PathEpisodeTargetView> targets) => targets.Count switch
    {
        0 => "a monitored target",
        1 => targets[0].Name,
        _ => $"{string.Join(", ", targets.Take(targets.Count - 1).Select(t => t.Name))} and {targets[^1].Name}"
    };

    /// <summary>The worst degraded target's readings: "peak 13% loss, 42 to 55 ms".</summary>
    internal static string Magnitude(PathEpisodeView e)
    {
        var worst = e.Degraded.OrderByDescending(t => t.PeakLossPct ?? 0).ThenByDescending(t => (t.PeakRttMs ?? 0) - (t.BaselineRttMs ?? 0)).FirstOrDefault();
        if (worst == null) return "no readings yet";
        var parts = new List<string>();
        if (worst.PeakLossPct is double loss && loss >= 0.5) parts.Add($"peak {loss:0.#}% loss");
        if (worst.BaselineRttMs is double b && worst.PeakRttMs is double p && p > b) parts.Add($"{b:0} to {p:0} ms");
        return parts.Count == 0 ? "no readings yet" : string.Join(", ", parts);
    }

    private static string Sentence(string s) => s.EndsWith('.') ? s : $"{char.ToUpperInvariant(s[0])}{s[1..]}.";

    /// <summary>A hop's role in the verdict, for its row's tag.</summary>
    public static string? HopTagLabel(HopReading h) =>
        h.Has(HopTag.Onset) && !h.Has(HopTag.Destination) ? "First elevated"
        : h.Has(HopTag.LastClean) ? "Last clean"
        : h.Has(HopTag.IcmpLimited) ? "ICMP-limited"
        : null;

    /// <summary>The Network Tools link that fills Run a Probe with an episode trace's target, vantage, and bind.</summary>
    public static string RerunUrl(string address, string vantage, int? wanContextId)
    {
        var from = vantage.StartsWith("agent-", StringComparison.Ordinal) && int.TryParse(vantage[6..], out var id)
            ? wanContextId is int ctx ? $"agent:{id}:{ctx}" : $"agent:{id}"
            : "server";
        return $"/network-tools?from={Uri.EscapeDataString(from)}&target={Uri.EscapeDataString(address)}";
    }
}
