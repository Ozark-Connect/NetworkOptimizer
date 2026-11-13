namespace NetworkOptimizer.Monitoring.Conntrack;

/// <summary>
/// One flow as Live Traffic shows it, in the client's frame: down is toward the client. Window
/// bytes are this pass's growth; totals are the flow's lifetime counters. <see cref="Lan"/> is a
/// routed flow between two site-local clients (no WAN, empty <see cref="WanIfName"/>);
/// <see cref="Gateway"/> is the gateway's own traffic. <see cref="AgeSeconds"/> is a lower bound
/// when <see cref="AgeIsLowerBound"/>: the flow predates the tracker and the kernel keeps no
/// creation time.
/// </summary>
public sealed record LiveFlowSample(
    string Key,
    string Proto,
    string ClientIp,
    string ClientMac,
    int ClientPort,
    string RemoteIp,
    int RemotePort,
    string WanIfName,
    bool Lan,
    bool Gateway,
    long DownBytes,
    long UpBytes,
    long TotalDownBytes,
    long TotalUpBytes,
    long TotalDownPackets,
    long TotalUpPackets,
    long AgeSeconds,
    bool AgeIsLowerBound);

/// <summary>What one pass hands Live Traffic: the moving flows, the flows that ended, and the table size.</summary>
public sealed record LiveFlowPass(IReadOnlyList<LiveFlowSample> Flows, IReadOnlyList<LiveFlowSample> Ended, int TotalFlows);

/// <summary>
/// Per-flow window deltas for Live Traffic from successive conntrack snapshots. Runs only while a
/// viewer holds the lease (<see cref="Reset"/> between leases), so it costs nothing otherwise.
/// Separate from <see cref="ConntrackAccountant"/> on purpose: the accountant's billing rules
/// (seed, grace, reconcile) exist to never inflate a stored total, while this only shows what
/// moved in the last window and stores nothing.
/// </summary>
public sealed class LiveFlowTracker
{
    // A flow a seq-file read misses for one pass is still alive; two misses in a row is gone.
    private const int MissesBeforeEnded = 2;
    // How long a gone flow's counters are kept, so a flow the reads missed comes back as a delta.
    private const int TombstonePasses = 30;
    private const int MaxTombstones = 20_000;
    // Without conntrack timestamps a flow the first reads missed looks born since the last pass, so
    // for these passes an ageless flow seen for the first time seeds instead of counting its lifetime.
    private const int AgelessSeedPasses = 3;

    private sealed class State
    {
        public long Orig, Reply;
        public DateTime FirstSeen;
        public bool PredatesTracker;
        public int LastSeenPass;
        public bool SentLastWindow;
        public bool EverSent;
        public LiveFlowSample? LastSample;
    }

    private readonly Dictionary<string, State> _flows = new();
    private readonly Dictionary<string, (long Orig, long Reply, int Pass)> _tombstones = new();
    private int _pass;
    private DateTime _lastPassAt;

    /// <summary>Forgets every flow; the next pass seeds again. Called whenever no lease is held.</summary>
    public void Reset()
    {
        if (_pass == 0) return;
        _flows.Clear();
        _tombstones.Clear();
        _pass = 0;
        _lastPassAt = default;
    }

