using FluentAssertions;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Monitoring.Probes;
using NetworkOptimizer.Web.Services.Monitoring;
using NetworkOptimizer.Web.Services.Monitoring.IspHealth;
using NetworkOptimizer.Web.Services.Monitoring.PathLocalization;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring.PathLocalization;

/// <summary>
/// A target degraded on its own ping series while every trace ran clean to it. The traces missed the
/// problem, so they place nothing and mark nothing, and the verdict says what the series saw.
/// </summary>
public class PathTraceAnalyzerMissedTests
{
    private static readonly IspHealthOptions Options = new();

    private const string Isp = "198.51.100.5";
    private const string Edge = "203.0.113.6";
    private const string Dest = "192.0.2.8";
    private const string OtherDest = "192.0.2.9";

    private static readonly Dictionary<string, AsnLookup> Asns = new()
    {
        [Isp] = new(64500, "Example ISP"),
        [Edge] = new(64501, "Example Transit"),
        [Dest] = new(64502, "Example DNS"),
        [OtherDest] = new(64503, "Example Cloud"),
    };

    private static TracerouteResult Round(string dest, double destMin, int destLost = 0) => new()
    {
        Target = new ProbeTarget(dest, ProbeMode.Icmp),
        Vantage = ProbeVantage.Server,
        ModeUsed = ProbeMode.Icmp,
        Timestamp = DateTime.UtcNow,
        Reached = true,
        Hops = new (string Ip, double Min, int Lost)[] { (Isp, 1.9, 0), (Edge, 2.0, 0), (dest, destMin, destLost) }
            .Select((h, i) => new TraceHop
            {
                HopNumber = i + 1,
                Address = h.Ip,
                Probes = 5,
                Responses = 5 - h.Lost,
                RttMinMs = h.Min,
                RttAvgMs = h.Min + 0.1,
                RttMaxMs = h.Min + 0.2
            }).ToList()
    };

    private static TargetTraces Target(string id, string name, string dest, IEnumerable<TracerouteResult> rounds, TargetSeries? series) =>
        new(id, name, dest, rounds.ToList(), null) { Series = series };

    private static EpisodeVerdict Analyze(params TargetTraces[] targets) =>
        PathTraceAnalyzer.Analyze(targets, Array.Empty<TargetTraces>(), ip => Asns.GetValueOrDefault(ip), _ => null, Options);

    private static IEnumerable<TracerouteResult> Clean(string dest, int n = 7) =>
        Enumerable.Range(0, n).Select(i => Round(dest, 2.0 + i % 2 * 0.05));

    [Fact]
    public void Loss_the_traces_never_saw_places_nothing_and_says_what_the_pings_saw()
    {
        var v = Analyze(Target("dns", "Example DNS", Dest, Clean(Dest), new TargetSeries(6, 4.6, 2.1)));

        var t = v.Targets[0];
        t.Missed.Should().BeTrue();
        t.Scope.Should().Be(CongestionScope.Unlocalized);
        t.Hops.Should().NotContain(h => h.Has(HopTag.InRun) || h.Has(HopTag.Onset) || h.Has(HopTag.LastClean));
        t.Hops.Single(h => h.Has(HopTag.Destination)).Elevated.Should().BeFalse();
        t.Hops.Single(h => h.Has(HopTag.Destination)).Has(HopTag.SeriesOnly).Should().BeTrue();
        v.Summary.Should().Be("Example DNS's pings lost up to 6%, but its 7 traces ran clean, so they can't place the problem. "
            + "Its RTT fell from 4.6 to 2.1 ms over the same time.");
    }

    [Fact]
    public void Loss_the_traces_did_see_is_still_placed()
    {
        var rounds = Enumerable.Range(0, 7).Select(i => Round(Dest, 2.0, destLost: i < 3 ? 1 : 0));

        var t = Analyze(Target("dns", "Example DNS", Dest, rounds, new TargetSeries(6, 4.6, 2.1))).Targets[0];

        t.Missed.Should().BeFalse();
        t.Hops.Single(h => h.Has(HopTag.Destination)).Has(HopTag.InRun).Should().BeTrue();
    }

