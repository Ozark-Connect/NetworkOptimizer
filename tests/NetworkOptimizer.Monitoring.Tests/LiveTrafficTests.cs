using System.Buffers.Binary;
using System.Net;
using FluentAssertions;
using NetworkOptimizer.Monitoring.Conntrack;
using NetworkOptimizer.Monitoring.LiveTraffic;
using NetworkOptimizer.Monitoring.UciInform;
using Xunit;

namespace NetworkOptimizer.Monitoring.Tests;

/// <summary>
/// Live Traffic's agent-side pieces: the parser's extra fields, the classifier's routed-LAN case,
/// the per-flow tracker, and Live DNS (frame parser, BPF program, query pairing).
/// All addresses are RFC 5737 / documentation ranges.
/// </summary>
public class LiveTrafficTests
{
    private static ConntrackHostView GatewayView()
    {
        var view = new ConntrackHostView();
        view.AddHostAddress(IPAddress.Parse("192.168.1.1"), "br0");
        view.AddConnectedSubnet(IPAddress.Parse("192.168.1.0"), 24);
        view.AddHostAddress(IPAddress.Parse("192.168.30.1"), "br30");
        view.AddConnectedSubnet(IPAddress.Parse("192.168.30.0"), 24);
        view.AddHostAddress(IPAddress.Parse("198.51.100.7"), "eth4");
        view.AddHostAddress(IPAddress.Parse("10.2.0.2"), "wgclt1");
        view.AddNeighbor(IPAddress.Parse("192.168.1.100"), "aa:bb:cc:dd:ee:01");
        view.AddNeighbor(IPAddress.Parse("192.168.30.50"), "aa:bb:cc:dd:ee:02");
        return view;
    }

    private static ConntrackFlow Flow(string line) => ConntrackParser.ParseLine(line)!;

    private static ConntrackFlow Wan(long orig, long reply, int sport = 51512, string natAddr = "198.51.100.7") => Flow(
        $"ipv4     2 tcp      6 431976 ESTABLISHED src=192.168.1.100 dst=203.0.113.34 sport={sport} dport=443 packets=10 bytes={orig} src=203.0.113.34 dst={natAddr} sport=443 dport={sport} packets=20 bytes={reply} [ASSURED] mark=0 use=2");

    // ---- Parser ----

    [Fact]
    public void ParserKeepsProtoPortsPacketsAndAge()
    {
        var flow = Flow("ipv4     2 udp      17 170 src=192.168.1.100 dst=203.0.113.9 sport=33471 dport=51820 packets=3495493 bytes=436317172 src=203.0.113.9 dst=198.51.100.7 sport=51820 dport=33471 packets=7621473 bytes=10110191976 [ASSURED] mark=0 delta-time=86400 use=2");

        flow.Proto.Should().Be("udp");
        flow.OrigSport.Should().Be(33471);
        flow.OrigDport.Should().Be(51820);
        flow.OrigPackets.Should().Be(3495493);
        flow.ReplyPackets.Should().Be(7621473);
        flow.AgeSeconds.Should().Be(86400);
    }

    [Fact]
    public void ParserAgeIsUnknownWithoutTimestamps()
    {
        Wan(100, 200).AgeSeconds.Should().Be(-1);
    }

    // ---- Classifier ----

    [Fact]
    public void RoutedLanFlowIsClassifiedToItsInitiator()
    {
        var flow = Flow("ipv4     2 tcp      6 100 ESTABLISHED src=192.168.1.100 dst=192.168.30.50 sport=40000 dport=8001 packets=5 bytes=500 src=192.168.30.50 dst=192.168.1.100 sport=8001 dport=40000 packets=5 bytes=900 mark=0");

        ConntrackClassifier.ClassifyWan(flow, GatewayView()).Should().BeNull();
        var side = ConntrackClassifier.ClassifyRoutedLan(flow, GatewayView());
        side.Should().NotBeNull();
        side!.Ip.Should().Be("192.168.1.100");
        side.Mac.Should().Be("aa:bb:cc:dd:ee:01");
        side.Remote.Should().Be(IPAddress.Parse("192.168.30.50"));
    }

