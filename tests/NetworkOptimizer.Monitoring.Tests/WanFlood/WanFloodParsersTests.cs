using FluentAssertions;
using NetworkOptimizer.Monitoring.WanFlood;
using Xunit;

namespace NetworkOptimizer.Monitoring.Tests.WanFlood;

public class WanFloodParsersTests
{
    [Fact]
    public void ProcStat_PerCpuSoftirqShare()
    {
        var before = WanFloodParsers.ParseProcStat("cpu  10 0 10 100 0 0 10 0 0 0\ncpu0 1 0 1 50 0 0 0 0 0 0\ncpu2 1 0 1 10 0 0 10 0 0 0\n");
        var after = WanFloodParsers.ParseProcStat("cpu  10 0 10 100 0 0 10 0 0 0\ncpu0 1 0 1 150 0 0 0 0 0 0\ncpu2 1 0 1 15 0 0 105 0 0 0\n");

        before.Keys.Should().BeEquivalentTo([0, 2]);
        WanFloodParsers.SoftirqPercent(before[0], after[0]).Should().Be(0);
        WanFloodParsers.SoftirqPercent(before[2], after[2]).Should().BeApproximately(95, 0.01);
        WanFloodParsers.SoftirqPercent(after[2], after[2]).Should().Be(0);
    }

    [Fact]
    public void SoftnetStat_HexColumnsByLine()
    {
        var rows = WanFloodParsers.ParseSoftnetStat(
            "005b10c9 00000000 00000ec2 00000000 00000000 00000000 00000000 00000000 00000000 00000000 00000000\n" +
            "c95f3b30 002b78f4 0000212d 00000000 00000000 00000000 00000000 00000000 00000000 1f4aa56f 00000000\n");
        rows.Should().HaveCount(2);
        rows[1].Dropped.Should().Be(2_849_012);
        rows[1].TimeSqueeze.Should().Be(8493);
    }

    [Fact]
    public void ConntrackStat_SumsDropColumnsButNotInsertFailed()
    {
        const string text =
            "entries  searched found new invalid ignore delete delete_list insert insert_failed drop early_drop icmp_error\n" +
            "00000100 00000000 00000000 00000000 00000000 00000000 00000000 00000000 00000000 00000002 00000001 00000000 00000000\n" +
            "00000100 00000000 00000000 00000000 00000000 00000000 00000000 00000000 00000000 00000000 00000010 00000003 00000000\n";
        // insert_failed (2 here) is routine on multi-core gateways and must not count as a full table.
        WanFloodParsers.ParseConntrackDrops(text).Should().Be(1 + 16 + 3);
        WanFloodParsers.ParseConntrackDrops("").Should().BeNull();
    }

    [Fact]
    public void IptablesCounters_LastDropOfTheChain()
    {
        const string text =
            "Chain UBIOS_WAN_LOCAL_USER (1 references)\n" +
            "    pkts      bytes target     prot opt in     out     source               destination\n" +
            "     120     9000 RETURN     all  --  *      *       0.0.0.0/0            0.0.0.0/0            ctstate RELATED,ESTABLISHED\n" +
            "      10      600 DROP       all  --  *      *       0.0.0.0/0            0.0.0.0/0            ctstate INVALID\n" +
            " 4380000 6240000000 DROP       all  --  *      *       0.0.0.0/0            0.0.0.0/0            /* 00000001097364144127 */\n";
        var rules = WanFloodParsers.ParseIptablesCounters(text);
        WanFloodParsers.LastDropPackets(rules).Should().Be(4_380_000);
        WanFloodParsers.DropPackets(rules).Should().Be(4_380_010);
        WanFloodParsers.LastDropPackets(WanFloodParsers.ParseIptablesCounters("Chain X (0 references)\n")).Should().BeNull();
    }

    [Fact]
    public void Hashlimit_EntriesAndOverLimit()
    {
        const string text =
            "9 203.0.113.5:0->0.0.0.0:0 0 64000 32000\n" +
            "9 198.51.100.9:0->0.0.0.0:0 128000 128000 32000\n" +
            "8 0.0.0.0:0->0.0.0.0:47000 10 64000 32000\n" +
            "7 2001:db8::1:0->:::0 0 64000 32000\n";
        var entries = WanFloodParsers.ParseHashlimit(text);
        entries.Should().HaveCount(4);
        entries[0].Should().Match<HashlimitEntry>(e => e.Source == "203.0.113.5" && e.OverLimit);
        entries[1].OverLimit.Should().BeFalse();
        entries[2].DestinationPort.Should().Be(47000);
        entries[3].Source.Should().Be("2001:db8::1");
    }

