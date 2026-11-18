using System.Net;
using NetworkOptimizer.Core.Helpers;

namespace NetworkOptimizer.Monitoring.Conntrack;

/// <summary>
/// Which client a conntrack flow belongs to and which way its bytes move. <see cref="Ip"/> and
/// <see cref="Mac"/> empty is the explicit unattributed remainder; an Ip with an empty Mac is the
/// gateway's own traffic (<see cref="IsGateway"/>) or a LAN end the neighbor table cannot name.
/// <see cref="DownIsReply"/> says the reply direction carries the client's download.
/// </summary>
public sealed record ConntrackSide(
    string Ip,
    string Mac,
    string IfName,
    bool DownIsReply,
    IPAddress? LanEnd,
    IPAddress? Remote,
    bool IsGateway);

/// <summary>
/// The one rule for classifying a conntrack flow, shared by the per-client WAN accountant and
/// Live Traffic. Direction is by the LAN endpoint, not by tuple order: an inbound port-forwarded
/// flow has the remote as the ORIGINAL tuple's source.
/// </summary>
public static class ConntrackClassifier
{
    /// <summary>
    /// The client side of a WAN flow, or null when the flow did not cross the WAN. Inter-VLAN
    /// routed traffic, which the gateway also conntracks, is excluded by the both-ends-site-local
    /// test - the exact mistake UniFi's own tallies make.
    /// </summary>
    public static ConntrackSide? ClassifyWan(ConntrackFlow flow, ConntrackHostView view)
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
            return natOrPublic ? new ConntrackSide("", "", "", true, null, flow.OrigDst, false) : null;
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
            return new ConntrackSide(lanEnd.ToString(), "", ifName, downIsReply, lanEnd, remote, true);

        // A LAN endpoint the neighbor table cannot name is never guessed to a client: an
        // IPv6 privacy address already rotated away, a VPN road warrior in a local-looking
        // subnet. It goes to the explicit unattributed remainder.
        var mac = view.MacFor(lanEnd);
        return mac == null
            ? new ConntrackSide("", "", ifName, downIsReply, lanEnd, remote, false)
            : new ConntrackSide(lanEnd.ToString(), mac, ifName, downIsReply, lanEnd, remote, false);
    }

    /// <summary>
    /// The initiating client of a routed flow between two site-local clients (inter-VLAN), or null
    /// when the flow is not one: a WAN flow, or traffic to or from the gateway itself. The
    /// initiator is the client; its download is the reply direction.
    /// </summary>
    public static ConntrackSide? ClassifyRoutedLan(ConntrackFlow flow, ConntrackHostView view)
    {
        if (!view.IsInConnectedSubnet(flow.OrigSrc) || !view.IsInConnectedSubnet(flow.OrigDst)) return null;
        if (view.IsHostAddress(flow.OrigSrc, out _) || view.IsHostAddress(flow.OrigDst, out _)) return null;
        // A NAT'd reply means a port forward or hairpin through a gateway address, not routing.
        if (!flow.OrigSrc.Equals(flow.ReplyDst) || !flow.OrigDst.Equals(flow.ReplySrc)) return null;
        var mac = view.MacFor(flow.OrigSrc) ?? "";
        return new ConntrackSide(flow.OrigSrc.ToString(), mac, "", true, flow.OrigSrc, flow.OrigDst, false);
    }
}