    [Fact]
    public void The_destination_row_leads_with_its_own_series()
    {
        var t = Analyze(Target("dns", "Example DNS", Dest, Clean(Dest), new TargetSeries(6, 4.6, 2.1))).Targets[0];
        var dest = t.Hops.Single(h => h.Has(HopTag.Destination)) with { SeriesBaselineRttMs = 4.6 };
        var detail = Detail(new Dictionary<string, double> { [Dest] = 1.6 }, new Dictionary<string, double> { [Dest] = 2.1 });

        var loss = PathEpisodePresentation.HopLoss(dest, detail)!;
        PathEpisodePresentation.HopLossText(loss).Should().Be("1.6% (trace 0%)");

        var rtt = PathEpisodePresentation.HopRtt(dest, detail)!;
        rtt.Text.Should().Be("4.6 → 2.1 ms");
        rtt.FromSeries.Should().BeTrue();
        rtt.TraceMs.Should().BeNull();
    }

    [Fact]
    public void A_series_rtt_far_from_the_trace_names_the_trace_figure()
    {
        var hop = new HopReading { Ttl = 3, Address = Dest, Sent = 35, Responses = 35, EpisodeRttMs = 85, SeriesBaselineRttMs = 71, Tags = HopTag.Destination };
        var detail = Detail(new Dictionary<string, double>(), new Dictionary<string, double> { [Dest] = 98 });

        var rtt = PathEpisodePresentation.HopRtt(hop, detail)!;
        rtt.Text.Should().Be("71.0 → 98.0 ms");
        rtt.TraceMs.Should().Be(85);
    }

    [Fact]
    public void A_hop_with_no_series_reads_the_trace_as_before()
    {
        var hop = new HopReading { Ttl = 2, Address = Edge, Sent = 35, Responses = 35, EpisodeRttMs = 2.0, ReferenceRttMs = 2.0 };

        var rtt = PathEpisodePresentation.HopRtt(hop, Detail(new Dictionary<string, double>(), new Dictionary<string, double>()))!;
        rtt.Text.Should().Be("2.0 → 2.0 ms");
        rtt.FromSeries.Should().BeFalse();
    }

    private static PathEpisodeDetail Detail(Dictionary<string, double> loss, Dictionary<string, double> rtt) =>
        new(new PathEpisodeView { Id = 1, OpenedAt = DateTime.UtcNow },Array.Empty<NetworkOptimizer.Web.Services.Monitoring.PathLocalization.PathEpisodeHopTable>(),
            MonitoredLoss: loss, MonitoredRtt: rtt);

    [Fact]
    public void A_series_rise_with_no_baseline_outside_the_episode_is_not_called_missed()
    {
        // A steady rise slows no round against the others: only a recovery or control trace could rule it out.
        var t = Analyze(Target("dns", "Example DNS", Dest, Clean(Dest), new TargetSeries(0, 2.0, 30))).Targets[0];

        t.Missed.Should().BeFalse();
    }

    [Fact]
    public void A_missed_target_stays_out_of_the_set_and_gets_its_own_sentence()
    {
        // The other target's destination is slow in most rounds, so its traces carry the problem.
        var slow = new double[] { 70, 95, 96, 71, 94, 97, 70 }.Select(d => Round(OtherDest, d));
        var v = Analyze(
            Target("cloud", "Example Cloud", OtherDest, slow, new TargetSeries(10, 70, 97)),
            Target("dns", "Example DNS", Dest, Clean(Dest), new TargetSeries(6, 4.6, 2.1)));

        // Example Cloud's verdict alone, not the set's "their routes share nothing" corridor.
        v.Targets.Should().HaveCount(2);
        v.ReturnFinding.Should().NotBe(NetworkOptimizer.Storage.Models.ReturnPathFinding.NoSharedForwardHop);
        v.Label.Should().NotContain("Example DNS");
        v.Summary.Should().EndWith("Example DNS's pings lost up to 6%, but its 7 traces ran clean, so they can't place the problem. "
            + "Its RTT fell from 4.6 to 2.1 ms over the same time.");
    }
}
