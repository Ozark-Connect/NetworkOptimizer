using System.Buffers.Binary;
using System.Text;
using FluentAssertions;
using NetworkOptimizer.Monitoring.UciInform;
using Xunit;

namespace NetworkOptimizer.Monitoring.Tests;

/// <summary>
/// The on-gateway UCI inform tap: the kernel filter it attaches, and the rebuilding of inform
/// POSTs from raw frames.
/// </summary>
public class UciInformCaptureTests
{
    private static readonly byte[] UciMac = { 0xaa, 0xbb, 0xcc, 0x00, 0x11, 0x22 };
    private static readonly byte[] OtherMac = { 0xaa, 0xbb, 0xcc, 0x00, 0x11, 0x23 };
    private static readonly byte[] GatewayMac = { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 };
    private static readonly DateTime T0 = new(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc);

    // ---- BPF ----

    [Fact]
    public void Filter_AcceptsOnlyTheConfiguredSources()
    {
        var program = UciBpfFilter.Build(new[] { UciMac });

        UciBpfFilter.Evaluate(program, Frame(UciMac)).Should().Be(UciBpfFilter.SnapLength);
        UciBpfFilter.Evaluate(program, Frame(OtherMac)).Should().Be(0);
        UciBpfFilter.Evaluate(program, Frame(GatewayMac)).Should().Be(0);
    }

    [Fact]
    public void Filter_MatchesAnyOfSeveralMacs()
    {
        var third = new byte[] { 0x02, 0x00, 0x00, 0x00, 0x00, 0x09 };
        var program = UciBpfFilter.Build(new[] { UciMac, OtherMac, third });

        UciBpfFilter.Evaluate(program, Frame(UciMac)).Should().Be(UciBpfFilter.SnapLength);
        UciBpfFilter.Evaluate(program, Frame(OtherMac)).Should().Be(UciBpfFilter.SnapLength);
        UciBpfFilter.Evaluate(program, Frame(third)).Should().Be(UciBpfFilter.SnapLength);
        UciBpfFilter.Evaluate(program, Frame(GatewayMac)).Should().Be(0);
    }

    [Fact]
    public void Filter_SameFirstFourBytes_IsNotAMatch()
    {
        // The two halves are compared separately; sharing the first half must not accept.
        var program = UciBpfFilter.Build(new[] { UciMac });
        UciBpfFilter.Evaluate(program, Frame(OtherMac)).Should().Be(0);
    }

    [Fact]
    public void Filter_WithNoMacs_DropsEverything()
    {
        var program = UciBpfFilter.Build(Array.Empty<byte[]>());
        UciBpfFilter.Evaluate(program, Frame(UciMac)).Should().Be(0);
    }

    [Fact]
    public void Serialize_WritesEightBytesPerInstruction()
    {
        var program = UciBpfFilter.Build(new[] { UciMac, OtherMac });
        var bytes = UciBpfFilter.Serialize(program);

        bytes.Length.Should().Be(program.Count * 8);
        BitConverter.ToUInt16(bytes, 0).Should().Be(program[0].Code);
        BitConverter.ToUInt32(bytes, 4).Should().Be(program[0].K);
    }

    [Theory]
    [InlineData("aa:bb:cc:00:11:22")]
    [InlineData("AA-BB-CC-00-11-22")]
    [InlineData("aabbcc001122")]
    public void ParseMac_AcceptsCommonForms(string text)
    {
        UciBpfFilter.FormatMac(UciBpfFilter.ParseMac(text)).Should().Be("aa:bb:cc:00:11:22");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("aa:bb:cc")]
    [InlineData("zz:bb:cc:00:11:22")]
    public void ParseMac_RejectsMalformed(string? text)
    {
        UciBpfFilter.ParseMac(text).Should().BeNull();
    }

    // ---- Reassembly ----

    [Fact]
    public void SingleSegmentPost_EmitsTheBody()
    {
        var body = InformBody(1);
        var reassembler = new UciInformReassembler();

        var result = reassembler.OnFrame(Ipv6Tcp(UciMac, 1000, Request(body)), T0);

        result.Should().ContainSingle();
        result[0].SourceMac.Should().Be("aa:bb:cc:00:11:22");
        result[0].Body.Should().Equal(body);
    }

