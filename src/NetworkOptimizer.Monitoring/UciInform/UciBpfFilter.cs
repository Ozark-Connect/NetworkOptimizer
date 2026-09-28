using System.Buffers.Binary;

namespace NetworkOptimizer.Monitoring.UciInform;

/// <summary>
/// Builds the classic BPF program an AF_PACKET socket attaches (SO_ATTACH_FILTER) to accept only
/// frames whose ethernet source is one of the given MACs. The kernel runs it before any copy to
/// user space, so a multi-gigabit WAN costs the capture nothing: only the UCI's own management
/// frames ever leave the kernel.
/// </summary>
public static class UciBpfFilter
{
    private const ushort LdWordAbs = 0x20;   // BPF_LD | BPF_W | BPF_ABS
    private const ushort LdHalfAbs = 0x28;   // BPF_LD | BPF_H | BPF_ABS
    private const ushort JeqK = 0x15;        // BPF_JMP | BPF_JEQ | BPF_K
    private const ushort RetK = 0x06;        // BPF_RET | BPF_K

    /// <summary>Bytes of each accepted frame handed to user space (covers a GRO-coalesced 64 KB segment).</summary>
    public const uint SnapLength = 262144;

    /// <summary>One <c>struct sock_filter</c>.</summary>
    public readonly record struct Instruction(ushort Code, byte Jt, byte Jf, uint K);

    /// <summary>
    /// The program for "ether src in <paramref name="macs"/>". Per MAC: load the first four source
    /// bytes (offset 6) and compare, then the last two (offset 10). A match returns
    /// <see cref="SnapLength"/>, and falling off the end returns 0 (drop).
    /// </summary>
    public static IReadOnlyList<Instruction> Build(IReadOnlyList<byte[]> macs)
    {
        if (macs.Count == 0)
            return new[] { new Instruction(RetK, 0, 0, 0) };

        var program = new List<Instruction>();
        // Each MAC block is 4 instructions; the two trailing returns are reject, then accept.
        var blocks = macs.Count;
        for (var i = 0; i < blocks; i++)
        {
            var mac = macs[i];
            if (mac.Length != 6)
                throw new ArgumentException("A MAC must be 6 bytes.", nameof(macs));

            var hi = BinaryPrimitives.ReadUInt32BigEndian(mac.AsSpan(0, 4));
            var lo = BinaryPrimitives.ReadUInt16BigEndian(mac.AsSpan(4, 2));

            // Instructions left after the current one, up to and including the reject return.
            var remainingBlocks = blocks - i - 1;
            var toNextBlockFromFirstJeq = (byte)2;                   // skip ldh + jeq
            var toAcceptFromSecondJeq = (byte)(remainingBlocks * 4 + 1); // skip later blocks + reject
            program.Add(new Instruction(LdWordAbs, 0, 0, 6));
            program.Add(new Instruction(JeqK, 0, toNextBlockFromFirstJeq, hi));
            program.Add(new Instruction(LdHalfAbs, 0, 0, 10));
            program.Add(new Instruction(JeqK, toAcceptFromSecondJeq, 0, lo));
        }
        program.Add(new Instruction(RetK, 0, 0, 0));
        program.Add(new Instruction(RetK, 0, 0, SnapLength));
        return program;
    }

    /// <summary>The program as the kernel's <c>struct sock_filter</c> array (8 bytes each, host order).</summary>
    public static byte[] Serialize(IReadOnlyList<Instruction> program)
    {
        var bytes = new byte[program.Count * 8];
        for (var i = 0; i < program.Count; i++)
        {
            var span = bytes.AsSpan(i * 8, 8);
            var ins = program[i];
            BitConverter.TryWriteBytes(span[..2], ins.Code);
            span[2] = ins.Jt;
            span[3] = ins.Jf;
            BitConverter.TryWriteBytes(span[4..], ins.K);
        }
        return bytes;
    }

    /// <summary>
    /// Runs the program against a frame, the way the kernel would. Lets tests prove a filter
    /// without a Linux socket.
    /// </summary>
    public static uint Evaluate(IReadOnlyList<Instruction> program, ReadOnlySpan<byte> frame)
    {
        uint acc = 0;
        var pc = 0;
        while (pc < program.Count)
        {
            var ins = program[pc];
            switch (ins.Code)
            {
                case LdWordAbs:
                    if (ins.K + 4 > frame.Length) return 0;
                    acc = BinaryPrimitives.ReadUInt32BigEndian(frame.Slice((int)ins.K, 4));
                    pc++;
                    break;
                case LdHalfAbs:
                    if (ins.K + 2 > frame.Length) return 0;
                    acc = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice((int)ins.K, 2));
                    pc++;
                    break;
                case JeqK:
                    pc += 1 + (acc == ins.K ? ins.Jt : ins.Jf);
                    break;
                case RetK:
                    return ins.K;
                default:
                    throw new InvalidOperationException($"Unsupported BPF opcode 0x{ins.Code:x}");
            }
        }
        return 0;
    }

    /// <summary>Parses "aa:bb:cc:dd:ee:ff" (or dash/no separator forms) into 6 bytes; null when malformed.</summary>
    public static byte[]? ParseMac(string? mac)
    {
        if (string.IsNullOrWhiteSpace(mac)) return null;
        var hex = mac.Replace(":", "").Replace("-", "").Replace(".", "").Trim();
        if (hex.Length != 12) return null;
        try { return Convert.FromHexString(hex); }
        catch (FormatException) { return null; }
    }

    /// <summary>Lowercase colon form of a 6-byte MAC.</summary>
    public static string FormatMac(ReadOnlySpan<byte> mac) =>
        string.Join(':', mac.ToArray().Select(b => b.ToString("x2")));
}