    [Fact]
    public void FlowToTheGatewayIsNeitherWanNorRoutedLan()
    {
        var flow = Flow("ipv4     2 udp      17 29 src=192.168.1.100 dst=192.168.1.1 sport=5353 dport=53 packets=1 bytes=70 src=192.168.1.1 dst=192.168.1.100 sport=53 dport=5353 packets=1 bytes=120 mark=0");

        ConntrackClassifier.ClassifyWan(flow, GatewayView()).Should().BeNull();
        ConntrackClassifier.ClassifyRoutedLan(flow, GatewayView()).Should().BeNull();
    }

    [Fact]
    public void VpnClientEgressNamesTheTunnelInterface()
    {
        var side = ConntrackClassifier.ClassifyWan(Wan(100, 200, natAddr: "10.2.0.2"), GatewayView());

        side!.IfName.Should().Be("wgclt1");
    }

    // ---- Tracker ----

    [Fact]
    public void TrackerSeedsOnItsFirstPassAndRatesAfter()
    {
        var tracker = new LiveFlowTracker();
        var view = GatewayView();
        var t0 = new DateTime(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc);

        var first = tracker.Track([Wan(1000, 5000)], view, t0, 100);
        first!.Flows.Should().BeEmpty("nothing is known about when the first snapshot's bytes moved");

        var second = tracker.Track([Wan(1500, 9000)], view, t0.AddSeconds(2), 100);
        var f = second!.Flows.Should().ContainSingle().Subject;
        f.DownBytes.Should().Be(4000);
        f.UpBytes.Should().Be(500);
        f.TotalDownBytes.Should().Be(9000);
        f.TotalDownPackets.Should().Be(20);
        f.ClientPort.Should().Be(51512);
        f.RemotePort.Should().Be(443);
        f.WanIfName.Should().Be("eth4");
        f.AgeIsLowerBound.Should().BeTrue("the flow was already open when tracking began");
    }

    [Fact]
    public void FlowBornBetweenPassesCountsItsWholeTotal()
    {
        var tracker = new LiveFlowTracker();
        var view = GatewayView();
        var t0 = DateTime.UtcNow;
        tracker.Track([], view, t0, 100);

        var pass = tracker.Track([Wan(300, 7000)], view, t0.AddSeconds(2), 100);
        var f = pass!.Flows.Should().ContainSingle().Subject;
        f.DownBytes.Should().Be(7000);
        f.AgeIsLowerBound.Should().BeFalse();
    }

    [Fact]
    public void IdleFlowSentLastWindowStaysAsZero()
    {
        var tracker = new LiveFlowTracker();
        var view = GatewayView();
        var t0 = DateTime.UtcNow;
        tracker.Track([Wan(100, 100)], view, t0, 100);
        tracker.Track([Wan(200, 900)], view, t0.AddSeconds(2), 100)!.Flows.Should().HaveCount(1);

        var idle = tracker.Track([Wan(200, 900)], view, t0.AddSeconds(4), 100);
        var f = idle!.Flows.Should().ContainSingle().Subject;
        f.DownBytes.Should().Be(0);

        tracker.Track([Wan(200, 900)], view, t0.AddSeconds(6), 100)!.Flows.Should().BeEmpty("only one idle window is carried");
    }

    [Fact]
    public void BusiestFlowsWinTheCap()
    {
        var tracker = new LiveFlowTracker();
        var view = GatewayView();
        var t0 = DateTime.UtcNow;
        tracker.Track([Wan(0, 0, 1000), Wan(0, 0, 1001), Wan(0, 0, 1002)], view, t0, 2);

        var pass = tracker.Track([Wan(0, 10, 1000), Wan(0, 3000, 1001), Wan(0, 900, 1002)], view, t0.AddSeconds(2), 2);
        pass!.Flows.Select(f => f.ClientPort).Should().Equal(1001, 1002);
        pass.TotalFlows.Should().Be(3);
    }

    [Fact]
    public void FlowGoneTwoPassesIsReportedEnded()
    {
        var tracker = new LiveFlowTracker();
        var view = GatewayView();
        var t0 = DateTime.UtcNow;
        tracker.Track([Wan(100, 100)], view, t0, 100);
        tracker.Track([Wan(200, 900)], view, t0.AddSeconds(2), 100);

        tracker.Track([], view, t0.AddSeconds(4), 100)!.Ended.Should().BeEmpty("one miss can be a seq-file skip");
        var ended = tracker.Track([], view, t0.AddSeconds(6), 100)!.Ended;
        ended.Should().ContainSingle().Which.TotalDownBytes.Should().Be(900);
    }

