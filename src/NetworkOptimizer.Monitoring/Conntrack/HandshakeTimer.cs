using System.Net;

namespace NetworkOptimizer.Monitoring.Conntrack;

/// <summary>
/// A flow's handshake as the Gateway timed it, in the flow's original direction. <see cref="OrigLegMs"/>:
/// the original destination answering (TCP SYN to SYN-ACK, UDP first packet to first reply).
/// <see cref="ReplyLegMs"/>: the original source answering that (TCP SYN-ACK to ACK); null for UDP and
/// until it is seen. <see cref="Queued"/>: an event behind it was read from a backlog and may read high.
/// </summary>
public sealed record FlowHandshake(IPAddress OrigSrc, double OrigLegMs, double? ReplyLegMs, bool Queued);

/// <summary>
/// Times connection handshakes from conntrack events: NEW (SYN_SENT) to SYN_RECV to ESTABLISHED for TCP,
/// NEW to the first update that has seen a reply for UDP. Times are when the events were read, which is
/// the only clock netlink offers. Called from the event thread and read from the sampling thread.
/// </summary>
public sealed class HandshakeTimer
{
    /// <summary>
    /// A leg this long means a SYN (or first datagram) was retried: NEW fires on the first one and no event
    /// marks the retry, so the leg would include the retry timer (1 s minimum on Linux, macOS, Windows).
    /// </summary>
    public static readonly TimeSpan RetriedLeg = TimeSpan.FromSeconds(1);

    private static readonly TimeSpan PendingLife = TimeSpan.FromSeconds(3);
    // A result lives as long as its flow (see Prune); this only bounds one whose flow is never pruned.
    private static readonly TimeSpan ResultLife = TimeSpan.FromHours(24);
    // Bounded for small Gateways: about 350 bytes an entry. The oldest go first.
    private const int MaxPending = 20_000;
    private const int MaxResults = 20_000;

    // Flows the last prune did not find in the table. A seq-file read can miss a live flow once, so a
    // result goes only when two prunes in a row miss it.
    private HashSet<string> _missedOnce = new();

    /// <summary>
    /// Drops the results of flows that left the conntrack table. Only results older than
    /// <paramref name="minAge"/> are judged, since a flow timed after the snapshot was read is not in it.
    /// </summary>
    public void Prune(Func<string, bool> alive, double nowSeconds, TimeSpan minAge)
    {
        lock (_gate)
        {
            var missed = new HashSet<string>();
            List<string>? gone = null;
            foreach (var (key, r) in _results)
            {
                if (nowSeconds - r.At <= minAge.TotalSeconds || alive(key)) continue;
                if (_missedOnce.Contains(key)) (gone ??= new()).Add(key);
                else missed.Add(key);
            }
            if (gone != null) foreach (var key in gone) _results.Remove(key);
            _missedOnce = missed;
        }
    }

    private sealed class Pending
    {
        public required IPAddress OrigSrc;
        public required bool Tcp;
        public required double NewAt;
        public double SynRecvAt = double.NaN;
        public bool Queued;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Pending> _pending = new();
    private readonly Dictionary<string, (FlowHandshake Value, double At)> _results = new();
    private readonly Queue<(string Key, double At)> _resultOrder = new();
    private double _lastSweep;

    public int PendingCount { get { lock (_gate) return _pending.Count; } }
    public int ResultCount { get { lock (_gate) return _results.Count; } }

