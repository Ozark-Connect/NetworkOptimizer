using System.Globalization;
using NetworkOptimizer.Core.Helpers;
using NetworkOptimizer.Web.Services.Monitoring.IspHealth;

namespace NetworkOptimizer.Web.Services.Monitoring.LanHealth;

/// <summary>How findings render times and ranges; the detectors stay pure.</summary>
public sealed record FindingText(Func<DateTime, string> Time, string Range)
{
    public static readonly FindingText Utc = new(t => t.ToString("MMM d HH:mm", CultureInfo.InvariantCulture) + " UTC", "48h");
}

/// <summary>
/// The per-link detectors. Each is a pure function of the link's evidence and its aligned incidents.
/// Thresholds come from the 2026-11-22 calibration on the test sites: a healthy wired port has zero
/// errors, normal broadcast is under ~5 pps, and gateway LAN port error counters read 0 even when the
/// far end counts CRC, so a gateway-side 0 never clears a link.
/// </summary>
public static class LanHealthDetectors
{
    /// <summary>Unexplained drops in the window that make a fabric link flapping (the reference case: 30 in 9 days).</summary>
    public const int FlappingDropsFabric = 3;
    /// <summary>Access ports churn more by nature; sleep cycles are already explained before this counts.</summary>
    public const int FlappingDropsAccess = 6;
    /// <summary>Errors on a stable link per day above which it is impaired ("more than a handful per day").</summary>
    public const double ErrorsPerDayFloor = 5;
    /// <summary>A storm needs both: well above the port's own normal, and above this absolute floor.</summary>
    public const double StormFloorPps = 50;
    public const double StormBaselineFactor = 10;
    /// <summary>Unusual utilization: above this share of link speed, sustained, and above the port's own p95.</summary>
    public const double UtilizationShare = 0.5;
    public const int UtilizationSustainHours = 1;
    /// <summary>A mesh backhaul's signal this far below its own baseline is soft.</summary>
    public const double MeshSignalDropDb = 8;
    /// <summary>A mesh PHY rate below this share of its baseline has dropped.</summary>
    public const double MeshRateShare = 0.5;
    // Mesh backhaul quality, calibrated on 90 days of an MLO backhaul and two Device Bridge links: a healthy backhaul
    // probes at 0 loss and about 1.9 ms over its wired parent, and its parent retries 5 to 8% of transmissions.
    /// <summary>Probe loss through the backhaul, over the parent's own, that is not normal.</summary>
    public const double MeshLossExcessPct = 0.5;
    /// <summary>RTT over the parent rising this many ms (or the normal gap again, if larger) past its normal.</summary>
    public const double MeshRttRiseMs = 2;
    /// <summary>A retry rate at or above this, and twice its own normal, is not normal.</summary>
    public const double MeshRetryPct = 15;
    /// <summary>Share of the backhaul's usable rate an hour's mean traffic must pass, in this many hours, to call it saturated.</summary>
    public const double MeshDemandShare = 0.5;
    public const int MeshDemandHours = 3;

    /// <summary>Runs every per-link detector.</summary>
    public static List<LinkFinding> Run(LinkEvidence ev, IReadOnlyList<LinkIncident> incidents, FindingText text)
    {
        var findings = new List<LinkFinding>();
        void Add(LinkFinding? f) { if (f != null) findings.Add(f); }
        Add(LinkDrops(ev, incidents, text));
        // A mesh backhaul's rate is a radio's PHY rate that moves all the time, with no cable, duplex,
        // port counters or PoE: the wired checks would read normal radio behavior as faults. Mesh() covers it.
        var wired = !ev.Link.IsMesh;
        if (wired)
        {
            var downshift = SpeedDownshift(ev, text);
            Add(downshift);
            Add(SpeedKeepsDropping(ev, downshift));
            Add(DuplexMismatch(ev));
            Add(StableLinkErrors(ev, incidents, text));
            Add(Discards(ev, incidents));
            Add(Microbursts(ev));
            Add(Optics(ev));
            Add(BelowCapability(ev));
            Add(PoeCycling(ev, incidents));
            Add(SpeedSpecificFlapping(ev, incidents));
            Add(UnusualUtilization(ev, text));
        }
        Add(LossWithoutEvidence(ev, incidents, text));
        Add(Storm(ev, text));
        foreach (var f in Mesh(ev, incidents)) Add(f);
        Add(BackhaulChannelMoves(ev, incidents, text));
        Add(AccessDegraded(ev));
        Add(HiddenSwitch(ev));
        Add(SpeedTestBelowNorm(ev));
        foreach (var f in findings) if (!Detectors.Scores(f.Detector)) f.Points = 0;
        return findings;
    }

    private static string Title(string cls, LinkRecord link) => $"{cls}: {link.Name}";

    /// <summary>lanh-10's {device kind}: what sits behind the link.</summary>
    internal static string DeviceKind(LinkRecord link, int count) =>
        link.ClientsBehind > 0 ? (count == 1 ? "client" : "clients") : (count == 1 ? "device" : "devices");

    /// <summary>lanh-10: "Fabric · 5 clients behind it".</summary>
    internal static string ImpactChip(LinkRecord link) =>
        link.IsFabric ? $"Fabric · {link.ClientsBehind} {DeviceKind(link, link.ClientsBehind)} behind it" : "Access";

    /// <summary>Which end receives the errors, by name, when only one end does.</summary>
    internal static (string End, string Other)? ErrorAsymmetry(LinkEvidence ev)
    {
        var up = ev.Upstream.RxErrors ?? 0;
        var down = ev.Downstream.RxErrors ?? 0;
        if (up > 0 && down == 0 && ev.Downstream.RxErrors != null) return (ev.Link.Upstream.DeviceName, ev.Link.Downstream.DeviceName);
        if (down > 0 && up == 0 && (ev.Upstream.RxErrors != null || ev.Upstream.ErrorsBlind)) return (ev.Link.Downstream.DeviceName, ev.Link.Upstream.DeviceName);
        return null;
    }