    [Fact]
    public void FlowTheReadsMissedComesBackAsItsGrowthOnly()
    {
        var tracker = new LiveFlowTracker();
        var view = GatewayView();
        var t0 = DateTime.UtcNow;
        tracker.Track([Wan(100, 100)], view, t0, 100);
        tracker.Track([Wan(200, 900)], view, t0.AddSeconds(2), 100);
        tracker.Track([], view, t0.AddSeconds(4), 100);
        tracker.Track([], view, t0.AddSeconds(6), 100);

        var back = tracker.Track([Wan(300, 1900)], view, t0.AddSeconds(8), 100);
        back!.Flows.Should().ContainSingle().Which.DownBytes.Should().Be(1000, "its lifetime bytes are not one window");
    }

    [Fact]
    public void FlowOlderThanTheWindowSeenFirstMidRunCountsNothing()
    {
        var tracker = new LiveFlowTracker();
        var view = GatewayView();
        var t0 = DateTime.UtcNow;
        tracker.Track([], view, t0, 100);
        var old = Flow("ipv4     2 tcp      6 431976 ESTABLISHED src=192.168.1.100 dst=203.0.113.34 sport=51512 dport=443 packets=10 bytes=5000000 src=203.0.113.34 dst=198.51.100.7 sport=443 dport=51512 packets=20 bytes=900000000 [ASSURED] mark=0 delta-time=3600 use=2");

        tracker.Track([old], view, t0.AddSeconds(2), 100)!.Flows.Should().BeEmpty("an hour-old flow did not move 900 MB in two seconds");
    }

    [Fact]
    public void ResetForgetsEverythingAndTheNextPassSeeds()
    {
        var tracker = new LiveFlowTracker();
        var view = GatewayView();
        var t0 = DateTime.UtcNow;
        tracker.Track([Wan(100, 100)], view, t0, 100);
        tracker.Track([Wan(200, 900)], view, t0.AddSeconds(2), 100);

        tracker.Reset();

        tracker.Track([Wan(300, 5000)], view, t0.AddSeconds(60), 100)!.Flows.Should().BeEmpty();
    }

    [Fact]
    public void WithoutALeaseTheTrackerBuildsNothing()
    {
        var tracker = new LiveFlowTracker();
        tracker.Track([Wan(1, 1)], GatewayView(), DateTime.UtcNow, 0).Should().BeNull();
    }

    // ---- DNS ----

    private static byte[] DnsQuery(ushort txId, string name, ushort qtype)
    {
        var msg = new List<byte> { (byte)(txId >> 8), (byte)txId, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0 };
        foreach (var label in name.Split('.'))
        {
            msg.Add((byte)label.Length);
            msg.AddRange(System.Text.Encoding.ASCII.GetBytes(label));
        }
        msg.Add(0);
        msg.AddRange([(byte)(qtype >> 8), (byte)qtype, 0, 1]);
        return msg.ToArray();
    }

    private static byte[] DnsResponse(ushort txId, string name, int rcode, params (ushort Type, byte[] Data)[] answers)
    {
        var q = DnsQuery(txId, name, answers.Length > 0 ? answers[0].Type : (ushort)1);
        q[2] = 0x81;
        q[3] = (byte)(0x80 | rcode);
        q[7] = (byte)answers.Length;
        var msg = new List<byte>(q);
        foreach (var (type, data) in answers)
        {
            msg.AddRange([0xc0, 0x0c]); // pointer to the question name
            msg.AddRange([(byte)(type >> 8), (byte)type, 0, 1, 0, 0, 0x01, 0x2c]); // ttl 300
            msg.AddRange([(byte)(data.Length >> 8), (byte)data.Length]);
            msg.AddRange(data);
        }
        return msg.ToArray();
    }

    private static byte[] Ipv4UdpFrame(string src, string dst, int sport, int dport, byte[] payload, bool vlanTag = false)
    {
        var frame = new List<byte>(new byte[12]);
        if (vlanTag) frame.AddRange([0x81, 0x00, 0x00, 0x1e]);
        frame.AddRange([0x08, 0x00]);
        var ip = new byte[20];
        ip[0] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(ip.AsSpan(2), (ushort)(20 + 8 + payload.Length));
        ip[8] = 64;
        ip[9] = 17;
        IPAddress.Parse(src).GetAddressBytes().CopyTo(ip, 12);
        IPAddress.Parse(dst).GetAddressBytes().CopyTo(ip, 16);
        frame.AddRange(ip);
        var udp = new byte[8];
        BinaryPrimitives.WriteUInt16BigEndian(udp.AsSpan(0), (ushort)sport);
        BinaryPrimitives.WriteUInt16BigEndian(udp.AsSpan(2), (ushort)dport);
        BinaryPrimitives.WriteUInt16BigEndian(udp.AsSpan(4), (ushort)(8 + payload.Length));
        frame.AddRange(udp);
        frame.AddRange(payload);
        return frame.ToArray();
    }