    [Fact]
    public void SegmentsOutOfOrder_EmitOnceWhenComplete()
    {
        var request = Request(InformBody(2, bodyLength: 3000));
        var (a, b, c) = (request[..1000], request[1000..2000], request[2000..]);
        var reassembler = new UciInformReassembler();

        reassembler.OnFrame(Ipv6Tcp(UciMac, 5000 + 2000, c), T0).Should().BeEmpty();
        reassembler.OnFrame(Ipv6Tcp(UciMac, 5000, a), T0).Should().BeEmpty();
        var done = reassembler.OnFrame(Ipv6Tcp(UciMac, 5000 + 1000, b), T0);

        done.Should().ContainSingle();
        done[0].Body.Should().Equal(InformBody(2, bodyLength: 3000));
    }

    [Fact]
    public void DuplicateSegments_DoNotDuplicateTheInform()
    {
        // A frame captured on a bridge and on its member port arrives twice.
        var request = Request(InformBody(3, bodyLength: 2000));
        var (a, b) = (request[..1200], request[1200..]);
        var reassembler = new UciInformReassembler();

        reassembler.OnFrame(Ipv6Tcp(UciMac, 100, a), T0).Should().BeEmpty();
        reassembler.OnFrame(Ipv6Tcp(UciMac, 100, a), T0).Should().BeEmpty();
        reassembler.OnFrame(Ipv6Tcp(UciMac, 100 + 1200, b), T0).Should().ContainSingle();
        reassembler.OnFrame(Ipv6Tcp(UciMac, 100 + 1200, b), T0).Should().BeEmpty();
    }

    [Fact]
    public void SameInformOnTwoInterfaces_IsEmittedOnce()
    {
        // Whole request seen twice (two capture points): the second copy is a retransmission.
        var request = Request(InformBody(4));
        var reassembler = new UciInformReassembler();

        reassembler.OnFrame(Ipv6Tcp(UciMac, 7000, request), T0).Should().ContainSingle();
        reassembler.OnFrame(Ipv6Tcp(UciMac, 7000, request), T0).Should().BeEmpty();
    }

    [Fact]
    public void KeepAlive_TwoPostsOnOneConnection_EmitBoth()
    {
        var first = Request(InformBody(5));
        var second = Request(InformBody(6));
        var reassembler = new UciInformReassembler();

        reassembler.OnFrame(Ipv6Tcp(UciMac, 1, first), T0).Should().ContainSingle();
        var next = reassembler.OnFrame(Ipv6Tcp(UciMac, 1 + (uint)first.Length, second), T0.AddSeconds(60));

        next.Should().ContainSingle();
        next[0].Body.Should().Equal(InformBody(6));
    }

    [Fact]
    public void Ipv4WithVlanTag_IsParsed()
    {
        var body = InformBody(7);
        var reassembler = new UciInformReassembler();

        reassembler.OnFrame(Ipv4Tcp(UciMac, 42, Request(body), vlan: 10), T0)
            .Should().ContainSingle().Which.Body.Should().Equal(body);
    }

    [Fact]
    public void PostToAnotherPath_IsIgnored()
    {
        var reassembler = new UciInformReassembler();
        reassembler.OnFrame(Ipv6Tcp(UciMac, 1, Request(InformBody(8), path: "/upload")), T0).Should().BeEmpty();
    }

    [Fact]
    public void BodyThatIsNotTnbu_IsDropped()
    {
        var notTnbu = Encoding.ASCII.GetBytes(new string('x', 64));
        var reassembler = new UciInformReassembler();
        reassembler.OnFrame(Ipv6Tcp(UciMac, 1, Request(notTnbu)), T0).Should().BeEmpty();
    }

    [Fact]
    public void JoiningMidRequest_WaitsForTheNextPost()
    {
        // The capture started after the headers went by: the tail is unusable, the next POST is not.
        var partial = Request(InformBody(9))[30..];
        var next = Request(InformBody(10));
        var reassembler = new UciInformReassembler();

        reassembler.OnFrame(Ipv6Tcp(UciMac, 500, partial), T0).Should().BeEmpty();
        reassembler.OnFrame(Ipv6Tcp(UciMac, 500 + (uint)partial.Length, next), T0)
            .Should().ContainSingle().Which.Body.Should().Equal(InformBody(10));
    }