    // ---- 1 Link drops (and the drop-and-retrain signature) ----
    internal static LinkFinding? LinkDrops(LinkEvidence ev, IReadOnlyList<LinkIncident> incidents, FindingText text)
    {
        var drops = incidents.Where(i => !i.IsExplained && i.Kind is IncidentKind.Drop or IncidentKind.Relink).ToList();
        var count = (int)drops.Sum(i => i.Count);
        var threshold = ev.Link.IsFabric ? FlappingDropsFabric : FlappingDropsAccess;
        if (count < threshold) return null;

        var durations = drops.Select(i => i.DownSeconds).OfType<double>().ToList();
        double? seconds = durations.Count > 0 ? Math.Max(1, Math.Round(Median(durations)!.Value)) : null;
        var speeds = drops.Select(i => i.SpeedAfterMbps).OfType<int>().Distinct().ToList();
        var sameSpeed = speeds.Count == 1 && (ev.UsualSpeedMbps == null || speeds[0] >= ev.UsualSpeedMbps);
        var onset = ev.TroubleSince is DateTime ts && ts < drops.Min(i => i.Start) ? ts : drops.Min(i => i.Start);

        // lanh-08, rendered from the parts the evidence supports.
        var parts = new List<string>();
        var first = seconds is double s
            ? $"{count} drops in {text.Range}, about {s:0} seconds each"
            : $"{count} drops in {text.Range}";
        if (sameSpeed) first += $"; the link comes back at {DisplayFormatters.FormatLinkSpeed(speeds[0])} every time";
        parts.Add(first + ".");
        if (ev.HistoryDays is int days && days > 0)
            parts.Add($"Started {text.Time(onset)}, with none in the {days} days before.");
        if (ErrorAsymmetry(ev) is { } asym)
            parts.Add($"Errors only on the {asym.End} side.");
        // A change made to fix it that the drops outlived.
        var lastChange = incidents.Where(i => i.Explained == ExplainReason.PortProfileChanged).OrderByDescending(i => i.Start).FirstOrDefault();
        if (lastChange != null && drops.Any(d => d.Start > lastChange.Start))
            parts.Add($"Still dropping after the {text.Time(lastChange.Start)} change.");
        // Debounce delays a link-down report, so a switch that missed drops other sources saw is explained by it.
        var debounce = ev.Link.UpstreamPort.LinkDebounce;
        if (debounce is "Auto" or "Manual" && drops.Any(d => d.MissedBy.Contains(LinkSource.SwitchLinkDownCount)))
            parts.Add($"Link Debounce is {debounce} on this port, so the switch's own drop count can miss short drops.");

        var chips = new List<string>();
        if (sameSpeed && seconds is <= 15) chips.Add("Short link drops");
        chips.Add(ImpactChip(ev.Link));

        var steps = new List<string>();
        if (ev.Link.Kind == LinkKind.FabricWired && ev.Link.Upstream.PortIdx is int p)
            steps.Add(PortSwapStep(ev.Link, p));

        var perDay = count / Math.Max(1.0 / 24, (ev.To - ev.From).TotalDays);
        // Time of day as ISP Health weighs it: a drop when the link is normally idle costs less (down to half).
        var profile = LanHealthScorer.UsageProfile(ev.BaselineHours.Concat(ev.Hours), TimeZoneInfo.Local);
        var weighted = drops.Sum(i => i.Count * LanHealthScorer.UsageWeight(profile, i.Start, TimeZoneInfo.Local));
        var pts = ScoreCurve.Interpolate(weighted, (0, 0), (3, 15), (10, 35), (30, 60), (100, 85))
                  * ScoreCurve.Interpolate(seconds ?? 5, (1, 70), (5, 100), (60, 130)) / 100;
        // A fault that has run for days weighs more than a burst: up to 1.5x at two weeks, then capped,
        // so an old fault never pins the score to the floor.
        if (ev.TroubleSince is DateTime since)
            pts *= ScoreCurve.Interpolate((ev.To - since).TotalDays, (2, 1.0), (7, 1.25), (14, 1.5));
        return new LinkFinding
        {
            Detector = Detectors.LinkDrops,
            LinkId = ev.Link.Id,
            Severity = ev.Link.IsFabric ? (perDay >= 10 ? FindingSeverity.Critical : FindingSeverity.Warning) : FindingSeverity.Info,
            Class = "Link Flapping",
            Title = Title("Link Flapping", ev.Link),
            Body = string.Join(" ", parts),
            Chips = chips,
            Points = Math.Min(90, pts),
            Onset = onset,
            Status = LinkStatus.Flapping,
            NextSteps = steps,
            EeeTest = ev.Link.UpstreamPort.Eee == true || ev.Link.DownstreamPort?.Eee == true,
            IdleDrops = MostlyIdle(ev, drops),
        };
    }

    /// <summary>Whether most drops came in hours the link was idle (under 1 Mbps both ways): EEE's wake from low power.</summary>
    internal static bool MostlyIdle(LinkEvidence ev, IReadOnlyList<LinkIncident> drops)
    {
        if (drops.Count == 0) return false;
        var hours = ev.Hours.ToDictionary(h => new DateTime(h.Hour.Year, h.Hour.Month, h.Hour.Day, h.Hour.Hour, 0, 0, h.Hour.Kind));
        var idle = drops.Count(d => hours.TryGetValue(new DateTime(d.Start.Year, d.Start.Month, d.Start.Day, d.Start.Hour, 0, 0, d.Start.Kind), out var h)
            && (h.MeanInBps ?? 0) < 1_000_000 && (h.MeanOutBps ?? 0) < 1_000_000);
        return idle * 2 > drops.Count;
    }

    /// <summary>lanh-39: swap the upstream port with a neighbor to tell the port from the device.</summary>
    internal static string PortSwapStep(LinkRecord link, int port)
    {
        var other = port > 1 ? port - 1 : port + 1;
        var moves = link.SwapNeighbor is { Length: > 0 } n ? $" ({n} moves to Port {port})" : "";
        return $"Swap the cables in Port {port} and Port {other} on {link.Upstream.DeviceName}{moves}. If the drops stay on Port {port}, it's the {(link.Upstream.IsGateway ? "gateway" : "switch")} port. If they follow {link.Downstream.DeviceName}, it's the {(link.Downstream.IsAccessPoint ? "AP" : link.Downstream.IsSwitch ? "switch" : "device")} or its cable.";
    }

