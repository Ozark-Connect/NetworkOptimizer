using FluentAssertions;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Monitoring.Probes;
using Xunit;

namespace NetworkOptimizer.Monitoring.Tests.Probes;

public class TracerouteOutputParserTests
{
    private static readonly ProbeTarget Target = new("203.0.113.10", ProbeMode.Icmp);
    private static readonly ProbeVantage Vantage = ProbeVantage.Server;

    [Fact]
    public void Parse_StandardLinuxOutput_ExtractsHopsWithRtts()
    {
        var output = """
            traceroute to 203.0.113.10 (203.0.113.10), 30 hops max, 60 byte packets
             1  10.0.0.1 (10.0.0.1)  1.234 ms  1.345 ms  1.456 ms
             2  192.0.2.1 (192.0.2.1)  5.6 ms  5.7 ms  5.8 ms
             3  cr1.example.net (198.51.100.1)  10.1 ms  10.2 ms  10.3 ms
             4  * * *
             5  203.0.113.10 (203.0.113.10)  20.5 ms  20.6 ms  20.7 ms
            """;

        var r = TracerouteOutputParser.Parse(output, Target, Vantage, ProbeMode.Icmp);

        r.Hops.Should().HaveCount(5);
        r.Reached.Should().BeTrue();

        r.Hops[0].Address.Should().Be("10.0.0.1");
        r.Hops[0].RttAvgMs.Should().BeApproximately(1.345, 0.01);
        r.Hops[0].Responses.Should().Be(3);

        r.Hops[2].Hostname.Should().Be("cr1.example.net");
        r.Hops[2].Address.Should().Be("198.51.100.1");

        r.Hops[3].Responded.Should().BeFalse();
        r.Hops[3].Address.Should().BeNull();

        r.Hops[4].Address.Should().Be("203.0.113.10");
    }

    [Fact]
    public void Parse_BusyBoxOutput_ExtractsHopsWithoutParens()
    {
        // busybox traceroute often emits "ip rtt rtt rtt" without parenthesized hostname.
        var output = """
            traceroute to 1.1.1.1 (1.1.1.1), 30 hops max
             1  10.0.0.1  1.2 ms  1.3 ms  1.4 ms
             2  *  *  *
             3  1.1.1.1  3.0 ms  3.1 ms  3.2 ms
            """;

        var r = TracerouteOutputParser.Parse(output, new ProbeTarget("1.1.1.1", ProbeMode.Icmp), Vantage, ProbeMode.Icmp);

        r.Hops.Should().HaveCount(3);
        r.Hops[0].Address.Should().Be("10.0.0.1");
        r.Hops[1].Responded.Should().BeFalse();
        r.Hops[2].Address.Should().Be("1.1.1.1");
        r.Reached.Should().BeTrue();
    }

    [Fact]
    public void Parse_EmptyOutput_ReturnsErrorResult()
    {
        var r = TracerouteOutputParser.Parse("", Target, Vantage, ProbeMode.Icmp);

        r.Hops.Should().BeEmpty();
        r.Reached.Should().BeFalse();
        r.ErrorMessage.Should().NotBeNullOrEmpty();
    }
}
