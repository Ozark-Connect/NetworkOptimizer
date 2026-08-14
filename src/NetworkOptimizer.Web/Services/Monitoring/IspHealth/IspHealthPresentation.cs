using NetworkOptimizer.Core.Helpers;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services.Monitoring.IspHealth;

/// <summary>Which kind of event a timeline entry describes, for the tab's filter chips.</summary>
public enum EventCategory
{
    Congestion,
    Shift,
    Change
}

/// <summary>
/// One rendered line of the Path &amp; Congestion Events feed: when it happened, the badge
/// it carries, and the sentence describing it. <see cref="BadgeClass"/> is the tab's CSS
/// class and is ignored by renderers that have no stylesheet (the PDF).
///
/// <see cref="Members"/> holds the per-hop readings behind a grouped congestion line, and is
/// empty for every other entry. They are the detail an ISP acts on, so a renderer may fold
/// them away but must not drop them.
/// </summary>
public record TimelineEntry(DateTime Time, string Badge, string BadgeClass, string Text, DateTime? End = null,
    EventCategory Category = EventCategory.Congestion, string? BadgeTooltip = null,
    IReadOnlyList<string>? Members = null);

/// <summary>
/// How an <see cref="IspHealthReport"/> reads: the wording, formatting, and event
/// descriptions the ISP Health tab shows. Kept out of the panel so the PDF export renders
/// the identical text from the identical computation - two renderers, one report, one set
/// of words. Nothing here scores anything; it only describes what the scorer produced.
/// </summary>
public static class IspHealthPresentation
{
    /// <summary>Below this share of the plan the line was effectively idle, and saying so about an
    /// elevation only invites the reader to blame their own traffic for it.</summary>
    private const double LoadMentionFloor = 0.10;