    // ---- 2 Speed downshift ----
    internal static LinkFinding? SpeedDownshift(LinkEvidence ev, FindingText text)
    {
        if (ev.Link.Kind == LinkKind.Access) return null;
        if (ev.UsualSpeedMbps is not int usual || ev.Link.SpeedMbps is not int now || now >= usual) return null;
        if (ev.Link.Forced) return null;
        var alternates = ev.Speeds.Select(s => s.SpeedMbps).Distinct().Count() > 2;
        var since = ev.Speeds.LastOrDefault(s => s.SpeedMbps >= usual)?.At;
        return new LinkFinding
        {
            Detector = Detectors.SpeedDownshift,
            LinkId = ev.Link.Id,
            Severity = ev.Link.IsFabric ? FindingSeverity.Warning : FindingSeverity.Info,
            Class = "Link Speed Degraded",
            Title = Title("Link Speed Degraded", ev.Link),
            Body = $"Linked at {DisplayFormatters.FormatLinkSpeed(now)}"
                + (since is DateTime t ? $" since {text.Time(t)}" : "")
                + $", down from its usual {DisplayFormatters.FormatLinkSpeed(usual)}"
                + (alternates ? ", and the speed has changed more than once." : ".")
                + " Check the cable, or force the port's speed.",
            Chips = [alternates ? "Alternating speed" : "Speed downshift", ImpactChip(ev.Link)],
            Points = 25,
            Onset = since,
            Status = LinkStatus.SpeedDegraded,
        };
    }

    // ---- 3 Duplex mismatch ----
    internal static LinkFinding? DuplexMismatch(LinkEvidence ev)
    {
        var half = ev.Link.FullDuplex == false;
        var late = (ev.Upstream.LateCollisions ?? 0) + (ev.Downstream.LateCollisions ?? 0);
        if (!half && late <= 0) return null;
        // Half duplex on an access port with many MACs is the hidden-hub detector's.
        if (ev.Link.Kind == LinkKind.Access && (ev.Link.UpstreamPort.MacTableCount ?? 0) >= 4) return null;
        return new LinkFinding
        {
            Detector = Detectors.DuplexMismatch,
            LinkId = ev.Link.Id,
            Severity = ev.Link.IsFabric ? FindingSeverity.Warning : FindingSeverity.Info,
            Class = "Link Speed Degraded",
            Title = Title("Link Speed Degraded", ev.Link),
            Body = half
                ? $"Half duplex{(late > 0 ? $", with {late:0} late collisions" : "")}. One end is forced and the other is on auto: set both the same."
                : $"{late:0} late collisions: the far end is likely at half duplex.",
            Chips = ["Duplex mismatch", ImpactChip(ev.Link)],
            Points = 40,
            Status = LinkStatus.SpeedDegraded,
        };
    }

    // ---- 4 / 5 Errors on a stable link, and which end receives them ----
    internal static LinkFinding? StableLinkErrors(LinkEvidence ev, IReadOnlyList<LinkIncident> incidents, FindingText text)
    {
        var bursts = incidents.Where(i => i.Kind == IncidentKind.ErrorBurst && !i.IsExplained).ToList();
        var errors = bursts.Sum(i => i.Count);
        var days = Math.Max(1.0 / 24, (ev.To - ev.From).TotalDays);
        var perDay = errors / days;
        if (perDay <= ErrorsPerDayFloor) return null;
        var asym = ErrorAsymmetry(ev);
        var packets = (ev.Upstream.RxPackets ?? 0) + (ev.Downstream.RxPackets ?? 0);
        double? perMillion = packets > 0 ? errors / packets * 1e6 : null;
        // lanh-31 carries the asymmetry sentence.
        var body = $"{errors:0} receive errors in {text.Range}"
            + (perMillion is double pm ? $" ({pm:0.###} per million packets)" : "")
            + (asym is { } a ? $", all at {a.End}" : "")
            + ". Check the cable, terminations, and keystones.";
        return new LinkFinding
        {
            Detector = asym != null ? Detectors.ErrorAsymmetry : Detectors.StableLinkErrors,
            LinkId = ev.Link.Id,
            Severity = ev.Link.IsFabric ? FindingSeverity.Warning : FindingSeverity.Info,
            Class = "Link Errors",
            Title = Title("Link Errors", ev.Link),
            Body = body,
            Chips = ["CRC errors", ImpactChip(ev.Link)],
            Points = ScoreCurve.Interpolate(perDay, (ErrorsPerDayFloor, 0), (50, 20), (500, 45), (5000, 70)),
            Onset = bursts.Count > 0 ? bursts.Min(i => i.Start) : null,
            Status = LinkStatus.Errors,
        };
    }

    // ---- 6 Loss bursts with no counter evidence ----
    internal static LinkFinding? LossWithoutEvidence(LinkEvidence ev, IReadOnlyList<LinkIncident> incidents, FindingText text)
    {
        // Probes to a switch never score: their control plane answers ICMP with fake loss.
        if (ev.Link.Downstream.IsSwitch) return null;
        var bursts = incidents.Where(i => i.Kind == IncidentKind.LossBurst && !i.IsExplained).ToList();
        if (bursts.Count < FlappingDropsFabric) return null;
        // Loss the parent shares is upstream's; the shared link carries it instead.
        if (ev.ParentProbeLossPct is double parent && ev.ProbeLossPct is double own && parent >= own * 0.5) return null;
        return new LinkFinding
        {
            Detector = Detectors.LossWithoutEvidence,
            LinkId = ev.Link.Id,
            Severity = FindingSeverity.Info,
            Class = "Packet Loss",
            Title = Title("Packet Loss", ev.Link),
            Body = $"{bursts.Count} bursts of total probe loss in {text.Range}, with no drop or error recorded. A drop of a few seconds can fall between polls.",
            Chips = ["Loss bursts", ImpactChip(ev.Link)],
            Points = ScoreCurve.Interpolate(bursts.Count, (3, 5), (10, 15), (30, 30)),
            Onset = bursts.Min(i => i.Start),
        };
    }

    /// <summary>
    /// Transmit discards per million packets that are normal for a link's class, from the 2026-11-22
    /// calibration: gateway ports egressing to APs ran up to ~515 on healthy links, other gateway ports
    /// up to ~27 (p90), and switch ports 0.
    /// </summary>
    internal static double NormalDiscardsPerMillion(LinkRecord link) =>
        link.Upstream.IsGateway ? (link.Downstream.IsAccessPoint ? 600 : 100) : 10;

