using FluentAssertions;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Monitoring.Probes;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services.Monitoring;
using NetworkOptimizer.Web.Services.Monitoring.IspHealth;
using NetworkOptimizer.Web.Services.Monitoring.PathLocalization;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring.PathLocalization;

/// <summary>
/// Localization from the reactive rounds (spec 5 and 6), on paths shaped like a real evening event: AWS
/// us-east and a Chicago speedtest degrading together over different transits. Addresses are documentation
/// ranges; the transit and destination ASNs are the real networks, the access ISP a documentation ASN.
///
///   ORD path: 1 gateway, 2 BNG (monitored), 3 first Lumen hop (monitored), 4 Lumen Denver,
///             5 Lumen-Akamai handoff, 6 Akamai DEN, 7 Akamai ORD, 8 Linode ORD (destination)
/// </summary>
public class PathTraceAnalyzerTests
{
    private static readonly IspHealthOptions Options = new();

    private const string Gw = "192.168.1.1";
    private const string Bng = "100.64.0.1";
    private const string LumenFirst = "198.51.100.3";
    private const string LumenAtl = "198.51.100.4";
    private const string LumenEdge = "198.51.100.5";
    private const string AkamaiAtl = "203.0.113.6";
    private const string AkamaiOrd = "203.0.113.7";
    private const string LinodeOrd = "203.0.113.80";

    private static readonly Dictionary<string, AsnLookup> Asns = new()
    {
        [Bng] = new(64500, "Access ISP"),
        [LumenFirst] = new(3356, "Lumen"),
        [LumenAtl] = new(3356, "Lumen"),
        [LumenEdge] = new(3356, "Lumen"),
        [AkamaiAtl] = new(20940, "Akamai"),
        [AkamaiOrd] = new(20940, "Akamai"),
        [LinodeOrd] = new(63949, "Linode"),
    };

    private static AsnLookup? AsnOf(string ip) => Asns.TryGetValue(ip, out var a) ? a : null;

    // The monitored hops' own series say clean, as the BNG and first Lumen hop stayed flat in the real event.
    private static bool? Anchors(string ip) => ip is Bng or LumenFirst ? false : null;

    private record H(string? Ip, double Min, int Lost = 0, int? Back = null, double? Max = null);

    private static TracerouteResult Round(params H[] hops) => new()
    {
        Target = new ProbeTarget(LinodeOrd, ProbeMode.Icmp),
        Vantage = ProbeVantage.Server,
        ModeUsed = ProbeMode.Icmp,
        Timestamp = DateTime.UtcNow,
        Reached = hops[^1].Ip == LinodeOrd,
        Hops = hops.Select((h, i) => new TraceHop
        {
            HopNumber = i + 1,
            Address = h.Ip,
            Probes = 5,
            Responses = h.Ip == null ? 0 : 5 - h.Lost,
            RttMinMs = h.Ip == null ? null : h.Min,
            RttAvgMs = h.Ip == null ? null : h.Min + 0.5,
            RttMaxMs = h.Ip == null ? null : h.Max ?? h.Min + 1,
            ReturnHops = h.Back
        }).ToList()
    };

    /// <summary>The ORD path after recovery: the per-hop baseline.</summary>
    private static TracerouteResult Recovery(int? destBack = null) => Round(
        new(Gw, 0.5), new(Bng, 2), new(LumenFirst, 9.5), new(LumenAtl, 14), new(LumenEdge, 15),
        new(AkamaiAtl, 16), new(AkamaiOrd, 31), new(LinodeOrd, 32, Back: destBack));

    private static TargetTraces Ord(IEnumerable<TracerouteResult> rounds, TracerouteResult? recovery = null) =>
        new("speedtest-ord", "Speedtest ORD", LinodeOrd, rounds.ToList(), recovery);

    private static EpisodeVerdict Analyze(TargetTraces t, params TargetTraces[] controls) =>
        PathTraceAnalyzer.Analyze(new[] { t }, controls, AsnOf, Anchors, Options);