    /// <summary>
    /// Builds the Path &amp; Congestion Events feed for a report: congestion events with
    /// their disposition-specific wording, then path shifts and route changes, in time order.
    /// </summary>
    public static IEnumerable<TimelineEntry> EventTimeline(IspHealthReport r)
    {
        var entries = new List<TimelineEntry>();
        foreach (var group in GroupCongestion(r.CongestionEvents))
        {
            // The nearest hop leads: it is the one closest to a cause the reader can act on, and
            // the rest of the group sits further along the same elevation.
            var evt = Head(group, r);
            var where = evt.IsShared
                ? $"{evt.AsnNames.Count} networks at once (could not be localized)"
                : (string.IsNullOrEmpty(evt.BottleneckLabel) ? string.Join(", ", evt.AsnNames) : evt.BottleneckLabel);
            var start = group.Min(e => e.Start);
            var end = group.Max(e => e.End);
            var span = FormatDuration(end - start);
            // Show both signals - congestion is detected on latency AND jitter together (or a
            // jitter-driven p90 burst), so reporting only RTT reads as latency-only.
            //
            // The named hop's own readings, never a range spanning the group: an envelope of one
            // hop's baseline and another's peak describes a rise that nothing measured. The members
            // below carry each hop's own.
            var mag = Magnitude(evt);
            // What the line was actually carrying, not just whether it cleared the heavy bar. Load
            // this side of the bar still shapes what a reader makes of an elevation, and it is only
            // ever narration: the score keys on LoadCoincident, which this never touches.
            var load = evt.MedianLoadUtilization is double u && u >= LoadMentionFloor
                ? $" under {u * 100:0}% WAN load" : "";
            // One shape for every line: "{duration} of elevated latency and jitter on {hop}{load}
            // ({mag}). {one plain sentence}." The badge reads "Congestion" except for the line-wide
            // self-inflicted case, which reads "Loaded Latency"; the disposition otherwise shows
            // through colour (confirmed prominent, the rest muted) and that closing sentence, so the
            // feed stays readable and still agrees with the cards (which badge only confirmed).
            // A localized, propagated bottleneck names the hop AND the hops beyond it in the subject,
            // which replaces the old trailing "it propagates ..." sentence. Sibling-confirmed,
            // clean-parallel, and unlocalized events don't get it. Require a placed bottleneck hop
            // (BottleneckHopIp) so an un-traced target that only surfaced as an unlocalized Confirmed
            // event - e.g. an access-ISP leaf with no saved trace - is never described as a hop with
            // topology beyond it; only genuinely localized hops and shared-incident owners carry a hop IP.
            // A group says how many hops carried it and lists them below, which is the stronger
            // statement and makes no claim about which sit beyond which.
            var beyond = group.Count > 1
                ? $" and {group.Count - 1} more {(group.Count == 2 ? "hop" : "hops")}"
                : evt.Disposition == CongestionDisposition.Confirmed
                    && !evt.ConfirmedBySibling
                    && !(evt.LoadCoincident && evt.CleanParallelPaths > 0)
                    && !evt.IsShared
                    && evt.BottleneckHopIp != null
                    ? " and the hops beyond" : "";
            var lead = $"{span} of elevated latency and jitter on {where}{beyond}{load} ({mag}).";
            // A group holding any Confirmed event is written as Confirmed - the absorbed
            // Unverifiable members were cross-checked by it (see GroupCongestion).
            var disposition = group.Any(e => e.Disposition == CongestionDisposition.Confirmed)
                ? CongestionDisposition.Confirmed
                : evt.Disposition;
            var (badge, badgeClass, text, tip) = disposition switch
            {
                CongestionDisposition.SelfInflicted => ("Loaded Latency", "isp-event-badge-congestion-soft",
                    $"{lead} Everything slowed together under load, so the limit was your access link, not one hop - bufferbloat or a congested shared-access network.",
                    (string?)"Latency rose under your own WAN load - likely your access link, not ISP congestion."),
                CongestionDisposition.ControlPlaneNoise => ("Congestion", "isp-event-badge-congestion-soft",
                    $"{lead} Its next hop stayed clean, so this is the hop's own ICMP handling, not a forwarding bottleneck.",
                    "This hop deprioritized ping replies while forwarding stayed clean - not confirmed as real congestion."),
                CongestionDisposition.Unverifiable => ("Congestion", "isp-event-badge-congestion-soft",
                    $"{lead}",
                    "Seen on one path and couldn't be cross-checked, so it's unconfirmed."),
                _ when evt.ConfirmedBySibling => ("Congestion", "isp-event-badge-congestion",
                    $"{lead}", null),
                _ when evt.LoadCoincident && evt.CleanParallelPaths > 0 => ("Congestion", "isp-event-badge-congestion",
                    $"{lead} {evt.CleanParallelPaths} other monitored paths stayed clean under the same load, so it was this hop's own capacity, not your access link.", null),
                _ => ("Congestion", "isp-event-badge-congestion",
                    $"{lead}", null)
            };
            var members = group.Count == 1
                ? null
                : group.Select(e => $"{HopLabel(e)} - {Magnitude(e)}").ToList();
            entries.Add(new TimelineEntry(start, badge, badgeClass, text, end, BadgeTooltip: tip, Members: members));
        }
        foreach (var shift in r.PathShifts)
        {
            var where = string.IsNullOrEmpty(shift.AsnName) ? (shift.TargetId ?? "path") : shift.AsnName;
            if (shift.IsUnreachable)
            {
                var span = shift.UnreachableEnd.HasValue ? FormatDuration(shift.UnreachableEnd.Value - shift.Time) : "the window";
                var hops = shift.CorrelatedTargetCount > 1 ? $" ({shift.CorrelatedTargetCount} monitored hops)" : "";
                entries.Add(new TimelineEntry(shift.Time, "Path change", "isp-event-badge-change",
                    $"{where} went fully unreachable for {span}{hops} - a routing (BGP) change, not access-layer loss. Excluded from the Packet Loss factor; still counted against {where}'s own network grade.",
                    shift.UnreachableEnd, EventCategory.Change));
                continue;
            }
            var direction = shift.Direction == PathShiftDirection.Up ? "up" : "down";
            var correlated = shift.CorrelatedTargetCount > 1 ? $", seen on {shift.CorrelatedTargetCount} paths" : "";
            entries.Add(new TimelineEntry(shift.Time, "Path shift", "isp-event-badge-shift",
                $"RTT stepped {direction} {Math.Abs(shift.DeltaMs):0.#} ms on {where} ({shift.BeforeMedianMs:0.#} to {shift.AfterMedianMs:0.#} ms){correlated}. BGP or transport fabric change.",
                Category: EventCategory.Shift));
        }
        return entries.OrderBy(e => e.Time);
    }