    /// <summary>
    /// Discards worth reporting: above the class's calibrated normal and well above the link's own
    /// baseline. Returns the rate per million when they are, else null.
    /// </summary>
    internal static double? UnusualDiscards(LinkEvidence ev)
    {
        var total = (ev.Upstream.TxDiscards ?? 0) + (ev.Downstream.TxDiscards ?? 0);
        var packets = (ev.Upstream.TxPackets ?? 0) + (ev.Downstream.TxPackets ?? 0);
        if (total < 100 || packets <= 0) return null;
        var rate = total / packets * 1e6;
        if (rate <= NormalDiscardsPerMillion(ev.Link)) return null;
        var baseDiscards = ev.BaselineHours.Sum(h => h.Discards ?? 0);
        var basePackets = ev.BaselineHours.Sum(h => h.TxPackets ?? 0);
        if (basePackets > 0 && rate <= baseDiscards / basePackets * 1e6 * 3) return null;
        return rate;
    }

    // ---- 7 Discards / saturation (load, not a link fault) ----
    internal static LinkFinding? Discards(LinkEvidence ev, IReadOnlyList<LinkIncident> incidents)
    {
        var up = ev.Upstream.TxDiscards ?? 0;
        var down = ev.Downstream.TxDiscards ?? 0;
        var total = up + down;
        if (UnusualDiscards(ev) == null) return null;
        var end = up >= down ? ev.Link.Upstream.DeviceName : ev.Link.Downstream.DeviceName;
        var flow = FlowControlOn(ev.Link);
        var busy = ev.Hours.Any(h => Utilization(ev.Link, h) >= UtilizationShare);
        var cause = flow ? "Flow Control pause frames" : busy ? "heavy load" : "bursts of traffic";
        var dropped = incidents.Any(i => !i.IsExplained && i.Kind is IncidentKind.Drop or IncidentKind.Relink);
        // lanh-12 / lanh-13.
        return new LinkFinding
        {
            Detector = Detectors.Discards,
            LinkId = ev.Link.Id,
            Severity = FindingSeverity.Info,
            Class = "Discards Under Load",
            Title = Title("Discards Under Load", ev.Link),
            Body = $"{total:0} transmit discards on the {end} port, during {cause}." + (dropped ? "" : " Load, not a link fault: the link never dropped."),
            Chips = [flow ? "Flow Control" : "Transmit discards", ImpactChip(ev.Link)],
            Points = 0,
            Status = busy ? LinkStatus.Busy : null,
        };
    }

    internal static bool FlowControlOn(LinkRecord link) =>
        link.UpstreamPort.FlowControlRx == true || link.UpstreamPort.FlowControlTx == true
        || link.DownstreamPort?.FlowControlRx == true || link.DownstreamPort?.FlowControlTx == true;

    // ---- 25 Microbursts ----
    internal static LinkFinding? Microbursts(LinkEvidence ev)
    {
        var discards = (ev.Upstream.TxDiscards ?? 0) + (ev.Downstream.TxDiscards ?? 0);
        if (UnusualDiscards(ev) == null || ev.Hours.Count == 0) return null;
        var peak = ev.Hours.Select(h => Utilization(ev.Link, h)).DefaultIfEmpty(0).Max();
        if (peak >= 0.2) return null;
        return new LinkFinding
        {
            Detector = Detectors.Microbursts,
            LinkId = ev.Link.Id,
            Severity = FindingSeverity.Info,
            Class = "Microbursts",
            Title = Title("Microbursts", ev.Link),
            Body = $"{discards:0} packets discarded while the link averaged under {peak * 100:0}% of its speed: short bursts are overflowing the port's buffer."
                + (FlowControlOn(ev.Link) ? " Flow Control is on for this port." : ""),
            Chips = ["Microbursts"],
        };
    }

    /// <summary>
    /// The hour's mean utilization, the busier direction, as a share of the resolved link speed. The
    /// mean, not the peak: one sample is not sustained load. An hour whose rate exceeds the link's
    /// speed is a counter anomaly (a relink or reset; gateway SNMP rates ride the 10G conduit, so the
    /// collector's own speed check lets them through) and counts as 0.
    /// </summary>
    internal static double Utilization(LinkRecord link, LinkHourSample h)
    {
        if (link.SpeedMbps is not int mbps || mbps <= 0) return 0;
        var capacity = mbps * 1e6;
        if (Math.Max(h.MaxInBps ?? 0, h.MaxOutBps ?? 0) > capacity * 1.05) return 0;
        return Math.Max(h.MeanInBps ?? 0, h.MeanOutBps ?? 0) / capacity;
    }

    // ---- 9 L2 instability: storms (loops are the site detector) ----
    internal static LinkFinding? Storm(LinkEvidence ev, FindingText text)
    {
        if (ev.Hours.Count == 0) return null;
        var baseline = Percentile(ev.BaselineHours.Select(h => h.BroadcastPps).OfType<double>().ToList(), 0.95) ?? 0;
        var stormHours = ev.Hours.Where(h => h.BroadcastPps is double b && b >= StormFloorPps && b >= Math.Max(1, baseline) * StormBaselineFactor).ToList();
        if (stormHours.Count == 0) return null;
        var peak = stormHours.Max(h => h.BroadcastPps!.Value);
        // Named only during an actual storm: storm control is off by default, never a finding on its own.
        var storm = ev.Link.UpstreamPort.StormControlBroadcast == true;
        return new LinkFinding
        {
            Detector = Detectors.L2Instability,
            LinkId = ev.Link.Id,
            Severity = FindingSeverity.Warning,
            Class = "Broadcast Storm",
            Title = Title("Broadcast Storm", ev.Link),
            Body = $"Broadcast hit {peak:0} packets per second, against a normal of {baseline:0.#}, starting {text.Time(stormHours[0].Hour)}."
                + (storm ? " Storm control is on for this port." : " Storm control is off for this port."),
            Chips = ["Broadcast", ImpactChip(ev.Link)],
            Points = 40,
            Onset = stormHours[0].Hour,
            Status = LinkStatus.Storm,
        };
    }