    [Fact]
    public void ElevationStartingAtAkamai_ReadsAsTheHandoff()
    {
        // Clean through the Lumen edge, then every hop to the destination carries +40 ms and loss.
        var during = Enumerable.Range(0, 5).Select(_ => Round(
            new(Gw, 0.5), new(Bng, 2), new(LumenFirst, 9.5), new(LumenAtl, 14), new(LumenEdge, 15),
            new(AkamaiAtl, 56, Lost: 1), new(AkamaiOrd, 71), new(LinodeOrd, 72, Lost: 1)));

        var v = Analyze(Ord(during, Recovery()));

        v.Scope.Should().Be(CongestionScope.Segment);
        v.Disposition.Should().Be(CongestionDisposition.Confirmed);
        v.Label.Should().Be("Between Lumen (AS3356) and Akamai (AS20940) (handoff)");
        v.BottleneckHopNumber.Should().Be(6);
        var hops = v.Targets[0].Hops;
        hops.Single(h => h.Has(HopTag.LastClean)).Address.Should().Be(LumenEdge);
        hops.Single(h => h.Has(HopTag.Onset)).Address.Should().Be(AkamaiAtl);
        hops.Where(h => h.Has(HopTag.Anchored)).Select(h => h.Address).Should().BeEquivalentTo(new[] { Bng, LumenFirst });
    }

    [Fact]
    public void ElevationInsideOneNetwork_ReadsAsThatAsn()
    {
        var during = Enumerable.Range(0, 3).Select(_ => Round(
            new(Gw, 0.5), new(Bng, 2), new(LumenFirst, 9.5), new(LumenAtl, 40), new(LumenEdge, 41),
            new(AkamaiAtl, 42), new(AkamaiOrd, 57), new(LinodeOrd, 58)));

        var v = Analyze(Ord(during, Recovery()));

        v.Scope.Should().Be(CongestionScope.Asn);
        v.Label.Should().Be("Inside Lumen (AS3356)");
    }

    [Fact]
    public void OnsetHopTheCleanControlAlsoCrosses_IsPinnedToThatHop()
    {
        var during = Enumerable.Range(0, 3).Select(_ => Round(
            new(Gw, 0.5), new(Bng, 2), new(LumenFirst, 9.5), new(LumenAtl, 40), new(LumenEdge, 41),
            new(AkamaiAtl, 42), new(AkamaiOrd, 57), new(LinodeOrd, 58)));
        // The DEN control goes through Lumen Denver too and stays clean there.
        var control = new TargetTraces("speedtest-den", "Speedtest DEN", "203.0.113.90",
            new[] { Round(new(Gw, 0.5), new(Bng, 2), new(LumenFirst, 9.5), new(LumenAtl, 14), new("203.0.113.90", 26)) },
            null);

        var v = Analyze(Ord(during, Recovery()), control);

        v.Scope.Should().Be(CongestionScope.Hop);
        v.Label.Should().Be("Starts at Lumen 198.51.100.4 (AS3356)");
        v.Summary.Should().Contain("Clean control: Speedtest DEN");
    }

    [Fact]
    public void LossThatLaterHopsDoNotCarry_IsIcmpLimited_AndTheVerdictIsUnverifiable()
    {
        // Lumen Denver drops 1-3 of 5 a round, nothing past it does, the destination is degraded on
        // its own series: the far side or the return path, never Lumen Denver.
        var during = Enumerable.Range(0, 5).Select(i => Round(
            new(Gw, 0.5), new(Bng, 2), new(LumenFirst, 9.5), new(LumenAtl, 14, Lost: 1 + i % 3), new(LumenEdge, 15),
            new(AkamaiAtl, 16), new(AkamaiOrd, 31), new(LinodeOrd, 33)));

        var v = Analyze(Ord(during, Recovery()));

        v.Scope.Should().Be(CongestionScope.Corridor);
        v.Disposition.Should().Be(CongestionDisposition.Unverifiable);
        v.Label.Should().Be("Past the last clean hop (Akamai 203.0.113.7 (AS20940)): the far side or the return path");
        v.Targets[0].Hops.Single(h => h.Address == LumenAtl).Has(HopTag.IcmpLimited).Should().BeTrue();
    }

