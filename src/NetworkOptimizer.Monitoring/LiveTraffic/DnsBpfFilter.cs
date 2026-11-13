using NetworkOptimizer.Monitoring.UciInform;

namespace NetworkOptimizer.Monitoring.LiveTraffic;

/// <summary>
/// The classic BPF program for "udp port 53" over Ethernet (IPv4 and IPv6), the same program
/// <c>tcpdump -dd 'udp port 53'</c> emits. The kernel runs it before any copy to user space, so
/// only DNS frames ever leave the kernel.
/// </summary>
public static class DnsBpfFilter
{
    /// <summary>Bytes of each accepted frame handed to user space: a full EDNS response.</summary>
    public const uint SnapLength = 4096 + 128;

    public static List<UciBpfFilter.Instruction> Build() =>
    [
        new(0x28, 0, 0, 12),          // 0  ldh [12]            ethertype
        new(0x15, 0, 6, 0x86dd),      // 1  jeq IPv6 ? 2 : 8
        new(0x30, 0, 0, 20),          // 2  ldb [20]            IPv6 next header
        new(0x15, 0, 15, 17),         // 3  jeq UDP ? 4 : reject
        new(0x28, 0, 0, 54),          // 4  ldh [54]            source port
        new(0x15, 12, 0, 53),         // 5  jeq 53 ? accept : 6
        new(0x28, 0, 0, 56),          // 6  ldh [56]            destination port
        new(0x15, 10, 11, 53),        // 7  jeq 53 ? accept : reject
        new(0x15, 0, 10, 0x0800),     // 8  jeq IPv4 ? 9 : reject
        new(0x30, 0, 0, 23),          // 9  ldb [23]            protocol
        new(0x15, 0, 8, 17),          // 10 jeq UDP ? 11 : reject
        new(0x28, 0, 0, 20),          // 11 ldh [20]            flags + fragment offset
        new(0x45, 6, 0, 0x1fff),      // 12 jset offset ? reject : 13
        new(0xb1, 0, 0, 14),          // 13 ldxb 4*([14]&0xf)   IP header length
        new(0x48, 0, 0, 14),          // 14 ldh [x+14]          source port
        new(0x15, 2, 0, 53),          // 15 jeq 53 ? accept : 16
        new(0x48, 0, 0, 16),          // 16 ldh [x+16]          destination port
        new(0x15, 0, 1, 53),          // 17 jeq 53 ? accept : reject
        new(0x06, 0, 0, SnapLength),  // 18 accept
        new(0x06, 0, 0, 0),           // 19 reject
    ];
}