    // ---- 11 Optics ----
    internal static LinkFinding? Optics(LinkEvidence ev)
    {
        var drift = ev.SfpRxPowerDbm is double now && ev.SfpRxPowerBestDbm is double best ? best - now : 0;
        var hot = ev.SfpTemperatureC is >= 75;
        var low = ev.SfpRxPowerDbm is <= -25;
        if (drift < 3 && !hot && !low) return null;
        var parts = new List<string>();
        if (drift >= 3) parts.Add($"SFP receive power is {drift:0.#} dB below its best this week ({ev.SfpRxPowerDbm:0.#} dBm).");
        else if (low) parts.Add($"SFP receive power is {ev.SfpRxPowerDbm:0.#} dBm.");
        if (hot) parts.Add($"The module runs at {ev.SfpTemperatureC:0} °C.");
        parts.Add("Clean the connectors or check the fiber's bends.");
        return new LinkFinding
        {
            Detector = Detectors.Optics,
            LinkId = ev.Link.Id,
            Severity = FindingSeverity.Warning,
            Class = "Optics",
            Title = Title("Optics", ev.Link),
            Body = string.Join(" ", parts),
            Chips = ["SFP"],
            Points = 15,
        };
    }

    /// <summary>
    /// A reading is the parent at its hour's end, so a change happened in the later reading's hour, and the child's
    /// re-association is a relink in that hour. When every drop and relink there is
    /// explained (a rollout or restart of the child or its parent) the change is that event's, not the backhaul's.
    /// Hours with no relink at all stay counted.
    /// </summary>
    internal static bool ParentChangeExplained(IReadOnlyList<LinkIncident>? incidents, DateTime from, DateTime to)
    {
        if (incidents == null) return false;
        var relinks = incidents.Where(i => i.Kind is IncidentKind.Drop or IncidentKind.Relink && i.Start < to && i.End >= from).ToList();
        return relinks.Count > 0 && relinks.All(i => i.IsExplained);
    }

    // ---- 12 / 28 Mesh uplink (signal, rate, parent changes, MLO, depth) ----
    internal static IEnumerable<LinkFinding> Mesh(LinkEvidence ev, IReadOnlyList<LinkIncident>? incidents = null)
    {
        if (!ev.Link.IsMesh) yield break;
        var baseSignal = Median(ev.MeshBaseline.Select(m => m.SignalDbm).OfType<int>().Select(v => (double)v));
        var nowSignal = Median(ev.Mesh.Select(m => m.SignalDbm).OfType<int>().Select(v => (double)v));
        foreach (var f in MeshQuality(ev, nowSignal)) yield return f;
        if (baseSignal is double b && nowSignal is double n && b - n >= MeshSignalDropDb)
            yield return MeshFinding(ev, "Mesh Signal Drop", $"Backhaul signal is {n:0} dBm, {b - n:0} dB weaker than its normal {b:0} dBm.", 15);

        var baseRate = Median(ev.MeshBaseline.Select(m => m.TxRateKbps).OfType<long>().Select(v => (double)v));
        var nowRate = Median(ev.Mesh.Select(m => m.TxRateKbps).OfType<long>().Select(v => (double)v));
        if (baseRate is double br && nowRate is double nr && br > 0 && nr < br * MeshRateShare)
            yield return MeshFinding(ev, "Mesh Rate Drop", $"Backhaul PHY rate is {nr / 1000:0} Mbps, less than half of its normal {br / 1000:0} Mbps.", 15);

        var readings = ev.Mesh.Where(m => !string.IsNullOrEmpty(m.ParentMac)).ToList();
        var changes = readings.Zip(readings.Skip(1)).Count(p => p.First.ParentMac != p.Second.ParentMac
            && !ParentChangeExplained(incidents, p.Second.At, p.Second.At.AddHours(1)));
        if (changes > 0)
            yield return MeshFinding(ev, "Mesh Parent Change", $"The mesh backhaul switched parent APs {changes} {(changes == 1 ? "time" : "times")} in this window.", 10);

        var usualMembers = ev.MeshBaseline.Select(m => m.MloMembers).OfType<int>().DefaultIfEmpty(0).Max();
        var nowMembers = ev.Mesh.Select(m => m.MloMembers).OfType<int>().LastOrDefault();
        if (usualMembers > 1 && nowMembers > 0 && nowMembers < usualMembers)
            yield return MeshFinding(ev, "MLO Member Lost", $"The MLO backhaul is running on {nowMembers} of its normal {usualMembers} links.", 15);

        if (ev.Link.Mlo.Count > 1)
        {
            var signals = ev.Link.Mlo.Select(m => m.SignalDbm).OfType<int>().ToList();
            if (signals.Count > 1 && signals.Max() - signals.Min() >= 15)
                yield return MeshFinding(ev, "MLO Imbalance", $"One MLO link's signal is {signals.Max() - signals.Min()} dB weaker than the other's.", 0);
        }
        if (ev.Link.UplinkDepth is > 1)
            yield return MeshFinding(ev, "Mesh Depth", $"This AP is {ev.Link.UplinkDepth} mesh hops from the wired network. Each hop shares airtime, so throughput drops with every hop.", 0, Detectors.MeshDepth);
    }

