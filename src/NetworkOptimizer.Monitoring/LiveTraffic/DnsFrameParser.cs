using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace NetworkOptimizer.Monitoring.LiveTraffic;

/// <summary>One answer record Live DNS keeps: an address or a CNAME target.</summary>
public sealed record DnsAnswer(string Type, string Data, int Ttl);

/// <summary>A DNS message lifted off an Ethernet frame, with the UDP endpoints it travelled between.</summary>
public sealed record DnsPacket(
    IPAddress Src,
    IPAddress Dst,
    int SrcPort,
    int DstPort,
    ushort TxId,
    bool IsResponse,
    string Name,
    string QType,
    string RCode,
    IReadOnlyList<DnsAnswer> Answers);

/// <summary>
/// Parses a DNS-over-UDP message out of an Ethernet frame: Ethernet II (with up to two 802.1Q
/// tags), IPv4 or IPv6 (no extension headers), UDP, then the DNS header, the first question, and
/// the answer section's A, AAAA, and CNAME records. Anything malformed or truncated is null:
/// a frame that cannot be read is skipped, never guessed at.
/// </summary>
public static class DnsFrameParser
{
    private const int MaxNameLength = 253;
    private const int MaxAnswers = 16;

    public static DnsPacket? Parse(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 14) return null;
        var offset = 12;
        var etherType = BinaryPrimitives.ReadUInt16BigEndian(frame[offset..]);
        offset += 2;
        for (var tags = 0; tags < 2 && etherType is 0x8100 or 0x88a8; tags++)
        {
            if (frame.Length < offset + 4) return null;
            etherType = BinaryPrimitives.ReadUInt16BigEndian(frame[(offset + 2)..]);
            offset += 4;
        }

        IPAddress src, dst;
        int udp;
        if (etherType == 0x0800)
        {
            if (frame.Length < offset + 20) return null;
            var ihl = (frame[offset] & 0x0f) * 4;
            if ((frame[offset] >> 4) != 4 || ihl < 20 || frame[offset + 9] != 17) return null;
            // A fragment past the first carries no UDP header.
            if ((BinaryPrimitives.ReadUInt16BigEndian(frame[(offset + 6)..]) & 0x1fff) != 0) return null;
            src = new IPAddress(frame.Slice(offset + 12, 4));
            dst = new IPAddress(frame.Slice(offset + 16, 4));
            udp = offset + ihl;
        }
        else if (etherType == 0x86dd)
        {
            if (frame.Length < offset + 40 || (frame[offset] >> 4) != 6 || frame[offset + 6] != 17) return null;
            src = new IPAddress(frame.Slice(offset + 8, 16));
            dst = new IPAddress(frame.Slice(offset + 24, 16));
            udp = offset + 40;
        }
        else
        {
            return null;
        }

