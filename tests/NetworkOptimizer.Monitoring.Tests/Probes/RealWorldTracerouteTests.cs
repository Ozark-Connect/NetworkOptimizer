using FluentAssertions;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Monitoring.Probes;
using Xunit;

namespace NetworkOptimizer.Monitoring.Tests.Probes;

/// <summary>
/// Verifies the traceroute parser against output shaped like real Linux traceroute
/// runs. Pins behavior we depend on for the upstream tracer wizard's hop-labelling
/// logic - most importantly, that hostname/IP pairs from PTR resolution land in
/// TraceHop.Hostname and TraceHop.Address respectively. All addresses and PTR
/// names below are synthetic (RFC 5737 / RFC 2606 spaces) so the fixtures don't
/// embed any specific operator's network.
/// </summary>
public class RealWorldTracerouteTests
{
    [Fact]
    public void Parse_PtrAndStarHops_PreservesHostnamesAndIps()
    {
        var output = """
            traceroute to 192.0.2.1 (192.0.2.1), 8 hops max, 60 byte packets
             1  _gateway (192.168.1.1)  0.201 ms  0.231 ms  0.172 ms
             2  edge1.example.net (198.51.100.1)  3.449 ms  2.907 ms  3.062 ms
             3  border1.example.net (198.51.100.2)  3.462 ms  3.431 ms  3.463 ms
             4  * * *
             5  * * *
             6  core1.transit.example.com (203.0.113.1)  13.217 ms  12.577 ms  12.929 ms
             7  203.0.113.50 (203.0.113.50)  13.154 ms  13.955 ms  13.980 ms
             8  203.0.113.99 (203.0.113.99)  13.555 ms  13.651 ms  13.350 ms
            """;

        var r = TracerouteOutputParser.Parse(
            output,
            new ProbeTarget("192.0.2.1", ProbeMode.Icmp),
            ProbeVantage.Server,
            ProbeMode.Icmp);

        r.Hops.Should().HaveCount(8);

        r.Hops[0].Hostname.Should().Be("_gateway");
        r.Hops[0].Address.Should().Be("192.168.1.1");

        // The key wizard signal: ISP-attributable hostnames preserved
        r.Hops[1].Hostname.Should().Be("edge1.example.net");
        r.Hops[1].Address.Should().Be("198.51.100.1");

        r.Hops[2].Hostname.Should().Be("border1.example.net");
        r.Hops[2].Address.Should().Be("198.51.100.2");

        // Non-responding hops
        r.Hops[3].Responded.Should().BeFalse();
        r.Hops[4].Responded.Should().BeFalse();

        // Transit-style hostname
        r.Hops[5].Hostname.Should().Be("core1.transit.example.com");
        r.Hops[5].Address.Should().Be("203.0.113.1");

        // Hops where PTR == IP - hostname capture still records the IP form
        r.Hops[6].Address.Should().Be("203.0.113.50");
        r.Hops[7].Address.Should().Be("203.0.113.99");

        // We didn't see the actual target (192.0.2.1) so Reached should be false
        r.Reached.Should().BeFalse();
    }

    [Fact]
    public void Parse_EcmpSplayAndTargetReached_CapturesPtrAndReached()
    {
        // Hop 7 exercises the ECMP edge case (the parser currently captures only the
        // first IP per hop; continuation lines aren't picked up by the hop-line regex,
        // which is acceptable for the MVP - first-responder semantics are what the
        // wizard's per-hop labelling needs).
        var output = """
            traceroute to 192.0.2.1 (192.0.2.1), 8 hops max, 40 byte packets
             1  router (192.168.50.1)  1.062 ms  0.808 ms  0.342 ms
             2  192.168.1.254 (192.168.1.254)  1.334 ms  1.346 ms  0.826 ms
             3  edge1.example.com (198.51.100.10)  2.265 ms  2.378 ms  2.267 ms
             4  198.51.100.11 (198.51.100.11)  2.053 ms  2.127 ms  2.047 ms
             5  203.0.113.20 (203.0.113.20)  2.371 ms  2.741 ms  2.319 ms
             6  * * *
             7  203.0.113.21 (203.0.113.21)  5.055 ms
                203.0.113.22 (203.0.113.22)  3.302 ms
                203.0.113.23 (203.0.113.23)  14.629 ms
             8  target.example.com (192.0.2.1)  6.351 ms  3.592 ms  3.311 ms
            """;

        var r = TracerouteOutputParser.Parse(
            output,
            new ProbeTarget("192.0.2.1", ProbeMode.Icmp),
            ProbeVantage.Server,
            ProbeMode.Icmp);

        r.Hops.Should().HaveCountGreaterThanOrEqualTo(7);

        // Hop with a real PTR survives the parse
        r.Hops[2].Hostname.Should().Be("edge1.example.com");
        r.Hops[2].Address.Should().Be("198.51.100.10");

        // Final hop reaches the target and carries an identity PTR
        var lastHop = r.Hops.Last(h => h.HopNumber == 8);
        lastHop.Hostname.Should().Be("target.example.com");
        lastHop.Address.Should().Be("192.0.2.1");

        r.Reached.Should().BeTrue();
    }
}