    /// <summary>The backhaul's own quality: weak signal for its band, probe loss and RTT beyond its parent's, and retries.</summary>
    internal static IEnumerable<LinkFinding> MeshQuality(LinkEvidence ev, double? nowSignal)
    {
        var parent = ev.ParentName ?? "its parent";
        // Each end on its own band: the stored worst end when there is one, else the child's own reading.
        var (signalDbm, signalRadio, atParent) = ev.MeshWorstSignalDbm is int w && ev.MeshWorstSignalRadio is { Length: > 0 } wr
            ? (w, wr, ev.MeshWorstSignalAtParent)
            : nowSignal is double sig && ev.Link.MeshRadio is { Length: > 0 } radio ? ((int)Math.Round(sig), radio, false) : (0, "", false);
        if (signalRadio.Length > 0)
        {
            var band = SignalBand(signalRadio);
            if (NetworkOptimizer.WiFi.Helpers.SignalClassification.IsWeakSignal(signalDbm, band))
            {
                var poor = NetworkOptimizer.WiFi.Helpers.SignalClassification.IsCriticalSignal(signalDbm, band);
                var who = atParent ? $"{parent} hears this device" : $"This device hears {parent}";
                yield return MeshFinding(ev, "Weak Mesh Signal",
                    $"{who} at {signalDbm} dBm on {BandLabel(band)}, {(poor ? "poor" : "weak")} for the band.", poor ? 20 : 10);
            }
        }

        if (ev.ProbeLossPct is double loss && loss - (ev.ParentProbeLossPct ?? 0) >= MeshLossExcessPct)
            yield return MeshFinding(ev, "Mesh Backhaul Loss", ev.ParentProbeLossPct is double pl
                ? $"Fabric probes through the backhaul lose {loss:0.##}%, against {pl:0.##}% to {parent}."
                : $"Fabric probes through the backhaul lose {loss:0.##}%.", loss >= 2 ? 25 : 15);

        if (ev.RttGapMs is double gap && ev.RttGapBaselineMs is double normal && gap >= normal + Math.Max(MeshRttRiseMs, normal))
            yield return MeshFinding(ev, "Mesh Backhaul Latency", ev.ParentName != null
                ? $"Probes through the backhaul take {gap:0.0} ms longer than to {parent}, against its normal {normal:0.0} ms."
                : $"Probes through the backhaul take {gap:0.0} ms, against its normal {normal:0.0} ms.", 10);

        // Demand: hours whose mean traffic (both ways, airtime is shared) passes half of what the backhaul can carry.
        if (ev.Link.MeshTxRateKbps is long phyKbps && phyKbps > 0)
        {
            var usable = phyKbps * 1000 * NetworkOptimizer.UniFi.Models.PathAnalysisResult.MeshBackhaulOverheadFactor;
            var busy = ev.Hours.Where(h => (h.MeanInBps ?? 0) + (h.MeanOutBps ?? 0) >= usable * MeshDemandShare).ToList();
            if (busy.Count >= MeshDemandHours)
            {
                var peak = busy.Max(h => (h.MeanInBps ?? 0) + (h.MeanOutBps ?? 0));
                yield return MeshFinding(ev, "Mesh Backhaul Saturated",
                    $"Traffic through the backhaul averaged {DisplayFormatters.FormatRate(peak)} in its busiest hour, {peak / usable * 100:0}% of the about {DisplayFormatters.FormatRate(usable)} it can carry at {DisplayFormatters.FormatLinkSpeed((int)(phyKbps / 1000))} PHY. {busy.Count} hours passed half.", 10);
            }
        }

        // Both directions; the worse one past its threshold is the finding.
        static bool Over(double? pct, double? normal) => pct is double p && p >= MeshRetryPct && (normal is not double n || p >= 2 * n);
        var down = Over(ev.MeshRetryPct, ev.MeshRetryPctBaseline);
        var up = Over(ev.MeshChildRetryPct, ev.MeshChildRetryPctBaseline);
        if (down || up)
        {
            var childSide = up && (!down || ev.MeshChildRetryPct > ev.MeshRetryPct);
            var (pct, retryNormal, dropped) = childSide
                ? (ev.MeshChildRetryPct!.Value, ev.MeshChildRetryPctBaseline, ev.MeshChildTxDropped)
                : (ev.MeshRetryPct!.Value, ev.MeshRetryPctBaseline, ev.MeshTxDropped);
            yield return MeshFinding(ev, "Mesh Retries",
                (childSide ? $"This device retries {pct:0}% of its transmissions to {parent}" : $"{parent} retries {pct:0}% of its transmissions to this device")
                + (retryNormal is double b ? $", against its normal {b:0}%." : ".")
                + (dropped > 0 ? $" {dropped:N0} frames were dropped after retries." : ""), 10);
        }
    }

    /// <summary>Channel moves that dropped a backhaul in the window before it is worth a finding.</summary>
    public const int BackhaulMovesToReport = 3;

    /// <summary>5 GHz channels 52 to 144 are DFS: radar there forces the AP off the channel.</summary>
    internal static bool IsDfsChannel(int channel) => channel is >= 52 and <= 144;

    /// <summary>"Channel change (116 to 36, off DFS)" from a "116>36" detail.</summary>
    public static string ChannelMoveText(string? detail)
    {
        var parts = (detail ?? "").Split('>');
        if (parts.Length != 2) return "Channel change";
        var dfs = int.TryParse(parts[0], out var from) && IsDfsChannel(from);
        return $"Channel change ({parts[0]} to {parts[1]}{(dfs ? ", off DFS" : "")})";
    }

    /// <summary>A parent moving the backhaul's channel again and again, each move dropping the link (spec: backhaul channel events).</summary>
    internal static LinkFinding? BackhaulChannelMoves(LinkEvidence ev, IReadOnlyList<LinkIncident> incidents, FindingText text)
    {
        if (!ev.Link.IsMesh) return null;
        var moves = incidents.Where(i => i.Explained == ExplainReason.BackhaulChannelChange).ToList();
        if (moves.Count < BackhaulMovesToReport) return null;
        var dfs = moves.Count(i => (i.ExplainedDetail ?? "").Split('>') is [var f, _] && int.TryParse(f, out var c) && IsDfsChannel(c));
        return MeshFinding(ev, "Backhaul Channel Changes",
            $"{ev.ParentName ?? "Its parent"} moved channel {moves.Count} times in {text.Range}, and the backhaul rejoined each time."
            + (dfs > 0 ? $" {dfs} of them left a DFS channel; a non-DFS channel for the backhaul avoids those." : ""), 0);
    }

    internal static NetworkOptimizer.WiFi.Models.RadioBand SignalBand(string radio) => radio switch
    {
        "ng" => NetworkOptimizer.WiFi.Models.RadioBand.Band2_4GHz,
        "6e" => NetworkOptimizer.WiFi.Models.RadioBand.Band6GHz,
        _ => NetworkOptimizer.WiFi.Models.RadioBand.Band5GHz,
    };

    private static string BandLabel(NetworkOptimizer.WiFi.Models.RadioBand band) => band switch
    {
        NetworkOptimizer.WiFi.Models.RadioBand.Band2_4GHz => "2.4 GHz",
        NetworkOptimizer.WiFi.Models.RadioBand.Band6GHz => "6 GHz",
        _ => "5 GHz",
    };

