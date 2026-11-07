using FluentAssertions;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services.Monitoring.IspHealth;
using NetworkOptimizer.Web.Services.Monitoring.PathLocalization;
using System.Text.Json;
using Xunit;

namespace NetworkOptimizer.Web.Tests.IspHealth;

/// <summary>Which Path Localization episodes count against the ISP, and over what span.</summary>
public class PathCongestionScoringTests
{
    private static readonly DateTime WindowStart = new(2026, 10, 30, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime WindowEnd = WindowStart.AddDays(1);

    // Target id -> ASN: 1 and 2 are one network (two regions), 3 another.
    private static readonly Dictionary<int, int?> AsnById = new() { [1] = 16509, [2] = 16509, [3] = 714, [4] = null };
    private static readonly HashSet<int> DestinationAsns = new() { 16509, 714, 13335, 15169 };

    private static PathEpisode Episode(int id, params int[] degradedTargetIds) => new()
    {
        Id = id,
        OpenedAt = WindowStart.AddHours(19),
        ClosedAt = WindowStart.AddHours(21),
        BottleneckLabel = "The common factor is most likely the return path into your ISP.",
        Targets = degradedTargetIds.Select(t => new PathEpisodeTarget
        {
            MonitoringTargetId = t, Role = PathEpisodeTargetRole.Degraded, PeakLossPct = 12, BaselineRttMs = 20, PeakRttMs = 33
        }).ToList()
    };

    private static List<PathCongestionEpisode> Score(params PathEpisode[] episodes) =>
        IspHealthService.ScorablePathCongestion(episodes, AsnById, DestinationAsns, WindowStart, WindowEnd, minAsns: 2);

    private static readonly Dictionary<int, string> TargetIdById = new() { [1] = "aws-us-east-1", [2] = "aws-us-east-2", [3] = "path-apple-as714" };

    private static PathEpisode StepEpisode(double peakLoss) => new()
    {
        Id = 9,
        OpenedAt = WindowStart.AddHours(19),
        ClosedAt = WindowStart.AddHours(21),
        BottleneckLabel = "The common factor is most likely the return path into your ISP.",
        Targets = new[] { 1, 3 }.Select(t => new PathEpisodeTarget
        {
            MonitoringTargetId = t, Role = PathEpisodeTargetRole.Degraded, PeakLossPct = peakLoss, BaselineRttMs = 20, PeakRttMs = 33
        }).ToList()
    };

    private static readonly List<PathShiftEvent> CorrelatedStep = new()
    {
        new PathShiftEvent
        {
            Time = WindowStart.AddHours(19).AddMinutes(10),
            TargetId = "aws-us-east-1",
            TargetIds = { "aws-us-east-1", "path-apple-as714" },
            BeforeMedianMs = 20,
            AfterMedianMs = 33,
            CorrelatedTargetCount = 2
        }
    };

    [Fact]
    public void AnEpisodeAPathShiftExplains_IsNotCongestion()
    {
        // Two networks, so it would score; both sit on one correlated step with no real loss.
        IspHealthService.ExplainedByShift(StepEpisode(peakLoss: 0), TargetIdById, CorrelatedStep, new IspHealthOptions())
            .Should().BeTrue();
    }

    [Fact]
    public void RealLossOnTheStep_StillScores()
    {
        IspHealthService.ExplainedByShift(StepEpisode(peakLoss: 12), TargetIdById, CorrelatedStep, new IspHealthOptions())
            .Should().BeFalse();
    }

    /// <summary>Two networks rising on RTT alone with no detected shift; the second target's peak jitter varies.</summary>
    private static PathEpisode JitterEpisode(double secondPeakJitter)
    {
        var e = StepEpisode(peakLoss: 0);
        foreach (var t in e.Targets)
        {
            t.BaselineJitterMs = 0.4;
            t.PeakJitterMs = t.MonitoringTargetId == 3 ? secondPeakJitter : 0.5;
        }
        return e;
    }

    [Fact]
    public void AllStepEpisode_WithNoDetectedShift_CostsNothing()
    {
        var e = JitterEpisode(secondPeakJitter: 0.6);
        IspHealthService.ExplainedByShift(e, TargetIdById, new List<PathShiftEvent>(), new IspHealthOptions())
            .Should().BeTrue("RTT rose on both with no loss and no wider jitter: a route change, which never scores");
    }

    [Fact]
    public void MixedEpisode_OneTargetQueued_StillScores()
    {
        var e = JitterEpisode(secondPeakJitter: 3.0);
        IspHealthService.ExplainedByShift(e, TargetIdById, new List<PathShiftEvent>(), new IspHealthOptions())
            .Should().BeFalse();
        Score(e).Should().ContainSingle();
    }

    [Fact]
    public void TwoNetworks_Count_WithTheirBreadthAndDepth()
    {
        var e = Score(Episode(1, 1, 3)).Single();

        e.DegradedAsns.Should().Be(2);
        e.DestinationAsns.Should().Be(4);
        e.PeakLossPct.Should().Be(12);
        e.RttRiseMs.Should().Be(13);
        (e.End - e.Start).Should().Be(TimeSpan.FromHours(2));
    }

    [Fact]
    public void OneNetwork_IsThatNetworksProblem()
    {
        // Two AWS regions are still one network; an unknown ASN adds nothing.
        Score(Episode(1, 1, 2, 4)).Should().BeEmpty();
    }

    [Fact]
    public void AccessLayerVerdict_AndUnanalyzed_DoNotCount()
    {
        var access = Episode(1, 1, 3);
        access.BottleneckHopIp = "198.51.100.1";
        access.GateSnapshot = JsonSerializer.Serialize(new PathGateSnapshot { AccessHops = new List<string> { "198.51.100.1" } });

        var unanalyzed = Episode(2, 1, 3);
        unanalyzed.BottleneckLabel = null;

        Score(access, unanalyzed).Should().BeEmpty();
    }

    [Fact]
    public void Span_IsClampedToTheWindow_AndAnOpenEpisodeRunsToItsEnd()
    {
        var straddles = Episode(1, 1, 3);
        straddles.OpenedAt = WindowStart.AddHours(-1);
        var open = Episode(2, 1, 3);
        open.OpenedAt = WindowEnd.AddHours(-1);
        open.ClosedAt = null;

        var scored = Score(straddles, open);

        scored.Single(e => e.EpisodeId == 1).Start.Should().Be(WindowStart);
        scored.Single(e => e.EpisodeId == 2).End.Should().Be(WindowEnd);
    }
}