    /// <summary>
    /// Congestion events that describe ONE incident, as groups ordered nearest hop first.
    ///
    /// A bottleneck's elevation shows on every hop that sits behind it, so one incident arrives as
    /// several events - four rows all reporting the same two hours, none of them saying they are
    /// the same thing. Overlapping spans with the same disposition are that case; a different
    /// disposition is a different claim about what happened and never merges. The one exception:
    /// Unverifiable means "no probe past this hop to cross-check", and an overlapping Confirmed
    /// event IS that cross-check, so those pool with Confirmed. SelfInflicted and ControlPlaneNoise
    /// are active dismissals and never join a Confirmed line.
    ///
    /// Display only. The events still score individually, per hop and per ASN, so nothing here can
    /// move a score or a congestion count.
    ///
    /// Ordered by where discovery placed each hop, falling back to baseline RTT for the hops it
    /// placed nowhere - those sort last, behind every hop with a real position.
    /// </summary>
    private static List<List<CongestionEvent>> GroupCongestion(IEnumerable<CongestionEvent> events)
    {
        var groups = new List<List<CongestionEvent>>();
        foreach (var byDisposition in events.GroupBy(e =>
                     e.Disposition == CongestionDisposition.Unverifiable ? CongestionDisposition.Confirmed : e.Disposition))
        {
            List<CongestionEvent>? open = null;
            var openEnd = DateTime.MinValue;
            foreach (var evt in byDisposition.OrderBy(e => e.Start))
            {
                if (open != null && evt.Start < openEnd)
                {
                    open.Add(evt);
                    if (evt.End > openEnd) openEnd = evt.End;
                    continue;
                }
                open = new List<CongestionEvent> { evt };
                openEnd = evt.End;
                groups.Add(open);
            }
        }
        foreach (var group in groups)
            group.Sort((a, b) => PathKey(a).CompareTo(PathKey(b)));
        return groups.OrderBy(g => g.Min(e => e.Start)).ToList();
    }

    private static (int Hop, double Rtt) PathKey(CongestionEvent e) =>
        (e.BottleneckHopNumber ?? int.MaxValue, e.BaselineRttMs);

    /// <summary>
    /// The event a grouped line is written about: the nearest hop our trace data actually placed.
    ///
    /// Failing that - nothing in the group is on a traced path - the site's single access-ISP hop
    /// leads if the group touches it. Some ISPs surface no traceroute-reachable hop at all
    /// (Deutsche Telekom among them), so discovery or the operator adds one by hand; it is then the
    /// nearest position known, untraced or not. Where the site has several ISP hops, an untraced one
    /// is a guess, and the shortest baseline RTT is the honest fallback - distance, not a claim
    /// about the path.
    /// </summary>
    private static CongestionEvent Head(List<CongestionEvent> group, IspHealthReport r)
    {
        var placed = group.FirstOrDefault(e => e.BottleneckHopNumber.HasValue);
        if (placed != null) return placed;

        if (r.IspTargets.Count == 1)
        {
            var soleHop = r.IspTargets[0].TargetId;
            var onIt = group.FirstOrDefault(e =>
                e.TargetIds.Any(id => string.Equals(id, soleHop, StringComparison.OrdinalIgnoreCase)));
            if (onIt != null) return onIt;
        }

        return group[0];
    }

    private static string HopLabel(CongestionEvent e) =>
        string.IsNullOrEmpty(e.BottleneckLabel) ? string.Join(", ", e.AsnNames) : e.BottleneckLabel;

    private static string Magnitude(CongestionEvent e) =>
        $"latency {e.BaselineRttMs:0.#} to {e.PeakRttMs:0.#} ms, jitter {e.BaselineJitterMs:0.#} to {e.PeakJitterMs:0.#} ms";

    public static string FormatDuration(TimeSpan d) =>
        d.TotalHours >= 1 ? $"{d.TotalHours:0.#} h"
        : d.TotalMinutes >= 1 ? $"{d.TotalMinutes:0} min"
        : $"{d.TotalSeconds:0} sec";

    // Event time in local time, prefixed with the date when it is not today so events in a
    // multi-day (7d/30d/custom) window are unambiguous.
    public static string EventTimeLabel(DateTime utc)
    {
        var local = utc.ToLocalTime();
        return local.Date == DateTime.Now.Date ? $"Today, {local:HH:mm}" : local.ToString("MMM d, HH:mm");
    }

    public static string OutageBadgeText(OutageEvent o) =>
        o.IsPartial ? (o.IsNearTotal ? "Total loss" : "Partial loss") : o.IsBrief ? "Brief" : "Outage";

