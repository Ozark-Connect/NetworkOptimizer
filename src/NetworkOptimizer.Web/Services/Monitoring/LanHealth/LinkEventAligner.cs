namespace NetworkOptimizer.Web.Services.Monitoring.LanHealth;

/// <summary>
/// Merges one link's observations into incidents. The same physical event seen by a kernel line, a
/// probe burst and a counter delta becomes one incident that lists every source that saw it.
/// <list type="number">
/// <item>Each event carries a window sized to its source's precision.</item>
/// <item>Precise events whose windows overlap or sit within <see cref="MergeGap"/> merge; the incident
/// time is the most precise source's time.</item>
/// <item>A down with an up within <see cref="DropMaxDown"/> is a drop; a down with no up is an outage the
/// device-offline path owns.</item>
/// <item>Error and discard bursts within <see cref="ErrorAttachWindow"/> after a drop attach to it.</item>
/// <item>Explain windows (an admin change, a reboot, a device state, a Firmware Rollout wave, a sleeping
/// client) mark an incident explained.</item>
/// <item>Counter resets are never error bursts (the caller turns decreases into <see cref="LinkEventType.CounterReset"/>).</item>
/// <item>Coverage: each incident lists the observing sources that did not see it.</item>
/// </list>
/// Imprecise events (a counter delta over a poll window) attach to the precise incidents they overlap,
/// up to their count; whatever count is left becomes one imprecise incident of that size.
/// </summary>
public static class LinkEventAligner
{
    internal static readonly TimeSpan MergeGap = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan DropMaxDown = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan ErrorAttachWindow = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan AdminChangeWindow = TimeSpan.FromMinutes(2);

    /// <summary>A source that covers this link, and the spans it was actually reporting.</summary>
    public sealed record Observer(LinkSource Source, IReadOnlyList<(DateTime Start, DateTime End)> Observing, bool CountsDrops, bool CountsErrors);

