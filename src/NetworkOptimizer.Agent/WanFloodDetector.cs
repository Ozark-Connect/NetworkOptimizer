using NetworkOptimizer.AgentProtocol;
using NetworkOptimizer.Monitoring.WanFlood;

namespace NetworkOptimizer.Agent;

/// <summary>
/// WAN Flood Protection's detector. Samples the receive path every second (per-CPU softirq, backlog drops,
/// conntrack fill) and the unsolicited-drop counters every five, and fires on one CPU overloaded by new
/// connections that never confirm. It then names what carries the flood by its share of those drops,
/// measured with tc filters that drop nothing, and asks <see cref="WanFloodGuard"/> to drop it. It never
/// picks a source by rate: a download is the top talker and carries none of the unsolicited drops.
/// </summary>
public sealed class WanFloodDetector
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan CounterEvery = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan AttributionSettle = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MeasureWindow = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ReattributeEvery = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan QuietToEnd = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan QuietToCool = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ContinueWithin = TimeSpan.FromSeconds(120);
    private const int TopPerTable = 3;

    private readonly WanFloodGuard _guard;
    private readonly Action<AgentMessage> _enqueue;
    private readonly FloodTrigger _trigger = new();

    private Dictionary<int, CpuTimes>? _lastCpu;
    private List<SoftnetRow>? _lastSoftnet;
    private long? _lastConntrackFailures;
    private DateTime _lastCounterAt = DateTime.MinValue;
    private long? _lastUnsolicitedTotal;
    private double _unsolicitedPps;
    private long? _lastShaperTotal;
    private double _shaperDropsPps;
    private string? _triggerReason;
    private HashSet<int> _receiveCpus = [];
    private DateTime _receiveCpusAt = DateTime.MinValue;

    private string _state = "idle";
    private Incident? _incident;
    private DateTime _lastTriggerAt = DateTime.MinValue;
    private DateTime _nextAttribution = DateTime.MaxValue;
    private DateTime _lastEndedAt = DateTime.MinValue;
    private readonly List<WanFloodDrop> _wouldDrops = new();

    private bool _conntrackPressure;
    private int _fillSeconds;
    private int _clearSeconds;
    private DateTime _nextForwardAttribution = DateTime.MaxValue;

    private volatile int _tableFullSize;
    private DateTime _tableFullAt = DateTime.MinValue;
    private DateTime _lastRestoreAt = DateTime.MinValue;

    public WanFloodDetector(WanFloodGuard guard, Action<AgentMessage> enqueue, KernelLogFollower kernelLog)
    {
        _guard = guard;
        _enqueue = enqueue;
        kernelLog.Subscribe(line =>
        {
            if (WanFloodParsers.ParseHashlimitFull(line) is { } size) _tableFullSize = size;
        });
    }

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Tick, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            try
            {
                if (!_guard.Enabled)
                {
                    if (_incident != null) EndIncident(DateTime.UtcNow);
                    _trigger.Clear();
                    continue;
                }
                await StepAsync(DateTime.UtcNow, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"WAN flood protection: detector step failed: {ex.Message}");
            }
        }
    }

    private async Task StepAsync(DateTime now, CancellationToken ct)
    {
        var t = _guard.Thresholds;
        if (now - _receiveCpusAt > TimeSpan.FromMinutes(1))
        {
            _receiveCpus = ReceiveCpus(_guard.Wans);
            _receiveCpusAt = now;
        }
        if (now - _lastCounterAt >= CounterEvery) ReadCounters(now);
        var sample = Sample(now);
        if (sample != null) _trigger.Add(sample);
        var conntrackPercent = SampleConntrack(t);

        HandleTableFull(now, t);
        if (now >= _nextPortCheck)
        {
            _nextPortCheck = now + ReattributeEvery;
            RecheckPortDrops();
        }

        var trigger = _trigger.Evaluate(t, _receiveCpus);
        if (trigger.Suspected)
        {
            _lastTriggerAt = now;
            _triggerReason = trigger.Reason;
        }

        if (_incident == null && (trigger.Suspected || _conntrackPressure))
            BeginIncident(now, trigger.Suspected ? "flood" : "conntrack");

        if (_incident != null)
        {
            _incident.PeakUnsolicited = Math.Max(_incident.PeakUnsolicited, (int)_unsolicitedPps);
            var (cpu, pct) = _trigger.Hottest(_receiveCpus);
            if (pct > _incident.PeakSoftirq) { _incident.PeakSoftirq = (int)pct; _incident.HotCpu = cpu; }
            _incident.PeakConntrack = Math.Max(_incident.PeakConntrack, conntrackPercent);
            if (sample != null) _incident.SoftnetDropped += sample.DroppedDelta.Values.Sum();
            if (!_guard.PerSource) _incident.TableFull = true;

            if (trigger.Suspected && now >= _nextAttribution && _guard.Detecting)
            {
                await AttributeAsync(ct);
                _nextAttribution = DateTime.UtcNow + ReattributeEvery;
                _guard.ExtendLimits(forward: false);
            }
            if (_conntrackPressure && now >= _nextForwardAttribution && _guard.Detecting)
            {
                ArmForwardCaps();
                _nextForwardAttribution = DateTime.UtcNow + ReattributeEvery;
                _guard.ExtendLimits(forward: true);
            }

            var quiet = now - _lastTriggerAt;
            var state = trigger.Suspected
                ? (_guard.Actions().Count > 0 || _wouldDrops.Count > 0 ? "mitigating" : "suspected")
                : _conntrackPressure ? "mitigating" : quiet >= QuietToCool ? "cooling" : _state;
            SetState(state);
            if (!trigger.Suspected && !_conntrackPressure && quiet >= QuietToEnd)
                EndIncident(now);
        }
        _guard.Counters = (Counters.Count, Counters.Max, Counters.L1, Counters.L3, Counters.WanLocal);
    }

    // ---- Samples ----

    private (int Count, int Max, long L1, long L3, bool WanLocal) Counters;

    private LoadSample? Sample(DateTime now)
    {
        var cpu = WanFloodParsers.ParseProcStat(ReadFile("/proc/stat"));
        var softnet = WanFloodParsers.ParseSoftnetStat(ReadFile("/proc/net/softnet_stat"));
        LoadSample? sample = null;
        if (_lastCpu != null && _lastSoftnet != null)
        {
            var softirq = new Dictionary<int, double>();
            var dropped = new Dictionary<int, long>();
            var squeezed = new Dictionary<int, long>();
            foreach (var (n, times) in cpu)
                if (_lastCpu.TryGetValue(n, out var before)) softirq[n] = WanFloodParsers.SoftirqPercent(before, times);
            for (var i = 0; i < softnet.Count && i < _lastSoftnet.Count; i++)
            {
                dropped[i] = Math.Max(0, softnet[i].Dropped - _lastSoftnet[i].Dropped);
                squeezed[i] = Math.Max(0, softnet[i].TimeSqueeze - _lastSoftnet[i].TimeSqueeze);
            }
            sample = new LoadSample(now, softirq, dropped, squeezed, _unsolicitedPps, _shaperDropsPps);
        }
        _lastCpu = cpu;
        _lastSoftnet = softnet;
        return sample;
    }

    // Updates the pressure flag; returns the fill percent.
    private int SampleConntrack(FloodThresholds t)
    {
        int.TryParse(ReadFile("/proc/sys/net/netfilter/nf_conntrack_count").Trim(), out var count);
        int.TryParse(ReadFile("/proc/sys/net/netfilter/nf_conntrack_max").Trim(), out var max);
        Counters = Counters with { Count = count, Max = max };
        // Only the table turning connections away counts (drop, early_drop); see ParseConntrackDrops.
        var drops = WanFloodParsers.ParseConntrackDrops(ReadFile("/proc/net/stat/nf_conntrack"));
        var percent = max > 0 ? (int)(100L * count / max) : 0;
        // Below the fill threshold a drop is an unresolved insert clash, not a full table: never pressure on its own.
        var dropping = percent >= t.ConntrackFillPercent && drops is { } d && _lastConntrackFailures is { } ld && d > ld;
        _lastConntrackFailures = drops;

        if (percent >= t.ConntrackFillPercent || dropping) { _fillSeconds++; _clearSeconds = 0; }
        else if (percent < t.ConntrackClearPercent) { _clearSeconds++; _fillSeconds = 0; }
        else { _fillSeconds = 0; _clearSeconds = 0; }

        if (!_conntrackPressure && (_fillSeconds >= 10 || dropping))
        {
            _conntrackPressure = true;
            _guard.SetAttribution(_state != "idle" || _incident != null, forward: true);
            _nextForwardAttribution = DateTime.UtcNow + AttributionSettle;
            Console.WriteLine($"WAN flood protection: conntrack table under pressure ({count}/{max}{(dropping ? ", dropping connections" : "")})");
        }
        else if (_conntrackPressure && _clearSeconds >= 60)
        {
            _conntrackPressure = false;
            _nextForwardAttribution = DateTime.MaxValue;
            _guard.SetAttribution(_incident is { Kind: not "conntrack" } && _state != "idle", forward: false);
            Console.WriteLine("WAN flood protection: conntrack table back to normal");
        }
        return percent;
    }

    // Two iptables-save -c reads per family (filter for UniFi's WAN-local drop, mangle for ours). iptables-save
    // takes no xtables lock, so this never holds up a UniFi provision.
    private void ReadCounters(DateTime now)
    {
        long total = 0;
        var wanLocal = false;
        long l1 = 0, l3 = 0;
        foreach (var tool in new[] { "iptables-save", "ip6tables-save" })
        {
            var filter = WanFloodGuard.Run(tool, ["-c", "-t", "filter"], null);
            if (filter.Exit == 0
                && WanFloodParsers.LastDropPackets(WanFloodParsers.ParseSaveCounters(filter.Stdout, "UBIOS_WAN_LOCAL_USER")) is { } drop)
            {
                total += drop;
                wanLocal = true;
            }
            var mangle = WanFloodGuard.Run(tool, ["-c", "-t", "mangle"], null);
            if (mangle.Exit != 0) continue;
            l1 += WanFloodParsers.DropPackets(WanFloodParsers.ParseSaveCounters(mangle.Stdout, WanFloodRules.Chain));
            l3 += WanFloodParsers.DropPackets(WanFloodParsers.ParseSaveCounters(mangle.Stdout, WanFloodRules.ForwardChain));
        }
        total += l1;
        // The Smart Queues ingress shaper's loss, from each WAN's IFB (absent on a WAN without Smart Queues).
        long shaper = 0;
        foreach (var wan in _guard.Wans)
        {
            var qdisc = WanFloodGuard.Run("tc", ["-s", "qdisc", "show", "dev", "ifb" + wan], null);
            if (qdisc.Exit == 0) shaper += WanFloodParsers.ParseLeafQdiscDrops(qdisc.Stdout);
        }
        var elapsed = Math.Max(1, (now - _lastCounterAt).TotalSeconds);
        // A decrease is a reset (a firewall provision, a chain rewrite, a Smart Queues re-provision): skip that interval.
        _unsolicitedPps = _lastUnsolicitedTotal is { } last && total >= last && _lastCounterAt != DateTime.MinValue
            ? (total - last) / elapsed : 0;
        _shaperDropsPps = _lastShaperTotal is { } lastShaper && shaper >= lastShaper && _lastCounterAt != DateTime.MinValue
            ? (shaper - lastShaper) / elapsed : 0;
        _lastUnsolicitedTotal = total;
        _lastShaperTotal = shaper;
        _lastCounterAt = now;
        Counters = Counters with { L1 = l1, L3 = l3, WanLocal = wanLocal };
    }

    // The CPUs a WAN's packets are steered to (its RPS masks, and its parent's for a VLAN). Empty = all.
    private static HashSet<int> ReceiveCpus(IReadOnlyList<string> wans)
    {
        var cpus = new HashSet<int>();
        foreach (var wan in wans)
        {
            var devices = new List<string> { wan };
            if (wan.Contains('.')) devices.Add(wan[..wan.IndexOf('.')]);
            foreach (var dev in devices)
            {
                var queues = $"/sys/class/net/{dev}/queues";
                if (!Directory.Exists(queues)) continue;
                foreach (var rx in Directory.GetDirectories(queues, "rx-*"))
                    cpus.UnionWith(WanFloodParsers.ParseCpuMask(ReadFile(Path.Combine(rx, "rps_cpus"))));
            }
        }
        return cpus;
    }

    // ---- Table full ----

    private void HandleTableFull(DateTime now, FloodThresholds t)
    {
        var size = _tableFullSize;
        if (size != 0)
        {
            _tableFullSize = 0;
            if (size == WanFloodRules.SourceTableMax) _tableFullAt = now;
            if (size == WanFloodRules.ForwardTableMax) _forwardFullAt = now;
            if (size == WanFloodRules.SourceTableMax && _guard.PerSource)
            {
                Console.WriteLine("WAN flood protection: a per-source table is full; removing the per-source rules until it drains");
                _guard.SetPerSource(false);
                if (_incident != null) { _incident.TableFull = true; SendIncident("update"); }
            }
            else if (size == WanFloodRules.ForwardTableMax && _guard.ForwardLimits)
            {
                // A wide forwarded range being scanned makes one key per port. A full table drops every new
                // forwarded connection, so the limit itself goes, not just its attribution.
                _forwardFullAt = now;
                Console.WriteLine("WAN flood protection: the port-forward table is full; removing the port-forward limit until it drains");
                _guard.SetForwardLimits(false);
                _guard.SetAttribution(_state != "idle", forward: false);
                if (_incident != null) { _incident.TableFull = true; SendIncident("update"); }
            }
            else if (size != WanFloodRules.SourceTableMax && size != WanFloodRules.ForwardTableMax)
            {
                Console.Error.WriteLine($"WAN flood protection: a hashlimit table of {size} entries reported full");
            }
        }
        // Restore after a quiet minute, at most every 5 minutes, so a flood near the limit cannot flap the chain.
        if (!_guard.PerSource && now - _tableFullAt >= TimeSpan.FromSeconds(60) && _unsolicitedPps < t.UnsolicitedPps
            && now - _lastRestoreAt >= TimeSpan.FromMinutes(5))
        {
            _lastRestoreAt = now;
            Console.WriteLine("WAN flood protection: restoring the per-source rules");
            _guard.SetPerSource(true);
        }
        if (!_guard.ForwardLimits && now - _forwardFullAt >= TimeSpan.FromSeconds(60)
            && now - _lastForwardRestoreAt >= TimeSpan.FromMinutes(5))
        {
            _lastForwardRestoreAt = now;
            Console.WriteLine("WAN flood protection: restoring the port-forward limit");
            _guard.SetForwardLimits(true);
        }
    }

    private DateTime _forwardFullAt = DateTime.MinValue;
    private DateTime _nextPortCheck = DateTime.MinValue;
    private DateTime _lastForwardRestoreAt = DateTime.MinValue;

    // ---- Attribution ----

    private async Task AttributeAsync(CancellationToken ct)
    {
        var t = _guard.Thresholds;
        var limits = _guard.Limits;
        var wans = _guard.Wans.Select(w => (Wan: w, Ip: _guard.WanIp(w))).Where(w => w.Ip != null).ToList();
        if (wans.Count == 0 || _unsolicitedPps <= 0) return;

        // Top sources and ports per protocol, from the attribution tables (keys over the attribution rate).
        var tops = new Dictionary<string, (List<string> Sources, List<int> Ports)>();
        foreach (var protocol in new[] { "udp", "tcp" })
        {
            var sources = _guard.PerSource
                ? Top(WanFloodRules.AttributionSourceTable(limits, protocol)).Select(e => e.Source).Distinct().Take(TopPerTable).ToList()
                : [];
            var ports = Top(WanFloodRules.AttributionPortTable(limits, protocol)).Select(e => e.DestinationPort).Distinct().Take(TopPerTable).ToList();
            tops[protocol] = (sources, ports);
        }

        var candidates = new List<FloodCandidate>();
        foreach (var (wan, ip) in wans)
            foreach (var (protocol, (sources, ports)) in tops)
                foreach (var source in sources)
                    foreach (var port in ports)
                        candidates.Add(new FloodCandidate(wan, source, ip!, protocol, port, 0));

        var unsolicited = _unsolicitedPps * MeasureWindow.TotalSeconds;
        var added = new List<WanFloodDrop>();
        if (candidates.Count > 0)
        {
            var measured = await _guard.MeasureAsync(candidates, MeasureWindow, ct);
            var refused = WithRefusals(measured, unsolicited, t);
            foreach (var (c, share) in FloodAttribution.ChooseSources(refused, unsolicited, t, 8))
                if (_guard.AddDrop(c, share, "tc-source") is { } drop) added.Add(drop);
        }

        // No dominant source (spoofed or reflected): look at the port instead.
        if (added.Count == 0)
        {
            var portCandidates = new List<FloodCandidate>();
            foreach (var (wan, ip) in wans)
                foreach (var (protocol, (_, ports)) in tops)
                    foreach (var port in ports.Take(1))
                        portCandidates.Add(new FloodCandidate(wan, null, ip!, protocol, port, 0));
            if (portCandidates.Count > 0)
            {
                foreach (var c in await _guard.MeasureAsync(portCandidates, MeasureWindow, ct))
                {
                    if (!FloodAttribution.PortDominates(c.Packets, unsolicited, t)) continue;
                    var share = Math.Min(1.0, c.Packets / unsolicited);
                    var drop = PortClosed(c) ? _guard.AddDrop(c, share, "tc-port") : _guard.ArmPortLimit(c.Wan, c.Protocol, c.Port, share);
                    if (drop != null) added.Add(drop);
                }
            }
        }

        if (_incident == null) return;
        var newWould = added.Where(d => d.WouldDrop && !_wouldDrops.Any(w => SameAction(w, d))).ToList();
        _wouldDrops.AddRange(newWould);
        if (added.Any(d => !d.WouldDrop) || newWould.Count > 0)
        {
            if (_incident.Kind is "flood" or "conntrack" && added.FirstOrDefault() is { } first) _incident.Kind = first.Protocol == "tcp" ? "syn" : "udp";
            SendIncident("update");
        }
    }

    private static List<HashlimitEntry> Top(string table) =>
        WanFloodParsers.ParseHashlimit(ReadFile(Path.Combine(WanFloodRules.HashlimitProcDir(false), table)))
            .Where(e => e.OverLimit)
            .OrderBy(e => e.Credit)
            .ToList();

    // A candidate with a real share is refused when it is a guarded address, not a public one, or holds a real
    // session. The session check is one pass over the conntrack table for every candidate together.
    private List<FloodCandidate> WithRefusals(List<FloodCandidate> measured, double unsolicited, FloodThresholds t)
    {
        var result = measured.Select(c => c.Source == null || c.Packets / unsolicited < t.MinShare ? c
            : _guard.IsGuarded(c.Source) ? c with { Refusal = "guarded" }
            : NetworkOptimizer.Core.Helpers.BlocklistEntry.Canonical(c.Source) == null ? c with { Refusal = "not public" }
            : c).ToList();
        var keys = result.Where(c => c.Source != null && c.Refusal == null && c.Packets / unsolicited >= t.MinShare)
            .Select(c => (c.Source!, c.Destination, c.Protocol, c.Port)).ToList();
        if (keys.Count == 0) return result;
        using var reader = OpenConntrack();
        // Unreadable conntrack: refuse rather than risk dropping a real session.
        var sessions = reader == null ? keys.ToHashSet() : WanFloodParsers.ScanConntrackSessions(reader, keys);
        return result.Select(c => c.Source != null && c.Refusal == null && sessions.Contains((c.Source, c.Destination, c.Protocol, c.Port))
            ? c with { Refusal = "has a session" } : c).ToList();
    }

    // Held port-only drops stay safe only while their port stays closed: a listener, a port forward, a UPnP
    // mapping, or a NAT'd flow can take the port after the drop went in.
    private void RecheckPortDrops()
    {
        foreach (var c in _guard.PortDrops())
            if (!PortClosed(c)) _guard.RemovePortDrop(c);
    }

    // Closed: no socket on the port, no port forward for it, no session to it, and no NAT'd outbound flow using it. Only then
    // is a port-only tc drop safe, since tc cannot tell a reply from a flood.
    private static bool PortClosed(FloodCandidate c)
    {
        var tcp = c.Protocol == "tcp";
        var file = tcp ? "tcp" : "udp";
        var ports = WanFloodParsers.ParseProcNetPorts(ReadFile($"/proc/net/{file}"), tcp);
        ports.UnionWith(WanFloodParsers.ParseProcNetPorts(ReadFile($"/proc/net/{file}6"), tcp));
        if (ports.Contains(c.Port)) return false;
        var nat = WanFloodGuard.Run("iptables-save", ["-t", "nat"], null);
        if (nat.Exit != 0 || WanFloodParsers.ParseDnatPorts(nat.Stdout).Contains(c.Port)) return false;
        using var reader = OpenConntrack();
        if (reader == null) return false;
        var (_, replyToPort, anyToPort) = WanFloodParsers.ScanConntrack(reader, null, c.Destination, c.Protocol, c.Port);
        return !replyToPort && !anyToPort;
    }

    private void ArmForwardCaps()
    {
        var limits = _guard.Limits;
        var wan = _guard.Wans.FirstOrDefault();
        if (wan == null) return;
        var entries = WanFloodParsers.ParseHashlimit(ReadFile(Path.Combine(WanFloodRules.HashlimitProcDir(false), WanFloodRules.ForwardAttributionTable(limits))))
            .Where(e => e.OverLimit).OrderBy(e => e.Credit).Take(4).ToList();
        var armed = false;
        foreach (var e in entries)
            foreach (var protocol in new[] { "tcp", "udp" })
                if (_guard.ArmForwardCap(wan, protocol, e.Destination, e.DestinationPort, 0) is { } drop)
                {
                    armed = true;
                    if (drop.WouldDrop && !_wouldDrops.Any(w => SameAction(w, drop))) _wouldDrops.Add(drop);
                }
        if (armed && _incident != null) SendIncident("update");
    }

    private static bool SameAction(WanFloodDrop a, WanFloodDrop b) =>
        a.Kind == b.Kind && a.Wan == b.Wan && a.Protocol == b.Protocol && a.Source == b.Source && a.Destination == b.Destination && a.Port == b.Port;

    // ---- Incidents ----

    private void BeginIncident(DateTime now, string kind)
    {
        // A flood back within two minutes of the last one continues it.
        var id = _lastEnded is { } last && now - _lastEndedAt <= ContinueWithin ? last.Id : Guid.NewGuid().ToString();
        var started = _lastEnded is { } prev && prev.Id == id ? prev.StartedAt : now;
        // The start report carries the numbers that fired it: the server's alert is built from this one.
        var (hotCpu, hotPct) = _trigger.Hottest(_receiveCpus);
        _incident = new Incident(id, started, kind)
        {
            Wan = string.Join(",", _guard.Wans),
            PeakUnsolicited = (int)_unsolicitedPps,
            PeakSoftirq = (int)hotPct,
            HotCpu = hotCpu,
        };
        _wouldDrops.Clear();
        _nextAttribution = now + AttributionSettle;
        if (_guard.Detecting) _guard.SetAttribution(true, _conntrackPressure);
        SetState("suspected");
        var why = _triggerReason switch
        {
            "rate" => ", sustained unsolicited drops",
            "shaper" => ", ingress shaper dropping",
            "cpu" => ", receive core overloaded",
            _ => "",
        };
        Console.WriteLine($"WAN flood protection: {(kind == "conntrack" ? "conntrack pressure" : "flood suspected")} "
            + $"({_unsolicitedPps:0} unsolicited/s{why})");
        SendIncident("start");
    }

    private Incident? _lastEnded;

    private void EndIncident(DateTime now)
    {
        if (_incident == null) return;
        _incident.EndedAt = now;
        SendIncident("end");
        Console.WriteLine("WAN flood protection: flood over");
        _lastEnded = _incident;
        _lastEndedAt = now;
        _incident = null;
        _nextAttribution = DateTime.MaxValue;
        _guard.SetAttribution(false, _conntrackPressure);
        SetState("idle");
    }

    private void SendIncident(string phase)
    {
        if (_incident is not { } i) return;
        var message = new WanFloodIncident
        {
            IncidentId = i.Id,
            Phase = phase,
            Wan = i.Wan,
            Kind = i.Kind,
            StartedUnixMs = new DateTimeOffset(i.StartedAt).ToUnixTimeMilliseconds(),
            EndedUnixMs = i.EndedAt is { } end ? new DateTimeOffset(end).ToUnixTimeMilliseconds() : 0,
            PeakUnsolicitedPps = i.PeakUnsolicited,
            PeakSoftirqPercent = i.PeakSoftirq,
            HotCpu = i.HotCpu,
            SoftnetDropped = i.SoftnetDropped,
            PeakConntrackPercent = i.PeakConntrack,
            TableFull = i.TableFull,
            Mode = _guard.Enforcing ? "enforce" : "detect",
        };
        message.Actions.AddRange(_guard.Actions());
        message.Actions.AddRange(_wouldDrops);
        _enqueue(new AgentMessage { WanFloodIncident = message });
    }

    private void SetState(string state)
    {
        if (_state == state) return;
        _state = state;
        _guard.DetectorState = state;
        _guard.ReportNow();
    }

    private sealed class Incident(string id, DateTime startedAt, string kind)
    {
        public string Id { get; } = id;
        public DateTime StartedAt { get; } = startedAt;
        public string Kind { get; set; } = kind;
        public string Wan { get; set; } = "";
        public DateTime? EndedAt { get; set; }
        public int PeakUnsolicited { get; set; }
        public int PeakSoftirq { get; set; }
        public int HotCpu { get; set; } = -1;
        public long SoftnetDropped { get; set; }
        public int PeakConntrack { get; set; }
        public bool TableFull { get; set; }
    }

    // ---- Files ----

    private static string ReadFile(string path)
    {
        try { return File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
    }

    private static StreamReader? OpenConntrack()
    {
        try { return new StreamReader("/proc/net/nf_conntrack"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