    [Fact]
    public void UnansweredHop_NeitherBreaksNorExtendsTheRun()
    {
        var during = Enumerable.Range(0, 3).Select(_ => Round(
            new(Gw, 0.5), new(Bng, 2), new(LumenFirst, 9.5), new(LumenAtl, 14), new(LumenEdge, 15),
            new(AkamaiAtl, 56), new(null, 0), new(LinodeOrd, 72)));

        var v = Analyze(Ord(during, Recovery()));

        v.BottleneckHopIp.Should().Be(AkamaiAtl);
        v.Targets[0].Hops.Single(h => h.Ttl == 7).Elevated.Should().BeNull();
    }

    [Fact]
    public void ConstantLossPerRound_IsARateLimit_NotForwardingLoss()
    {
        // Lumen Denver loses exactly 1 of 5 every round; the run really starts at Akamai.
        var during = Enumerable.Range(0, 4).Select(_ => Round(
            new(Gw, 0.5), new(Bng, 2), new(LumenFirst, 9.5), new(LumenAtl, 14, Lost: 1), new(LumenEdge, 15, Lost: 1),
            new(AkamaiAtl, 56), new(AkamaiOrd, 71), new(LinodeOrd, 72)));

        var v = Analyze(Ord(during, Recovery()));

        v.BottleneckHopIp.Should().Be(AkamaiAtl);
        v.Targets[0].Hops.Single(h => h.Address == LumenAtl).Has(HopTag.RateLimited).Should().BeTrue();
    }

    [Fact]
    public void DestinationReturnHopsChange_IsAReturnPathChange()
    {
        var during = Enumerable.Range(0, 3).Select(_ => Round(
            new(Gw, 0.5), new(Bng, 2), new(LumenFirst, 9.5), new(LumenAtl, 14), new(LumenEdge, 15),
            new(AkamaiAtl, 16), new(AkamaiOrd, 31), new(LinodeOrd, 45, Back: 11)));

        var v = Analyze(Ord(during, Recovery(destBack: 8)));

        v.ReturnFinding.Should().Be(ReturnPathFinding.ReturnHopCountChanged);
        v.Scope.Should().Be(CongestionScope.Corridor);
    }

    [Fact]
    public void ForwardPathOnAnotherNetwork_IsAPathChange()
    {
        // During: Cox instead of Lumen at hop 3. After recovery: back on Lumen.
        Asns["198.18.0.3"] = new AsnLookup(22773, "Cox");
        var during = Enumerable.Range(0, 3).Select(_ => Round(
            new(Gw, 0.5), new(Bng, 2), new("198.18.0.3", 13), new(AkamaiAtl, 30), new(AkamaiOrd, 41), new(LinodeOrd, 42)));

        var v = Analyze(Ord(during, Recovery()));

        v.Targets[0].PathChanged.Should().BeTrue();
        v.Label.Should().Be("Path moved from Lumen (AS3356) to Cox (AS22773) at hop 3");
        v.Scope.Should().Be(CongestionScope.Segment);
    }

