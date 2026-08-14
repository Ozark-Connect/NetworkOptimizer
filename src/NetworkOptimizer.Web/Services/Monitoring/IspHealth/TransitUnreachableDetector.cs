namespace NetworkOptimizer.Web.Services.Monitoring.IspHealth;

/// <summary>
/// Finds windows where a transit target went totally unreachable (~100% loss) for minutes at
/// a time. That is a routing/BGP change - the hop left the forwarding path or stopped
/// answering - not access-layer loss, so the service carves these windows out of THAT
/// target's loss-pool contribution (every other access/transit/DNS series keeps feeding the
/// pool) and surfaces them as path events. The target's own ASN grade still sees the loss,
/// and a transit hop that is merely lossy (below the total-loss threshold) is untouched: a
/// steady loss floor or loaded loss on a stable transit path stays in the pool, which also
/// keeps the loss signal usable for sites with few pingable access-ISP hops.
/// </summary>
public static class TransitUnreachableDetector
{
    /// <summary>A contiguous span where one transit target was totally unreachable.</summary>
    public record DarkWindow(string TargetId, int AsnNumber, string? AsnName, DateTime Start, DateTime End);

    /// <summary>Per-ASN merge of overlapping <see cref="DarkWindow"/>s, for one path event per network.</summary>
    public record AsnDarkEvent(int AsnNumber, string? AsnName, DateTime Start, DateTime End, int TargetCount)
    {
        /// <summary>The dark targets behind the event, so it can be matched to a chart line.</summary>
        public IReadOnlyList<string> TargetIds { get; init; } = Array.Empty<string>();
    }

    /// <summary>
    /// Scans one transit target's series for sustained total-loss runs. A run needs every
    /// sample at or above <see cref="IspHealthOptions.TransitUnreachableLossPct"/> and must
    /// span at least <see cref="IspHealthOptions.TransitUnreachableMinSeconds"/>; a
    /// monitoring gap between dark samples no longer than
    /// <see cref="IspHealthOptions.TransitUnreachableMaxGapSeconds"/> keeps a run intact
    /// (an unreachable target stays dark through a console restart). Window bounds are the
    /// first and last dark sample times, so masking by [Start, End] matches exactly the
    /// samples that formed the run.
    /// </summary>
    public static List<DarkWindow> Detect(string targetId, int asnNumber, string? asnName,
        IReadOnlyList<LatencySample> samples, IspHealthOptions options)
    {
        var windows = new List<DarkWindow>();
        DateTime? runStart = null;
        DateTime? lastDark = null;

        void CloseRun()
        {
            if (runStart.HasValue && lastDark.HasValue
                && (lastDark.Value - runStart.Value).TotalSeconds >= options.TransitUnreachableMinSeconds)
                windows.Add(new DarkWindow(targetId, asnNumber, asnName, runStart.Value, lastDark.Value));
            runStart = null;
            lastDark = null;
        }

        foreach (var s in samples)
        {
            if (!s.LossPercent.HasValue) continue;
            if (s.LossPercent.Value >= options.TransitUnreachableLossPct)
            {
                if (runStart.HasValue && lastDark.HasValue
                    && (s.Time - lastDark.Value).TotalSeconds > options.TransitUnreachableMaxGapSeconds)
                    CloseRun();
                runStart ??= s.Time;
                lastDark = s.Time;
            }
            else
            {
                CloseRun();
            }
        }
        CloseRun();
        return windows;
    }

