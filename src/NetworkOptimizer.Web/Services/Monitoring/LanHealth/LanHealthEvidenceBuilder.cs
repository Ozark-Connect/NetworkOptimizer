using System.Text.Json;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.Web.Services.ApAgent;
using static NetworkOptimizer.Storage.Services.MonitoringInfluxClient;

namespace NetworkOptimizer.Web.Services.Monitoring.LanHealth;

/// <summary>
/// Turns one hour of raw readings into each link's events, explain windows and observers, then
/// aligns them. Pure over its inputs, so the reference-case replay and the unit tests drive it
/// without Influx.
/// </summary>
public static class LanHealthEvidenceBuilder
{
    private static bool IsChip(PortFieldStats r) => r.PortId.StartsWith("swchip:", StringComparison.Ordinal);

    /// <summary>The hour's slowest and fastest console link speed, from the upstream end or else the downstream one.</summary>
    internal static (double? Min, double? Max) ConsoleSpeedRange(EndSeries up, EndSeries down)
    {
        static (double? Min, double? Max) Of(EndSeries s)
        {
            var rows = s.Rows.Where(r => r.Field == InfluxFieldNames.LinkSpeedMbps).ToList();
            return (rows.Select(r => r.Min).OfType<double>().DefaultIfEmpty().Min() is var mn && rows.Any(r => r.Min != null) ? mn : null,
                    rows.Select(r => r.Max).OfType<double>().DefaultIfEmpty().Max() is var mx && rows.Any(r => r.Max != null) ? mx : null);
        }
        var u = Of(up);
        var d = Of(down);
        return (u.Min ?? d.Min, u.Max ?? d.Max);
    }

    /// <summary>An access port down at least this long in an hour is a client asleep or off, not a flap.</summary>
    internal static readonly TimeSpan AsleepAfter = TimeSpan.FromMinutes(2);

    /// <summary>interface_counters fields the hourly roll-up reads. Numeric only: min/max reject booleans.</summary>
    public static readonly string[] PortFields =
    [
        "errors_in", "errors_out", "discards_in", "discards_out", "ucast_pkts_in", "ucast_pkts_out",
        "bcast_pkts_in", "mcast_pkts_in", "rate_in_bps", "rate_out_bps", "oper_status",
        InfluxFieldNames.LastChange, InfluxFieldNames.LinkSpeedMbps, InfluxFieldNames.ChipLinkSpeedMbps, InfluxFieldNames.LinkDownCount,
        InfluxFieldNames.StpStateChangeCount, InfluxFieldNames.PortRxErrors, InfluxFieldNames.PortTxErrors,
        InfluxFieldNames.PortRxDropped, InfluxFieldNames.PortTxDropped, InfluxFieldNames.PortRxPackets,
        InfluxFieldNames.PortTxPackets, InfluxFieldNames.PortRxBroadcast, InfluxFieldNames.PortRxMulticast,
        InfluxFieldNames.PoePower, InfluxFieldNames.ChipRxCrcErrors, InfluxFieldNames.ChipTxCrcErrors,
        InfluxFieldNames.ChipLateCollisions, InfluxFieldNames.ChipOutDiscards, InfluxFieldNames.UplinkCarrierChanges, InfluxFieldNames.UplinkRxCrcErrors,
        InfluxFieldNames.MacTableCount,
    ];

    /// <summary>device_health fields the roll-up reads for mesh links.</summary>
    public static readonly string[] DeviceFields =
    [
        InfluxFieldNames.UplinkSignalDbm, InfluxFieldNames.UplinkTxRateKbps, InfluxFieldNames.UplinkMloLinkCount,
        InfluxFieldNames.UplinkParentMac, InfluxFieldNames.UplinkTxRetries, InfluxFieldNames.UplinkTxAttempts,
        InfluxFieldNames.UplinkTxDropped, InfluxFieldNames.UplinkAssocSeconds, InfluxFieldNames.UplinkRxBps, InfluxFieldNames.UplinkTxBps,
        InfluxFieldNames.UplinkChildTxRetries, InfluxFieldNames.UplinkChildTxAttempts, InfluxFieldNames.UplinkChildTxDropped,
        InfluxFieldNames.UplinkParentSignalField("ng"), InfluxFieldNames.UplinkParentSignalField("na"), InfluxFieldNames.UplinkParentSignalField("6e"),
        InfluxFieldNames.UplinkMloField(0, "signal_dbm"), InfluxFieldNames.UplinkMloField(0, "radio"),
        InfluxFieldNames.UplinkMloField(1, "signal_dbm"), InfluxFieldNames.UplinkMloField(1, "radio"),
        InfluxFieldNames.UplinkMloField(2, "signal_dbm"), InfluxFieldNames.UplinkMloField(2, "radio"),
    ];

    /// <summary>events the roll-up reads.</summary>
    public static readonly string[] EventTypes =
    [
        ApAgentLinkEventSink.EventTypes.LinkDown, ApAgentLinkEventSink.EventTypes.LinkUp,
        ApAgentLinkEventSink.EventTypes.UplinkReconfigure, ApAgentLinkEventSink.EventTypes.UplinkLost,
        ApAgentLinkEventSink.EventTypes.InformFailed,
        SwitchPortLinkSink.LinkDownEvent, SwitchPortLinkSink.LinkUpEvent, LanPortSnapshotRecorder.StateEventType,
        LanHealthPortInsights.EventType,
    ];

