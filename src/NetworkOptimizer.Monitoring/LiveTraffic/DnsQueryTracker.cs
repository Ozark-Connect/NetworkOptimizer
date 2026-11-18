using System.Net;

namespace NetworkOptimizer.Monitoring.LiveTraffic;

/// <summary>
/// One DNS lookup as Live DNS shows it. <see cref="Answered"/> false is a query no response was
/// seen for within the timeout; <see cref="LatencyMs"/> is -1 when only the response was seen.
/// </summary>
public sealed record LiveDnsRecord(
    DateTime At,
    string ClientIp,
    string ServerIp,
    string Name,
    string QType,
    string RCode,
    double LatencyMs,
    bool Answered,
    IReadOnlyList<DnsAnswer> Answers,
    int ClientPort = 0);

/// <summary>
/// Pairs DNS queries with their responses by (client, client port, transaction id, server) and
/// keeps only the client-facing leg: a query whose source is a client, a response whose
/// destination is one. A routed query is seen twice (the client's bridge and the server's), and
/// a bridged one on the bridge and its member port, so repeats inside a short window are dropped.
/// </summary>
public sealed class DnsQueryTracker
{
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan DuplicateWindow = TimeSpan.FromSeconds(2);
    private const int MaxPending = 5000;
    private const int MaxReady = 2000;

    private readonly record struct Key(string Client, int Port, ushort TxId, string Server);

    private sealed record Pending(DateTime At, string Name, string QType);

    private readonly Dictionary<Key, Pending> _pending = new();
    private readonly Dictionary<Key, DateTime> _done = new();
    private readonly List<LiveDnsRecord> _ready = new();

    /// <summary>
    /// Takes one parsed packet. <paramref name="isClient"/> says whether an address is a site
    /// client (a connected-subnet address that is not the gateway's own).
    /// </summary>
    public void OnPacket(DnsPacket packet, DateTime at, Func<IPAddress, bool> isClient)
    {
        if (!packet.IsResponse)
        {
            if (packet.DstPort != 53 || !isClient(packet.Src)) return;
            var key = new Key(packet.Src.ToString(), packet.SrcPort, packet.TxId, packet.Dst.ToString());
            if (_pending.ContainsKey(key) || _done.ContainsKey(key) || _pending.Count >= MaxPending) return;
            _pending[key] = new Pending(at, packet.Name, packet.QType);
            return;
        }

        if (packet.SrcPort != 53 || !isClient(packet.Dst)) return;
        var rkey = new Key(packet.Dst.ToString(), packet.DstPort, packet.TxId, packet.Src.ToString());
        if (_done.ContainsKey(rkey)) return;
        _done[rkey] = at;
        if (_pending.Remove(rkey, out var query))
        {
            Emit(new LiveDnsRecord(query.At, rkey.Client, rkey.Server, query.Name, query.QType, packet.RCode,
                Math.Max(0, (at - query.At).TotalMilliseconds), true, packet.Answers, rkey.Port));
        }
        else
        {
            Emit(new LiveDnsRecord(at, rkey.Client, rkey.Server, packet.Name, packet.QType, packet.RCode,
                -1, true, packet.Answers, rkey.Port));
        }
    }

    /// <summary>Returns the finished lookups, timing out queries that never got a response.</summary>
    public List<LiveDnsRecord> Drain(DateTime now)
    {
        List<Key>? expired = null;
        foreach (var (key, query) in _pending)
        {
            if (now - query.At < ResponseTimeout) continue;
            (expired ??= new List<Key>()).Add(key);
            Emit(new LiveDnsRecord(query.At, key.Client, key.Server, query.Name, query.QType, "", 0, false,
                Array.Empty<DnsAnswer>(), key.Port));
        }
        if (expired != null)
            foreach (var key in expired)
            {
                _pending.Remove(key);
                _done[key] = now;
            }

        List<Key>? stale = null;
        foreach (var (key, at) in _done)
            if (now - at > DuplicateWindow) (stale ??= new List<Key>()).Add(key);
        if (stale != null)
            foreach (var key in stale) _done.Remove(key);

        var drained = new List<LiveDnsRecord>(_ready);
        _ready.Clear();
        drained.Sort((a, b) => a.At.CompareTo(b.At));
        return drained;
    }

    /// <summary>Forgets everything, for a capture that stops.</summary>
    public void Clear()
    {
        _pending.Clear();
        _done.Clear();
        _ready.Clear();
    }

    private void Emit(LiveDnsRecord record)
    {
        if (_ready.Count < MaxReady) _ready.Add(record);
    }
}
