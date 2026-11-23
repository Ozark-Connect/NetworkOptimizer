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

    /// <summary>Runs every per-link detector.</summary>
    public static List<LinkFinding> Run(LinkEvidence ev, IReadOnlyList<LinkIncident> incidents, FindingText text)
    {
        var findings = new List<LinkFinding>();
        void Add(LinkFinding? f) { if (f != null) findings.Add(f); }
        Add(LinkDrops(ev, incidents, text));
        Add(SpeedDownshift(ev, text));
        Add(DuplexMismatch(ev));
        Add(StableLinkErrors(ev, incidents, text));
        Add(LossWithoutEvidence(ev, incidents, text));
        Add(Discards(ev, incidents));
        Add(Microbursts(ev));
        Add(Storm(ev, text));
        Add(Optics(ev));
        foreach (var f in Mesh(ev)) Add(f);
        Add(UnusualUtilization(ev, text));
        Add(BelowCapability(ev));
        Add(AccessDegraded(ev));
        Add(HiddenSwitch(ev));
        Add(PoeCycling(ev, incidents));
        Add(SpeedSpecificFlapping(ev, incidents));
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
        var onset = drops.Min(i => i.Start);

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

        var chips = new List<string>();
        if (sameSpeed && seconds is <= 15) chips.Add("Drop and retrain");
        chips.Add(ImpactChip(ev.Link));

        var steps = new List<string>();
        if (ev.Link.Kind == LinkKind.FabricWired && ev.Link.Upstream.PortIdx is int p)
            steps.Add(PortSwapStep(ev.Link, p));

        var perDay = count / Math.Max(1.0 / 24, (ev.To - ev.From).TotalDays);
        var pts = ScoreCurve.Interpolate(count, (0, 0), (3, 15), (10, 35), (30, 60), (100, 85))
                  * ScoreCurve.Interpolate(seconds ?? 5, (1, 70), (5, 100), (60, 130)) / 100;
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
        };
    }

    /// <summary>lanh-39: swap the upstream port with a neighbor to tell the port from the device.</summary>
    internal static string PortSwapStep(LinkRecord link, int port)
    {
        var other = port > 1 ? port - 1 : port + 1;
        var kind = link.Downstream.IsAccessPoint ? "AP" : link.Downstream.IsSwitch ? "switch" : "device";
        return $"Swap Port {port} with Port {other} ({link.Upstream.DeviceName}). If the drops stay on Port {port}, it is the {link.Upstream.DeviceName} port; if they follow the {kind}, it is the {link.Downstream.DeviceName}.";
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
            Body = $"Linked at {DisplayFormatters.FormatLinkSpeed(now)}, below its usual {DisplayFormatters.FormatLinkSpeed(usual)}"
                + (since is DateTime t ? $" since {text.Time(t)}" : "")
                + (alternates ? ", and the speed has changed more than once." : ".")
                + " A pair fault or auto-negotiation trouble does this; forcing the speed is the usual fix.",
            Chips = [alternates ? "Alternating speed" : "Downshift", ImpactChip(ev.Link)],
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
                ? $"The link runs at half duplex{(late > 0 ? $", with {late:0} late collisions" : "")}. One end forced and the other on auto does this: set both the same."
                : $"{late:0} late collisions on a full-duplex link: the far end is likely at half duplex.",
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
        var body = $"{errors:0} receive errors in {text.Range} on a link that never dropped"
            + (perMillion is double pm ? $" ({pm:0.###} per million packets)" : "") + "."
            + (asym is { } a ? $" Errors only on the {a.End}'s receive side: the {a.Other}-to-{a.End} direction is the one damaged." : "")
            + " A steady error rate is a marginal channel: cable, termination, keystone, or length.";
        return new LinkFinding
        {
            Detector = asym != null ? Detectors.ErrorAsymmetry : Detectors.StableLinkErrors,
            LinkId = ev.Link.Id,
            Severity = ev.Link.IsFabric ? FindingSeverity.Warning : FindingSeverity.Info,
            Class = "Link Errors",
            Title = Title("Link Errors", ev.Link),
            Body = body,
            Chips = ["Marginal channel", ImpactChip(ev.Link)],
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
            Body = $"{bursts.Count} short bursts of total probe loss in {text.Range} with no drop or error recorded on the link. A drop between counter polls looks like this; so does the device itself pausing.",
            Chips = ["Loss bursts", ImpactChip(ev.Link)],
            Points = ScoreCurve.Interpolate(bursts.Count, (3, 5), (10, 15), (30, 30)),
            Onset = bursts.Min(i => i.Start),
        };
    }

    // ---- 7 Discards / saturation (load, not a link fault) ----
    internal static LinkFinding? Discards(LinkEvidence ev, IReadOnlyList<LinkIncident> incidents)
    {
        var up = ev.Upstream.TxDiscards ?? 0;
        var down = ev.Downstream.TxDiscards ?? 0;
        var total = up + down;
        var packets = (ev.Upstream.TxPackets ?? 0) + (ev.Downstream.TxPackets ?? 0);
        if (total < 100 || (packets > 0 && total / packets < 1e-5)) return null;
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
            Chips = [flow ? "Flow Control" : "Egress queue", ImpactChip(ev.Link)],
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
        if (discards < 100 || ev.Hours.Count == 0) return null;
        var peak = ev.Hours.Select(h => Utilization(ev.Link, h)).DefaultIfEmpty(0).Max();
        if (peak >= 0.2) return null;
        return new LinkFinding
        {
            Detector = Detectors.Microbursts,
            LinkId = ev.Link.Id,
            Severity = FindingSeverity.Info,
            Class = "Microbursts",
            Title = Title("Microbursts", ev.Link),
            Body = $"{discards:0} discards while the link never averaged above {peak * 100:0}% of its speed: buffers, not bandwidth."
                + (FlowControlOn(ev.Link) ? " Flow Control is on for this port." : ""),
            Chips = ["Microbursts"],
        };
    }

    /// <summary>Peak hourly utilization as a share of the resolved link speed (never raw SNMP speed).</summary>
    internal static double Utilization(LinkRecord link, LinkHourSample h)
    {
        if (link.SpeedMbps is not int mbps || mbps <= 0) return 0;
        var peak = Math.Max(h.MaxInBps ?? 0, h.MaxOutBps ?? 0);
        return peak / (mbps * 1e6);
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
            Body = $"Broadcast reached {peak:0} packets per second, against a normal of {baseline:0.#}, starting {text.Time(stormHours[0].Hour)}."
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

    // ---- 12 / 28 Mesh uplink (signal, rate, parent changes, MLO, depth) ----
    internal static IEnumerable<LinkFinding> Mesh(LinkEvidence ev)
    {
        if (!ev.Link.IsMesh) yield break;
        var baseSignal = Median(ev.MeshBaseline.Select(m => m.SignalDbm).OfType<int>().Select(v => (double)v));
        var nowSignal = Median(ev.Mesh.Select(m => m.SignalDbm).OfType<int>().Select(v => (double)v));
        if (baseSignal is double b && nowSignal is double n && b - n >= MeshSignalDropDb)
            yield return MeshFinding(ev, "Mesh Signal Drop", $"Backhaul signal is {n:0} dBm, {b - n:0} dB below its usual {b:0} dBm.", 15);

        var baseRate = Median(ev.MeshBaseline.Select(m => m.TxRateKbps).OfType<long>().Select(v => (double)v));
        var nowRate = Median(ev.Mesh.Select(m => m.TxRateKbps).OfType<long>().Select(v => (double)v));
        if (baseRate is double br && nowRate is double nr && br > 0 && nr < br * MeshRateShare)
            yield return MeshFinding(ev, "Mesh Rate Drop", $"Backhaul PHY rate is {nr / 1000:0} Mbps, under half its usual {br / 1000:0} Mbps. The rate falls before loss shows.", 15);

        var parents = ev.Mesh.Select(m => m.ParentMac).Where(p => !string.IsNullOrEmpty(p)).ToList();
        var changes = parents.Zip(parents.Skip(1)).Count(p => p.First != p.Second);
        if (changes > 0)
            yield return MeshFinding(ev, "Mesh Parent Change", $"The backhaul moved parent {changes} {(changes == 1 ? "time" : "times")} in the window.", 10);

        var usualMembers = ev.MeshBaseline.Select(m => m.MloMembers).OfType<int>().DefaultIfEmpty(0).Max();
        var nowMembers = ev.Mesh.Select(m => m.MloMembers).OfType<int>().LastOrDefault();
        if (usualMembers > 1 && nowMembers > 0 && nowMembers < usualMembers)
            yield return MeshFinding(ev, "MLO Member Lost", $"The MLO backhaul runs on {nowMembers} of its usual {usualMembers} links.", 15);

        if (ev.Link.Mlo.Count > 1)
        {
            var signals = ev.Link.Mlo.Select(m => m.SignalDbm).OfType<int>().ToList();
            if (signals.Count > 1 && signals.Max() - signals.Min() >= 15)
                yield return MeshFinding(ev, "MLO Imbalance", $"One MLO member's signal is {signals.Max() - signals.Min()} dB below the other's.", 0);
        }
        if (ev.Link.UplinkDepth is > 1)
            yield return MeshFinding(ev, "Mesh Depth", $"This backhaul is {ev.Link.UplinkDepth} hops from the wired network: every hop shares the airtime.", 0, Detectors.MeshDepth);
    }

    private static LinkFinding MeshFinding(LinkEvidence ev, string cls, string body, double points, int detector = Detectors.MeshUplink) => new()
    {
        Detector = detector,
        LinkId = ev.Link.Id,
        Severity = points > 0 ? FindingSeverity.Warning : FindingSeverity.Info,
        Class = cls,
        Title = Title(cls, ev.Link),
        Body = body + (points > 0 ? " Re-pair Uplink in Wi-Fi Optimizer can move it to a better parent." : ""),
        Chips = [ev.Link.Kind == LinkKind.FabricMeshMlo ? "Fabric · mesh MLO" : "Fabric · mesh", ImpactChip(ev.Link)],
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
                    Body = $"Above {u * 100:0}% of {DisplayFormatters.FormatLinkSpeed(ev.Link.SpeedMbps!.Value)} since {text.Time(start!.Value)}, where its busiest hours normally reach {p95 * 100:0}%.",
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
            Body = $"Both ends support {DisplayFormatters.FormatLinkSpeed(cap)}, and the link negotiated {DisplayFormatters.FormatLinkSpeed(now)}. A cable, a keystone, or a patch panel rated lower is the usual limit.",
            Chips = ["Capability", ImpactChip(ev.Link)],
            Points = 10,
            Status = LinkStatus.SpeedDegraded,
        };
    }

    // ---- 16 Access port degraded vs its own history (sleep is not degradation) ----
    internal static LinkFinding? AccessDegraded(LinkEvidence ev)
    {
        if (ev.Link.Kind != LinkKind.Access || ev.UsualSpeedMbps is not int usual || ev.Link.SpeedMbps is not int now || now >= usual) return null;
        // A drop to 10/100 with idle traffic is a sleep cycle: only real traffic at the lower speed counts.
        var busyAtLowSpeed = ev.Hours.Count(h => (h.MeanInBps ?? 0) > 1_000_000 || (h.MeanOutBps ?? 0) > 1_000_000) >= 2;
        if (!busyAtLowSpeed) return null;
        return new LinkFinding
        {
            Detector = Detectors.AccessDegraded,
            LinkId = ev.Link.Id,
            Severity = FindingSeverity.Info,
            Class = "Link Speed Degraded",
            Title = Title("Link Speed Degraded", ev.Link),
            Body = $"The client links at {DisplayFormatters.FormatLinkSpeed(now)} while it carries traffic, where it used to link at {DisplayFormatters.FormatLinkSpeed(usual)}.",
            Chips = ["Access"],
            Points = 15,
            Status = LinkStatus.SpeedDegraded,
        };
    }

    // ---- 17 Hidden unmanaged switch or hub ----
    internal static LinkFinding? HiddenSwitch(LinkEvidence ev)
    {
        if (ev.Link.Kind != LinkKind.Access) return null;
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
                ? $"Half duplex and {macs} MAC addresses on an access port: a hub or an old unmanaged switch is on it."
                : $"{macs} MAC addresses on one access port: an unmanaged switch sits behind it.",
            Chips = ["Access"],
        };
    }

    // ---- 8 / 26 PoE: powered device power cycling (link drop + reboot together) ----
    internal static LinkFinding? PoeCycling(LinkEvidence ev, IReadOnlyList<LinkIncident> incidents)
    {
        if (ev.DownstreamReboots.Count == 0) return null;
        var cycles = ev.DownstreamReboots.Count(r => incidents.Any(i => Math.Abs((i.Start - r).TotalMinutes) <= 5));
        var zeroDraw = ev.PoeDraw.Any(p => p.Watts <= 0.1) && ev.PoeDraw.Any(p => p.Watts > 1);
        if (cycles < 2 && !(cycles >= 1 && zeroDraw)) return null;
        return new LinkFinding
        {
            Detector = Detectors.PoeHealth,
            LinkId = ev.Link.Id,
            Severity = FindingSeverity.Warning,
            Class = "PoE Power Cycling",
            Title = Title("PoE Power Cycling", ev.Link),
            Body = $"{ev.Link.Downstream.DeviceName} rebooted {cycles} {(cycles == 1 ? "time" : "times")} together with a link drop"
                + (zeroDraw ? ", and its PoE draw fell to 0 W." : ".") + " Check the PoE budget, the injector, and the cable.",
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
            Body = $"The drops happen only at {DisplayFormatters.FormatLinkSpeed(now)} and stop when the link runs slower. Force the port to {DisplayFormatters.FormatLinkSpeed(lower)} as a test.",
            Chips = ["Speed-specific"],
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
            Detector = Detectors.CapacityTrend,
            LinkId = ev.Link.Id,
            Severity = FindingSeverity.Info,
            Class = "Slow Speed Tests",
            Title = Title("Slow Speed Tests", ev.Link),
            Body = $"{tests.Count} speed tests across this link reached {ratio * 100:0}% of what tests on paths like it normally reach.",
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