    /// <summary>
    /// Accounts one snapshot. With <paramref name="maxFlows"/> above zero, returns the flows to
    /// send: the busiest by window bytes, plus every flow sent last window that is still alive (so
    /// a flow that goes idle reads as zero instead of vanishing), and the flows that ended since.
    /// </summary>
    public LiveFlowPass? Track(IReadOnlyList<ConntrackFlow> flows, ConntrackHostView view, DateTime now, int maxFlows)
    {
        _pass++;
        var firstPass = _pass == 1;
        var sinceLastPass = firstPass ? 0 : (long)Math.Ceiling((now - _lastPassAt).TotalSeconds);
        _lastPassAt = now;
        var build = maxFlows > 0;
        var moving = build ? new List<(LiveFlowSample Sample, long Window, State State)>() : null;
        var keepIdle = build ? new List<(LiveFlowSample Sample, State State)>() : null;

        foreach (var flow in flows)
        {
            var isNew = !_flows.TryGetValue(flow.Key, out var state);
            var seeding = firstPass || (flow.AgeSeconds < 0 && _pass <= AgelessSeedPasses);
            if (isNew)
            {
                state = new State { FirstSeen = now, PredatesTracker = seeding };
                _flows[flow.Key] = state;
            }
            state!.LastSeenPass = _pass;

            long dOrig, dReply;
            if (isNew && _tombstones.Remove(flow.Key, out var tomb) && flow.OrigBytes >= tomb.Orig && flow.ReplyBytes >= tomb.Reply)
            {
                // A flow the reads missed for two passes, back: only its growth since counts.
                (dOrig, dReply) = (flow.OrigBytes - tomb.Orig, flow.ReplyBytes - tomb.Reply);
            }
            else if (isNew)
            {
                // Born since the last pass: everything it carried moved in this window. While seeding,
                // or when the kernel says it is older than the window, nothing is known about when its
                // bytes moved, so nothing counts (lifetime bytes as one window read as a spike).
                var olderThanWindow = flow.AgeSeconds > sinceLastPass + 1;
                (dOrig, dReply) = seeding || olderThanWindow ? (0, 0) : (flow.OrigBytes, flow.ReplyBytes);
            }
            else if (flow.OrigBytes < state.Orig || flow.ReplyBytes < state.Reply)
            {
                // A counter went backward: a new flow reusing the tuple.
                state.FirstSeen = now;
                state.PredatesTracker = false;
                (dOrig, dReply) = (flow.OrigBytes, flow.ReplyBytes);
            }
            else
            {
                (dOrig, dReply) = (flow.OrigBytes - state.Orig, flow.ReplyBytes - state.Reply);
            }
            state.Orig = flow.OrigBytes;
            state.Reply = flow.ReplyBytes;

            if (!build) continue;
            if (Describe(flow, view, state, now, dOrig, dReply) is not { } sample) continue;
            state.LastSample = sample;
            var window = sample.DownBytes + sample.UpBytes;
            if (window > 0) moving!.Add((sample, window, state));
            else if (state.SentLastWindow) keepIdle!.Add((sample, state));
        }

        var ended = new List<LiveFlowSample>();
        List<string>? gone = null;
        foreach (var (key, state) in _flows)
        {
            if (_pass - state.LastSeenPass < MissesBeforeEnded) continue;
            (gone ??= new List<string>()).Add(key);
            if (build && state.EverSent && state.LastSample is { } last)
                ended.Add(last with { DownBytes = 0, UpBytes = 0 });
        }
        if (gone != null)
        {
            foreach (var key in gone)
            {
                if (_flows.Remove(key, out var dead) && _tombstones.Count < MaxTombstones)
                    _tombstones[key] = (dead.Orig, dead.Reply, _pass);
            }
        }
        if (_pass % TombstonePasses == 0 && _tombstones.Count > 0)
        {
            foreach (var key in _tombstones.Where(t => _pass - t.Value.Pass >= TombstonePasses).Select(t => t.Key).ToList())
                _tombstones.Remove(key);
        }

        if (!build) return null;

        foreach (var state in _flows.Values) state.SentLastWindow = false;
        var sent = new List<LiveFlowSample>();
        foreach (var (sample, _, state) in moving!.OrderByDescending(m => m.Window).Take(maxFlows))
        {
            sent.Add(sample);
            state.SentLastWindow = state.EverSent = true;
        }
        // Carried once at zero, not re-armed: how long an idle flow stays listed is the server's call.
        foreach (var (sample, _) in keepIdle!)
        {
            if (sent.Count >= maxFlows + maxFlows / 4) break;
            sent.Add(sample);
        }
        return new LiveFlowPass(sent, ended, flows.Count);
    }

    private static LiveFlowSample? Describe(ConntrackFlow flow, ConntrackHostView view, State state, DateTime now, long dOrig, long dReply)
    {
        var lan = false;
        var side = ConntrackClassifier.ClassifyWan(flow, view);
        if (side == null)
        {
            side = ConntrackClassifier.ClassifyRoutedLan(flow, view);
            lan = side != null;
        }
        if (side == null) return null;

        // The client end is the original source unless the flow came in from outside (a port
        // forward), where the original source is the remote.
        var clientIsOrig = side.LanEnd == null || side.LanEnd.Equals(flow.OrigSrc);
        var age = flow.AgeSeconds >= 0 ? flow.AgeSeconds : (long)(now - state.FirstSeen).TotalSeconds;
        return new LiveFlowSample(
            flow.Key,
            flow.Proto,
            side.Ip.Length > 0 ? side.Ip : side.LanEnd?.ToString() ?? "",
            side.Mac,
            clientIsOrig ? flow.OrigSport : flow.OrigDport,
            side.Remote?.ToString() ?? "",
            clientIsOrig ? flow.OrigDport : flow.OrigSport,
            side.IfName,
            lan,
            side.IsGateway,
            side.DownIsReply ? dReply : dOrig,
            side.DownIsReply ? dOrig : dReply,
            side.DownIsReply ? flow.ReplyBytes : flow.OrigBytes,
            side.DownIsReply ? flow.OrigBytes : flow.ReplyBytes,
            side.DownIsReply ? flow.ReplyPackets : flow.OrigPackets,
            side.DownIsReply ? flow.OrigPackets : flow.ReplyPackets,
            age,
            flow.AgeSeconds < 0 && state.PredatesTracker);
    }
}
