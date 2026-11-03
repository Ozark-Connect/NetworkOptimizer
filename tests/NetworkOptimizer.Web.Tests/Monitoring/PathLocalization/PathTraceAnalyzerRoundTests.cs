using FluentAssertions;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Monitoring.Probes;
using NetworkOptimizer.Web.Services.Monitoring;
using NetworkOptimizer.Web.Services.Monitoring.IspHealth;
using NetworkOptimizer.Web.Services.Monitoring.PathLocalization;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring.PathLocalization;

/// <summary>
/// A rise that comes and goes across an episode's rounds. Each round keeps its own minimum, the episode
/// reads their median, and a hop is judged by whether it slowed in the same rounds as the destination.
/// The destination series is a real evening event's: slow in 6 of 10 rounds, back to normal in between.
/// </summary>
public class PathTraceAnalyzerRoundTests
{
    private static readonly IspHealthOptions Options = new();

    private const string Gw = "192.168.1.1";
    private const string Edge = "198.51.100.5";
    private const string Far = "203.0.113.6";
    private const string Last = "203.0.113.7";
    private const string Dest = "203.0.113.80";

    private static readonly double[] DestRounds = { 52, 58, 66, 32, 32, 43, 32, 70, 61, 32 };
    private static readonly bool[] DestSlow = DestRounds.Select(v => v - 32 >= 5).ToArray();

    private static readonly Dictionary<string, AsnLookup> Asns = new()
    {
        [Edge] = new(3356, "Lumen"),
        [Far] = new(20940, "Akamai"),
        [Last] = new(20940, "Akamai"),
        [Dest] = new(63949, "Linode"),
    };

    /// <summary>One round over gateway, edge, far, last, destination; a null RTT is a hop that did not answer.</summary>
    private static TracerouteResult Round(double? far, double? last, double dest) => new()
    {
        Target = new ProbeTarget(Dest, ProbeMode.Icmp),
        Vantage = ProbeVantage.Server,
        ModeUsed = ProbeMode.Icmp,
        Timestamp = DateTime.UtcNow,
        Reached = true,
        Hops = new (string Ip, double? Min)[] { (Gw, 0.5), (Edge, 15), (Far, far), (Last, last), (Dest, dest) }
            .Select((h, i) => new TraceHop
            {
                HopNumber = i + 1,
                Address = h.Min == null ? null : h.Ip,
                Probes = 5,
                Responses = h.Min == null ? 0 : 5,
                RttMinMs = h.Min,
                RttAvgMs = h.Min + 0.3,
                RttMaxMs = h.Min + 0.6
            }).ToList()
    };

    private static TargetVerdict Analyze(IEnumerable<TracerouteResult> rounds, TracerouteResult? recovery = null) =>
        PathTraceAnalyzer.Analyze(new[] { new TargetTraces("ord", "Speedtest ORD", Dest, rounds.ToList(), recovery) },
            Array.Empty<TargetTraces>(), ip => Asns.GetValueOrDefault(ip), _ => null, Options).Targets[0];

    private static HopReading Hop(TargetVerdict v, int ttl) => v.Hops.Single(h => h.Ttl == ttl);

    [Fact]
    public void An_intermittent_destination_rise_shows_in_the_episode_rtt()
    {
        var v = Analyze(DestRounds.Select(d => Round(16, 31, d)));

        var dest = Hop(v, 5);
        dest.EpisodeRttMs.Should().Be(47.5);
        dest.MinRttMs.Should().Be(32);
        dest.SlowRounds.Should().Be(6);
    }

    [Fact]
    public void Round_minimums_keep_round_order_and_gaps()
    {
        var rounds = DestRounds.Select((d, i) => Round(16, i == 2 ? null : 31 + i, d));

        Hop(Analyze(rounds), 4).RoundMinRttMs.Should().Equal(31, 32, null, 34, 35, 36, 37, 38, 39, 40);
    }

    [Fact]
    public void A_hop_that_slows_with_the_destination_is_where_it_starts()
    {
        var v = Analyze(DestRounds.Select(d => Round(d - 2, d - 1, d)));

        var far = Hop(v, 3);
        far.PairedRounds.Should().Be(6);
        far.Has(HopTag.InRun).Should().BeTrue();
        far.Has(HopTag.Onset).Should().BeTrue();
        Hop(v, 4).Has(HopTag.InRun).Should().BeTrue();
        Hop(v, 2).Has(HopTag.LastClean).Should().BeTrue();

        // Moving together does not say which way: the replies from there may come home another way.
        v.Disposition.Should().Be(CongestionDisposition.Unverifiable);
        v.Label.Should().Be("The problem first shows at Akamai `203.0.113.6` (`AS20940`), and every router after it shows it too. "
            + "Their replies may come home a different way than the routers before it, so it could be on the return path.");
    }

    [Fact]
    public void A_hop_that_stays_fast_while_the_destination_slows_is_the_last_clean_one()
    {
        var v = Analyze(DestRounds.Select(d => Round(16, 31, d)));

        var last = Hop(v, 4);
        last.PairedRounds.Should().Be(0);
        last.Has(HopTag.LastClean).Should().BeTrue();
        Hop(v, 5).Has(HopTag.Onset).Should().BeTrue();
    }