    /// <summary>Everything one hour brings, already fetched.</summary>
    public sealed class HourInputs
    {
        public DateTime Hour { get; init; }
        public IReadOnlyList<PortFieldStats> Ports { get; init; } = [];
        public IReadOnlyList<LanEventPoint> Events { get; init; } = [];
        public IReadOnlyList<ProbeLossPoint> Loss { get; init; } = [];
        public IReadOnlyDictionary<string, (int Samples, double MeanLoss, double? RttMs)> Probes { get; init; } = new Dictionary<string, (int, double, double?)>();
        public IReadOnlyList<(DateTime WindowStart, string DeviceMac, string Field, double? Mean, object? Last, double? First)> Devices { get; init; } = [];
        /// <summary>Fabric target id to device MAC.</summary>
        public IReadOnlyDictionary<string, string> TargetDevices { get; init; } = new Dictionary<string, string>();
        /// <summary>(device MAC, SNMP if_name) to port number, from InterfaceNameMaps.</summary>
        public IReadOnlyDictionary<(string Mac, string IfName), int> NameMap { get; init; } = new Dictionary<(string, string), int>();
        public IReadOnlyList<ExplainWindow> Explain { get; init; } = [];
        public IReadOnlyDictionary<string, List<DateTime>> Reboots { get; init; } = new Dictionary<string, List<DateTime>>();
        public IReadOnlyList<DateTime> RebootsFor(string mac) => Reboots.TryGetValue(mac, out var r) ? r : [];
        /// <summary>Port Anomalies answered this run, so it is an observer of every UniFi switch and gateway port.</summary>
        public bool PortInsightsAvailable { get; init; }
    }

    /// <summary>One end's series and the fields read from them.</summary>
    internal sealed class EndSeries
    {
        public List<PortFieldStats> Rows { get; } = [];

        public double? Rise(params string[] fields)
        {
            double? best = null;
            foreach (var f in fields)
                foreach (var r in Rows.Where(r => r.Field == f))
                    if (CounterRise(r) is double v && (best == null || v > best)) best = v;
            return best;
        }

        public bool Has(string field) => Rows.Any(r => r.Field == field);

        public double? Max(string field) => Pick(field, r => r.Max, Math.Max);

        public double? Min(string field) => Pick(field, r => r.Min, Math.Min);

        public double? Mean(string field) => Pick(field, r => r.Mean, Math.Max);

        public double? First(string field) => Pick(field, r => r.First, Math.Max);

        public double? Last(string field) => Pick(field, r => r.Last, Math.Max);

        public bool Changed(string field) => Rows.Any(r => r.Field == field && r.First != null && r.Last != null && r.First != r.Last);

        private double? Pick(string field, Func<PortFieldStats, double?> get, Func<double, double, double> combine)
        {
            double? acc = null;
            foreach (var r in Rows.Where(r => r.Field == field))
                if (get(r) is double v) acc = acc is double a ? combine(a, v) : v;
            return acc;
        }
    }

    /// <summary>A cumulative counter's growth over the window: last minus first, or last alone after a reset.</summary>
    internal static double? CounterRise(PortFieldStats r)
    {
        if (r.First is not double first || r.Last is not double last) return null;
        return last >= first ? last - first : last;
    }