    public static List<LinkIncident> Align(
        IReadOnlyList<LinkEvent> events,
        IReadOnlyList<ExplainWindow> explain,
        IReadOnlyList<Observer> observers,
        IReadOnlySet<string> linkDeviceMacs)
    {
        var incidents = new List<LinkIncident>();
        var faults = events.Where(e => e.Type != LinkEventType.CounterReset).OrderBy(e => e.Start).ToList();

        // 1-2: precise events anchor incidents.
        foreach (var e in faults.Where(e => e.Precise && e.Type is not (LinkEventType.ErrorBurst or LinkEventType.DiscardBurst)))
        {
            var open = incidents.LastOrDefault();
            if (open != null && e.Start <= open.End + MergeGap && !(open.HasUp() && e.Type == LinkEventType.LinkDown))
            {
                open.Events.Add(e);
                open.SeenBy.Add(e.Source);
                if (e.End > open.End) open.End = e.End;
                continue;
            }
            var incident = new LinkIncident { Start = e.Start, End = e.End };
            incident.Events.Add(e);
            incident.SeenBy.Add(e.Source);
            incidents.Add(incident);
        }

        // 3: classify each anchor.
        foreach (var i in incidents)
        {
            var down = i.Events.Any(e => e.Type == LinkEventType.LinkDown);
            var up = i.Events.Where(e => e.Type == LinkEventType.LinkUp).ToList();
            var loss = i.Events.Any(e => e.Type == LinkEventType.LossBurst);
            var speed = i.Events.Any(e => e.Type == LinkEventType.SpeedChange);
            if (down && up.Count > 0)
            {
                var d = i.DownSeconds;
                i.Kind = d is null || d <= DropMaxDown.TotalSeconds ? IncidentKind.Drop : IncidentKind.Outage;
                i.SpeedAfterMbps = up.Select(e => e.SpeedMbps).LastOrDefault(s => s != null);
            }
            else if (down) i.Kind = IncidentKind.Outage;
            else if (up.Count > 0) { i.Kind = IncidentKind.Drop; i.SpeedAfterMbps = up.Select(e => e.SpeedMbps).LastOrDefault(s => s != null); }
            else if (speed) i.Kind = IncidentKind.SpeedChange;
            else if (loss) i.Kind = IncidentKind.LossBurst;
        }

        // A down followed by its up in a separate anchor (more than MergeGap apart, under a minute) is one drop.
        for (var k = 0; k + 1 < incidents.Count; k++)
        {
            var a = incidents[k];
            var b = incidents[k + 1];
            if (a.Kind == IncidentKind.Outage && !a.HasUp() && b.Events.Any(e => e.Type == LinkEventType.LinkUp) && !b.HasDown()
                && b.Start - a.Start <= DropMaxDown)
            {
                foreach (var e in b.Events) { a.Events.Add(e); a.SeenBy.Add(e.Source); }
                a.End = b.End;
                a.Kind = IncidentKind.Drop;
                a.SpeedAfterMbps = b.SpeedAfterMbps;
                incidents.RemoveAt(k + 1);
            }
        }

        // Imprecise relinks attach to the precise drops they overlap, up to their count.
        foreach (var e in faults.Where(e => !e.Precise && e.Type is LinkEventType.Relink or LinkEventType.LinkDown or LinkEventType.LinkUp))
        {
            var remaining = (int)Math.Round(e.Count);
            foreach (var i in incidents.Where(i => i.Kind is IncidentKind.Drop or IncidentKind.Outage or IncidentKind.LossBurst
                         && !i.SeenBy.Contains(e.Source) && i.Start <= e.End && i.End >= e.Start))
            {
                if (remaining <= 0) break;
                i.Events.Add(e);
                i.SeenBy.Add(e.Source);
                // A probe loss burst that a counter confirms was a drop.
                if (i.Kind == IncidentKind.LossBurst) i.Kind = IncidentKind.Drop;
                remaining--;
            }
            if (remaining > 0)
            {
                var r = new LinkIncident { Start = e.Start, End = e.End, Kind = IncidentKind.Relink, Count = remaining };
                r.Events.Add(e with { Count = remaining });
                r.SeenBy.Add(e.Source);
                incidents.Add(r);
            }
        }

        // Imprecise speed changes attach to a drop in their window, else stand alone.
        foreach (var e in faults.Where(e => !e.Precise && e.Type is LinkEventType.SpeedChange or LinkEventType.DuplexChange))
        {
            var host = incidents.FirstOrDefault(i => i.Kind is IncidentKind.Drop or IncidentKind.Relink && i.Start <= e.End && i.End >= e.Start);
            if (host != null) { host.Events.Add(e); host.SeenBy.Add(e.Source); host.SpeedAfterMbps ??= e.SpeedMbps; continue; }
            var s = new LinkIncident { Start = e.Start, End = e.End, Kind = IncidentKind.SpeedChange, SpeedAfterMbps = e.SpeedMbps };
            s.Events.Add(e);
            s.SeenBy.Add(e.Source);
            incidents.Add(s);
        }

        // 4: errors within a minute after a drop belong to the drop; the rest are their own bursts.
        foreach (var e in faults.Where(e => e.Type is LinkEventType.ErrorBurst or LinkEventType.DiscardBurst))
        {
            var host = incidents.FirstOrDefault(i => i.Kind is IncidentKind.Drop or IncidentKind.Relink or IncidentKind.Outage
                && e.Start <= i.End + ErrorAttachWindow && e.End >= i.Start);
            if (host != null && e.Type == LinkEventType.ErrorBurst)
            {
                host.AttachedErrors += e.Count;
                host.Events.Add(e);
                host.SeenBy.Add(e.Source);
                continue;
            }
            var burst = new LinkIncident
            {
                Start = e.Start, End = e.End, Count = e.Count,
                Kind = e.Type == LinkEventType.ErrorBurst ? IncidentKind.ErrorBurst : IncidentKind.DiscardBurst,
            };
            burst.Events.Add(e);
            burst.SeenBy.Add(e.Source);
            incidents.Add(burst);
        }

        incidents.Sort((a, b) => a.Start.CompareTo(b.Start));

        // 5: explanations.
        foreach (var i in incidents)
        {
            foreach (var w in explain)
            {
                if (w.Reason == ExplainReason.ConsoleCutOff || !linkDeviceMacs.Contains(w.DeviceMac)) continue;
                var covers = w.Reason is ExplainReason.PortProfileChanged or ExplainReason.ManualLinkReset
                    ? i.Start >= w.Start && i.Start <= w.End + AdminChangeWindow
                    : w.Covers(i.Start, i.End);
                if (!covers) continue;
                i.Explained = w.Reason;
                i.ExplainedDetail = w.Detail;
                break;
            }
        }

        // 7: coverage. A source that was not reporting at the time did not miss anything.
        foreach (var i in incidents)
        {
            foreach (var o in observers)
            {
                if (i.SeenBy.Contains(o.Source)) continue;
                var relevant = i.Kind is IncidentKind.ErrorBurst ? o.CountsErrors
                    : i.Kind is IncidentKind.Drop or IncidentKind.Relink or IncidentKind.Outage ? o.CountsDrops
                    : false;
                if (!relevant) continue;
                if (o.Observing.Any(s => s.Start <= i.Start && s.End >= i.End)) i.MissedBy.Add(o.Source);
            }
        }
        return incidents;
    }

    private static bool HasUp(this LinkIncident i) => i.Events.Any(e => e.Type == LinkEventType.LinkUp);

    private static bool HasDown(this LinkIncident i) => i.Events.Any(e => e.Type == LinkEventType.LinkDown);

    /// <summary>Per source: how many incidents it caught of those it was there to catch.</summary>
    public static List<SourceCoverage> Coverage(IReadOnlyList<LinkIncident> incidents, IReadOnlyList<Observer> observers)
    {
        var drops = incidents.Where(i => i.Kind is IncidentKind.Drop or IncidentKind.Relink or IncidentKind.Outage).ToList();
        return observers.Select(o =>
        {
            var due = drops.Where(i => i.SeenBy.Contains(o.Source) || i.MissedBy.Contains(o.Source)).ToList();
            var caught = due.Count(i => i.SeenBy.Contains(o.Source));
            var since = o.Observing.Count > 0 ? o.Observing.Min(s => s.Start) : (DateTime?)null;
            return new SourceCoverage(o.Source, caught, due.Count, o.Observing.Count > 0, since);
        }).ToList();
    }
}