    [Fact]
    public void OversizedRequest_IsDroppedNotBuffered()
    {
        var header = Encoding.ASCII.GetBytes(
            $"POST /inform HTTP/1.1\r\nContent-Length: {UciInformReassembler.MaxFlowBytes * 2}\r\n\r\n");
        var reassembler = new UciInformReassembler();

        reassembler.OnFrame(Ipv6Tcp(UciMac, 1, header), T0).Should().BeEmpty();
        reassembler.FlowCount.Should().Be(0);
        reassembler.DroppedFlows.Should().Be(1);
    }

    [Fact]
    public void IdleFlow_IsSweptAway()
    {
        var request = Request(InformBody(11, bodyLength: 2000));
        var reassembler = new UciInformReassembler();

        reassembler.OnFrame(Ipv6Tcp(UciMac, 1, request[..500]), T0);
        reassembler.FlowCount.Should().Be(1);

        reassembler.Sweep(T0 + UciInformReassembler.FlowIdleTimeout + TimeSpan.FromSeconds(1));
        reassembler.FlowCount.Should().Be(0);
    }

    // ---- helpers ----

    /// <summary>A TNBU-shaped body whose IV (bytes 16..32) is unique per <paramref name="seed"/>.</summary>
    private static byte[] InformBody(int seed, int bodyLength = 200)
    {
        var body = new byte[bodyLength];
        Encoding.ASCII.GetBytes("TNBU").CopyTo(body, 0);
        for (var i = 4; i < body.Length; i++) body[i] = (byte)(seed * 31 + i);
        return body;
    }

    private static byte[] Request(byte[] body, string path = "/inform")
    {
        var head = Encoding.ASCII.GetBytes(
            $"POST {path} HTTP/1.1\r\nHost: [fe80::1]:8080\r\nContent-Type: application/x-binary\r\nContent-Length: {body.Length}\r\n\r\n");
        return head.Concat(body).ToArray();
    }

    private static byte[] Frame(byte[] srcMac)
    {
        var frame = new byte[60];
        GatewayMac.CopyTo(frame, 0);
        srcMac.CopyTo(frame, 6);
        return frame;
    }

    private static byte[] Ipv6Tcp(byte[] srcMac, uint seq, byte[] payload)
    {
        var frame = new byte[14 + 40 + 20 + payload.Length];
        GatewayMac.CopyTo(frame, 0);
        srcMac.CopyTo(frame, 6);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(12), 0x86DD);
        var ip = frame.AsSpan(14);
        ip[0] = 0x60;
        BinaryPrimitives.WriteUInt16BigEndian(ip[4..], (ushort)(20 + payload.Length));
        ip[6] = 6;
        ip[7] = 64;
        ip[8] = 0xfe; ip[9] = 0x80; ip[23] = 0x22;   // fe80::22
        ip[24] = 0xfe; ip[25] = 0x80; ip[39] = 0x01; // fe80::1
        WriteTcp(frame.AsSpan(14 + 40), seq, payload);
        return frame;
    }

    private static byte[] Ipv4Tcp(byte[] srcMac, uint seq, byte[] payload, ushort vlan)
    {
        var frame = new byte[14 + 4 + 20 + 20 + payload.Length];
        GatewayMac.CopyTo(frame, 0);
        srcMac.CopyTo(frame, 6);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(12), 0x8100);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(14), vlan);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(16), 0x0800);
        var ip = frame.AsSpan(18);
        ip[0] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(ip[2..], (ushort)(20 + 20 + payload.Length));
        ip[8] = 64;
        ip[9] = 6;
        new byte[] { 192, 0, 2, 10 }.CopyTo(ip[12..]);
        new byte[] { 192, 0, 2, 1 }.CopyTo(ip[16..]);
        WriteTcp(frame.AsSpan(18 + 20), seq, payload);
        return frame;
    }

    private static void WriteTcp(Span<byte> tcp, uint seq, byte[] payload)
    {
        BinaryPrimitives.WriteUInt16BigEndian(tcp, 49152);
        BinaryPrimitives.WriteUInt16BigEndian(tcp[2..], 8080);
        BinaryPrimitives.WriteUInt32BigEndian(tcp[4..], seq);
        tcp[12] = 0x50; // data offset 5 words
        tcp[13] = 0x18; // PSH, ACK
        payload.CopyTo(tcp[20..]);
    }
}