    [Fact]
    public void A_hop_that_slows_with_the_destination_but_not_the_next_hop_is_icmp_limited()
    {
        var v = Analyze(DestRounds.Select((d, i) => Round(DestSlow[i] ? 40 : 16, 31, d)));

        var far = Hop(v, 3);
        far.PairedRounds.Should().Be(6);
        far.Has(HopTag.IcmpLimited).Should().BeTrue();
        far.Has(HopTag.InRun).Should().BeFalse();
    }

    [Fact]
    public void One_slow_destination_round_does_not_pair()
    {
        var v = Analyze(Enumerable.Range(0, 10).Select(i => Round(16, i == 0 ? 50 : 31, i == 0 ? 52 : 32)));

        Hop(v, 4).Elevated.Should().BeFalse();
    }

    [Fact]
    public void A_steady_rise_still_reads_against_the_recovery_baseline()
    {
        var v = Analyze(Enumerable.Range(0, 10).Select(_ => Round(16, 51, 52)), Round(16, 31, 32));

        var last = Hop(v, 4);
        last.EpisodeRttMs.Should().Be(51);
        last.Has(HopTag.InRun).Should().BeTrue();
        // Normal is the recovery trace here: the episode's own rounds were all high.
        PathEpisodePresentation.DisplayBaseline(last).Should().Be(31);
    }

    [Fact]
    public void Normal_is_never_a_one_trace_minimum_above_most_rounds()
    {
        // A jittery link whose recovery trace caught the hop high: normal is its own reference round.
        double[] access = { 20, 35, 36, 20, 43, 87, 21, 22, 24, 30 };
        var v = Analyze(access.Select(a => Round(16, a, 52)), Round(16, 47, 32));

        PathEpisodePresentation.DisplayBaseline(Hop(v, 4)).Should().Be(20);
    }

    [Fact]
    public void Several_destinations_sharing_a_hop_placed_by_pairing_stay_unverifiable()
    {
        const string other = "203.0.113.81";
        TracerouteResult To(string dest, double d) => Round(d - 2, d - 1, d) with
        {
            Target = new ProbeTarget(dest, ProbeMode.Icmp),
            Hops = Round(d - 2, d - 1, d).Hops.Select(h => h.Address == Dest ? h with { Address = dest } : h).ToList()
        };
        var a = new TargetTraces("ord", "Speedtest ORD", Dest, DestRounds.Select(d => To(Dest, d)).ToList(), null);
        var b = new TargetTraces("ord2", "Speedtest ORD 2", other, DestRounds.Select(d => To(other, d)).ToList(), null);

        var v = PathTraceAnalyzer.Analyze(new[] { a, b }, Array.Empty<TargetTraces>(), ip => Asns.GetValueOrDefault(ip), _ => null, Options);

        v.Disposition.Should().Be(CongestionDisposition.Unverifiable);
        v.BottleneckHopNumber.Should().Be(3);
        v.Label.Should().EndWith("Their replies may come home a different way from there, so it could be on the return path.");
    }

    [Fact]
    public void A_swing_that_starts_at_the_access_hop_places_nothing()
    {
        // Starlink: a handover moves the first hop and everything after it in the same rounds, while
        // the noisy middle hops pair unevenly. No hop past the access link is the start.
        double[] access = { 20, 35, 36, 20, 43, 87, 21, 60, 22, 70 };
        var rounds = access.Select(a => new TracerouteResult
        {
            Target = new ProbeTarget(Dest, ProbeMode.Icmp),
            Vantage = ProbeVantage.Server,
            ModeUsed = ProbeMode.Icmp,
            Timestamp = DateTime.UtcNow,
            Reached = true,
            Hops = new[] { ("100.64.0.1", a), (Edge, a + 8), (Far, a + 10), (Last, a + 11), (Dest, a + 12) }
                .Select((h, i) => new TraceHop { HopNumber = i + 1, Address = h.Item1, Probes = 5, Responses = 5, RttMinMs = h.Item2, RttAvgMs = h.Item2 + 0.3, RttMaxMs = h.Item2 + 0.6 })
                .ToList()
        }).ToList();

        var v = PathTraceAnalyzer.Analyze(new[] { new TargetTraces("dns", "Quad9 DFW", Dest, rounds, null) },
            Array.Empty<TargetTraces>(), ip => Asns.GetValueOrDefault(ip), _ => null, Options,
            accessHops: new HashSet<string> { "100.64.0.1" }).Targets[0];

        v.Hops.Where(h => !h.Has(HopTag.Destination)).Should().OnlyContain(h => h.Elevated != true);
        v.OnsetTtl.Should().BeNull();
    }

    [Fact]
    public void The_reading_names_how_a_hop_did_against_the_destination()
    {
        var fast = Analyze(DestRounds.Select(d => Round(16, 31, d)));
        PathEpisodePresentation.RoundsReading(Hop(fast, 4), fast.Hops, "Speedtest ORD")
            .Should().Be("Fast in all 6 traces where Speedtest ORD was slow");
        PathEpisodePresentation.RoundsReading(Hop(fast, 5), fast.Hops, "Speedtest ORD")
            .Should().Be("Slower in 6 of 10 traces");

        var with = Analyze(DestRounds.Select(d => Round(16, d - 1, d)));
        PathEpisodePresentation.RoundsReading(Hop(with, 4), with.Hops, "Speedtest ORD")
            .Should().Be("Slow in 6 of the 6 traces where Speedtest ORD was");
    }
}