    [Fact]
    public void Tc_PacketsAndPrefs()
    {
        const string text =
            "filter parent ffff: protocol ip pref 100 u32 chain 0 fh 801::800 order 2048 key ht 801 bkt 0 terminal flowid ???\n" +
            "  match c6336405/ffffffff at 12\n" +
            "\taction order 1: gact action drop\n" +
            "\t random type none pass val 0\n" +
            "\t index 1 ref 1 bind 1 installed 5 sec\n" +
            "\tAction statistics:\n" +
            "\tSent 56000 bytes 1234 pkt (dropped 1234, overlimits 0 requeues 0)\n" +
            "filter parent ffff: protocol all pref 49152 u32 chain 0\n";
        WanFloodParsers.ParseTcFilterPackets(text).Should().Be(1234);
        WanFloodParsers.ParseTcPrefs(text).Should().BeEquivalentTo([100, 49152]);
    }

    [Fact]
    public void HashlimitFull_LogLine()
    {
        WanFloodParsers.ParseHashlimitFull("xt_hashlimit: max count of 262144 reached").Should().Be(262144);
        WanFloodParsers.ParseHashlimitFull("kernel: [12.3] xt_hashlimit: max count of 4096 reached").Should().Be(4096);
        WanFloodParsers.ParseHashlimitFull("NETOPT_OUT: IN=br0").Should().BeNull();
    }

    [Fact]
    public void ProcNet_ListeningPorts()
    {
        const string tcp =
            "  sl  local_address rem_address   st tx_queue rx_queue\n" +
            "   0: 00000000:0016 00000000:0000 0A 00000000:00000000\n" +
            "   1: 0100007F:1F90 0100007F:9C40 01 00000000:00000000\n";
        WanFloodParsers.ParseProcNetPorts(tcp, tcp: true).Should().BeEquivalentTo([22]);
        WanFloodParsers.ParseProcNetPorts(tcp, tcp: false).Should().BeEquivalentTo([22, 8080]);
    }

    [Fact]
    public void Dnat_PortsSinglesListsAndRanges()
    {
        const string text =
            "-A UBIOS_PREROUTING_USER_HOOK -i eth4 -p tcp -m tcp --dport 443 -j DNAT --to-destination 192.0.2.10:443\n" +
            "-A X -i eth4 -p udp -m multiport --dports 27015,27016,7000:7002 -j DNAT --to-destination 192.0.2.11\n" +
            "-A X -i eth4 -p udp -m udp --dport 6000:6002 -j DNAT --to-destination 192.0.2.12\n" +
            "-A X -i eth4 -p tcp -m tcp --dport 8443 -j ACCEPT\n";
        WanFloodParsers.ParseDnatPorts(text).Should().BeEquivalentTo([443, 27015, 27016, 7000, 7001, 7002, 6000, 6001, 6002]);
    }

    [Fact]
    public void Conntrack_SessionAndReplyPort()
    {
        const string text =
            "ipv4     2 udp      17 29 src=203.0.113.5 dst=198.51.100.2 sport=40000 dport=51820 src=192.0.2.10 dst=203.0.113.5 sport=51820 dport=40000 [ASSURED] mark=0\n" +
            "ipv4     2 udp      17 29 src=192.0.2.20 dst=1.1.1.1 sport=5353 dport=53 src=1.1.1.1 dst=198.51.100.2 sport=53 dport=47000 mark=0\n";
        WanFloodParsers.ScanConntrack(new StringReader(text), "203.0.113.5", "198.51.100.2", "udp", 51820).FromSource.Should().BeTrue();
        WanFloodParsers.ScanConntrack(new StringReader(text), "203.0.113.6", "198.51.100.2", "udp", 51820).FromSource.Should().BeFalse();
        WanFloodParsers.ScanConntrack(new StringReader(text), null, "198.51.100.2", "udp", 47000).ReplyToPort.Should().BeTrue();
        WanFloodParsers.ScanConntrack(new StringReader(text), null, "198.51.100.2", "udp", 47001).ReplyToPort.Should().BeFalse();
        WanFloodParsers.ScanConntrack(new StringReader(text), null, "198.51.100.2", "udp", 51820).AnyToPort.Should().BeTrue();
        WanFloodParsers.ScanConntrack(new StringReader(text), null, "198.51.100.2", "udp", 47000).AnyToPort.Should().BeFalse();
    }

    [Fact]
    public void CpuMask_AndInterfaceAddress()
    {
        WanFloodParsers.ParseCpuMask("e\n").Should().BeEquivalentTo([1, 2, 3]);
        WanFloodParsers.ParseCpuMask("00000000,00000101").Should().BeEquivalentTo([0, 8]);
        WanFloodParsers.ParseCpuMask("0").Should().BeEmpty();
        WanFloodParsers.ParseInterfaceIpv4("5: eth4    inet 198.51.100.2/24 brd 198.51.100.255 scope global eth4\n").Should().Be("198.51.100.2");
        WanFloodParsers.ParseInterfaceIpv4("").Should().BeNull();
    }
}