    /// <summary>The rows that belong to one end of a link.</summary>
    internal static EndSeries SeriesFor(LinkEnd end, IReadOnlyList<PortFieldStats> ports, IReadOnlyDictionary<(string, string), int> nameMap, string? extraIfName = null)
    {
        var s = new EndSeries();
        if (!end.IsUniFi || end.DeviceMac.Length == 0) return s;
        var ifNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(end.IfName)) ifNames.Add(end.IfName);
        if (!string.IsNullOrEmpty(extraIfName)) ifNames.Add(extraIfName);
        foreach (var r in ports)
        {
            if (!string.Equals(r.DeviceMac, end.DeviceMac, StringComparison.OrdinalIgnoreCase)) continue;
            if (Belongs(r, end, ifNames, nameMap)) s.Rows.Add(r);
        }
        return s;
    }

    private static bool Belongs(PortFieldStats r, LinkEnd end, HashSet<string> ifNames, IReadOnlyDictionary<(string, string), int> nameMap)
    {
        if (r.PortId.StartsWith("pt:", StringComparison.Ordinal))
            return end.PortIdx is int p && r.PortId == $"pt:{p}";
        if (r.PortId.StartsWith("apagent:", StringComparison.Ordinal) || r.PortId.StartsWith("swchip:", StringComparison.Ordinal))
            return ifNames.Contains(r.PortId[(r.PortId.IndexOf(':') + 1)..]);
        if (NetworkOptimizer.UniFi.LanHealthPorts.IsVlanSubInterface(r.IfName)) return false;
        if (ifNames.Contains(r.IfName) || ifNames.Contains(r.PortId)) return true;
        if (end.PortIdx is int idx)
        {
            if (nameMap.TryGetValue((r.DeviceMac.ToLowerInvariant(), r.IfName), out var mapped)) return mapped == idx;
            if (string.Equals(r.IfName, $"Port {idx}", StringComparison.OrdinalIgnoreCase)) return true;
            if (!string.IsNullOrEmpty(end.PortName) && string.Equals(r.IfName, end.PortName, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>The result of one hour on one link.</summary>
    public sealed record LinkHour(LinkEvidence Evidence, List<LinkIncident> Incidents, LanHealthLinkHour Row);

    /// <summary>Builds and aligns one link's hour.</summary>
    public static LinkHour BuildHour(LinkRecord link, HourInputs x, IReadOnlyDictionary<string, LinkRecord> downstreamToLink,
        IReadOnlyDictionary<string, HashSet<string>> subtreeApsByDevice, int version)
    {
        var from = x.Hour;
        var to = x.Hour.AddHours(1);
        var up = SeriesFor(link.Upstream, x.Ports, x.NameMap);
        var downIf = link.Downstream.IsAccessPoint && link.Kind == LinkKind.FabricWired && string.IsNullOrEmpty(link.Downstream.IfName) ? "eth0" : null;
        var down = link.Kind == LinkKind.FabricWired ? SeriesFor(link.Downstream, x.Ports, x.NameMap, downIf) : new EndSeries();

        var events = new List<LinkEvent>();
        var observers = new List<LinkEventAligner.Observer>();
        var hour = new[] { (from, to) };

        // Precise: kernel PHY lines from the AP Agent (downstream AP), and the gateway switch chip (upstream gateway port).
        foreach (var e in x.Events)
        {
            var mac = e.DeviceMac.ToLowerInvariant();
            if (link.Kind == LinkKind.FabricWired && mac == link.Downstream.DeviceMac && link.Downstream.IsAccessPoint
                && (e.IfName == null || e.IfName.Equals(downIf ?? link.Downstream.IfName ?? "eth0", StringComparison.OrdinalIgnoreCase)))
            {
                if (e.EventType == ApAgentLinkEventSink.EventTypes.LinkDown)
                    events.Add(new LinkEvent(e.Time.AddSeconds(-1), e.Time.AddSeconds(1), LinkEventType.LinkDown, LinkSource.ApKernelLog, LinkSide.Downstream));
                else if (e.EventType == ApAgentLinkEventSink.EventTypes.LinkUp)
                    events.Add(new LinkEvent(e.Time.AddSeconds(-1), e.Time.AddSeconds(1), LinkEventType.LinkUp, LinkSource.ApKernelLog, LinkSide.Downstream,
                        SpeedMbps: ApAgentLinkEventSink.SpeedOf(e.NewValue)));
            }
            if (link.Upstream.IsGateway && mac == link.Upstream.DeviceMac && !string.IsNullOrEmpty(link.Upstream.IfName)
                && string.Equals(e.IfName, link.Upstream.IfName, StringComparison.OrdinalIgnoreCase))
            {
                if (e.EventType == SwitchPortLinkSink.LinkDownEvent)
                    events.Add(new LinkEvent(e.Time.AddSeconds(-3), e.Time, LinkEventType.LinkDown, LinkSource.GatewaySwitchChip, LinkSide.Upstream));
                else if (e.EventType == SwitchPortLinkSink.LinkUpEvent)
                    events.Add(new LinkEvent(e.Time.AddSeconds(-3), e.Time, LinkEventType.LinkUp, LinkSource.GatewaySwitchChip, LinkSide.Upstream,
                        SpeedMbps: ApAgentLinkEventSink.SpeedOf(e.NewValue)));
            }
        }
        var apAgentHere = link.Downstream.IsAccessPoint && (down.Has(InfluxFieldNames.UplinkCarrierChanges)
            || x.Events.Any(e => e.DeviceMac.Equals(link.Downstream.DeviceMac, StringComparison.OrdinalIgnoreCase) && e.EventType.StartsWith("ap_", StringComparison.Ordinal)));
        if (apAgentHere && link.Kind == LinkKind.FabricWired) observers.Add(new(LinkSource.ApKernelLog, hour, CountsDrops: true, CountsErrors: false));
        // swconfig port N is ethN only where verified; a port whose chip speed disagrees with the port
        // table's is not this port, and its readings are dropped.
        var chipSpeed = up.Rows.Where(r => IsChip(r) && r.Field == InfluxFieldNames.ChipLinkSpeedMbps)
            .Select(r => r.Max).OfType<double>().DefaultIfEmpty(0).Max();
        var chipHere = up.Rows.Any(r => r.PortId.StartsWith("swchip:", StringComparison.Ordinal))
            && (chipSpeed <= 0 || link.SpeedMbps is not int ls || (int)chipSpeed == ls);
        if (!chipHere)
        {
            up.Rows.RemoveAll(r => r.PortId.StartsWith("swchip:", StringComparison.Ordinal));
            events.RemoveAll(e => e.Source == LinkSource.GatewaySwitchChip);
        }
        if (chipHere) observers.Add(new(LinkSource.GatewaySwitchChip, hour, true, true));

        // Probe loss bursts: an AP's loss belongs to the deepest link above it that all lossy APs share.
        var probed = ProbeTargetsFor(link, x, subtreeApsByDevice);
        if (probed.Count > 0)
        {
            foreach (var (time, lossy) in LossBuckets(x))
                if (OwnerLink(lossy, downstreamToLink, subtreeApsByDevice) == link.Id)
                    events.Add(new LinkEvent(time.AddSeconds(-6), time, LinkEventType.LossBurst, LinkSource.FabricProbe, LinkSide.Downstream));
            observers.Add(new(LinkSource.FabricProbe, hour, CountsDrops: true, CountsErrors: false));
        }

        // Mesh re-association: the parent's association uptime for the child reset in the hour.
        var meshTx = MeshTxFor(link, x, from, to);
        if (meshTx.ReassociatedAt is DateTime reassoc)
        {
            // Uptime is read once a poll, so the time is good to about a minute either way.
            events.Add(new LinkEvent(reassoc.AddSeconds(-60), reassoc.AddSeconds(60), LinkEventType.LinkDown, LinkSource.MeshAssociation, LinkSide.Downstream));
            events.Add(new LinkEvent(reassoc.AddSeconds(MeshReassociationSeconds - 60), reassoc.AddSeconds(MeshReassociationSeconds + 60), LinkEventType.LinkUp, LinkSource.MeshAssociation, LinkSide.Downstream));
        }
        if (meshTx.Observed) observers.Add(new(LinkSource.MeshAssociation, hour, CountsDrops: true, CountsErrors: false));

        // Imprecise: counters over the hour.
        // A missing counter is "source unavailable" (link_down_count is per switch model), never 0 drops:
        // it joins coverage as not reported, and the drop evidence comes from the other sources.
        void Relink(EndSeries s, string field, LinkSource src, LinkSide side, double perDrop = 1)
        {
            if (!s.Has(field))
            {
                if (src == LinkSource.SwitchLinkDownCount && side == LinkSide.Upstream && link.Upstream.IsSwitch && s.Rows.Count > 0)
                    observers.Add(new(src, [], true, false));
                return;
            }
            observers.Add(new(src, hour, true, false));
            if (s.Rise(field) is double rise && rise >= perDrop)
                events.Add(new LinkEvent(from, to, LinkEventType.Relink, src, side, Count: Math.Floor(rise / perDrop)));
        }
        Relink(up, InfluxFieldNames.LinkDownCount, LinkSource.SwitchLinkDownCount, LinkSide.Upstream);
        Relink(down, InfluxFieldNames.LinkDownCount, LinkSource.SwitchLinkDownCount, LinkSide.Downstream);
        Relink(down, InfluxFieldNames.UplinkCarrierChanges, LinkSource.ApCarrierCounter, LinkSide.Downstream, perDrop: 2);
        if (up.Has(InfluxFieldNames.LastChange) || down.Has(InfluxFieldNames.LastChange))
        {
            observers.Add(new(LinkSource.SnmpLastChange, hour, true, false));
            if (up.Changed(InfluxFieldNames.LastChange) || down.Changed(InfluxFieldNames.LastChange))
                events.Add(new LinkEvent(from, to, LinkEventType.Relink, LinkSource.SnmpLastChange, LinkSide.Upstream));
        }
        if (up.Has("oper_status") || down.Has("oper_status"))
        {
            observers.Add(new(LinkSource.SnmpOperStatus, hour, true, false));
            // A relink needs both states in the hour: a port down the whole hour is off, not flapping.
            static bool Flipped(EndSeries s) => (s.Min("oper_status") ?? 1) < 2 && (s.Max("oper_status") ?? 1) >= 2;
            if (Flipped(up) || Flipped(down))
                events.Add(new LinkEvent(from, to, LinkEventType.Relink, LinkSource.SnmpOperStatus, LinkSide.Upstream));
        }

        // Errors: the best counter per end. A gateway LAN port reads 0 regardless; the far end is the evidence.
        var upErrorsBlind = link.Upstream.IsGateway && !chipHere;
        var (upRx, upSrc) = BestRx(up, link.Upstream, chipHere);
        var (downRx, downSrc) = BestRx(down, link.Downstream, false);
        if (upRx is > 0 && !upErrorsBlind)
            events.Add(new LinkEvent(from, to, LinkEventType.ErrorBurst, upSrc, LinkSide.Upstream, Count: upRx.Value));
        if (downRx is > 0)
            events.Add(new LinkEvent(from, to, LinkEventType.ErrorBurst, downSrc, LinkSide.Downstream, Count: downRx.Value));
        if (up.Rows.Count > 0 && !upErrorsBlind) observers.Add(new(upSrc, hour, false, true));
        if (down.Rows.Count > 0) observers.Add(new(downSrc, hour, false, true));

        // A gateway LAN port's SNMP and port-table discards count the kernel queue on the 10G conduit,
        // not the port: only the switch chip's own count is the port's.
        var upTxDiscards = link.Upstream.IsGateway
            ? (chipHere ? up.Rise(InfluxFieldNames.ChipOutDiscards) : null)
            : up.Rise("discards_out", InfluxFieldNames.PortTxDropped);
        var downTxDiscards = down.Rise("discards_out", InfluxFieldNames.PortTxDropped);
        if ((upTxDiscards ?? 0) + (downTxDiscards ?? 0) > 0)
            events.Add(new LinkEvent(from, to, LinkEventType.DiscardBurst, LinkSource.SnmpCounters, LinkSide.Upstream, Count: (upTxDiscards ?? 0) + (downTxDiscards ?? 0)));

        // Speed: a change within the hour.
        var (speedMin, speedMax) = ConsoleSpeedRange(up, down);
        if (speedMin is double smin && speedMax is double smax && smin > 0 && smin != smax)
            events.Add(new LinkEvent(from, to, LinkEventType.SpeedChange, LinkSource.PortTableState, LinkSide.Upstream, SpeedMbps: (int)smin));

        // Explanations: everything that touches either end, plus sleep on an access port with no traffic.
        var explain = x.Explain.ToList();
        var macs = new HashSet<string> { link.Upstream.DeviceMac, link.Downstream.DeviceMac };
        // A mesh backhaul has no port: the parent's live rates for the child stand in (in = child to parent).
        var meanIn = up.Mean("rate_in_bps") ?? down.Mean("rate_out_bps") ?? MeshMean(link, x, InfluxFieldNames.UplinkRxBps);
        var meanOut = up.Mean("rate_out_bps") ?? down.Mean("rate_in_bps") ?? MeshMean(link, x, InfluxFieldNames.UplinkTxBps);
        if (link.Kind == LinkKind.Access && (meanIn ?? 0) < 2_000 && (meanOut ?? 0) < 20_000)
            explain.Add(new ExplainWindow(from, to, link.Upstream.DeviceMac, ExplainReason.DeviceAsleep));
        // Down for minutes is a client asleep or powered off; a flap is down for seconds. ifOperStatus is
        // 1 up / 2 down, so the hour's mean less 1 is the share of it spent down.
        if (link.Kind == LinkKind.Access && up.Has("oper_status")
            && ((up.Mean("oper_status") ?? 1) - 1 >= AsleepAfter.TotalSeconds / 3600
                || (up.First("oper_status") ?? 1) >= 2 || (up.Last("oper_status") ?? 1) >= 2))
            explain.Add(new ExplainWindow(from, to, link.Upstream.DeviceMac, ExplainReason.DeviceAsleep));
        // A 10/100 relink on an access port is the sleep signature too.
        if (link.Kind == LinkKind.Access && speedMin is <= 100 && speedMax is > 100)
            explain.Add(new ExplainWindow(from, to, link.Upstream.DeviceMac, ExplainReason.DeviceAsleep));

        // Port Anomalies: an anomaly on the upstream port in the hour counts as having seen that hour's drops.
        var anomalies = x.Events.Where(e => e.EventType == LanHealthPortInsights.EventType
            && string.Equals(e.DeviceMac, link.Upstream.DeviceMac, StringComparison.OrdinalIgnoreCase)
            && e.IfName == link.Upstream.PortIdx?.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToList();
        if (x.PortInsightsAvailable && link.Upstream.PortIdx != null && (link.Upstream.IsSwitch || link.Upstream.IsGateway))
            observers.Add(new(LinkSource.PortInsights, hour, true, false));
        var distinctObservers = observers.OrderByDescending(o => o.Observing.Count).DistinctBy(o => o.Source).ToList();
        var incidents = LinkEventAligner.Align(events, explain, distinctObservers, macs);
        if (anomalies.Count > 0)
            foreach (var i in incidents.Where(i => i.Kind is IncidentKind.Drop or IncidentKind.Relink))
            {
                i.SeenBy.Add(LinkSource.PortInsights);
                i.MissedBy.Remove(LinkSource.PortInsights);
            }

        double? lossPct = probed.Count > 0 && probed.Any(x.Probes.ContainsKey)
            ? probed.Where(x.Probes.ContainsKey).Average(t => x.Probes[t].MeanLoss)
            : null;
        var rtts = probed.Where(x.Probes.ContainsKey).Select(t => x.Probes[t].RttMs).OfType<double>().ToList();
        double? rttMs = rtts.Count > 0 ? rtts.Average() : null;
        var mesh = MeshFor(link, x);
        var worstSignal = link.IsMesh ? WorstSignal(MeshSignals(link, x)) : null;

        var row = new LanHealthLinkHour
        {
            HourStart = from,
            LinkId = link.Id,
            Kind = (int)link.Kind,
            Name = Trim(link.Name, 200),
            ClientsBehind = link.ClientsBehind,
            Drops = incidents.Where(i => !i.IsExplained && i.Kind == IncidentKind.Drop).Sum(i => i.Count),
            DropsExplained = incidents.Where(i => i.IsExplained && i.Kind is IncidentKind.Drop or IncidentKind.Relink).Sum(i => i.Count),
            Relinks = incidents.Where(i => !i.IsExplained && i.Kind == IncidentKind.Relink).Sum(i => i.Count),
            LossBursts = incidents.Count(i => !i.IsExplained && i.Kind == IncidentKind.LossBurst),
            ErrorBursts = incidents.Where(i => !i.IsExplained && i.Kind == IncidentKind.ErrorBurst).Sum(i => i.Count),
            SpeedChanges = incidents.Count(i => i.Kind == IncidentKind.SpeedChange),
            DownSeconds = incidents.Where(i => !i.IsExplained && i.Kind is IncidentKind.Drop or IncidentKind.Relink)
                .Sum(i => (i.DownSeconds ?? 5) * i.Count),
            UpRxErrors = upErrorsBlind ? null : upRx,
            DownRxErrors = downRx,
            UpTxErrors = up.Rise("errors_out", InfluxFieldNames.PortTxErrors, InfluxFieldNames.ChipTxCrcErrors),
            DownTxErrors = down.Rise("errors_out", InfluxFieldNames.PortTxErrors),
            UpTxDiscards = upTxDiscards,
            DownTxDiscards = downTxDiscards,
            UpRxPackets = up.Rise("ucast_pkts_in", InfluxFieldNames.PortRxPackets),
            DownRxPackets = down.Rise("ucast_pkts_in", InfluxFieldNames.PortRxPackets),
            UpTxPackets = up.Rise("ucast_pkts_out", InfluxFieldNames.PortTxPackets),
            DownTxPackets = down.Rise("ucast_pkts_out", InfluxFieldNames.PortTxPackets),
            LateCollisions = up.Rise(InfluxFieldNames.ChipLateCollisions),
            StpChanges = up.Rise(InfluxFieldNames.StpStateChangeCount),
            UpErrorsBlind = upErrorsBlind,
            UpLinkDownCount = up.Max(InfluxFieldNames.LinkDownCount),
            DownLinkDownCount = down.Max(InfluxFieldNames.LinkDownCount),
            DownCarrierChanges = down.Max(InfluxFieldNames.UplinkCarrierChanges),
            MaxInBps = up.Max("rate_in_bps") ?? down.Max("rate_out_bps"),
            MaxOutBps = up.Max("rate_out_bps") ?? down.Max("rate_in_bps"),
            MeanInBps = meanIn,
            MeanOutBps = meanOut,
            BroadcastPps = PerSecond(up.Rise("bcast_pkts_in", InfluxFieldNames.PortRxBroadcast)),
            MulticastPps = PerSecond(up.Rise("mcast_pkts_in", InfluxFieldNames.PortRxMulticast)),
            PacketsPps = PerSecond(up.Rise("ucast_pkts_in", InfluxFieldNames.PortRxPackets)),
            SpeedMinMbps = speedMin is double a && a > 0 ? (int)a : link.SpeedMbps,
            SpeedMaxMbps = speedMax is double b && b > 0 ? (int)b : link.SpeedMbps,
            HalfDuplex = link.FullDuplex == false ? true : null,
            MeshSignalDbm = mesh.Signal,
            MeshTxRateKbps = mesh.Rate,
            MloMembers = mesh.Members,
            ParentMac = mesh.Parent,
            ProbeLossPct = lossPct,
            ProbeRttMs = rttMs is double rv ? Math.Round(rv, 3) : null,
            MeshTxRetries = meshTx.Retries,
            MeshTxAttempts = meshTx.Attempts,
            MeshTxDropped = meshTx.Dropped,
            MeshReassociations = meshTx.ReassociatedAt != null ? 1 : 0,
            MeshChildTxRetries = meshTx.ChildRetries,
            MeshChildTxAttempts = meshTx.ChildAttempts,
            MeshChildTxDropped = meshTx.ChildDropped,
            MeshWorstSignalDbm = worstSignal?.Dbm,
            MeshWorstSignalRadio = worstSignal?.Radio,
            MeshWorstSignalAtParent = worstSignal?.AtParent,
            PoeWattsMin = up.Min(InfluxFieldNames.PoePower),
            PoeWattsMax = up.Max(InfluxFieldNames.PoePower),
            Reboots = x.RebootsFor(link.Downstream.DeviceMac).Count,
            // An inform that fails inside an explained window (a Network app upgrade, a rollout step at or
            // above the AP, its own reboot or provisioning) is that event, not the AP's path to the console.
            InformFailures = x.Events.Count(e => e.EventType == ApAgentLinkEventSink.EventTypes.InformFailed
                && string.Equals(e.DeviceMac, link.Downstream.DeviceMac, StringComparison.OrdinalIgnoreCase)
                && !x.Explain.Any(w => e.Time >= w.Start && e.Time <= w.End
                    && (w.DeviceMac.Length == 0 || string.Equals(w.DeviceMac, link.Downstream.DeviceMac, StringComparison.OrdinalIgnoreCase)))),
            IncidentsJson = Trim(LanHealthIncidentJson.Serialize(incidents), 16000),
            CoverageJson = Trim(JsonSerializer.Serialize(LinkEventAligner.Coverage(incidents, distinctObservers)
                .Select(c => new LanHealthIncidentJson.CoverageDto((int)c.Source, c.Caught, c.Total, c.Observing))), 2000),
            Version = version,
            ComputedAt = DateTime.UtcNow,
        };
        var ev = new LinkEvidence { Link = link, From = from, To = to, Events = events, Observers = distinctObservers };
        return new LinkHour(ev, incidents, row);
    }

    private static (double? Rx, LinkSource Source) BestRx(EndSeries s, LinkEnd end, bool chip)
    {
        if (chip && s.Rise(InfluxFieldNames.ChipRxCrcErrors) is double c) return (c, LinkSource.GatewaySwitchChip);
        if (end.IsAccessPoint && s.Rise(InfluxFieldNames.UplinkRxCrcErrors) is double crc) return (crc, LinkSource.ApCrcCounter);
        if (s.Rise("errors_in") is double snmp) return (snmp, LinkSource.SnmpCounters);
        if (s.Rise(InfluxFieldNames.PortRxErrors) is double pt) return (pt, LinkSource.PortTableCounters);
        return (null, LinkSource.SnmpCounters);
    }

    private static double? PerSecond(double? countPerHour) => countPerHour is double c ? c / 3600 : null;

    /// <summary>Fabric targets that probe through this link: its own AP, or the APs below a switch (switches are poor targets).</summary>
    internal static List<string> ProbeTargetsFor(LinkRecord link, HourInputs x, IReadOnlyDictionary<string, HashSet<string>> subtreeAps)
    {
        if (link.Kind == LinkKind.Access) return [];
        var aps = subtreeAps.TryGetValue(link.Downstream.DeviceMac, out var set) ? set : [];
        return x.TargetDevices.Where(kv => aps.Contains(kv.Value.ToLowerInvariant())).Select(kv => kv.Key).ToList();
    }

    /// <summary>Loss samples bucketed to the 5 s probe cadence: each bucket lists the AP MACs that lost packets then.</summary>
    internal static List<(DateTime Time, HashSet<string> Macs)> LossBuckets(HourInputs x)
    {
        var buckets = new List<(DateTime Time, HashSet<string> Macs)>();
        foreach (var p in x.Loss.OrderBy(p => p.Time))
        {
            if (!x.TargetDevices.TryGetValue(p.TargetId, out var mac)) continue;
            if (buckets.Count > 0 && (p.Time - buckets[^1].Time).TotalSeconds <= 6) buckets[^1].Macs.Add(mac.ToLowerInvariant());
            else buckets.Add((p.Time, new HashSet<string> { mac.ToLowerInvariant() }));
        }
        return buckets;
    }

    /// <summary>
    /// The deepest link above every AP that lost packets in a bucket: the link whose probed subtree
    /// holds all of them. One lossy AP is its own uplink's; every AP below a switch is the switch's uplink's.
    /// </summary>
    internal static string? OwnerLink(HashSet<string> lossy, IReadOnlyDictionary<string, LinkRecord> downstreamToLink,
        IReadOnlyDictionary<string, HashSet<string>> subtreeAps)
    {
        LinkRecord? best = null;
        var bestSize = int.MaxValue;
        foreach (var (device, aps) in subtreeAps)
        {
            if (aps.Count == 0 || !lossy.All(aps.Contains)) continue;
            if (!downstreamToLink.TryGetValue(device, out var link)) continue;
            if (aps.Count < bestSize) { best = link; bestSize = aps.Count; }
        }
        return best?.Id;
    }

    /// <summary>A mesh re-association's outage, for the timeline: the child rescans and rejoins in a few seconds.</summary>
    internal const double MeshReassociationSeconds = 5;

    /// <summary>A since-association counter's rise over the hour: last minus first, or the last alone when it reset.</summary>
    internal static double? CounterRise(double? first, double? last) =>
        first is double f && last is double l ? (l >= f ? l - f : l) : null;

    /// <summary>
    /// When the mesh child re-associated in [from, to), from the parent's association uptime at the hour's first
    /// and last reading. A reset (last below first) or an association that began inside the hour is one
    /// re-association; two in one hour count once, and the other sources still see each drop.
    /// </summary>
    internal static DateTime? Reassociation(DateTime from, DateTime to, double? firstUptime, double? lastUptime)
    {
        if (lastUptime is not double last) return null;
        var began = to.AddSeconds(-last);
        if (firstUptime is double first && last < first) return began < from ? from : began;
        if (began >= from && began < to && (firstUptime is not double f0 || f0 <= last)) return began;
        return null;
    }

    private static double? MeshMean(LinkRecord link, HourInputs x, string field) => !link.IsMesh ? null
        : x.Devices.FirstOrDefault(d => d.Field == field && string.Equals(d.DeviceMac, link.Downstream.DeviceMac, StringComparison.OrdinalIgnoreCase)).Mean;

    /// <summary>One end's reading of one mesh link: which band, and whether the parent took it.</summary>
    internal sealed record MeshSignal(int Dbm, string Radio, bool AtParent);

    /// <summary>
    /// The reading closest to weak for its own band (<see cref="NetworkOptimizer.WiFi.Helpers.SignalClassification.WeakBelowDbm"/>):
    /// each end is graded on its own, so an AP that hears its peer better than the peer hears it is not a fault.
    /// </summary>
    internal static MeshSignal? WorstSignal(IEnumerable<MeshSignal> signals) =>
        signals.OrderBy(s => s.Dbm - NetworkOptimizer.WiFi.Helpers.SignalClassification.WeakBelowDbm(LanHealthDetectors.SignalBand(s.Radio))).FirstOrDefault();

    /// <summary>
    /// The hour's mean signal at both ends of a mesh link, per band: the child's own (every MLO link, else its one
    /// uplink on <see cref="LinkRecord.MeshRadio"/>) and the parent's downlink_table reading of the child.
    /// </summary>
    private static IEnumerable<MeshSignal> MeshSignals(LinkRecord link, HourInputs x)
    {
        var rows = x.Devices.Where(d => string.Equals(d.DeviceMac, link.Downstream.DeviceMac, StringComparison.OrdinalIgnoreCase)).ToList();
        double? Mean(string f) => rows.FirstOrDefault(r => r.Field == f).Mean;
        string? Text(string f) => rows.FirstOrDefault(r => r.Field == f).Last as string;
        var members = 0;
        for (var i = 0; i < 3; i++)
            if (Mean(InfluxFieldNames.UplinkMloField(i, "signal_dbm")) is double s && Text(InfluxFieldNames.UplinkMloField(i, "radio")) is { Length: > 0 } r)
            {
                members++;
                yield return new MeshSignal((int)Math.Round(s), r, false);
            }
        if (members == 0 && Mean(InfluxFieldNames.UplinkSignalDbm) is double own && link.MeshRadio is { Length: > 0 } radio)
            yield return new MeshSignal((int)Math.Round(own), radio, false);
        foreach (var r in new[] { "ng", "na", "6e" })
            if (Mean(InfluxFieldNames.UplinkParentSignalField(r)) is double p)
                yield return new MeshSignal((int)Math.Round(p), r, true);
    }

    private static (double? Retries, double? Attempts, double? Dropped, DateTime? ReassociatedAt, bool Observed,
        double? ChildRetries, double? ChildAttempts, double? ChildDropped) MeshTxFor(LinkRecord link, HourInputs x, DateTime from, DateTime to)
    {
        if (!link.IsMesh) return (null, null, null, null, false, null, null, null);
        var rows = x.Devices.Where(d => string.Equals(d.DeviceMac, link.Downstream.DeviceMac, StringComparison.OrdinalIgnoreCase)).ToList();
        (double? First, double? Last) Of(string field) => rows.FirstOrDefault(r => r.Field == field) is var r && r.Field != null
            ? (r.First, r.Last switch { long l => l, double d => d, _ => (double?)null }) : (null, null);
        var retries = Of(InfluxFieldNames.UplinkTxRetries);
        var attempts = Of(InfluxFieldNames.UplinkTxAttempts);
        var dropped = Of(InfluxFieldNames.UplinkTxDropped);
        var assoc = Of(InfluxFieldNames.UplinkAssocSeconds);
        var childRetries = Of(InfluxFieldNames.UplinkChildTxRetries);
        var childAttempts = Of(InfluxFieldNames.UplinkChildTxAttempts);
        var childDropped = Of(InfluxFieldNames.UplinkChildTxDropped);
        return (CounterRise(retries.First, retries.Last), CounterRise(attempts.First, attempts.Last), CounterRise(dropped.First, dropped.Last),
            Reassociation(from, to, assoc.First, assoc.Last), assoc.Last != null,
            CounterRise(childRetries.First, childRetries.Last), CounterRise(childAttempts.First, childAttempts.Last), CounterRise(childDropped.First, childDropped.Last));
    }

    private static (int? Signal, long? Rate, int? Members, string? Parent) MeshFor(LinkRecord link, HourInputs x)
    {
        if (!link.IsMesh) return (null, null, null, null);
        var rows = x.Devices.Where(d => string.Equals(d.DeviceMac, link.Downstream.DeviceMac, StringComparison.OrdinalIgnoreCase)).ToList();
        var signalRow = rows.FirstOrDefault(r => r.Field == InfluxFieldNames.UplinkSignalDbm);
        var rateRow = rows.FirstOrDefault(r => r.Field == InfluxFieldNames.UplinkTxRateKbps);
        var membersRow = rows.FirstOrDefault(r => r.Field == InfluxFieldNames.UplinkMloLinkCount);
        var parentRow = rows.FirstOrDefault(r => r.Field == InfluxFieldNames.UplinkParentMac);
        int? signal = signalRow.Mean is double s ? (int)Math.Round(s) : link.MeshSignalDbm;
        long? rate = rateRow.Mean is double t ? (long)t : link.MeshTxRateKbps;
        int? members = membersRow.Last switch
        {
            long l => (int)l,
            double d => (int)d,
            _ => link.Mlo.Count > 0 ? link.Mlo.Count : null,
        };
        var parent = parentRow.Last as string ?? link.Upstream.DeviceMac;
        return (signal, rate, members, parent);
    }

    private static string? Trim(string? s, int max) => s == null ? null : s.Length <= max ? s : s[..max];
}

/// <summary>The compact JSON a link hour stores its incidents and coverage in.</summary>
public static class LanHealthIncidentJson
{
    public sealed record IncidentDto(DateTime S, DateTime E, int K, double? D, double C, int[] By, int[] Miss, int? X, string? Xd, int? Sp, double Err);

    public sealed record CoverageDto(int Source, int Caught, int Total, bool Observing);

    public static string Serialize(IReadOnlyList<LinkIncident> incidents) =>
        JsonSerializer.Serialize(incidents.Take(200).Select(i => new IncidentDto(i.Start, i.End, (int)i.Kind, i.DownSeconds, i.Count,
            i.SeenBy.Select(s => (int)s).ToArray(), i.MissedBy.Select(s => (int)s).ToArray(),
            i.Explained is { } x ? (int)x : null, i.ExplainedDetail, i.SpeedAfterMbps, i.AttachedErrors)));

    public static List<LinkIncident> Deserialize(string? json)
    {
        if (string.IsNullOrEmpty(json)) return [];
        try
        {
            var dtos = JsonSerializer.Deserialize<List<IncidentDto>>(json) ?? [];
            return dtos.Select(d =>
            {
                var i = new LinkIncident
                {
                    Start = DateTime.SpecifyKind(d.S, DateTimeKind.Utc),
                    End = DateTime.SpecifyKind(d.E, DateTimeKind.Utc),
                    Kind = (IncidentKind)d.K,
                    Count = d.C,
                    Explained = d.X is int x ? (ExplainReason)x : null,
                    ExplainedDetail = d.Xd,
                    SpeedAfterMbps = d.Sp,
                    AttachedErrors = d.Err,
                };
                foreach (var s in d.By) i.SeenBy.Add((LinkSource)s);
                foreach (var s in d.Miss) i.MissedBy.Add((LinkSource)s);
                // The stored duration stands in for the precise edges the detectors read.
                if (d.D is double secs)
                {
                    var src = i.SeenBy.FirstOrDefault();
                    i.Events.Add(new LinkEvent(i.Start, i.Start, LinkEventType.LinkDown, src, LinkSide.Downstream));
                    i.Events.Add(new LinkEvent(i.Start.AddSeconds(secs), i.Start.AddSeconds(secs), LinkEventType.LinkUp, src, LinkSide.Downstream, SpeedMbps: d.Sp));
                }
                return i;
            }).ToList();
        }
        catch (JsonException) { return []; }
    }

    public static List<CoverageDto> DeserializeCoverage(string? json)
    {
        if (string.IsNullOrEmpty(json)) return [];
        try { return JsonSerializer.Deserialize<List<CoverageDto>>(json) ?? []; }
        catch (JsonException) { return []; }
    }
}