    private static byte[] Ipv6UdpFrame(string src, string dst, int sport, int dport, byte[] payload)
    {
        var frame = new List<byte>(new byte[12]) { 0x86, 0xdd };
        var ip = new byte[40];
        ip[0] = 0x60;
        BinaryPrimitives.WriteUInt16BigEndian(ip.AsSpan(4), (ushort)(8 + payload.Length));
        ip[6] = 17;
        ip[7] = 64;
        IPAddress.Parse(src).GetAddressBytes().CopyTo(ip, 8);
        IPAddress.Parse(dst).GetAddressBytes().CopyTo(ip, 24);
        frame.AddRange(ip);
        var udp = new byte[8];
        BinaryPrimitives.WriteUInt16BigEndian(udp.AsSpan(0), (ushort)sport);
        BinaryPrimitives.WriteUInt16BigEndian(udp.AsSpan(2), (ushort)dport);
        frame.AddRange(udp);
        frame.AddRange(payload);
        return frame.ToArray();
    }

    [Fact]
    public void ParsesAQueryFrame()
    {
        var frame = Ipv4UdpFrame("192.168.1.100", "192.168.1.1", 40000, 53, DnsQuery(0x1234, "api.example.com", 28));
        var p = DnsFrameParser.Parse(frame);

        p.Should().NotBeNull();
        p!.IsResponse.Should().BeFalse();
        p.Name.Should().Be("api.example.com");
        p.QType.Should().Be("AAAA");
        p.TxId.Should().Be(0x1234);
        p.Src.Should().Be(IPAddress.Parse("192.168.1.100"));
    }

    [Fact]
    public void ParsesAResponseWithCompressedNamesAndCname()
    {
        var cname = new List<byte> { 3, (byte)'c', (byte)'d', (byte)'n', 0xc0, 0x0c };
        var resp = DnsResponse(7, "www.example.com", 0,
            (5, cname.ToArray()),
            (1, [203, 0, 113, 34]));
        var p = DnsFrameParser.Parse(Ipv4UdpFrame("192.168.1.1", "192.168.1.100", 53, 40000, resp, vlanTag: true));

        p!.IsResponse.Should().BeTrue();
        p.RCode.Should().Be("NOERROR");
        p.Answers.Select(a => (a.Type, a.Data)).Should().Equal(("CNAME", "cdn.www.example.com"), ("A", "203.0.113.34"));
        p.Answers[0].Ttl.Should().Be(300);
    }

    [Fact]
    public void ParsesIpv6AndNxdomain()
    {
        var p = DnsFrameParser.Parse(Ipv6UdpFrame("2001:db8::1", "2001:db8::100", 53, 40001, DnsResponse(9, "nope.example", 3)));

        p!.RCode.Should().Be("NXDOMAIN");
        p.Dst.Should().Be(IPAddress.Parse("2001:db8::100"));
    }

    [Fact]
    public void TruncatedOrLoopingMessagesAreNull()
    {
        var query = DnsQuery(1, "a.example", 1);
        DnsFrameParser.ParseMessage(query.AsSpan(0, 15), IPAddress.Loopback, IPAddress.Loopback, 1, 53).Should().BeNull();

        var loop = DnsQuery(1, "a.example", 1);
        loop[12] = 0xc0;
        loop[13] = 0x0c; // name points at itself
        DnsFrameParser.ParseMessage(loop, IPAddress.Loopback, IPAddress.Loopback, 1, 53).Should().BeNull();
    }