    /// <summary>
    /// Second pass for a transit target that FLAPS rather than going cleanly dark: unreachable,
    /// answering for a probe or two, unreachable again. <see cref="Detect"/> requires every sample in
    /// a run to be dark, so a single answered probe resets it and a target that spent most of an hour
    /// unreachable never produces a window - while contributing its ~100% samples to the pooled loss
    /// the whole time. That is the same routing event, just seen through a hop that answers
    /// intermittently, and it is no more usable as an access-layer loss measurement.
    ///
    /// A span opens on the first dark sample and closes once the target has answered continuously for
    /// <see cref="IspHealthOptions.TransitUnreachableRecoverySeconds"/>, and only counts if it was
    /// dark for at least <see cref="IspHealthOptions.TransitUnreachableMostlyDarkFraction"/> of its
    /// samples across at least <see cref="IspHealthOptions.TransitUnreachableMostlyDarkMinSeconds"/>.
    /// The bar is deliberately longer than the solid-dark rule's: intermittent evidence is weaker, so
    /// it has to persist before we stop believing the target. Bounds still run first dark sample to
    /// last, so masking never hides a sample the target actually answered after recovering.
    /// </summary>
    public static List<DarkWindow> DetectMostlyDark(string targetId, int asnNumber, string? asnName,
        IReadOnlyList<LatencySample> samples, IspHealthOptions options)
    {
        var windows = new List<DarkWindow>();
        DateTime? runStart = null, lastDark = null, aliveSince = null;
        // Counted as of the last dark sample, so trailing answered probes before recovery cannot
        // dilute the fraction of the span we actually emit.
        int darkAtLastDark = 0, totalAtLastDark = 0, dark = 0, total = 0;

        void CloseRun()
        {
            if (runStart.HasValue && lastDark.HasValue && totalAtLastDark > 0
                && (lastDark.Value - runStart.Value).TotalSeconds >= options.TransitUnreachableMostlyDarkMinSeconds
                && (double)darkAtLastDark / totalAtLastDark >= options.TransitUnreachableMostlyDarkFraction)
                windows.Add(new DarkWindow(targetId, asnNumber, asnName, runStart.Value, lastDark.Value));
            runStart = null;
            lastDark = null;
            aliveSince = null;
            darkAtLastDark = totalAtLastDark = dark = total = 0;
        }

        foreach (var s in samples)
        {
            if (!s.LossPercent.HasValue) continue;
            if (s.LossPercent.Value >= options.TransitUnreachableLossPct)
            {
                runStart ??= s.Time;
                lastDark = s.Time;
                aliveSince = null;
                dark++;
                total++;
                darkAtLastDark = dark;
                totalAtLastDark = total;
            }
            else if (runStart.HasValue)
            {
                total++;
                aliveSince ??= s.Time;
                if ((s.Time - aliveSince.Value).TotalSeconds >= options.TransitUnreachableRecoverySeconds)
                    CloseRun();
            }
        }
        CloseRun();
        return windows;
    }

    /// <summary>
    /// Collapses per-target dark windows into one event per ASN: windows on the same network
    /// that overlap (or sit within the run-coalescing gap of each other) merge, so an RTT
    /// cluster's members washing out together read as a single path event rather than one per
    /// monitored hop. Targets without an ASN stay their own event.
    /// </summary>
    public static List<AsnDarkEvent> MergeByAsn(IReadOnlyList<DarkWindow> windows, IspHealthOptions options)
    {
        var events = new List<AsnDarkEvent>();
        var gap = TimeSpan.FromSeconds(options.TransitUnreachableMaxGapSeconds);
        foreach (var group in windows.GroupBy(w => w.AsnNumber != 0 ? $"as{w.AsnNumber}" : w.TargetId))
        {
            AsnDarkEvent? current = null;
            var targets = new HashSet<string>();
            foreach (var w in group.OrderBy(w => w.Start))
            {
                if (current != null && w.Start <= current.End + gap)
                {
                    targets.Add(w.TargetId);
                    current = current with
                    {
                        End = w.End > current.End ? w.End : current.End,
                        TargetCount = targets.Count,
                        TargetIds = targets.ToList()
                    };
                }
                else
                {
                    if (current != null) events.Add(current);
                    targets = new HashSet<string> { w.TargetId };
                    current = new AsnDarkEvent(w.AsnNumber, w.AsnName, w.Start, w.End, 1) { TargetIds = new[] { w.TargetId } };
                }
            }
            if (current != null) events.Add(current);
        }
        return events.OrderBy(e => e.Start).ToList();
    }
}