        if (frame.Length < udp + 8) return null;
        var srcPort = BinaryPrimitives.ReadUInt16BigEndian(frame[udp..]);
        var dstPort = BinaryPrimitives.ReadUInt16BigEndian(frame[(udp + 2)..]);
        if (srcPort != 53 && dstPort != 53) return null;
        var message = frame[(udp + 8)..];
        return ParseMessage(message, src, dst, srcPort, dstPort);
    }

    /// <summary>Parses the DNS message itself. Public for tests.</summary>
    public static DnsPacket? ParseMessage(ReadOnlySpan<byte> msg, IPAddress src, IPAddress dst, int srcPort, int dstPort)
    {
        if (msg.Length < 12) return null;
        var txId = BinaryPrimitives.ReadUInt16BigEndian(msg);
        var flags = BinaryPrimitives.ReadUInt16BigEndian(msg[2..]);
        var isResponse = (flags & 0x8000) != 0;
        var opcode = (flags >> 11) & 0x0f;
        if (opcode != 0) return null; // standard queries only
        var rcode = RCodeName(flags & 0x0f);
        var qdCount = BinaryPrimitives.ReadUInt16BigEndian(msg[4..]);
        var anCount = BinaryPrimitives.ReadUInt16BigEndian(msg[6..]);
        if (qdCount < 1) return null;

        var pos = 12;
        if (ReadName(msg, ref pos) is not { } name) return null;
        if (msg.Length < pos + 4) return null;
        var qtype = TypeName(BinaryPrimitives.ReadUInt16BigEndian(msg[pos..]));
        pos += 4;
        // Skip any further questions; servers send one.
        for (var q = 1; q < qdCount; q++)
        {
            if (ReadName(msg, ref pos) == null || msg.Length < pos + 4) return null;
            pos += 4;
        }

        var answers = new List<DnsAnswer>();
        if (isResponse)
        {
            for (var a = 0; a < anCount && answers.Count < MaxAnswers; a++)
            {
                if (ReadName(msg, ref pos) == null || msg.Length < pos + 10) break;
                var type = BinaryPrimitives.ReadUInt16BigEndian(msg[pos..]);
                var ttl = (int)Math.Min(int.MaxValue, BinaryPrimitives.ReadUInt32BigEndian(msg[(pos + 4)..]));
                var rdLength = BinaryPrimitives.ReadUInt16BigEndian(msg[(pos + 8)..]);
                pos += 10;
                if (msg.Length < pos + rdLength) break;
                var rdata = msg.Slice(pos, rdLength);
                switch (type)
                {
                    case 1 when rdLength == 4:
                        answers.Add(new DnsAnswer("A", new IPAddress(rdata).ToString(), ttl));
                        break;
                    case 28 when rdLength == 16:
                        answers.Add(new DnsAnswer("AAAA", new IPAddress(rdata).ToString(), ttl));
                        break;
                    case 5:
                        var cnamePos = pos;
                        if (ReadName(msg, ref cnamePos) is { } target)
                            answers.Add(new DnsAnswer("CNAME", target, ttl));
                        break;
                }
                pos += rdLength;
            }
        }

        return new DnsPacket(src, dst, srcPort, dstPort, txId, isResponse, name, qtype, rcode, answers);
    }

    /// <summary>
    /// Reads a possibly compressed name at <paramref name="pos"/>, advancing it past the name's
    /// in-place bytes. Pointer loops and over-long names are null.
    /// </summary>
    private static string? ReadName(ReadOnlySpan<byte> msg, ref int pos)
    {
        var sb = new StringBuilder();
        var cursor = pos;
        var jumped = false;
        var jumps = 0;
        while (true)
        {
            if (cursor >= msg.Length) return null;
            var len = msg[cursor];
            if (len == 0)
            {
                cursor++;
                break;
            }
            if ((len & 0xc0) == 0xc0)
            {
                if (cursor + 1 >= msg.Length || ++jumps > 16) return null;
                var target = ((len & 0x3f) << 8) | msg[cursor + 1];
                if (!jumped) pos = cursor + 2;
                jumped = true;
                cursor = target;
                continue;
            }
            if ((len & 0xc0) != 0 || cursor + 1 + len > msg.Length) return null;
            if (sb.Length > 0) sb.Append('.');
            foreach (var b in msg.Slice(cursor + 1, len))
                sb.Append(b is >= 0x21 and <= 0x7e ? (char)b : '?');
            if (sb.Length > MaxNameLength) return null;
            cursor += 1 + len;
        }
        if (!jumped) pos = cursor;
        return sb.Length == 0 ? "." : sb.ToString().ToLowerInvariant();
    }

    private static string TypeName(ushort type) => type switch
    {
        1 => "A",
        2 => "NS",
        5 => "CNAME",
        6 => "SOA",
        12 => "PTR",
        15 => "MX",
        16 => "TXT",
        28 => "AAAA",
        33 => "SRV",
        64 => "SVCB",
        65 => "HTTPS",
        255 => "ANY",
        _ => $"TYPE{type}",
    };

    private static string RCodeName(int rcode) => rcode switch
    {
        0 => "NOERROR",
        1 => "FORMERR",
        2 => "SERVFAIL",
        3 => "NXDOMAIN",
        4 => "NOTIMP",
        5 => "REFUSED",
        _ => $"RCODE{rcode}",
    };
}