    private static LinkFinding MeshFinding(LinkEvidence ev, string cls, string body, double points, int detector = Detectors.MeshUplink) => new()
    {
        Detector = detector,
        LinkId = ev.Link.Id,
        Severity = points > 0 ? FindingSeverity.Warning : FindingSeverity.Info,
        Class = cls,
        Title = Title(cls, ev.Link),
        Body = body,
        Chips = [ev.Link.Kind == LinkKind.FabricMeshMlo ? "Fabric · Mesh MLO" : "Fabric · Mesh", ImpactChip(ev.Link)],
        Points = points,
    };

    // ---- 13 Out-of-character utilization ----
    internal static LinkFinding? UnusualUtilization(LinkEvidence ev, FindingText text)
    {
        if (!ev.Link.IsFabric || ev.Link.SpeedMbps is not int || ev.BaselineHours.Count < 24) return null;
        var p95 = Percentile(ev.BaselineHours.Select(h => Utilization(ev.Link, h)).ToList(), 0.95) ?? 0;
        var run = 0;
        DateTime? start = null;
        foreach (var h in ev.Hours)
        {
            var u = Utilization(ev.Link, h);
            if (u >= UtilizationShare && u >= p95 * 1.25) { run++; start ??= h.Hour; }
            else { run = 0; start = null; }
            if (run >= UtilizationSustainHours)
                return new LinkFinding
                {
                    Detector = Detectors.UnusualUtilization,
                    LinkId = ev.Link.Id,
                    Severity = FindingSeverity.Info,
                    Class = "Unusual Link Utilization",
                    Title = Title("Unusual Link Utilization", ev.Link),
                    Body = $"Above {u * 100:0}% of {DisplayFormatters.FormatLinkSpeed(ev.Link.SpeedMbps!.Value)} since {text.Time(start!.Value)}, where its busiest hours normally utilize {p95 * 100:0}% of it.",
                    Chips = ["Load", ImpactChip(ev.Link)],
                    Onset = start,
                    Status = LinkStatus.Busy,
                };
        }
        return null;
    }

    // ---- 14 Link below capability ----
    internal static LinkFinding? BelowCapability(LinkEvidence ev)
    {
        if (ev.Link.CapabilityMbps is not int cap || ev.Link.SpeedMbps is not int now || now >= cap || ev.Link.Forced) return null;
        return new LinkFinding
        {
            Detector = Detectors.BelowCapability,
            LinkId = ev.Link.Id,
            Severity = FindingSeverity.Info,
            Class = "Below Capability",
            Title = Title("Below Capability", ev.Link),
            Body = $"Both ends support {DisplayFormatters.FormatLinkSpeed(cap)}, but the link runs at {DisplayFormatters.FormatLinkSpeed(now)}. Check the cable, keystones, and patch panel.",
            Chips = ["Below port capability", ImpactChip(ev.Link)],
            Points = 10,
            Status = LinkStatus.SpeedDegraded,
        };
    }

    // ---- 16 Access port degraded vs its own history (sleep is not degradation) ----
    internal static LinkFinding? AccessDegraded(LinkEvidence ev)
    {
        if (ev.Link.Kind != LinkKind.Access || ev.UsualSpeedMbps is not int usual || ev.Link.SpeedMbps is not int now || now >= usual) return null;
        // A drop to 10/100 with idle traffic is a sleep cycle: only hours spent at the lower speed, and
        // busy there, count. A busy hour at full speed is the client awake, not degraded.
        var speeds = ev.Speeds.OrderBy(s => s.At).ToList();
        int? SpeedIn(DateTime hour)
        {
            var inHour = speeds.Where(s => s.At >= hour && s.At < hour.AddHours(1)).Select(s => s.SpeedMbps).ToList();
            return inHour.Count > 0 ? inHour.Max() : speeds.LastOrDefault(s => s.At < hour)?.SpeedMbps;
        }
        var busyAtLowSpeed = ev.Hours.Count(h => SpeedIn(h.Hour) is int s && s < usual
            && ((h.MeanInBps ?? 0) > 1_000_000 || (h.MeanOutBps ?? 0) > 1_000_000)) >= 2;
        if (!busyAtLowSpeed) return null;
        return new LinkFinding
        {
            Detector = Detectors.AccessDegraded,
            LinkId = ev.Link.Id,
            Severity = FindingSeverity.Info,
            Class = "Link Speed Degraded",
            Title = Title("Link Speed Degraded", ev.Link),
            Body = $"The client links at {DisplayFormatters.FormatLinkSpeed(now)}, down from {DisplayFormatters.FormatLinkSpeed(usual)}. Check the cable and its network adapter.",
            Chips = ["Access"],
            Points = 15,
            Status = LinkStatus.SpeedDegraded,
        };
    }

    // ---- 17 Hidden unmanaged switch or hub ----
    internal static LinkFinding? HiddenSwitch(LinkEvidence ev)
    {
        if (ev.Link.Kind != LinkKind.Access || ev.Link.ServerOnPort) return null;
        var macs = ev.Link.UpstreamPort.MacTableCount ?? 0;
        var half = ev.Link.FullDuplex == false;
        if (macs < 4 && !(half && macs >= 2)) return null;
        return new LinkFinding
        {
            Detector = Detectors.HiddenSwitch,
            LinkId = ev.Link.Id,
            Severity = FindingSeverity.Info,
            Class = "Unmanaged Switch",
            Title = Title("Unmanaged Switch", ev.Link),
            Body = half
                ? $"Half duplex with {macs} MAC addresses: a hub or an old unmanaged switch is behind it."
                : $"{macs} MAC addresses on one access port: there's an unmanaged switch behind it.",
            Chips = ["Access"],
        };
    }