    public static string OutageSectionTitle(IspHealthReport r) =>
        r.Outages.Any(o => !o.IsPartial && !o.IsBrief) ? "Internet Outages"
        : r.Outages.All(o => o.IsPartial) ? "Internet Disruptions"
        : "Brief Internet Disruptions";

    public static string OutageScopeLabel(OutageEvent o) => o.Scope switch
    {
        OutageScope.Local => "LAN / Gateway outage",
        OutageScope.Upstream when !string.IsNullOrEmpty(o.LastReachableHop) => $"Break upstream of {o.LastReachableHop}",
        OutageScope.Upstream when !string.IsNullOrEmpty(o.BrokenNetwork) => $"Break in {o.BrokenNetwork}",
        _ when o.IsPartial && o.IsNearTotal => "Path-wide total loss",
        _ when o.IsPartial => "Path-wide partial loss",
        _ => "Whole-WAN outage"
    };

    public static string OutageScoreTooltip(OutageEvent o) =>
        o.Acknowledged
            ? "Not scored; you marked this as caused by your own work."
            : o.Scope == OutageScope.Local
                ? "Not scored; a LAN/gateway outage is your own network, not the ISP."
                : o.ScorePenaltyPoints > 0
                    ? $"Lowered your ISP Health score by {o.ScorePenaltyPoints} {(o.ScorePenaltyPoints == 1 ? "point" : "points")}."
                    : "No score impact.";

    public static string OutageHopStatus(OutageTierState t) =>
        !t.WentDark ? "stayed up"
        : t.RecoveredAt.HasValue ? $"back @ {t.RecoveredAt.Value.ToLocalTime():HH:mm:ss}"
        : "down";

    public static string OutageHopTooltip(OutageTierState t) =>
        t.WentDark ? $"Went dark; peak loss {t.PeakLossPct:0}%." : "Stayed reachable through the outage.";

    /// <summary>
    /// The hero pill's window phrase. "last {span}" only reads correctly when the window actually
    /// ends at (about) now; a custom window ending in the past instead shows a compact span pointing
    /// to its end date ("7d -> Jun 17") - kept short so it doesn't widen the pill and shift the layout.
    /// </summary>
    public static string ScoredWindowLabel(IspHealthReport r) =>
        DateTime.UtcNow - r.WindowEnd < TimeSpan.FromHours(1)
            ? $"last {WindowLabel(r)}"
            : $"{ShortSpan(r)} → {r.WindowEnd.ToLocalTime():MMM d}";

    /// <summary>Compact window span for the tight hero pill: "90m", "24h", "7d" - same tiering as
    /// <see cref="WindowLabel"/>, abbreviated units, no space.</summary>
    public static string ShortSpan(IspHealthReport r)
    {
        var span = r.WindowEnd - r.WindowStart;
        if (span.TotalMinutes < 60) return $"{span.TotalMinutes:0}m";
        if (span.TotalHours < 72) return $"{span.TotalHours:0.#}h";
        return $"{span.TotalDays:0.#}d";
    }

    public static string WindowLabel(IspHealthReport r)
    {
        var span = r.WindowEnd - r.WindowStart;
        if (span.TotalMinutes < 60)
            return $"{span.TotalMinutes:0} min";
        if (span.TotalHours < 72)
            return $"{span.TotalHours:0.#} hr";
        return $"{span.TotalDays:0.#} {(Math.Abs(span.TotalDays - 1) < 0.05 ? "day" : "days")}";
    }

    /// <summary>Window span written out for prose ("30 days", "48 hours"), where
    /// <see cref="WindowLabel"/>'s abbreviated units would read badly mid-sentence.</summary>
    public static string ProseWindowLabel(IspHealthReport r)
    {
        var span = r.WindowEnd - r.WindowStart;
        if (span.TotalMinutes < 60)
            return $"{span.TotalMinutes:0} minutes";
        if (span.TotalHours < 72)
            return $"{span.TotalHours:0.#} hours";
        return $"{span.TotalDays:0.#} {(Math.Abs(span.TotalDays - 1) < 0.05 ? "day" : "days")}";
    }

    /// <summary>
    /// The window's uptime percentage. A window that had ANY downtime never renders as a flat 100%:
    /// the outage is right there on the timeline, so a near-miss is held at 99.99% rather than
    /// rounding into a claim the timeline contradicts.
    /// </summary>
    public static string FormatUptime(IspHealthReport r) =>
        r.Downtime > TimeSpan.Zero && r.UptimePercent >= 99.995
            ? "99.99%"
            : $"{r.UptimePercent:0.##}%";

