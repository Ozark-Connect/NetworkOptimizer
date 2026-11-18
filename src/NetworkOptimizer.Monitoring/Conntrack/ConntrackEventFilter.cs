using System.Buffers.Binary;
using NetworkOptimizer.Monitoring.UciInform;
using Ins = NetworkOptimizer.Monitoring.UciInform.UciBpfFilter.Instruction;

namespace NetworkOptimizer.Monitoring.Conntrack;

/// <summary>
/// The classic BPF socket filter for the handshake timer's ctnetlink socket. Keeps NEW events, TCP
/// updates in SYN_RECV or ESTABLISHED, and UDP updates that have seen a reply but are not yet assured;
/// drops the rest in the kernel (about half the events under load). Attributes are found with the
/// kernel's netlink extensions: SKF_AD_NLATTR sets A to the offset of attribute type X searched from
/// offset A, SKF_AD_NLATTR_NEST searches inside the attribute at A.
/// </summary>
public static class ConntrackEventFilter
{
    private const ushort LdImm = 0x00, LdxImm = 0x01, LdWordAbs = 0x20, LdByteAbs = 0x30, LdWordInd = 0x40,
        LdByteInd = 0x50, Tax = 0x07, JeqK = 0x15, JsetK = 0x45, RetK = 0x06;
    private const uint SkfAdOff = 0xFFFFF000, SkfAdNlattr = SkfAdOff + 12, SkfAdNlattrNest = SkfAdOff + 16;

    public static List<Ins> Build()
    {
        var p = new List<(ushort Code, string? Jt, string? Jf, uint K, string? Label)>
        {
            (LdByteAbs, null, null, 7, null),                       // high byte of nlmsg_flags (host order)
            (JsetK, "accept", null, 0x06, null),                    // NLM_F_CREATE | NLM_F_EXCL: a NEW event
            (LdImm, null, null, ConntrackEventParser.AttributesOffset, null),
            (LdxImm, null, null, ConntrackEventParser.CtaProtoinfo, null),
            (LdWordAbs, null, null, SkfAdNlattr, null),
            (JeqK, "udp", null, 0, null),                           // no protoinfo: not TCP
            (LdxImm, null, null, ConntrackEventParser.CtaProtoinfoTcp, null),
            (LdWordAbs, null, null, SkfAdNlattrNest, null),
            (JeqK, "drop", null, 0, null),
            (LdxImm, null, null, ConntrackEventParser.CtaProtoinfoTcpState, null),
            (LdWordAbs, null, null, SkfAdNlattrNest, null),
            (JeqK, "drop", null, 0, null),
            (Tax, null, null, 0, null),
            (LdByteInd, null, null, 4, null),                       // the state byte, after the 4-byte header
            (JeqK, "accept", null, (uint)ConntrackTcpState.SynRecv, null),
            (JeqK, "accept", "drop", (uint)ConntrackTcpState.Established, null),
            (LdImm, null, null, ConntrackEventParser.AttributesOffset, "udp"),
            (LdxImm, null, null, ConntrackEventParser.CtaStatus, null),
            (LdWordAbs, null, null, SkfAdNlattr, null),
            (JeqK, "drop", null, 0, null),
            (Tax, null, null, 0, null),
            (LdWordInd, null, null, 4, null),                       // status, big-endian as BPF loads it
            (JsetK, "drop", null, ConntrackEventParser.IpsAssured, null),
            (JsetK, "accept", "drop", ConntrackEventParser.IpsSeenReply, null),
            (RetK, null, null, 0, "drop"),
            (RetK, null, null, 0xFFFFFFFF, "accept"),             // the whole message: the kernel trims to this
        };
        var at = new Dictionary<string, int>();
        for (var i = 0; i < p.Count; i++)
            if (p[i].Label is { } label) at[label] = i;
        byte Jump(string? label, int from) => label == null ? (byte)0 : checked((byte)(at[label] - from - 1));
        return p.Select((x, i) => new Ins(x.Code, Jump(x.Jt, i), Jump(x.Jf, i), x.K)).ToList();
    }

    /// <summary>The program as <c>struct sock_filter</c> bytes.</summary>
    public static byte[] Serialize(IReadOnlyList<Ins> program) => UciBpfFilter.Serialize(program);

    /// <summary>Runs the program on one netlink message the way the kernel would, for tests.</summary>
    public static uint Evaluate(IReadOnlyList<Ins> program, ReadOnlySpan<byte> msg)
    {
        uint a = 0, x = 0;
        var pc = 0;
        while (pc < program.Count)
        {
            var ins = program[pc];
            switch (ins.Code)
            {
                case LdImm: a = ins.K; pc++; break;
                case LdxImm: x = ins.K; pc++; break;
                case Tax: x = a; pc++; break;
                case LdWordAbs when ins.K == SkfAdNlattr:
                    a = NlaFind(msg, (int)a, msg.Length - (int)a, (int)x);
                    pc++;
                    break;
                case LdWordAbs when ins.K == SkfAdNlattrNest:
                    if (a + 4 > msg.Length) { a = 0; pc++; break; }
                    var nestLen = BinaryPrimitives.ReadUInt16LittleEndian(msg[(int)a..]);
                    a = nestLen > msg.Length - a ? 0 : NlaFind(msg, (int)a + 4, nestLen - 4, (int)x);
                    pc++;
                    break;
                case LdByteAbs:
                    if (ins.K + 1 > msg.Length) return 0;
                    a = msg[(int)ins.K];
                    pc++;
                    break;
                case LdByteInd:
                    if (x + ins.K + 1 > msg.Length) return 0;
                    a = msg[(int)(x + ins.K)];
                    pc++;
                    break;
                case LdWordInd:
                    if (x + ins.K + 4 > msg.Length) return 0;
                    a = BinaryPrimitives.ReadUInt32BigEndian(msg[(int)(x + ins.K)..]);
                    pc++;
                    break;
                case JeqK: pc += 1 + (a == ins.K ? ins.Jt : ins.Jf); break;
                case JsetK: pc += 1 + ((a & ins.K) != 0 ? ins.Jt : ins.Jf); break;
                case RetK: return ins.K;
                default: throw new InvalidOperationException($"Unsupported BPF opcode 0x{ins.Code:x}");
            }
        }
        return 0;
    }

    // nla_find: the offset (from the message start) of the first attribute of this type, or 0.
    private static uint NlaFind(ReadOnlySpan<byte> msg, int start, int length, int type)
    {
        if (start < 0 || length < 4 || start + length > msg.Length) return 0;
        var span = msg.Slice(start, length);
        var o = 0;
        while (o + 4 <= span.Length)
        {
            int len = BinaryPrimitives.ReadUInt16LittleEndian(span[o..]);
            if (len < 4 || o + len > span.Length) return 0;
            if ((BinaryPrimitives.ReadUInt16LittleEndian(span[(o + 2)..]) & 0x3FFF) == type) return (uint)(start + o);
            o += (len + 3) & ~3;
        }
        return 0;
    }
}