    [Fact]
    public void NetworkThatStopsAnswering_IsNotAPathChange()
    {
        // Recovery crosses Lumen; during the episode every Lumen hop is silent. A network missing from
        // one side is ICMP going quiet, not the path moving - lined up by position, Akamai would read
        // as having replaced Lumen.
        var during = Enumerable.Range(0, 3).Select(_ => Round(
            new(Gw, 0.5), new(Bng, 2), new(null, 0), new(null, 0), new(null, 0),
            new(AkamaiAtl, 16), new(AkamaiOrd, 31), new(LinodeOrd, 45)));

        var v = Analyze(Ord(during, Recovery()));

        v.Targets[0].PathChanged.Should().BeFalse();

        // The other way round: a recovery trace whose Lumen hops went quiet is no move either.
        var quietRecovery = Round(new(Gw, 0.5), new(Bng, 2), new(null, 0), new(null, 0), new(null, 0),
            new(AkamaiAtl, 16), new(AkamaiOrd, 31), new(LinodeOrd, 32));
        var lumenDuring = Enumerable.Range(0, 3).Select(_ => Round(
            new(Gw, 0.5), new(Bng, 2), new(LumenFirst, 9.5), new(LumenAtl, 14), new(LumenEdge, 15),
            new(AkamaiAtl, 16), new(AkamaiOrd, 31), new(LinodeOrd, 45)));
        Analyze(Ord(lumenDuring, quietRecovery)).Targets[0].PathChanged.Should().BeFalse();
    }

    [Fact]
    public void HostnameTarget_EndsAtTheAddressTheTraceResolved_NotAtTheLastHopThatAnswered()
    {
        // A round cut short by its deadline ends on a transit hop; that hop is not the destination.
        var cut = Round(new(Gw, 0.5), new(Bng, 2), new(LumenFirst, 9.5), new(LumenAtl, 14)) with { ResolvedAddress = LinodeOrd };
        var full = Round(new(Gw, 0.5), new(Bng, 2), new(LumenFirst, 9.5), new(LumenAtl, 14), new(LumenEdge, 15),
            new(AkamaiAtl, 56), new(AkamaiOrd, 71), new(LinodeOrd, 72)) with { ResolvedAddress = LinodeOrd };
        var t = new TargetTraces("speedtest-ord", "Speedtest ORD", "speedtest.ord.example", new[] { cut, full, full }, Recovery());

        var v = Analyze(t);

        v.Targets[0].Hops.Single(h => h.Has(HopTag.Destination)).Address.Should().Be(LinodeOrd);
        v.BottleneckHopIp.Should().Be(AkamaiAtl);
    }

    [Fact]
    public void ConcurrentTargets_SharingNoForwardHop_PointAtTheReturnPath()
    {
        // The motivating result: AWS us-east via Cox, ORD via Lumen, nothing in common past the
        // access ISP, every forward hop clean.
        Asns["198.18.1.1"] = new AsnLookup(22773, "Cox");
        Asns["198.18.1.2"] = new AsnLookup(16509, "Amazon");
        Asns["198.18.1.9"] = new AsnLookup(16509, "Amazon");
        var ord = Ord(Enumerable.Range(0, 3).Select(_ => Round(
            new(Gw, 0.5), new(Bng, 2), new(LumenFirst, 9.5), new(LumenAtl, 14), new(LumenEdge, 15),
            new(AkamaiAtl, 16), new(AkamaiOrd, 31), new(LinodeOrd, 45))), Recovery());
        var awsRound = new TracerouteResult
        {
            Target = new ProbeTarget("198.18.1.9", ProbeMode.Icmp),
            Vantage = ProbeVantage.Server,
            ModeUsed = ProbeMode.Icmp,
            Timestamp = DateTime.UtcNow,
            Reached = true,
            Hops = new[]
            {
                new TraceHop { HopNumber = 1, Address = Gw, Probes = 5, Responses = 5, RttMinMs = 0.5, RttAvgMs = 1, RttMaxMs = 1.5 },
                new TraceHop { HopNumber = 2, Address = Bng, Probes = 5, Responses = 5, RttMinMs = 2, RttAvgMs = 2.5, RttMaxMs = 3 },
                new TraceHop { HopNumber = 3, Address = "198.18.1.1", Probes = 5, Responses = 5, RttMinMs = 13, RttAvgMs = 13.5, RttMaxMs = 14 },
                new TraceHop { HopNumber = 4, Address = "198.18.1.2", Probes = 5, Responses = 5, RttMinMs = 14, RttAvgMs = 14.5, RttMaxMs = 15 },
                new TraceHop { HopNumber = 5, Address = "198.18.1.9", Probes = 5, Responses = 4, RttMinMs = 55, RttAvgMs = 56, RttMaxMs = 60 },
            }
        };
        var aws = new TargetTraces("aws-us-east-1", "AWS us-east-1", "198.18.1.9", new[] { awsRound, awsRound }, awsRound with { });
        var den = new TargetTraces("speedtest-den", "Speedtest DEN", "203.0.113.90",
            new[] { Round(new(Gw, 0.5), new(Bng, 2), new("203.0.113.90", 26)) }, null);

        var v = PathTraceAnalyzer.Analyze(new[] { aws, ord }, new[] { den }, AsnOf, Anchors, Options);

        v.Scope.Should().Be(CongestionScope.Corridor);
        v.Disposition.Should().Be(CongestionDisposition.Unverifiable);
        v.ReturnFinding.Should().Be(ReturnPathFinding.NoSharedForwardHop);
        v.Label.Should().Be("Return path or far side: the degraded destinations share no forward hop");
        v.Summary.Should().Contain("Degraded: AWS us-east-1, Speedtest ORD. Clean: Speedtest DEN.");
    }