    /// <summary>Total downtime for the score card's uptime sub-line; "no downtime" when nothing went dark.</summary>
    public static string FormatDowntime(TimeSpan d) =>
        d <= TimeSpan.Zero ? "no downtime"
        : d.TotalSeconds < 90 ? $"{d.TotalSeconds:0} sec down"
        : d.TotalMinutes < 90 ? $"{d.TotalMinutes:0} min down"
        : $"{d.TotalHours:0.#} h down";

    /// <summary>
    /// Which of the two score-only grade components is costing this network more, for the single stat
    /// the row has space for. Jitter and loss already appear as measurements; stability and congestion
    /// exist only as scores, so without this the card can show four healthy numbers beside a grade
    /// that does not follow from them - a hop reading 100 stability, 0% loss and unremarkable jitter
    /// while grading in the sixties on congestion nothing on screen mentioned.
    ///
    /// Compared by contribution to the deficit - weight x shortfall - not by raw score, because the
    /// components carry different weights. Congestion reports its event COUNT rather than its score:
    /// "2 Events" says what happened, where "Congestion 48" invites being read as a percentage.
    /// </summary>
    public static (string Label, string Value, int? Score)? LimitingAspect(
        int? stabilityScore, int? congestionScore, int congestionEvents,
        double stabilityWeight, double congestionWeight)
    {
        if (stabilityScore is null && congestionScore is null) return null;

        var stabilityDeficit = stabilityScore is int s ? stabilityWeight * (100 - s) : -1;
        var congestionDeficit = congestionScore is int c ? congestionWeight * (100 - c) : -1;

        // Ties go to congestion, except when it has nothing to report - "0 Events" only ever wins
        // the slot against a perfect stability score, and the number is the more useful of the two.
        if (congestionScore is int cs && congestionDeficit >= stabilityDeficit
            && (congestionEvents > 0 || stabilityScore is null))
            return ("Congestion", congestionEvents == 1 ? "1 Event" : $"{congestionEvents} Events", cs);
        return stabilityScore is int ss ? ("Stability", ss.ToString(), ss) : null;
    }

    /// <summary>The window's absolute bounds in local time, for a report that outlives the page.</summary>
    public static string WindowRangeLabel(IspHealthReport r) =>
        $"{r.WindowStart.ToLocalTime():MMM d, yyyy HH:mm} to {r.WindowEnd.ToLocalTime():MMM d, yyyy HH:mm}";

    public static string FormatSpeed(double? mbps) => mbps.HasValue ? mbps.Value.ToString(mbps >= 100 ? "0" : "0.#") : "--";

    public static string FormatMs(double? ms) => ms.HasValue ? $"{ms.Value:0.#} ms" : "--";

    public static string FormatJitter(double? ms) => ms.HasValue ? $"{ms.Value:0.00} ms" : "--";

    public static string JitterAssimilationTooltip(IspAsnHealth asn, bool isIsp) => isIsp
        ? $"Showing {FormatJitter(asn.ScoredJitterMs)} from the cleanest network beyond your ISP, not your ISP routers' {FormatJitter(asn.RawJitterMs)}. All traffic crosses the ISP to reach it, so the ISP is no jitterier - its higher reading is likely ICMP deprioritization."
        : $"Showing {FormatJitter(asn.ScoredJitterMs)} from a router deeper in this network, not the {FormatJitter(asn.RawJitterMs)} a closer router reported. Traffic passes through the closer router to reach it, so the lower value is the true path jitter - the closer router just deprioritizes ICMP.";

    public static string JitterAssimilationTooltip(IspTargetHealth target) =>
        $"Showing {FormatJitter(target.ScoredJitterMs)} measured to a network reached through this router, not its own {FormatJitter(target.RawJitterMs)}. Traffic passes through it cleanly, so the higher reading is this router deprioritizing ICMP, not real jitter.";

    public static string FormatRtt(double? ms) => ms.HasValue ? $"{ms.Value:0.00} ms" : "--";

    public static string FormatRttRange(double? min, double? max) =>
        min.HasValue && max.HasValue ? $"{min.Value:0.00} - {max.Value:0.00} ms" : "--";

    public static string FormatLossPct(double? pct) => pct switch { null => "--", 0 => "0%", _ => $"{pct.Value:0.##}%" };

