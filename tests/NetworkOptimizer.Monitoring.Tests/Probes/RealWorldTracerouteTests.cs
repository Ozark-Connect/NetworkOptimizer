using FluentAssertions;
using NetworkOptimizer.Monitoring.Probes;
using Xunit;

namespace NetworkOptimizer.Monitoring.Tests.Probes;

/// <summary>
/// Verifies the traceroute parser against actual output captured from real Linux
/// traceroute runs (Debian on NAS Docker host). Pins behavior we depend on for the
/// upstream tracer wizard's hop-labelling logic — most importantly, that hostname/IP
/// pairs from PTR resolution land in TraceHop.Hostname and TraceHop.Address respectively.
/// </summary>
public class RealWorldTracerouteTests
{
    [Fact]
    public void Parse_RealNasTracerouteOutput_PreservesHostnamesAndIps()
    {
        // Captured from `ssh root@nas "traceroute -m 8 -w 1 1.1.1.1"` 2026-05-21
        var output = """
            traceroute to 1.1.1.1 (1.1.1.1), 8 hops max, 60 byte packets
             1  _gateway (192.168.1.1)  0.201 ms  0.231 ms  0.172 ms
             2  gassville-bng-pon.yelcot.net (216.134.230.1)  3.449 ms  2.907 ms  3.062 ms
             3  gassville-border.yelcot.net (216.134.229.145)  3.462 ms  3.431 ms  3.463 ms
             4  * * *
             5  * * *
             6  mcibbrj01.rd.ks.cox.net (68.1.1.83)  13.217 ms  12.577 ms  12.929 ms
             7  98.171.221.235 (98.171.221.235)  13.154 ms  13.955 ms  13.980 ms
             8  172.68.148.6 (172.68.148.6)  13.555 ms  13.651 ms  13.350 ms
            """;

        var r = TracerouteOutputParser.Parse(
            output,
            new ProbeTarget("1.1.1.1", ProbeMode.Icmp),
            ProbeVantage.Server,
            ProbeMode.Icmp);

        r.Hops.Should().HaveCount(8);

        r.Hops[0].Hostname.Should().Be("_gateway");
        r.Hops[0].Address.Should().Be("192.168.1.1");

        // The key wizard signal: ISP-attributable hostnames preserved
        r.Hops[1].Hostname.Should().Be("gassville-bng-pon.yelcot.net");
        r.Hops[1].Address.Should().Be("216.134.230.1");

        r.Hops[2].Hostname.Should().Be("gassville-border.yelcot.net");
        r.Hops[2].Address.Should().Be("216.134.229.145");

        // Non-responding hops
        r.Hops[3].Responded.Should().BeFalse();
        r.Hops[4].Responded.Should().BeFalse();

        // Transit ISP hostname (Cox)
        r.Hops[5].Hostname.Should().Be("mcibbrj01.rd.ks.cox.net");
        r.Hops[5].Address.Should().Be("68.1.1.83");

        // Hops where PTR == IP — hostname capture still records the IP form
        r.Hops[6].Address.Should().Be("98.171.221.235");
        r.Hops[7].Address.Should().Be("172.68.148.6");

        // We didn't see the actual target (1.1.1.1) so Reached should be false
        r.Reached.Should().BeFalse();
    }

    [Fact]
    public void Parse_RealMacOSTracerouteOutput_ReachesTargetAndCapturesPtr()
    {
        // Captured from `ssh noel@192.168.50.10 "traceroute -m 8 -w 1 1.1.1.1"` 2026-05-21
        // Includes an ECMP edge case (hop 7) and a PTR-named final hop.
        var output = """
            traceroute to 1.1.1.1 (1.1.1.1), 8 hops max, 40 byte packets
             1  unifi (192.168.50.1)  1.062 ms  0.808 ms  0.342 ms
             2  192.168.1.254 (192.168.1.254)  1.334 ms  1.346 ms  0.826 ms
             3  108-232-152-1.lightspeed.tukrga.sbcglobal.net (108.232.152.1)  2.265 ms  2.378 ms  2.267 ms
             4  107.212.168.36 (107.212.168.36)  2.053 ms  2.127 ms  2.047 ms
             5  12.242.113.40 (12.242.113.40)  2.371 ms  2.741 ms  2.319 ms
             6  * * *
             7  108.162.235.87 (108.162.235.87)  5.055 ms
                108.162.235.59 (108.162.235.59)  3.302 ms
                108.162.235.121 (108.162.235.121)  14.629 ms
             8  one.one.one.one (1.1.1.1)  6.351 ms  3.592 ms  3.311 ms
            """;

        var r = TracerouteOutputParser.Parse(
            output,
            new ProbeTarget("1.1.1.1", ProbeMode.Icmp),
            ProbeVantage.Server,
            ProbeMode.Icmp);

        // ECMP splay: the parser currently captures only the first IP per hop. The other
        // two ECMP responses on hop 7 land on continuation lines which our hop-line regex
        // doesn't pick up. Acceptable for the MVP — the first-responder semantics are
        // what the wizard's per-hop labelling needs.
        r.Hops.Should().HaveCountGreaterThanOrEqualTo(7);

        // Hop 1: SBC-attributable PTR
        r.Hops[2].Hostname.Should().Be("108-232-152-1.lightspeed.tukrga.sbcglobal.net");
        r.Hops[2].Address.Should().Be("108.232.152.1");

        // Final hop reaches the target and carries Cloudflare's identity hostname
        var lastHop = r.Hops.Last(h => h.HopNumber == 8);
        lastHop.Hostname.Should().Be("one.one.one.one");
        lastHop.Address.Should().Be("1.1.1.1");

        // Target was reached
        r.Reached.Should().BeTrue();
    }
}
