using FluentAssertions;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Monitoring.Probes;
using Xunit;

namespace NetworkOptimizer.Monitoring.Tests.Probes;

public class PingOutputParserTests
{
    private static readonly ProbeTarget Target = new("1.1.1.1", ProbeMode.Icmp);
    private static readonly ProbeVantage Vantage = ProbeVantage.Server;

    [Fact]
    public void Parse_StandardLinuxIputilsOutput_ExtractsAllFields()
    {
        var output = """
            PING 1.1.1.1 (1.1.1.1) 56(84) bytes of data.
            64 bytes from 1.1.1.1: icmp_seq=1 ttl=58 time=12.3 ms
            64 bytes from 1.1.1.1: icmp_seq=2 ttl=58 time=11.8 ms
            64 bytes from 1.1.1.1: icmp_seq=3 ttl=58 time=12.1 ms

            --- 1.1.1.1 ping statistics ---
            3 packets transmitted, 3 received, 0% packet loss, time 2004ms
            rtt min/avg/max/mdev = 11.800/12.067/12.300/0.205 ms
            """;

        var r = PingOutputParser.Parse(output, Target, Vantage, 3);

        r.Sent.Should().Be(3);
        r.Received.Should().Be(3);
        r.Success.Should().BeTrue();
        r.LossPercent.Should().Be(0);
        r.RttMinMs.Should().BeApproximately(11.8, 0.001);
        r.RttAvgMs.Should().BeApproximately(12.067, 0.001);
        r.RttMaxMs.Should().BeApproximately(12.3, 0.001);
        r.JitterMs.Should().BeApproximately(0.205, 0.001);
    }

    [Fact]
    public void Parse_BusyBoxOutput_ExtractsViaPerReplyFallback()
    {
        var output = """
            PING 192.0.2.1 (192.0.2.1): 56 data bytes
            64 bytes from 192.0.2.1: seq=0 ttl=64 time=1.234 ms
            64 bytes from 192.0.2.1: seq=1 ttl=64 time=2.345 ms
            64 bytes from 192.0.2.1: seq=2 ttl=64 time=3.456 ms

            --- 192.0.2.1 ping statistics ---
            3 packets transmitted, 3 packets received, 0% packet loss
            round-trip min/avg/max = 1.234/2.345/3.456 ms
            """;

        var r = PingOutputParser.Parse(output, Target, Vantage, 3);

        r.Sent.Should().Be(3);
        r.Received.Should().Be(3);
        r.RttMinMs.Should().BeApproximately(1.234, 0.001);
        r.RttAvgMs.Should().BeApproximately(2.345, 0.001);
        r.RttMaxMs.Should().BeApproximately(3.456, 0.001);
    }

    [Fact]
    public void Parse_PacketLoss_SetsLossPercent()
    {
        var output = """
            PING 198.51.100.1 (198.51.100.1) 56(84) bytes of data.

            --- 198.51.100.1 ping statistics ---
            10 packets transmitted, 4 received, 60% packet loss, time 9123ms
            rtt min/avg/max/mdev = 20.1/22.5/25.0/2.0 ms
            """;

        var r = PingOutputParser.Parse(output, Target, Vantage, 10);

        r.Sent.Should().Be(10);
        r.Received.Should().Be(4);
        r.LossPercent.Should().Be(60.0);
    }

    [Fact]
    public void Parse_EmptyOutput_ReturnsZeroReceived()
    {
        var r = PingOutputParser.Parse("", Target, Vantage, 5);

        r.Sent.Should().Be(5);
        r.Received.Should().Be(0);
        r.Success.Should().BeFalse();
        r.ErrorMessage.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Parse_PerReplyOnlyMissingSummary_ComputesAggregates()
    {
        var output = """
            64 bytes from 1.1.1.1: icmp_seq=1 ttl=58 time=10 ms
            64 bytes from 1.1.1.1: icmp_seq=2 ttl=58 time=20 ms
            64 bytes from 1.1.1.1: icmp_seq=3 ttl=58 time=30 ms
            """;

        var r = PingOutputParser.Parse(output, Target, Vantage, 3);

        r.Received.Should().Be(3);
        r.RttMinMs.Should().Be(10);
        r.RttMaxMs.Should().Be(30);
        r.RttAvgMs.Should().Be(20);
        r.JitterMs.Should().NotBeNull();
    }
}
