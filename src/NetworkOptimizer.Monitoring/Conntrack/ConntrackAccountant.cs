using System.Net;
using NetworkOptimizer.Core.Helpers;

namespace NetworkOptimizer.Monitoring.Conntrack;

/// <summary>One client's WAN byte deltas over a sample window, per egress interface.
/// Empty <see cref="Ip"/> and <see cref="Mac"/> is the explicit unattributed remainder;
/// a non-empty Ip with an empty Mac is an endpoint the server must map (the gateway's own
/// addresses among them). <see cref="ReconDownBytes"/>/<see cref="ReconUpBytes"/> are
/// destroy-event reconcile bytes - exact, but possibly minutes late - which totals readers
/// add and rate readers ignore.</summary>
public sealed record ClientWanDelta(
    string Ip, string Mac, string WanIfName, long DownBytes, long UpBytes, int Flows,
    long ReconDownBytes = 0, long ReconUpBytes = 0);

/// <summary>
/// Turns successive conntrack table snapshots into per-client WAN window deltas, and destroy
/// events into per-flow reconcile deltas. Per-tuple state persists across snapshots: a seed
/// (the counters at first sight, billed only when the flow's death reconciles), the last
/// billed counters, and a last-seen pass - so a tuple a seq-file read misses for a pass keeps
/// its baseline instead of re-seeding and losing its growth. The first pass seeds and emits
/// nothing: a flow's pre-existing total must not be billed to the window the runner started in.
/// </summary>
public sealed class ConntrackAccountant
{
    // A tuple unseen this many passes is gone (its destroy event was lost or never fired for
    // us): evicted unbilled - an undercount, never an inflation, which is this feed's doctrine.
    private const int RetainUnseenPasses = 5;

    private sealed class Entry
    {
        public long Orig, Reply;
        public long SeedOrig, SeedReply;
        // Whether the seed is billable at destroy: true for a flow first seen mid-run (its
        // seed is its own pre-first-sight bytes), false for flows already in the very first
        // snapshot (their seed is pre-coverage history, which DPI-sourced hours already hold).
        public bool SeedBillable;
        public int LastSeenPass;
    }

    private readonly Dictionary<string, Entry> _flows = new();
    private int _pass;

    /// <summary>Whether a pass has seeded the snapshot yet (the first emits no deltas).</summary>
    public bool Seeded => _pass > 0;

    /// <summary>
    /// Accounts one snapshot against the tracked state, returning per-client deltas summed by
    /// (client, egress interface). WAN flows only: inter-VLAN routed traffic - which the
    /// gateway also conntracks - is excluded by the both-ends-site-local test, the exact
    /// mistake UniFi's own tallies make.
    /// </summary>
    public List<ClientWanDelta> Account(IReadOnlyList<ConntrackFlow> flows, ConntrackHostView view)
    {
        _pass++;
        var seededBefore = _pass > 1;
        var totals = new Dictionary<(string Ip, string Mac, string IfName), (long Down, long Up, int Flows)>();

        foreach (var flow in flows)
        {
            if (_flows.TryGetValue(flow.Key, out var entry))
            {
                entry.LastSeenPass = _pass;
                // A counter that went backward is a new flow reusing the tuple. SEED-ONLY,
                // never billed its full total: billing a reappearing flow's cumulative bytes
                // as one window inflated a client by orders of magnitude. The seed is retained
                // and billed if and when the flow's destroy event reconciles it.
                if (flow.OrigBytes < entry.Orig || flow.ReplyBytes < entry.Reply)
                {
                    entry.Orig = flow.OrigBytes;
                    entry.Reply = flow.ReplyBytes;
                    entry.SeedOrig = flow.OrigBytes;
                    entry.SeedReply = flow.ReplyBytes;
                    entry.SeedBillable = true;
                    continue;
                }
                var dOrig = flow.OrigBytes - entry.Orig;
                var dReply = flow.ReplyBytes - entry.Reply;
                entry.Orig = flow.OrigBytes;
                entry.Reply = flow.ReplyBytes;
                if (dOrig == 0 && dReply == 0) continue;
                if (!seededBefore) continue;

                if (Classify(flow, view) is not { } c) continue;
                var key = (c.Ip, c.Mac, c.IfName);
                var sum = totals.TryGetValue(key, out var t) ? t : (0L, 0L, 0);
                totals[key] = (
                    sum.Item1 + (c.DownIsReply ? dReply : dOrig),
                    sum.Item2 + (c.DownIsReply ? dOrig : dReply),
                    sum.Item3 + 1);
            }
            else
            {
                _flows[flow.Key] = new Entry
                {
                    Orig = flow.OrigBytes,
                    Reply = flow.ReplyBytes,
                    SeedOrig = flow.OrigBytes,
                    SeedReply = flow.ReplyBytes,
                    SeedBillable = seededBefore,
                    LastSeenPass = _pass,
                };
            }
        }

        // Evict tuples gone from the table longer than the retention: their destroy event was
        // lost, and holding them forever would grow the map with the table's whole history.
        List<string>? stale = null;
        foreach (var (key, entry) in _flows)
            if (_pass - entry.LastSeenPass > RetainUnseenPasses)
                (stale ??= new List<string>()).Add(key);
        if (stale != null)
            foreach (var key in stale) _flows.Remove(key);

        return totals
            .Select(kv => new ClientWanDelta(kv.Key.Ip, kv.Key.Mac, kv.Key.IfName, kv.Value.Down, kv.Value.Up, kv.Value.Flows))
            .Where(d => d.DownBytes > 0 || d.UpBytes > 0)
            .ToList();
    }