    [Fact]
    public void ConcurrentTargets_SharingAnElevatedHop_ConfirmThatHop()
    {
        var ord = Ord(Enumerable.Range(0, 3).Select(_ => Round(
            new(Gw, 0.5), new(Bng, 2), new(LumenFirst, 9.5), new(LumenAtl, 40), new(LumenEdge, 41),
            new(AkamaiAtl, 42), new(AkamaiOrd, 57), new(LinodeOrd, 58))), Recovery());
        Asns["198.18.2.9"] = new AsnLookup(16509, "Amazon");
        var awsRounds = Enumerable.Range(0, 3).Select(_ => new TracerouteResult
        {
            Target = new ProbeTarget("198.18.2.9", ProbeMode.Icmp),
            Vantage = ProbeVantage.Server,
            ModeUsed = ProbeMode.Icmp,
            Timestamp = DateTime.UtcNow,
            Reached = true,
            Hops = new[]
            {
                new TraceHop { HopNumber = 1, Address = Gw, Probes = 5, Responses = 5, RttMinMs = 0.5 },
                new TraceHop { HopNumber = 2, Address = Bng, Probes = 5, Responses = 5, RttMinMs = 2 },
                new TraceHop { HopNumber = 3, Address = LumenFirst, Probes = 5, Responses = 5, RttMinMs = 9.5 },
                new TraceHop { HopNumber = 4, Address = LumenAtl, Probes = 5, Responses = 4, RttMinMs = 40 },
                new TraceHop { HopNumber = 5, Address = "198.18.2.9", Probes = 5, Responses = 4, RttMinMs = 52 },
            }
        }).ToList();
        var awsRecovery = awsRounds[0] with
        {
            Hops = awsRounds[0].Hops.Select(h => h with { Responses = 5, RttMinMs = h.HopNumber switch { 4 => 14, 5 => 25, _ => h.RttMinMs } }).ToList()
        };
        var aws = new TargetTraces("aws", "AWS us-east-2", "198.18.2.9", awsRounds, awsRecovery);

        var v = PathTraceAnalyzer.Analyze(new[] { ord, aws }, Array.Empty<TargetTraces>(), AsnOf, Anchors, Options);

        v.Scope.Should().Be(CongestionScope.Hop);
        v.Disposition.Should().Be(CongestionDisposition.Confirmed);
        v.BottleneckHopIp.Should().Be(LumenAtl);
    }

    [Fact]
    public void ElevatedRun_SkipsUnknowns_AndStopsAtTheFirstCleanHop()
    {
        bool?[] path = { true, false, true, null, true };
        ElevatedRun.Onset(path.Length, i => path[i]).Should().Be(2);
        bool?[] cleanEnd = { true, true, false };
        ElevatedRun.Onset(cleanEnd.Length, i => cleanEnd[i]).Should().BeNull();
    }
}
