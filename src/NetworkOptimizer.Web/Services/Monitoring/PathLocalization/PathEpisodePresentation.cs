using System.Text.RegularExpressions;
using NetworkOptimizer.Web.Services.Monitoring.IspHealth;

namespace NetworkOptimizer.Web.Services.Monitoring.PathLocalization;

/// <summary>A hop's Loss cell: the figure shown, and the highest raw one with its source when it differs.</summary>
public sealed record PathHopLoss(double Shown, double? Higher, string HigherSource, string? Tooltip);

/// <summary>The ISP Health banner for one episode.</summary>
/// <param name="EpisodeId">The episode it is about.</param>
/// <param name="Phase">"tracing" or "done": a dismissal covers one phase, so the verdict still shows after the tracing banner was dismissed.</param>
/// <param name="CssClass">The alert class.</param>
/// <param name="Lead">The bold opening.</param>
/// <param name="Text">The rest of the sentence.</param>
/// <param name="More">Other episodes that would have qualified.</param>
public sealed record PathEpisodeBanner(int EpisodeId, string Phase, string CssClass, string Lead, string Text, int More)
{
    /// <summary>The per-user dismissal key. Keyed by episode, so a new episode shows again; the slug
    /// because episode ids repeat across site databases.</summary>
    public string DismissKey(string siteSlug) => $"PathEpisodeBannerDismissed:{siteSlug}:{EpisodeId}:{Phase}";
}

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
    /// <summary>Each scored episode's points off the overall score, by episode id, from the report.</summary>
    public static IReadOnlyDictionary<int, int> PointsById(IspHealthReport? report) =>
        report?.PathCongestion.ToDictionary(p => p.EpisodeId, p => p.ScorePenaltyPoints) ?? new Dictionary<int, int>();

    /// <summary>" It lowered your ISP Health score by N points." or empty, as outage rows say it.</summary>
    internal static string ScoreSentence(int points, bool several = false) => points > 0
        ? $" {(several ? "Together they" : "It")} lowered your ISP Health score by {points} {(points == 1 ? "point" : "points")}."
        : "";

    /// <summary>One event row per episode.</summary>
    /// <param name="pointsById">What each scored episode took off the score; see <see cref="PointsById"/>.</param>
    public static IEnumerable<TimelineEntry> Entries(IEnumerable<PathEpisodeView> episodes, IReadOnlyDictionary<int, int>? pointsById = null) =>
        episodes.Select(e =>
        {
            var names = Names(e.Degraded);
            var end = e.ClosedAt ?? DateTime.UtcNow;
            var label = string.IsNullOrEmpty(e.Label) ? "No trace has completed yet." : e.Label;
            var one = e.Degraded.Count == 1;
            var lead = e.ClosedAt == null
                ? $"{names} {(one ? "is" : "are")} {Symptom(e)}, for {IspHealthPresentation.FormatDuration(end - e.OpenedAt)} so far."
                : $"{names} {PastSymptom(e, IspHealthPresentation.FormatDuration(end - e.OpenedAt))} while the rest of your connection stayed clean.";
            if (!one) label = WithoutNames(label);
            var confirmed = e.Disposition == CongestionDisposition.Confirmed;
            return new TimelineEntry(e.OpenedAt,
                BadgeLabel(e),
                confirmed ? "isp-event-badge-congestion" : "isp-event-badge-congestion-soft",
                $"{lead} {Sentence(label)}",
                e.ClosedAt,
                EventCategory.Congestion,
                (confirmed
                    ? "Traceroutes during the degradation place where it first appears."
                    : "Traceroutes during the degradation found no forward hop that explains it.")
                + ScoreSentence(pointsById?.GetValueOrDefault(e.Id) ?? 0),
                TargetIds: e.Degraded.Select(t => t.TargetId).Where(id => id.Length > 0).ToList(),
                EpisodeId: e.Id);
        });

    /// <summary>An episode's badge: tracing while rounds run, Ongoing while the problem outlasts them, then Congestion.</summary>
    public static string BadgeLabel(PathEpisodeView e) => e.Tracing ? "Tracing now" : e.Ongoing ? "Ongoing" : "Congestion";

    /// <summary>
    /// Findings: a Confirmed episode is worth telling the user about with its localization attached, and
    /// so is an Unverifiable one that keeps coming back - the same targets and the same reading on
    /// <see cref="IspHealthOptions.PathRepeatFindingDays"/> or more days, which is a pattern (a peak-hour
    /// return-path squeeze reads exactly like this).
    /// </summary>
    public static IEnumerable<IspHealthIssue> Findings(IReadOnlyList<PathEpisodeView> episodes, IspHealthOptions options,
        IReadOnlyDictionary<int, int>? pointsById = null)
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
            var cost = ScoreSentence(list.Sum(e => pointsById?.GetValueOrDefault(e.Id) ?? 0), list.Count > 1);
            var when = list.Count == 1
                ? IspHealthPresentation.EventTimeLabel(latest.OpenedAt)
                : $"{list.Count} times on {days} days, last {IspHealthPresentation.EventTimeLabel(latest.OpenedAt)}";
            if (g.Key.Disposition == CongestionDisposition.Confirmed)
            {
                yield return new IspHealthIssue
                {
                    Severity = IspIssueSeverity.Warning,
                    Title = $"Path degradation localized on {names}",
                    Description = $"{names} degraded while the rest of your connection stayed clean ({when}). {latest.Summary}{cost}",
                    Recommendation = "This is where the degradation first appears on the forward path, which is the hop table to hand your ISP.",
                    LinkUrl = "#isp-events",
                    LinkText = "See the trace",
                    EpisodeId = latest.Id
                };
            }
            else if (days >= options.PathRepeatFindingDays)
            {
                yield return new IspHealthIssue
                {
                    Severity = IspIssueSeverity.Info,
                    Title = $"Recurring path degradation on {names}",
                    Description = $"{names} degraded{(latest.Degraded.Count > 1 ? " together" : "")} while the rest of your connection stayed clean, {when}. {Sentence(latest.Label!)}{cost}",
                    Recommendation = "A traceroute cannot see the return path, so this cannot be pinned on a forward hop. A pattern that repeats at the same hours is worth raising with your ISP with these times.",
                    LinkUrl = "#isp-events",
                    LinkText = "See the traces",
                    EpisodeId = latest.Id
                };
            }
        }
    }

    /// <summary>How long after an episode closes its banner stays up.</summary>
    public static readonly TimeSpan BannerHold = TimeSpan.FromHours(24);

    /// <summary>
    /// The banner at the top of ISP Health: the newest open episode, else the newest one that closed
    /// within <see cref="BannerHold"/>. Null when there is neither.
    /// </summary>
    /// <param name="episodes">Episodes overlapping the last <see cref="BannerHold"/>, any order.</param>
    /// <param name="options">For the round count when the episode is not running here.</param>
    public static PathEpisodeBanner? Banner(IReadOnlyList<PathEpisodeView> episodes, DateTime nowUtc, IspHealthOptions? options = null)
    {
        var shown = episodes
            .Where(e => e.ClosedAt == null || nowUtc - e.ClosedAt.Value <= BannerHold)
            .OrderByDescending(e => e.ClosedAt == null).ThenByDescending(e => e.OpenedAt)
            .ToList();
        if (shown.Count == 0) return null;

        var e = shown[0];
        var names = Names(e.Degraded);
        var label = string.IsNullOrEmpty(e.Label) ? null : Sentence(e.Label);
        var verb = e.Degraded.Count == 1 ? "is" : "are";
        if (e.Tracing)
        {
            return new PathEpisodeBanner(e.Id, "tracing", "alert-info", "Tracing now",
                $"{names} {verb} {Symptom(e)} while the rest of your connection is clean. Network Optimizer is tracing the path every minute to find where it starts.",
                shown.Count - 1);
        }
        if (e.Ongoing)
        {
            var it = e.Degraded.Count == 1 ? "it" : "them";
            var next = e.Metered
                ? $"This WAN is metered, so Network Optimizer traced {it} once."
                : $"Network Optimizer traced {it} {Times(e.RoundsTraced ?? (options ?? new IspHealthOptions()).PathMaxRoundsPerTarget)}, and traces {it} once more after {(it == "it" ? "it recovers" : "they recover")}, to compare.";
            return new PathEpisodeBanner(e.Id, "ongoing", "alert-info", StillLead(e),
                $"{names} {verb} still {Symptom(e)}. {next}", shown.Count - 1);
        }

        var confirmed = e.Disposition == CongestionDisposition.Confirmed;
        var text = $"{names}, {Span(e.OpenedAt, e.ClosedAt!.Value)}.{(label == null ? "" : $" {label}")}";
        return new PathEpisodeBanner(e.Id, "done", confirmed ? "alert-warning" : "alert-info",
            confirmed ? "Path degradation localized" : "Path trace finished", text, shown.Count - 1);
    }

    /// <summary>"once", "10 times".</summary>
    private static string Times(int n) => n == 1 ? "once" : $"{n} times";

    /// <summary>The Ongoing banner's lead, by what the worst target is doing.</summary>
    private static string StillLead(PathEpisodeView e)
    {
        var symptom = Symptom(e);
        return symptom.Contains("slower") ? "Still slow" : symptom.StartsWith("losing") ? "Still losing packets" : "Still degraded";
    }

    /// <summary>"losing packets (peak 27%)", "running 42 ms slower", or both, from the worst degraded target.</summary>
    internal static string Symptom(PathEpisodeView e)
    {
        var worst = e.Degraded.OrderByDescending(t => t.PeakLossPct ?? 0).ThenByDescending(t => (t.PeakRttMs ?? 0) - (t.BaselineRttMs ?? 0)).FirstOrDefault();
        var parts = new List<string>();
        if (worst?.PeakLossPct is double loss && loss >= 0.5) parts.Add($"losing packets (peak {Percent(loss)})");
        // A few ms either way is ordinary window-to-window noise, not a symptom worth naming.
        if (worst?.BaselineRttMs is double b && worst.PeakRttMs is double p && p - b >= 5) parts.Add($"running {p - b:0} ms slower");
        return parts.Count == 0 ? "degraded" : string.Join(" and ", parts);
    }

    /// <summary>"lost packets for 18 min (peak 46%)", "ran 42 ms slower for 18 min", or both, from the worst degraded target.</summary>
    internal static string PastSymptom(PathEpisodeView e, string duration)
    {
        var worst = e.Degraded.OrderByDescending(t => t.PeakLossPct ?? 0).ThenByDescending(t => (t.PeakRttMs ?? 0) - (t.BaselineRttMs ?? 0)).FirstOrDefault();
        var lost = worst?.PeakLossPct is double loss && loss >= 0.5 ? loss : (double?)null;
        var slower = worst?.BaselineRttMs is double b && worst.PeakRttMs is double p && p - b >= 5 ? p - b : (double?)null;
        return (lost, slower) switch
        {
            (double l, double s) => $"lost packets and ran {s:0} ms slower for {duration} (peak {Percent(l)} loss)",
            (double l, null) => $"lost packets for {duration} (peak {Percent(l)})",
            (null, double s) => $"ran {s:0} ms slower for {duration}",
            _ => $"degraded for {duration}"
        };
    }

    /// <summary>"46%", or "4.5%" under ten, where the decimal still says something.</summary>
    private static string Percent(double pct) => pct >= 10 ? $"{pct:0}%" : $"{pct:0.#}%";

    /// <summary>
    /// What several degraded targets did, for "… lost packets together": "lost packets", "slowed
    /// down", or both, by the same thresholds as <see cref="Symptom"/>.
    /// </summary>
    internal static string Together(IEnumerable<(double? PeakLossPct, double? BaselineRttMs, double? PeakRttMs)> targets)
    {
        var list = targets.ToList();
        var lost = list.Any(t => t.PeakLossPct >= 0.5);
        var slow = list.Any(t => t.BaselineRttMs is double b && t.PeakRttMs is double p && p - b >= 5);
        return (lost, slow) switch
        {
            (true, true) => "lost packets and slowed down",
            (true, false) => "lost packets",
            (false, true) => "slowed down",
            _ => "degraded"
        };
    }

    /// <summary>
    /// The RTT column's baseline: a monitored hop's own series where it has one, else the trace's
    /// recovery or control minimum. Shared by the page and the PDF.
    /// </summary>
    public static double? DisplayBaseline(HopReading hop) => hop.SeriesBaselineRttMs ?? hop.BaselineMinRttMs;

    /// <summary>
    /// What a hop's traces say, in the reader's terms. Against a destination that was slow, a hop that
    /// slowed in the same traces is on the way to the problem and one that stayed fast is not.
    /// </summary>
    /// <param name="hops">The target's whole hop table, for its destination.</param>
    /// <param name="targetName">The degraded target, as the reader knows it.</param>
    public static string RoundsReading(HopReading hop, IReadOnlyList<HopReading> hops, string targetName)
    {
        var traced = hop.RoundMinRttMs.Count(v => v.HasValue);
        var destSlow = hops.FirstOrDefault(h => h.Has(HopTag.Destination))?.SlowRounds ?? 0;
        if (!hop.Has(HopTag.Destination) && destSlow > 0 && hop.PairedRounds is int paired)
        {
            if (paired == 0)
                return destSlow == 1 ? $"Fast in the trace where {targetName} was slow" : $"Fast in all {destSlow} traces where {targetName} was slow";
            return destSlow == 1 ? $"Slow in the trace where {targetName} was" : $"Slow in {paired} of the {destSlow} traces where {targetName} was";
        }
        return hop.SlowRounds == 0
            ? $"Not slower in any of {traced} {(traced == 1 ? "trace" : "traces")}"
            : $"Slower in {hop.SlowRounds} of {traced} {(traced == 1 ? "trace" : "traces")}";
    }

    /// <summary>
    /// A hop's Loss cell, lowest first: the trace's loss capped at what the hops beyond it lost, the
    /// hop's own pings, or a monitored hop's own series. The highest raw figure follows, named by its
    /// source, when it differs. Null for a hop that never answered. Shared by the page and the PDF.
    /// </summary>
    public static PathHopLoss? HopLoss(HopReading hop, PathEpisodeDetail detail)
    {
        if (hop.Sent == 0 || hop.Responses == 0) return null;
        var ping = hop.Address != null ? detail.HopPings?.GetValueOrDefault(hop.Address) : null;
        // A router that answered no ping at all ignores ping; its 100% is not loss, as the analysis reads it.
        var pingLoss = ping is { Sent: > 0, Received: > 0 } ? ping.LossPct : (double?)null;
        var seriesLoss = hop.Address != null && detail.MonitoredLoss?.TryGetValue(hop.Address, out var s) == true ? s : (double?)null;

        var figures = new List<(string Source, double Raw, double Forwarding)> { ("trace", hop.LossPct, hop.EffectiveLossPct) };
        if (pingLoss is double pf) figures.Add(("ping", pf, pf));
        if (seriesLoss is double sf) figures.Add(("monitored", sf, sf));
        var shown = figures.Min(f => f.Forwarding);
        var highest = figures.OrderByDescending(f => f.Raw).First();

        var notes = new List<string>();
        if (pingLoss is double pl)
            notes.Add($"Pinged during the episode: {ping!.Received} of {ping.Sent} answered ({pl:0.#}% lost).");
        if (seriesLoss is double sl)
            notes.Add($"Its own monitored ping series lost {sl:0.#}% over the episode, which is what decided it.");
        if (hop.EffectiveLossPct < hop.LossPct)
            notes.Add($"The trace lost {hop.LossPct:0.#}% here but only {hop.DownstreamFloorPct:0.#}% further along, so the rest is this router limiting its replies, not forwarding loss.");
        return new PathHopLoss(shown, highest.Raw - shown >= 0.5 ? highest.Raw : null, highest.Source,
            notes.Count > 0 ? string.Join(" ", notes) : null);
    }

    /// <summary>"0% (trace 85%)": a hop's Loss cell as text, for the PDF.</summary>
    public static string HopLossText(PathHopLoss? loss) => loss == null ? "-"
        : loss.Higher is double h ? $"{loss.Shown:0.#}% ({loss.HigherSource} {h:0.#}%)" : $"{loss.Shown:0.#}%";

    /// <summary>"Today, 14:38 to 14:54", or both ends in full when they fall on different days.</summary>
    private static string Span(DateTime fromUtc, DateTime toUtc) =>
        fromUtc.ToLocalTime().Date == toUtc.ToLocalTime().Date
            ? $"{IspHealthPresentation.EventTimeLabel(fromUtc)} to {toUtc.ToLocalTime():HH:mm}"
            : $"{IspHealthPresentation.EventTimeLabel(fromUtc)} to {IspHealthPresentation.EventTimeLabel(toUtc)}";

    /// <summary>"AWS us-east-1, AWS us-east-2 and Speedtest ORD".</summary>
    internal static string Names(IReadOnlyList<PathEpisodeTargetView> targets) => targets.Count switch
    {
        0 => "a monitored target",
        1 => targets[0].Name,
        _ => $"{string.Join(", ", targets.Take(targets.Count - 1).Select(t => t.Name))} and {targets[^1].Name}"
    };

    private static readonly Regex CorridorVerdict = new(
        @"^.+ (?:lost packets and slowed down|lost packets|slowed down|degraded) together, but their routes share nothing (?<where>.+?)\. The common factor is (?<rest>.+)$",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex SharedVerdict = new(@"^.+? all (?<verb>go through|cross|sit inside) ", RegexOptions.Compiled);

    /// <summary>
    /// A several-target verdict without the names and symptom the event row has already given:
    /// "Their routes share nothing …, so the common factor is …", "They all cross …". Works on the
    /// stored label, so episodes recorded before the row changed read the same. Others unchanged.
    /// </summary>
    internal static string WithoutNames(string label)
    {
        var corridor = CorridorVerdict.Match(label);
        if (corridor.Success)
            return $"Their routes share nothing {corridor.Groups["where"].Value}, so the common factor is {corridor.Groups["rest"].Value}";
        var shared = SharedVerdict.Match(label);
        return shared.Success ? $"They all {shared.Groups["verb"].Value} {label[shared.Length..]}" : label;
    }

    private static string Sentence(string s) => s.EndsWith('.') ? s : $"{char.ToUpperInvariant(s[0])}{s[1..]}.";

    /// <summary>A hop's role in the verdict, for its row's tag.</summary>
    public static string? HopTagLabel(HopReading h) =>
        h.Has(HopTag.Onset) && !h.Has(HopTag.Destination) ? "First elevated"
        : h.Has(HopTag.LastClean) ? "Last clean"
        : h.Has(HopTag.IcmpLimited) ? "ICMP-limited"
        : null;

    /// <summary>How much of the series either side of an episode the target link frames, so its onset and recovery show.</summary>
    internal static readonly TimeSpan TargetLinkPad = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Network Performance on one episode target: its chart category with only that target drawn,
    /// framed on the episode plus <see cref="TargetLinkPad"/> either side. Custom and Fabric targets
    /// are not reached over one WAN, so their link asks for every WAN as the Live View links do.
    /// </summary>
    public static string TargetUrl(PathEpisodeView e, PathEpisodeTargetView t, string? wanKey, DateTime nowUtc)
    {
        var start = PathEpisodeTraceMapping.Utc(e.OpenedAt) - TargetLinkPad;
        var end = (e.ClosedAt is DateTime c ? PathEpisodeTraceMapping.Utc(c) : nowUtc) + TargetLinkPad;
        if (end > nowUtc) end = nowUtc;
        var startMs = new DateTimeOffset(start).ToUnixTimeMilliseconds();
        var spanMs = Math.Max(1, new DateTimeOffset(end).ToUnixTimeMilliseconds() - startMs);
        var wan = t.Category is MonitoringLinks.CustomCategory or MonitoringLinks.FabricCategory ? LiveWanScope.AllWansToken : wanKey;
        var url = MonitoringLinks.Analysis(t.Category, startMs + spanMs / 2, spanMs, wan);
        return string.IsNullOrEmpty(t.TargetId) ? url : $"{url}&target={Uri.EscapeDataString(t.TargetId)}";
    }

    /// <summary>The Network Tools link that fills Run a Probe with an episode trace's target, vantage, and bind.</summary>
    public static string RerunUrl(string address, string vantage, int? wanContextId)
    {
        var from = PathVantage.FromLabel(vantage, null).AgentId is int id
            ? wanContextId is int ctx ? $"agent:{id}:{ctx}" : $"agent:{id}"
            : "server";
        return $"/network-tools?from={Uri.EscapeDataString(from)}&target={Uri.EscapeDataString(address)}";
    }
}