    /// <summary>
    /// Reconciles one destroy event: the dying flow's final counters minus what the sampled
    /// deltas already billed, plus its retained seed where billable. A tuple never seen at all
    /// (born and dead between passes) bills its full final counters - the event is
    /// authoritative for exactly one connection's lifetime and cannot fire twice, so none of
    /// the seq-file-miss ambiguity that forbids full-billing in <see cref="Account"/> applies.
    /// Null when there is nothing to bill or the flow is not accountable WAN traffic.
    /// </summary>
    public ClientWanDelta? AccountDestroy(ConntrackFlow flow, ConntrackHostView view)
    {
        // Before the second pass nothing distinguishes pre-coverage flows; bill nothing.
        if (_pass < 2) return null;

        long reconOrig, reconReply;
        if (_flows.TryGetValue(flow.Key, out var entry))
        {
            // Final counters below the tracked baseline: this event is not for the flow the
            // entry tracks (the tuple was reused). Skip and keep the entry - undercount doctrine.
            if (flow.OrigBytes < entry.Orig || flow.ReplyBytes < entry.Reply) return null;
            reconOrig = flow.OrigBytes - entry.Orig + (entry.SeedBillable ? entry.SeedOrig : 0);
            reconReply = flow.ReplyBytes - entry.Reply + (entry.SeedBillable ? entry.SeedReply : 0);
            _flows.Remove(flow.Key);
        }
        else
        {
            reconOrig = flow.OrigBytes;
            reconReply = flow.ReplyBytes;
        }
        if (reconOrig == 0 && reconReply == 0) return null;

        if (Classify(flow, view) is not { } c) return null;
        return new ClientWanDelta(c.Ip, c.Mac, c.IfName, 0, 0, 0,
            ReconDownBytes: c.DownIsReply ? reconReply : reconOrig,
            ReconUpBytes: c.DownIsReply ? reconOrig : reconReply);
    }

    private readonly record struct Classified(string Ip, string Mac, string IfName, bool DownIsReply);

    /// <summary>
    /// Which client a flow belongs to and whether it crossed the WAN. Direction is by the LAN
    /// endpoint, not by tuple order: an inbound port-forwarded flow has the remote as the
    /// ORIGINAL tuple's source, so classifying down/up by original-vs-reply would invert it.
    /// Null = not a WAN flow, or not accountable.
    /// </summary>
    private static Classified? Classify(ConntrackFlow flow, ConntrackHostView view)
    {
        // Find the site-local end. The gateway's own flows first (its WAN address is a host
        // address, never a connected-subnet client), then the connected-subnet end of either
        // tuple. Original-side first: a client-originated SNAT flow also has a host address
        // as the REPLY tuple's destination, which must not claim the flow for the gateway.
        IPAddress lanEnd;
        IPAddress remote;
        IPAddress wanSide;
        bool downIsReply;
        bool lanEndIsSelf;
        if (view.IsHostAddress(flow.OrigSrc, out _))
        {
            (lanEnd, remote, wanSide, downIsReply, lanEndIsSelf) = (flow.OrigSrc, flow.OrigDst, flow.ReplyDst, true, true);
        }
        else if (view.IsInConnectedSubnet(flow.OrigSrc))
        {
            (lanEnd, remote, wanSide, downIsReply, lanEndIsSelf) = (flow.OrigSrc, flow.OrigDst, flow.ReplyDst, true, false);
        }
        else if (view.IsHostAddress(flow.ReplySrc, out _))
        {
            (lanEnd, remote, wanSide, downIsReply, lanEndIsSelf) = (flow.ReplySrc, flow.OrigSrc, flow.OrigDst, false, true);
        }
        else if (view.IsInConnectedSubnet(flow.ReplySrc))
        {
            (lanEnd, remote, wanSide, downIsReply, lanEndIsSelf) = (flow.ReplySrc, flow.OrigSrc, flow.OrigDst, false, false);
        }
        else
        {
            // Neither end is site-local. Forwarded VPN-to-VPN or transit oddities: only a
            // NAT'd or public-remote flow is WAN at all, and it goes to the remainder.
            var natOrPublic = !flow.OrigSrc.Equals(flow.ReplyDst) || !NetworkUtilities.IsPrivateIpAddress(flow.OrigDst);
            return natOrPublic ? new Classified("", "", "", DownIsReply: true) : null;
        }

        // WAN test: the remote endpoint left the site (public), or NAT rewrote an end - and
        // never when the real remote is itself site-local (inter-VLAN routing, hairpin NAT
        // to an internal server, a client talking to the gateway).
        if (view.IsInConnectedSubnet(remote) || view.IsHostAddress(remote, out _)) return null;
        var natted = !flow.OrigSrc.Equals(flow.ReplyDst) || !flow.OrigDst.Equals(flow.ReplySrc);
        if (!natted && NetworkUtilities.IsPrivateIpAddress(remote)) return null;

        // Egress interface: the WAN-side address is the gateway's own on the interface the
        // flow uses (the NAT address, or the gateway's own source). IPv6 without NAT carries
        // the client's own address there, which resolves to no interface - read as default WAN.
        view.IsHostAddress(wanSide, out var ifName);

        if (lanEndIsSelf)
            return new Classified(lanEnd.ToString(), "", ifName, downIsReply);

        // A LAN endpoint the neighbor table cannot name is never guessed to a client: an
        // IPv6 privacy address already rotated away, a VPN road warrior in a local-looking
        // subnet. It goes to the explicit unattributed remainder.
        var mac = view.MacFor(lanEnd);
        return mac == null
            ? new Classified("", "", ifName, downIsReply)
            : new Classified(lanEnd.ToString(), mac, ifName, downIsReply);
    }
}