    [Fact]
    public void BpfProgramAcceptsDnsAndRejectsOtherUdp()
    {
        var program = DnsBpfFilter.Build();
        var dns4 = Ipv4UdpFrame("192.168.1.100", "203.0.113.53", 40000, 53, DnsQuery(1, "a.example", 1));
        var dns6 = Ipv6UdpFrame("2001:db8::100", "2001:db8::53", 40000, 53, DnsQuery(1, "a.example", 1));
        var dnsReply = Ipv4UdpFrame("203.0.113.53", "192.168.1.100", 53, 40000, DnsQuery(1, "a.example", 1));
        var wg = Ipv4UdpFrame("192.168.1.100", "203.0.113.9", 33471, 51820, new byte[64]);

        UciBpfFilter.Evaluate(program, dns4).Should().Be(DnsBpfFilter.SnapLength);
        UciBpfFilter.Evaluate(program, dns6).Should().Be(DnsBpfFilter.SnapLength);
        UciBpfFilter.Evaluate(program, dnsReply).Should().Be(DnsBpfFilter.SnapLength);
        UciBpfFilter.Evaluate(program, wg).Should().Be(0);
    }

    private static readonly Func<IPAddress, bool> IsClient = ip =>
        ip.ToString().StartsWith("192.168.") && !ip.ToString().EndsWith(".1");

    [Fact]
    public void TrackerPairsQueryAndResponseWithLatency()
    {
        var tracker = new DnsQueryTracker();
        var t0 = DateTime.UtcNow;
        var q = DnsFrameParser.Parse(Ipv4UdpFrame("192.168.1.100", "192.168.30.53", 40000, 53, DnsQuery(5, "api.example.com", 1)))!;
        var r = DnsFrameParser.Parse(Ipv4UdpFrame("192.168.30.53", "192.168.1.100", 53, 40000,
            DnsResponse(5, "api.example.com", 0, (1, [203, 0, 113, 7]))))!;

        tracker.OnPacket(q, t0, IsClient);
        tracker.OnPacket(q, t0.AddMilliseconds(1), IsClient); // the second bridge's sighting
        tracker.OnPacket(r, t0.AddMilliseconds(12), IsClient);
        tracker.OnPacket(r, t0.AddMilliseconds(13), IsClient);

        var done = tracker.Drain(t0.AddSeconds(1));
        var rec = done.Should().ContainSingle().Subject;
        rec.ClientIp.Should().Be("192.168.1.100");
        rec.ServerIp.Should().Be("192.168.30.53");
        rec.LatencyMs.Should().Be(12);
        rec.Answers.Should().ContainSingle().Which.Data.Should().Be("203.0.113.7");
    }

    [Fact]
    public void TrackerKeepsSubMillisecondLatency()
    {
        var tracker = new DnsQueryTracker();
        var t0 = DateTime.UtcNow;
        var q = DnsFrameParser.Parse(Ipv4UdpFrame("192.168.1.100", "192.168.30.53", 40000, 53, DnsQuery(6, "api.example.com", 1)))!;
        var r = DnsFrameParser.Parse(Ipv4UdpFrame("192.168.30.53", "192.168.1.100", 53, 40000,
            DnsResponse(6, "api.example.com", 0, (1, [203, 0, 113, 7]))))!;

        tracker.OnPacket(q, t0, IsClient);
        tracker.OnPacket(r, t0.AddTicks(4_000), IsClient); // 0.4 ms

        tracker.Drain(t0.AddSeconds(1)).Single().LatencyMs.Should().BeApproximately(0.4, 0.001);
    }

    [Fact]
    public void UnansweredQueryTimesOut()
    {
        var tracker = new DnsQueryTracker();
        var t0 = DateTime.UtcNow;
        tracker.OnPacket(DnsFrameParser.Parse(Ipv4UdpFrame("192.168.1.100", "203.0.113.53", 40000, 53, DnsQuery(6, "slow.example", 1)))!, t0, IsClient);

        tracker.Drain(t0.AddSeconds(1)).Should().BeEmpty();
        var rec = tracker.Drain(t0.AddSeconds(4)).Should().ContainSingle().Subject;
        rec.Answered.Should().BeFalse();
    }

    [Fact]
    public void GatewayUpstreamQueriesAreNotClientLookups()
    {
        var tracker = new DnsQueryTracker();
        // The gateway resolver forwarding upstream: neither end is a client.
        tracker.OnPacket(DnsFrameParser.Parse(Ipv4UdpFrame("192.168.1.1", "203.0.113.53", 40000, 53, DnsQuery(8, "a.example", 1)))!, DateTime.UtcNow, IsClient);

        tracker.Drain(DateTime.UtcNow.AddSeconds(5)).Should().BeEmpty();
    }
}