    /// <summary>
    /// The access medium's name for exports, with its qualifier where one helps ("DOCSIS (Cable)").
    /// Every enum member is named explicitly: the old catch-all fell through to tech.ToString(),
    /// which printed the C# casing - a legacy PppoE site's PDF read "PppoE".
    /// </summary>
    public static string FormatTechName(AccessTechnology tech) => tech switch
    {
        AccessTechnology.Gpon => "GPON",
        AccessTechnology.XgsPon => "XGS-PON",
        AccessTechnology.Docsis => "DOCSIS (Cable)",
        AccessTechnology.DirectEthernet => "Active Ethernet",
        AccessTechnology.FixedWireless => "Fixed Wireless",
        AccessTechnology.Satellite => "Satellite (LEO)",
        AccessTechnology.Cellular => "Cellular",
        AccessTechnology.Dsl => "DSL (ADSL/VDSL)",
        AccessTechnology.PppoE => "PPPoE",
        AccessTechnology.Other => "Other",
        AccessTechnology.Unknown => "Not detected",
        _ => "Not detected"
    };

    /// <summary>
    /// How the scored profile is named in an export: the medium, plus the encapsulation when a
    /// session is detected - "GPON (PPPoE)", "DSL (ADSL/VDSL, PPPoE)". PPPoE folds into the
    /// medium's existing qualifier rather than stacking a second bracket after it.
    ///
    /// Replaces "{Profile.DisplayName} ({FormatTechName})", which printed the technology twice
    /// ("DOCSIS (DOCSIS (Cable))") and had nowhere to put the session. The medium is the single
    /// source here; the profile's own DisplayName says the same thing less precisely.
    /// </summary>
    public static string ScoredAsLabel(IspHealthReport report)
    {
        var medium = FormatTechName(report.AccessTechnology);
        if (!report.PppoeSession) return medium;
        return medium.EndsWith(')')
            ? $"{medium[..^1]}, PPPoE)"
            : $"{medium} (PPPoE)";
    }

    /// <summary>How an ASN is labelled on the Networks on Your Path card.</summary>
    public static string AsnDisplayName(IspAsnHealth asn) =>
        string.IsNullOrEmpty(asn.AsnName) ? $"AS{asn.AsnNumber}" : asn.AsnName;

    /// <summary>The role line under an ASN's name: its network, a direct peer, or transit.</summary>
    public static string AsnRoleLabel(IspAsnHealth asn, bool isIspAsn) =>
        isIspAsn ? "ISP network" : asn.AsnNumber < 0 ? "Direct peering" : "Transit";

    /// <summary>
    /// The access ISP's own ASN - the network the report is really about. Only a real
    /// (positive) ASN qualifies; a synthetic entry names no operator. Null when discovery
    /// has not identified one.
    /// </summary>
    public static IspAsnHealth? AccessIspAsn(IspHealthReport r) =>
        r.IspAsns.FirstOrDefault(a => a.AsnNumber > 0);

    /// <summary>
    /// The ISP named for a report header: "Example Access (AS64500)". Null when no access
    /// ASN is known, so callers can leave the line out rather than print a placeholder.
    /// </summary>
    public static string? AccessIspLabel(IspHealthReport r)
    {
        var asn = AccessIspAsn(r);
        return asn == null ? null : $"{AsnDisplayName(asn)} (AS{asn.AsnNumber})";
    }

    /// <summary>
    /// Which WAN the report scored, as the console names it: "Comcast (WAN2, eth5)". The
    /// network group is what tells two WANs apart once more than one is scored, and the
    /// interface pins it to a physical port. Null when the console reported no WAN.
    /// </summary>
    public static string? ScoredWanLabel(IspHealthReport r)
    {
        var group = string.IsNullOrWhiteSpace(r.WanNetworkGroup)
            ? null
            : DisplayFormatters.NormalizeWanDisplay(r.WanNetworkGroup);

        var qualifiers = new[] { group, r.WanInterface }
            .Where(q => !string.IsNullOrWhiteSpace(q))
            .ToList();
        var suffix = qualifiers.Count > 0 ? $" ({string.Join(", ", qualifiers)})" : "";

        if (!string.IsNullOrWhiteSpace(r.WanName))
            return $"{r.WanName}{suffix}";
        // No user label: the group alone still identifies the link ("WAN2 (eth5)").
        return group == null
            ? (string.IsNullOrWhiteSpace(r.WanInterface) ? null : r.WanInterface)
            : $"{group}{(string.IsNullOrWhiteSpace(r.WanInterface) ? "" : $" ({r.WanInterface})")}";
    }
}
