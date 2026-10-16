using FluentAssertions;
using Xunit;

namespace NetworkOptimizer.AgentProtocol.Tests;

public class GatewayDropLogTests
{
    [Fact]
    public void Parse_ReadsAnIpv4Drop()
    {
        var line = "[123.456] NETOPT_OUT:IN=br0 OUT=eth4 MAC=aa:bb:cc:dd:ee:ff SRC=10.0.0.20 DST=203.0.113.7 LEN=60 TOS=0x00 PROTO=TCP SPT=51234 DPT=443 WINDOW=64240 SYN";
        GatewayDropLog.Parse(line, "NETOPT_OUT:").Should().Be(new GatewayDropLog("10.0.0.20", "203.0.113.7", "TCP", 443));
    }

    [Fact]
    public void Parse_ReadsAnIpv6Drop()
    {
        var line = "NETOPT_OUT:IN=br0 OUT=eth4 SRC=2001:db8::20 DST=2001:db8:ffff::7 LEN=80 PROTO=UDP SPT=5353 DPT=8443";
        GatewayDropLog.Parse(line, "NETOPT_OUT:").Should().Be(new GatewayDropLog("2001:db8::20", "2001:db8:ffff::7", "UDP", 8443));
    }

    [Fact]
    public void ParseNeighbors_MapsAddressesToMacs()
    {
        var output = """
            10.0.0.20 dev br0 lladdr AA:BB:CC:00:11:22 REACHABLE
            2001:db8::20 dev br0 lladdr aa:bb:cc:00:11:22 STALE
            10.0.0.99 dev br0  FAILED
            """;
        var map = GatewayDropLog.ParseNeighbors(output);
        map.Should().HaveCount(2);
        map["2001:db8::20"].Should().Be("aa:bb:cc:00:11:22");
        map["10.0.0.20"].Should().Be("aa:bb:cc:00:11:22");
    }

    [Fact]
    public void Parse_IgnoresOtherLines()
    {
        GatewayDropLog.Parse("sfe_ipv4_remove_connection: Connection has been removed already", "NETOPT_OUT:").Should().BeNull();
        GatewayDropLog.Parse("NETOPT_OUT:IN=br0 OUT=eth4 PROTO=ICMP", "NETOPT_OUT:").Should().BeNull();
    }
}
