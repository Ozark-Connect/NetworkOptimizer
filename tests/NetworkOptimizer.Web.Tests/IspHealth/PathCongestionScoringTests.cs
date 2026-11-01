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
