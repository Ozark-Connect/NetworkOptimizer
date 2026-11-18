using System.Buffers.Binary;
using System.Net;

namespace NetworkOptimizer.Monitoring.Conntrack;

/// <summary>TCP connection states as conntrack numbers them (<c>enum tcp_conntrack</c>).</summary>
public enum ConntrackTcpState : byte
{
    None = 0,
    SynSent = 1,
    SynRecv = 2,
    Established = 3,
    Unknown = 255,
}

/// <summary>
/// One ctnetlink NEW or UPDATE event for a TCP or UDP flow. <see cref="Key"/> is built exactly as
/// <see cref="ConntrackParser"/> builds it from <c>/proc/net/nf_conntrack</c>, so an event joins the
/// flow Live Traffic samples by string equality.
/// </summary>
public sealed record ConntrackEvent(
    string Key,
    bool IsNew,
    string Proto,
    IPAddress OrigSrc,
    ConntrackTcpState TcpState,
    bool SeenReply,
    bool Assured);

/// <summary>
/// Parses ctnetlink event messages (<c>linux/netfilter/nfnetlink_conntrack.h</c>). Allocates only the
/// event it returns; anything that is not a TCP or UDP NEW/UPDATE comes back null.
/// </summary>
public static class ConntrackEventParser
{
    /// <summary>nlmsghdr (16) + nfgenmsg (4): where the attributes start.</summary>
    public const int AttributesOffset = 20;

    /// <summary>NFNL_SUBSYS_CTNETLINK &lt;&lt; 8 | IPCTNL_MSG_CT_NEW: the type of both NEW and UPDATE events.</summary>
    public const ushort MsgCtNew = 0x0100;

    private const ushort NlmFCreate = 0x400, NlmFExcl = 0x200;

    public const int CtaTupleOrig = 1, CtaTupleReply = 2, CtaStatus = 3, CtaProtoinfo = 4;
    private const int CtaTupleIp = 1, CtaTupleProto = 2;
    private const int CtaIpV4Src = 1, CtaIpV4Dst = 2, CtaIpV6Src = 3, CtaIpV6Dst = 4;
    private const int CtaProtoNum = 1, CtaProtoSrcPort = 2, CtaProtoDstPort = 3;
    public const int CtaProtoinfoTcp = 1, CtaProtoinfoTcpState = 1;
    public const uint IpsSeenReply = 0x2, IpsAssured = 0x4;

    /// <summary>
    /// Walks one datagram, which may hold several netlink messages, and hands each event to
    /// <paramref name="onEvent"/>.
    /// </summary>
    public static void ParseDatagram(ReadOnlySpan<byte> datagram, Action<ConntrackEvent> onEvent)
    {
        var off = 0;
        while (off + AttributesOffset <= datagram.Length)
        {
            var len = (int)BinaryPrimitives.ReadUInt32LittleEndian(datagram[off..]);
            if (len < AttributesOffset || off + len > datagram.Length) return;
            if (ParseMessage(datagram.Slice(off, len)) is { } e) onEvent(e);
            off += (len + 3) & ~3;
        }
    }

    /// <summary>One netlink message (header included), or null when it is not a TCP or UDP event.</summary>
    public static ConntrackEvent? ParseMessage(ReadOnlySpan<byte> message)
    {
        if (message.Length < AttributesOffset) return null;
        if (BinaryPrimitives.ReadUInt16LittleEndian(message[4..]) != MsgCtNew) return null;
        var flags = BinaryPrimitives.ReadUInt16LittleEndian(message[6..]);
        var attrs = message[AttributesOffset..];

        if (!Find(attrs, CtaTupleOrig, out var orig) || !Find(attrs, CtaTupleReply, out var reply)) return null;
        if (!Tuple(orig, out var proto, out var oSrc, out var oDst, out var oSp, out var oDp)) return null;
        if (!Tuple(reply, out _, out var rSrc, out var rDst, out var rSp, out var rDp)) return null;
        var protoName = proto switch { 6 => "tcp", 17 => "udp", _ => null };
        if (protoName == null) return null;

        uint status = 0;
        if (Find(attrs, CtaStatus, out var s) && s.Length >= 4) status = BinaryPrimitives.ReadUInt32BigEndian(s);
        var state = ConntrackTcpState.Unknown;
        if (proto == 6 && Find(attrs, CtaProtoinfo, out var pi) && Find(pi, CtaProtoinfoTcp, out var pt)
            && Find(pt, CtaProtoinfoTcpState, out var st) && st.Length >= 1)
            state = (ConntrackTcpState)st[0];

        // The same layout ConntrackParser keys a /proc line by: ports in the order the line lists them.
        var key = $"{protoName}|{oSrc}|{oDst}|sport{oSp}dport{oDp}|{rSrc}|{rDst}|sport{rSp}dport{rDp}";
        return new ConntrackEvent(key, (flags & (NlmFCreate | NlmFExcl)) != 0, protoName, oSrc, state,
            (status & IpsSeenReply) != 0, (status & IpsAssured) != 0);
    }

    private static bool Tuple(ReadOnlySpan<byte> tuple, out byte proto, out IPAddress src, out IPAddress dst, out int sport, out int dport)
    {
        proto = 0; src = dst = IPAddress.None; sport = dport = 0;
        if (!Find(tuple, CtaTupleIp, out var ip) || !Find(tuple, CtaTupleProto, out var pr)) return false;
        if (Find(ip, CtaIpV4Src, out var s4) && s4.Length == 4 && Find(ip, CtaIpV4Dst, out var d4) && d4.Length == 4)
            (src, dst) = (new IPAddress(s4), new IPAddress(d4));
        else if (Find(ip, CtaIpV6Src, out var s6) && s6.Length == 16 && Find(ip, CtaIpV6Dst, out var d6) && d6.Length == 16)
            (src, dst) = (new IPAddress(s6), new IPAddress(d6));
        else return false;
        if (!Find(pr, CtaProtoNum, out var n) || n.Length < 1) return false;
        proto = n[0];
        if (Find(pr, CtaProtoSrcPort, out var sp) && sp.Length >= 2) sport = BinaryPrimitives.ReadUInt16BigEndian(sp);
        if (Find(pr, CtaProtoDstPort, out var dp) && dp.Length >= 2) dport = BinaryPrimitives.ReadUInt16BigEndian(dp);
        return true;
    }

    /// <summary>The first attribute of <paramref name="type"/> (flag bits masked), or false.</summary>
    public static bool Find(ReadOnlySpan<byte> attrs, int type, out ReadOnlySpan<byte> value)
    {
        var o = 0;
        while (o + 4 <= attrs.Length)
        {
            int len = BinaryPrimitives.ReadUInt16LittleEndian(attrs[o..]);
            if (len < 4 || o + len > attrs.Length) break;
            if ((BinaryPrimitives.ReadUInt16LittleEndian(attrs[(o + 2)..]) & 0x3FFF) == type)
            {
                value = attrs.Slice(o + 4, len - 4);
                return true;
            }
            o += (len + 3) & ~3;
        }
        value = default;
        return false;
    }
}
