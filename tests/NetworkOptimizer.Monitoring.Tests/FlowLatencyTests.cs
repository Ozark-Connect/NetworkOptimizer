using System.Buffers.Binary;
using System.Net;
using FluentAssertions;
using NetworkOptimizer.Monitoring.Conntrack;
using Xunit;

namespace NetworkOptimizer.Monitoring.Tests;

/// <summary>
/// Flow latency on the agent: ctnetlink event parsing, the kernel filter, the handshake timer, and the
/// client-frame orientation. Messages are built byte for byte as the kernel sends them.
/// Addresses are RFC 5737 / documentation ranges.
/// </summary>
public class FlowLatencyTests
{
    // ---------------------------------------------------------------- ctnetlink message builder

    private const ushort Nested = 0x8000;

    private static byte[] Attr(int type, ReadOnlySpan<byte> payload, bool nested = false)
    {
        var len = 4 + payload.Length;
        var bytes = new byte[(len + 3) & ~3];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)len);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), (ushort)(type | (nested ? Nested : 0)));
        payload.CopyTo(bytes.AsSpan(4));
        return bytes;
    }

    private static byte[] Nest(int type, params byte[][] children) => Attr(type, children.SelectMany(c => c).ToArray(), nested: true);
    private static byte[] U8(int type, byte v) => Attr(type, [v]);
    private static byte[] Be16(int type, int v) { var b = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)v); return Attr(type, b); }
    private static byte[] Be32(int type, uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); return Attr(type, b); }

    private static byte[] Tuple(int type, string src, string dst, byte proto, int sport, int dport)
    {
        var s = IPAddress.Parse(src);
        var v6 = s.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
        return Nest(type,
            Nest(1, Attr(v6 ? 3 : 1, s.GetAddressBytes()), Attr(v6 ? 4 : 2, IPAddress.Parse(dst).GetAddressBytes())),
            Nest(2, U8(1, proto), Be16(2, sport), Be16(3, dport)));
    }

    /// <summary>A conntrack event: original tuple, reply tuple, status, and for TCP the state.</summary>
    private static byte[] Event(bool isNew, byte proto, (string Src, string Dst, int Sport, int Dport) orig,
        (string Src, string Dst, int Sport, int Dport) reply, uint status = 0, ConntrackTcpState? state = null)
    {
        var attrs = new List<byte[]>
        {
            Tuple(1, orig.Src, orig.Dst, proto, orig.Sport, orig.Dport),
            Tuple(2, reply.Src, reply.Dst, proto, reply.Sport, reply.Dport),
            Be32(3, status),
        };
        if (state is { } st) attrs.Add(Nest(4, Nest(1, U8(1, (byte)st), U8(2, 7), U8(3, 7))));
        attrs.Add(Be32(8, 0x21960000)); // CTA_MARK, ahead of CTA_ID as the kernel orders them
        attrs.Add(Be32(12, 12345));     // CTA_ID
        var body = attrs.SelectMany(a => a).ToArray();
        var msg = new byte[20 + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(msg, (uint)msg.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(msg.AsSpan(4), ConntrackEventParser.MsgCtNew);
        BinaryPrimitives.WriteUInt16LittleEndian(msg.AsSpan(6), (ushort)(isNew ? 0x0600 : 0));
        msg[16] = (byte)(IPAddress.Parse(orig.Src).AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 10 : 2);
        body.CopyTo(msg.AsSpan(20));
        return msg;
    }

    private static readonly (string, string, int, int) OutOrig = ("192.168.1.100", "203.0.113.5", 51512, 443);
    private static readonly (string, string, int, int) OutReply = ("203.0.113.5", "198.51.100.7", 443, 51512);

    private static byte[] Tcp(bool isNew, ConntrackTcpState state) =>
        Event(isNew, 6, OutOrig, OutReply, state: state, status: state == ConntrackTcpState.SynSent ? 0 : ConntrackEventParser.IpsSeenReply);

    // ---------------------------------------------------------------- parser

    [Fact]
    public void Event_key_matches_the_proc_snapshot_key_for_the_same_flow()
    {
        var e = ConntrackEventParser.ParseMessage(Tcp(true, ConntrackTcpState.SynSent))!;
        var line = "ipv4     2 tcp      6 118 SYN_SENT src=192.168.1.100 dst=203.0.113.5 sport=51512 dport=443 packets=1 bytes=60 "
                   + "[UNREPLIED] src=203.0.113.5 dst=198.51.100.7 sport=443 dport=51512 packets=0 bytes=0 mark=0 use=1";

        e.Key.Should().Be(ConntrackParser.ParseLine(line)!.Key);
        e.IsNew.Should().BeTrue();
        e.TcpState.Should().Be(ConntrackTcpState.SynSent);
        e.OrigSrc.Should().Be(IPAddress.Parse("192.168.1.100"));
    }

    [Fact]
    public void Ipv6_event_key_matches_the_proc_snapshot_key()
    {
        var msg = Event(true, 17, ("2001:db8:1::10", "2001:db8:ffff::1", 50000, 443), ("2001:db8:ffff::1", "2001:db8:1::10", 443, 50000));
        var e = ConntrackEventParser.ParseMessage(msg)!;
        var line = "ipv6     10 udp      17 29 src=2001:db8:1::10 dst=2001:db8:ffff::1 sport=50000 dport=443 packets=1 bytes=1280 "
                   + "[UNREPLIED] src=2001:db8:ffff::1 dst=2001:db8:1::10 sport=443 dport=50000 packets=0 bytes=0 mark=0 use=1";

        e.Key.Should().Be(ConntrackParser.ParseLine(line)!.Key);
        e.Proto.Should().Be("udp");
    }

    [Fact]
    public void Datagram_with_two_messages_yields_both_and_skips_other_protocols()
    {
        var icmp = Event(true, 1, ("192.168.1.100", "203.0.113.5", 0, 0), ("203.0.113.5", "198.51.100.7", 0, 0));
        var datagram = Tcp(true, ConntrackTcpState.SynSent).Concat(icmp).Concat(Tcp(false, ConntrackTcpState.SynRecv)).ToArray();
        var events = new List<ConntrackEvent>();

        ConntrackEventParser.ParseDatagram(datagram, events.Add);

        events.Select(e => (e.IsNew, e.TcpState)).Should().Equal((true, ConntrackTcpState.SynSent), (false, ConntrackTcpState.SynRecv));
    }

    // ---------------------------------------------------------------- kernel filter

    [Fact]
    public void Filter_keeps_only_the_events_a_handshake_needs()
    {
        var p = ConntrackEventFilter.Build();
        bool Keeps(byte[] m) => ConntrackEventFilter.Evaluate(p, m) > 0;
        var udpOrig = ("192.168.1.100", "203.0.113.5", 50000, 443);
        var udpReply = ("203.0.113.5", "198.51.100.7", 443, 50000);

        Keeps(Tcp(true, ConntrackTcpState.SynSent)).Should().BeTrue("NEW");
        Keeps(Tcp(false, ConntrackTcpState.SynRecv)).Should().BeTrue();
        Keeps(Tcp(false, ConntrackTcpState.Established)).Should().BeTrue();
        Keeps(Tcp(false, (ConntrackTcpState)4)).Should().BeFalse("FIN_WAIT");
        Keeps(Tcp(false, (ConntrackTcpState)7)).Should().BeFalse("TIME_WAIT");
        Keeps(Event(true, 17, udpOrig, udpReply)).Should().BeTrue("UDP NEW");
        Keeps(Event(false, 17, udpOrig, udpReply)).Should().BeFalse("a mark rewrite before any reply");
        Keeps(Event(false, 17, udpOrig, udpReply, ConntrackEventParser.IpsSeenReply)).Should().BeTrue("the first reply");
        Keeps(Event(false, 17, udpOrig, udpReply, ConntrackEventParser.IpsSeenReply | ConntrackEventParser.IpsAssured))
            .Should().BeFalse("assured comes after the reply");
    }

    [Fact]
    public void Filter_serializes_to_kernel_sock_filter_layout()
    {
        var p = ConntrackEventFilter.Build();
        var bytes = ConntrackEventFilter.Serialize(p);

        bytes.Length.Should().Be(p.Count * 8);
        p.Count.Should().BeLessThan(256, "jump offsets are one byte");
    }

    // ---------------------------------------------------------------- handshake timer

    private static ConntrackEvent Parse(byte[] m) => ConntrackEventParser.ParseMessage(m)!;

    [Fact]
    public void Tcp_handshake_times_both_legs()
    {
        var t = new HandshakeTimer();
        var key = Parse(Tcp(true, ConntrackTcpState.SynSent)).Key;

        t.OnEvent(Parse(Tcp(true, ConntrackTcpState.SynSent)), 10.000, false);
        t.OnEvent(Parse(Tcp(false, ConntrackTcpState.SynRecv)), 10.016, false);
        t.TryGet(key, out var half).Should().BeTrue("the remote leg is known at SYN_RECV");
        half.ReplyLegMs.Should().BeNull();
        t.OnEvent(Parse(Tcp(false, ConntrackTcpState.Established)), 10.0201, false);

        t.TryGet(key, out var h).Should().BeTrue();
        h.OrigLegMs.Should().BeApproximately(16, 0.01);
        h.ReplyLegMs.Should().BeApproximately(4.1, 0.01);
        h.Queued.Should().BeFalse();
        t.PendingCount.Should().Be(0);
    }

    [Fact]
    public void Retried_syn_gives_no_round_trip()
    {
        var t = new HandshakeTimer();
        t.OnEvent(Parse(Tcp(true, ConntrackTcpState.SynSent)), 10.0, false);
        t.OnEvent(Parse(Tcp(false, ConntrackTcpState.SynRecv)), 12.0477, false);

        t.TryGet(Parse(Tcp(true, ConntrackTcpState.SynSent)).Key, out _).Should().BeFalse("2 s is the SYN retry timer, not the path");
    }

    [Fact]
    public void Lost_events_drop_handshakes_in_progress_but_keep_finished_legs()
    {
        var t = new HandshakeTimer();
        var key = Parse(Tcp(true, ConntrackTcpState.SynSent)).Key;
        t.OnEvent(Parse(Tcp(true, ConntrackTcpState.SynSent)), 10.000, false);
        t.OnEvent(Parse(Tcp(false, ConntrackTcpState.SynRecv)), 10.016, false);

        t.OnLoss();
        // The lost event may have been ESTABLISHED; a later update in that state must not complete the leg.
        t.OnEvent(Parse(Tcp(false, ConntrackTcpState.Established)), 10.300, false);

        t.TryGet(key, out var h).Should().BeTrue();
        h.OrigLegMs.Should().BeApproximately(16, 0.01);
        h.ReplyLegMs.Should().BeNull();
    }

    [Fact]
    public void Udp_times_the_first_reply_and_ignores_a_mark_rewrite_before_it()
    {
        var t = new HandshakeTimer();
        var orig = ("192.168.1.100", "203.0.113.5", 50000, 443);
        var reply = ("203.0.113.5", "198.51.100.7", 443, 50000);
        var key = Parse(Event(true, 17, orig, reply)).Key;

        t.OnEvent(Parse(Event(true, 17, orig, reply)), 5.0000, false);
        t.OnEvent(Parse(Event(false, 17, orig, reply)), 5.0002, false);
        t.TryGet(key, out _).Should().BeFalse("still unreplied");
        t.OnEvent(Parse(Event(false, 17, orig, reply, ConntrackEventParser.IpsSeenReply)), 5.0129, true);

        t.TryGet(key, out var h).Should().BeTrue();
        h.OrigLegMs.Should().BeApproximately(12.9, 0.01);
        h.ReplyLegMs.Should().BeNull();
        h.Queued.Should().BeTrue();
    }

    [Fact]
    public void Tcp_picked_up_mid_stream_and_reused_tuples_are_not_timed_as_the_old_flow()
    {
        var t = new HandshakeTimer();
        var key = Parse(Tcp(true, ConntrackTcpState.SynSent)).Key;
        t.OnEvent(Parse(Tcp(true, ConntrackTcpState.Established)), 1.0, false);
        t.OnEvent(Parse(Tcp(false, ConntrackTcpState.SynRecv)), 1.01, false);
        t.TryGet(key, out _).Should().BeFalse("no SYN was seen");

        t.OnEvent(Parse(Tcp(true, ConntrackTcpState.SynSent)), 2.0, false);
        t.OnEvent(Parse(Tcp(false, ConntrackTcpState.SynRecv)), 2.02, false);
        t.TryGet(key, out _).Should().BeTrue();
        t.OnEvent(Parse(Tcp(true, ConntrackTcpState.SynSent)), 3.0, false);
        t.TryGet(key, out _).Should().BeFalse("a new flow on the same tuple replaces the old result");
    }

    [Fact]
    public void A_result_lives_as_long_as_its_flow_and_survives_one_missed_read()
    {
        var t = new HandshakeTimer();
        var key = Parse(Tcp(true, ConntrackTcpState.SynSent)).Key;
        t.OnEvent(Parse(Tcp(true, ConntrackTcpState.SynSent)), 1.0, false);
        t.OnEvent(Parse(Tcp(false, ConntrackTcpState.SynRecv)), 1.02, false);
        var thirty = TimeSpan.FromSeconds(30);

        t.Prune(_ => false, 20, thirty);
        t.TryGet(key, out _).Should().BeTrue("too new to judge: it may postdate the snapshot");

        t.Prune(_ => true, 3600, thirty);
        t.TryGet(key, out _).Should().BeTrue("an hour on, the flow is still in the table");

        t.Prune(_ => false, 3630, thirty);
        t.TryGet(key, out _).Should().BeTrue("one missed read is not the flow ending");
        t.Prune(_ => false, 3660, thirty);
        t.TryGet(key, out _).Should().BeFalse("two in a row is");
    }

    [Fact]
    public void Clear_forgets_everything()
    {
        var t = new HandshakeTimer();
        t.OnEvent(Parse(Tcp(true, ConntrackTcpState.SynSent)), 1.0, false);
        t.OnEvent(Parse(Tcp(false, ConntrackTcpState.SynRecv)), 1.01, false);

        t.Clear();

        t.ResultCount.Should().Be(0);
        t.PendingCount.Should().Be(0);
    }

    // ---------------------------------------------------------------- first-seen times between viewers

    private static ConntrackFlow Line(int sport, long bytes = 1000) => ConntrackParser.ParseLine(
        $"ipv4     2 tcp      6 100 ESTABLISHED src=192.168.1.100 dst=203.0.113.34 sport={sport} dport=443 packets=1 bytes={bytes} " +
        $"src=203.0.113.34 dst=198.51.100.7 sport=443 dport={sport} packets=1 bytes={bytes} [ASSURED] mark=0")!;

    private static ConntrackHostView View()
    {
        var view = new ConntrackHostView();
        view.AddHostAddress(IPAddress.Parse("192.168.1.1"), "br0");
        view.AddConnectedSubnet(IPAddress.Parse("192.168.1.0"), 24);
        view.AddHostAddress(IPAddress.Parse("198.51.100.7"), "eth4");
        view.AddNeighbor(IPAddress.Parse("192.168.1.100"), "aa:bb:cc:dd:ee:01");
        return view;
    }

    [Fact]
    public void Accountant_first_seen_is_a_lower_bound_in_the_startup_grace_and_exact_after()
    {
        var a = new ConntrackAccountant();
        var t0 = new DateTime(2026, 11, 18, 1, 0, 0, DateTimeKind.Utc);
        var old = Line(50001);
        for (var i = 0; i < 6; i++) a.Account([old, .. i == 5 ? new[] { Line(50002) } : []], View(), t0.AddSeconds(2 * i));

        a.TryGetFirstSeen(old.Key, out var oldAt, out var oldExact).Should().BeTrue();
        (oldAt, oldExact).Should().Be((t0, false), "it was in the first snapshot: older than the agent's view");
        a.TryGetFirstSeen(Line(50002).Key, out var newAt, out var newExact).Should().BeTrue();
        (newAt, newExact).Should().Be((t0.AddSeconds(10), true), "first seen past the grace: born then");

        a.Account([Line(50001, bytes: 10)], View(), t0.AddSeconds(12));
        a.TryGetFirstSeen(old.Key, out var reusedAt, out var reusedExact);
        (reusedAt, reusedExact).Should().Be((t0.AddSeconds(12), true), "counters went backward: a new connection");
        a.TryGetFirstSeen(Line(50099).Key, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void Tracker_dates_flows_from_first_sight_but_keeps_pre_session_flows_a_lower_bound()
    {
        var tracker = new LiveFlowTracker();
        var view = View();
        var t0 = new DateTime(2026, 11, 18, 1, 0, 0, DateTimeKind.Utc);
        var old = Line(50001);
        (DateTime, bool)? Known(string key) =>
            key == old.Key ? (t0.AddHours(-20), true) : key == Line(50002).Key ? (t0.AddSeconds(7), true) : null;

        // A viewer arrives: a connection the agent saw open 20 hours ago.
        tracker.Track([old], view, t0, 100, Known);
        var first = tracker.Track([Line(50001, 5000)], view, t0.AddSeconds(2), 100, Known)!.Flows.Single();
        first.AgeSeconds.Should().Be((long)TimeSpan.FromHours(20).TotalSeconds + 2);
        first.AgeIsLowerBound.Should().BeTrue("it predates this viewer, as before: the console backfill skips it");

        // One that opens while the viewer watches, past the ageless seeding passes, is exact.
        for (var i = 2; i <= 3; i++) tracker.Track([Line(50001, 5000 + i)], view, t0.AddSeconds(2 * i), 100, Known);
        var born = tracker.Track([Line(50001, 6000), Line(50002, 3000)], view, t0.AddSeconds(8), 100, Known)!
            .Flows.Single(f => f.ClientPort == 50002);
        born.AgeSeconds.Should().Be(1);
        born.AgeIsLowerBound.Should().BeFalse();
    }

    // ---------------------------------------------------------------- client frame

    [Fact]
    public void Round_trips_orient_to_the_client()
    {
        var outbound = new FlowHandshake(IPAddress.Parse("192.168.1.100"), 16, 4, false);
        FlowRoundTrips.Orient(outbound, "192.168.1.100", gateway: false).Should().Be(((double?)16, (double?)4));

        // A port forward: the remote opened it, so the client answered first.
        var inbound = new FlowHandshake(IPAddress.Parse("203.0.113.5"), 0.3, 22, false);
        FlowRoundTrips.Orient(inbound, "192.168.1.100", gateway: false).Should().Be(((double?)22, (double?)0.3));

        var gatewayOwn = new FlowHandshake(IPAddress.Parse("198.51.100.7"), 16, 0.02, false);
        FlowRoundTrips.Orient(gatewayOwn, "198.51.100.7", gateway: true).Should().Be(((double?)16, (double?)null));

        var v6 = new FlowHandshake(IPAddress.Parse("2001:db8:1::10"), 9, null, false);
        FlowRoundTrips.Orient(v6, "2001:0db8:0001::0010", gateway: false).Remote.Should().Be(9, "addresses compare as addresses");
    }

    [Fact]
    public void Wire_microseconds_keep_zero_for_not_measured()
    {
        FlowRoundTrips.ToMicroseconds(null).Should().Be(0);
        FlowRoundTrips.ToMicroseconds(16.2346).Should().Be(16235);
        FlowRoundTrips.ToMicroseconds(0.0001).Should().Be(1, "a measured value is never sent as 0");
    }
}
