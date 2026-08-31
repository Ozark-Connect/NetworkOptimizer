using System.Net;
using NetworkOptimizer.Core.Helpers;

namespace NetworkOptimizer.Monitoring.Conntrack;

/// <summary>One client's WAN byte deltas over a sample window, per egress interface.
/// Empty <see cref="Ip"/> and <see cref="Mac"/> is the explicit unattributed remainder;
/// a non-empty Ip with an empty Mac is an endpoint the server must map (the gateway's own
/// addresses among them).</summary>
public sealed record ClientWanDelta(string Ip, string Mac, string WanIfName, long DownBytes, long UpBytes, int Flows);

/// <summary>
/// Turns successive conntrack table snapshots into per-client WAN window deltas. Holds the
/// previous snapshot's per-flow counters; a counter that went backward is a new flow reusing
/// the tuple (its counter holds only its own bytes, so the full value is the delta), and a
/// negative delta therefore cannot exist. The first pass seeds and emits nothing - a flow's
/// pre-existing total must not be billed to the window the runner started in.
/// </summary>
public sealed class ConntrackAccountant
{
    private Dictionary<string, (long Orig, long Reply)>? _previous;

    /// <summary>Whether a pass has seeded the snapshot yet (the first emits no deltas).</summary>
    public bool Seeded => _previous != null;

    /// <summary>
    /// Accounts one snapshot against the previous, returning per-client deltas summed by
    /// (client, egress interface). WAN flows only: inter-VLAN routed traffic - which the
    /// gateway also conntracks - is excluded by the both-ends-site-local test, the exact
    /// mistake UniFi's own tallies make.
    /// </summary>
    public List<ClientWanDelta> Account(IReadOnlyList<ConntrackFlow> flows, ConntrackHostView view)
    {
        var snapshot = new Dictionary<string, (long Orig, long Reply)>(flows.Count);
        var totals = new Dictionary<(string Ip, string Mac, string IfName), (long Down, long Up, int Flows)>();
        var seeded = _previous != null;

        foreach (var flow in flows)
        {
            snapshot[flow.Key] = (flow.OrigBytes, flow.ReplyBytes);
            if (!seeded) continue;

            // A tuple with no prior counters is SEED-ONLY, never billed its full total: the proc
            // table is a seq-file read, so an existing long-lived flow can be missed in one pass
            // under churn and reappear the next carrying its whole history - billing that as one
            // window inflated a client by orders of magnitude. Seeding instead loses at most one
            // window of a genuinely new flow's bytes: an undercount, never an inflation, which is
            // this feed's doctrine (v2's destroy events recover exact short-flow bytes).
            if (!_previous!.TryGetValue(flow.Key, out var prev)
                || flow.OrigBytes < prev.Orig || flow.ReplyBytes < prev.Reply)
                continue;
            var dOrig = flow.OrigBytes - prev.Orig;
            var dReply = flow.ReplyBytes - prev.Reply;
            if (dOrig == 0 && dReply == 0) continue;

            if (Classify(flow, view) is not { } c) continue;
            var key = (c.Ip, c.Mac, c.IfName);
            var sum = totals.TryGetValue(key, out var t) ? t : (0L, 0L, 0);
            totals[key] = (
                sum.Item1 + (c.DownIsReply ? dReply : dOrig),
                sum.Item2 + (c.DownIsReply ? dOrig : dReply),
                sum.Item3 + 1);
        }

        _previous = snapshot;
        return totals
            .Select(kv => new ClientWanDelta(kv.Key.Ip, kv.Key.Mac, kv.Key.IfName, kv.Value.Down, kv.Value.Up, kv.Value.Flows))
            .Where(d => d.DownBytes > 0 || d.UpBytes > 0)
            .ToList();
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