    // ---- 8 / 26 PoE: powered device power cycling (link drop + reboot together) ----
    internal static LinkFinding? PoeCycling(LinkEvidence ev, IReadOnlyList<LinkIncident> incidents)
    {
        // Only reboots the device classified as a power loss: every reboot drops the link, so a crash or a
        // commanded restart would otherwise read as a power fault.
        var cycles = (int)incidents.Where(i => i.Kind is IncidentKind.Drop or IncidentKind.Relink
            && i.Explained == ExplainReason.DeviceRebooted && i.ExplainedDetail == nameof(Monitoring.RebootReason.RebootCategory.PowerLoss)).Sum(i => i.Count);
        var zeroDraw = ev.PoeDraw.Any(p => p.Watts <= 0.1) && ev.PoeDraw.Any(p => p.Watts > 1);
        if (cycles < 2 && !(cycles >= 1 && zeroDraw)) return null;
        // The port supplies PoE when it is not off and has drawn power: then the switch is the power source.
        var portPowers = !string.Equals(ev.Link.UpstreamPort.PoeMode ?? "off", "off", StringComparison.OrdinalIgnoreCase)
            && (ev.Link.UpstreamPort.PoePowerW > 0 || ev.PoeDraw.Any(p => p.Watts > 1));
        return new LinkFinding
        {
            Detector = Detectors.PoeHealth,
            LinkId = ev.Link.Id,
            Severity = FindingSeverity.Warning,
            Class = "PoE Power Cycling",
            Title = Title("PoE Power Cycling", ev.Link),
            Body = $"{ev.Link.Downstream.DeviceName} lost power {cycles} {(cycles == 1 ? "time" : "times")}"
                + (portPowers && zeroDraw ? ", and its PoE draw dropped to 0 W." : ".")
                + (portPowers
                    ? $" Check {ev.Link.Upstream.DeviceName}'s PoE budget and the cable."
                    : " Check its injector or power supply, and the cable."),
            Chips = ["PoE", ImpactChip(ev.Link)],
            Points = 25,
        };
    }

    // ---- Speed-specific flapping: drops only at the top speed ----
    internal static LinkFinding? SpeedSpecificFlapping(LinkEvidence ev, IReadOnlyList<LinkIncident> incidents)
    {
        var drops = incidents.Where(i => !i.IsExplained && i.Kind == IncidentKind.Drop).ToList();
        if (drops.Count < FlappingDropsFabric || ev.Link.SpeedMbps is not int now || now < 2500) return null;
        var lowerSpeeds = ev.Speeds.Where(s => s.SpeedMbps < now).ToList();
        if (lowerSpeeds.Count == 0) return null;
        var lowerSince = lowerSpeeds.Min(s => s.At);
        var lowerUntil = lowerSpeeds.Max(s => s.At);
        // The drops happen at the top speed and not while it ran slower.
        if (drops.Any(d => d.Start >= lowerSince && d.Start <= lowerUntil)) return null;
        var lower = now >= 10000 ? 5000 : now >= 5000 ? 2500 : 1000;
        return new LinkFinding
        {
            Detector = Detectors.LinkDrops,
            LinkId = ev.Link.Id,
            Severity = FindingSeverity.Info,
            Class = "Speed-Specific Flapping",
            Title = Title("Speed-Specific Flapping", ev.Link),
            Body = $"The link only drops at {DisplayFormatters.FormatLinkSpeed(now)}. Set the port to {DisplayFormatters.FormatLinkSpeed(lower)} and see if the drops stop.",
            Chips = [$"Drops at {DisplayFormatters.FormatLinkSpeedShort(now)} only"],
        };
    }

    // ---- Speed keeps dropping: Config Optimizer's WAN check, on a wired fabric link ----
    internal static LinkFinding? SpeedKeepsDropping(LinkEvidence ev, LinkFinding? downshiftNow)
    {
        // A link below its usual speed now is the speed downshift finding's; this one is the history of it.
        if (downshiftNow != null || ev.Link.Kind != LinkKind.FabricWired) return null;
        if (ev.SpeedHistory is not { } h || h.DegradedEpisodes < NetworkOptimizer.Diagnostics.Analyzers.PerformanceAnalyzer.WanLinkSpeedEpisodesToRecommend) return null;
        var days = Math.Max(1, (int)Math.Round(h.Lookback.TotalDays));
        var hours = Math.Max(1, (int)Math.Round(h.DegradedTime.TotalHours));
        var body = $"Dropped below its usual {DisplayFormatters.FormatLinkSpeed(h.UsualMbps)} {h.DegradedEpisodes} times in the last {(days == 1 ? "day" : $"{days} days")}, "
            + $"as low as {DisplayFormatters.FormatLinkSpeed(h.LowestMbps)}, for about {hours} {(hours == 1 ? "hour" : "hours")} in total. "
            + (ev.Link.Forced
                ? "The port's speed is already set manually: check or replace the cable."
                : $"Set the port's speed to {DisplayFormatters.FormatLinkSpeed(h.UsualMbps)} instead of auto-negotiation. If it still drops, replace the cable.");
        return new LinkFinding
        {
            Detector = Detectors.SpeedKeepsDropping,
            LinkId = ev.Link.Id,
            Severity = FindingSeverity.Info,
            Class = "Link Speed Keeps Dropping",
            Title = Title("Link Speed Keeps Dropping", ev.Link),
            Body = body,
            Chips = ["Speed history", ImpactChip(ev.Link)],
        };
    }

    // ---- Speed tests across the link vs the norm for their path shape ----
    internal static LinkFinding? SpeedTestBelowNorm(LinkEvidence ev)
    {
        var tests = ev.SpeedTests.Where(t => !t.DuringIncident && t.NormDownMbps is > 0).ToList();
        if (tests.Count < 2) return null;
        var ratio = tests.Average(t => Math.Max(t.DownMbps / t.NormDownMbps!.Value, t.NormUpMbps is > 0 ? t.UpMbps / t.NormUpMbps.Value : 0));
        if (ratio >= 0.6) return null;
        return new LinkFinding
        {
            Detector = Detectors.SlowSpeedTests,
            LinkId = ev.Link.Id,
            Severity = FindingSeverity.Info,
            Class = "Slow Speed Tests",
            Title = Title("Slow Speed Tests", ev.Link),
            Body = $"{tests.Count} speed tests across this link averaged {ratio * 100:0}% of what similar paths normally get.",
            Chips = ["Speed tests"],
        };
    }

    internal static double? Median(IEnumerable<double> values)
    {
        var list = values.ToList();
        return list.Count == 0 ? null : ChangeControl.SiteHealth.SiteHealthJob.Median(list);
    }

    internal static double? Percentile(IReadOnlyList<double> values, double p)
    {
        if (values.Count == 0) return null;
        var sorted = values.OrderBy(v => v).ToList();
        var idx = Math.Clamp((int)Math.Ceiling(p * sorted.Count) - 1, 0, sorted.Count - 1);
        return sorted[idx];
    }
}