    /// <summary>One event, read at <paramref name="atSeconds"/> (any monotonic clock, in seconds).</summary>
    public void OnEvent(ConntrackEvent e, double atSeconds, bool queued)
    {
        lock (_gate)
        {
            Sweep(atSeconds);
            if (e.IsNew)
            {
                // A reused tuple starts over: the old flow's result no longer describes it.
                _results.Remove(e.Key);
                var tcp = e.Proto == "tcp";
                // TCP picked up mid-stream (loose tracking) has no handshake to time.
                if (tcp && e.TcpState != ConntrackTcpState.SynSent) return;
                if (_pending.Count >= MaxPending) return;
                _pending[e.Key] = new Pending { OrigSrc = e.OrigSrc, Tcp = tcp, NewAt = atSeconds, Queued = queued };
                return;
            }
            if (!_pending.TryGetValue(e.Key, out var p)) return;
            if (p.Tcp)
            {
                if (e.TcpState == ConntrackTcpState.SynRecv && double.IsNaN(p.SynRecvAt))
                {
                    if (!Leg(p.NewAt, atSeconds, out var orig)) { _pending.Remove(e.Key); return; }
                    p.SynRecvAt = atSeconds;
                    p.Queued |= queued;
                    Store(e.Key, new FlowHandshake(p.OrigSrc, orig, null, p.Queued), atSeconds);
                }
                else if (e.TcpState == ConntrackTcpState.Established && !double.IsNaN(p.SynRecvAt))
                {
                    _pending.Remove(e.Key);
                    if (!Leg(p.SynRecvAt, atSeconds, out var replyLeg)) return;
                    if (_results.TryGetValue(e.Key, out var r))
                        _results[e.Key] = (r.Value with { ReplyLegMs = replyLeg, Queued = r.Value.Queued || queued }, r.At);
                }
            }
            else if (e.SeenReply)
            {
                _pending.Remove(e.Key);
                if (Leg(p.NewAt, atSeconds, out var orig))
                    Store(e.Key, new FlowHandshake(p.OrigSrc, orig, null, p.Queued || queued), atSeconds);
            }
        }
    }

    /// <summary>
    /// Events were lost (ENOBUFS): any handshake in progress may have missed one, and a later update in
    /// the same state would then complete it with a leg that is too long. Finished results stand.
    /// </summary>
    public void OnLoss()
    {
        lock (_gate) _pending.Clear();
    }

    public bool TryGet(string key, out FlowHandshake handshake)
    {
        lock (_gate)
        {
            if (_results.TryGetValue(key, out var r)) { handshake = r.Value; return true; }
            handshake = null!;
            return false;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _pending.Clear();
            _results.Clear();
            _resultOrder.Clear();
        }
    }

    private static bool Leg(double from, double to, out double ms)
    {
        ms = (to - from) * 1000;
        return ms >= 0 && ms < RetriedLeg.TotalMilliseconds;
    }

    private void Store(string key, FlowHandshake value, double at)
    {
        if (_results.Count >= MaxResults && !_results.ContainsKey(key)) EvictOldest();
        _results[key] = (value, at);
        _resultOrder.Enqueue((key, at));
    }

    private void EvictOldest()
    {
        while (_resultOrder.TryDequeue(out var old))
        {
            // A queue entry is stale when the key was stored again later.
            if (_results.TryGetValue(old.Key, out var r) && r.At == old.At)
            {
                _results.Remove(old.Key);
                return;
            }
        }
    }

    private void Sweep(double now)
    {
        if (now - _lastSweep < 1) return;
        _lastSweep = now;
        List<string>? stale = null;
        foreach (var (key, p) in _pending)
            if (now - p.NewAt > PendingLife.TotalSeconds) (stale ??= new()).Add(key);
        if (stale != null) foreach (var key in stale) _pending.Remove(key);
        while (_resultOrder.TryPeek(out var old) && now - old.At > ResultLife.TotalSeconds)
        {
            _resultOrder.Dequeue();
            if (_results.TryGetValue(old.Key, out var r) && r.At == old.At) _results.Remove(old.Key);
        }
        // The order queue keeps one entry per store; drop it when it runs well past the live results.
        if (_resultOrder.Count > 2 * MaxResults)
        {
            var keep = _results.Select(kv => (kv.Key, kv.Value.At)).OrderBy(x => x.At).ToList();
            _resultOrder.Clear();
            foreach (var x in keep) _resultOrder.Enqueue(x);
        }
    }
}
