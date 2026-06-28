using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkOptimizer.WiFi.Data;
using NetworkOptimizer.WiFi.Helpers;
using NetworkOptimizer.WiFi.Models;
using NetworkOptimizer.WiFi.Services;
using Xunit;

namespace NetworkOptimizer.WiFi.Tests;

public class ChannelRecommendationServiceTests
{
    private readonly ChannelRecommendationService _service;

    public ChannelRecommendationServiceTests()
    {
        var loader = new AntennaPatternLoader(NullLogger<AntennaPatternLoader>.Instance);
        var propagationService = new PropagationService(loader, NullLogger<PropagationService>.Instance);
        _service = new ChannelRecommendationService(
            propagationService,
            NullLogger<ChannelRecommendationService>.Instance);
    }

    private static AccessPointSnapshot CreateAp(
        string mac, string name, RadioBand band, int channel,
        int width = 80, int txPower = 20, bool hasDfs = false,
        bool isMeshChild = false, string? meshParentMac = null,
        RadioBand? meshUplinkBand = null, int? meshUplinkChannel = null) => new()
        {
            Mac = mac,
            Name = name,
            IsOnline = true,
            IsMeshChild = isMeshChild,
            MeshParentMac = meshParentMac,
            MeshUplinkBand = meshUplinkBand,
            MeshUplinkChannel = meshUplinkChannel,
            Radios = new()
        {
            new RadioSnapshot
            {
                Band = band,
                Channel = channel,
                ChannelWidth = width,
                TxPower = txPower,
                AntennaGain = 3,
                HasDfs = hasDfs
            }
        }
        };

    // --- Graph Building ---

    [Fact]
    public void BuildInterferenceGraph_TwoAps_CreatesCorrectGraph()
    {
        var aps = new List<AccessPointSnapshot>
        {
            CreateAp("aa:bb:cc:dd:ee:01", "AP-1", RadioBand.Band5GHz, 36),
            CreateAp("aa:bb:cc:dd:ee:02", "AP-2", RadioBand.Band5GHz, 36)
        };

        var graph = _service.BuildInterferenceGraph(aps, RadioBand.Band5GHz, null, null, null);

        graph.Nodes.Should().HaveCount(2);
        graph.InternalWeights[0, 1].Should().BeGreaterThan(0);
        graph.InternalWeights[0, 1].Should().Be(graph.InternalWeights[1, 0]);
    }

    [Fact]
    public void BuildInterferenceGraph_OfflineAp_Excluded()
    {
        var aps = new List<AccessPointSnapshot>
        {
            CreateAp("aa:bb:cc:dd:ee:01", "AP-1", RadioBand.Band5GHz, 36),
            new()
            {
                Mac = "aa:bb:cc:dd:ee:02", Name = "AP-Offline", IsOnline = false,
                Radios = new() { new RadioSnapshot { Band = RadioBand.Band5GHz, Channel = 36, ChannelWidth = 80 } }
            }
        };

        var graph = _service.BuildInterferenceGraph(aps, RadioBand.Band5GHz, null, null, null);

        graph.Nodes.Should().HaveCount(1);
    }

    [Fact]
    public void BuildInterferenceGraph_DifferentBand_NotIncluded()
    {
        var aps = new List<AccessPointSnapshot>
        {
            CreateAp("aa:bb:cc:dd:ee:01", "AP-1", RadioBand.Band5GHz, 36),
            CreateAp("aa:bb:cc:dd:ee:02", "AP-2", RadioBand.Band2_4GHz, 6)
        };

        var graph = _service.BuildInterferenceGraph(aps, RadioBand.Band5GHz, null, null, null);

        graph.Nodes.Should().HaveCount(1);
    }

    [Fact]
    public void BuildInterferenceGraph_UnplacedAps_UseDefaultWeight()
    {
        var aps = new List<AccessPointSnapshot>
        {
            CreateAp("aa:bb:cc:dd:ee:01", "AP-1", RadioBand.Band5GHz, 36),
            CreateAp("aa:bb:cc:dd:ee:02", "AP-2", RadioBand.Band5GHz, 36)
        };

        var graph = _service.BuildInterferenceGraph(aps, RadioBand.Band5GHz, null, null, null);

        // -65 dBm, CCA-anchored: (-65 + 82) / 32 = 0.531
        graph.InternalWeights[0, 1].Should().BeApproximately(0.531, 0.01);
    }

    [Fact]
    public void BuildInterferenceGraph_MeshPair_CreatesConstraint()
    {
        var aps = new List<AccessPointSnapshot>
        {
            CreateAp("aa:bb:cc:dd:ee:01", "AP-Parent", RadioBand.Band5GHz, 36),
            CreateAp("aa:bb:cc:dd:ee:02", "AP-Child", RadioBand.Band5GHz, 36,
                isMeshChild: true, meshParentMac: "aa:bb:cc:dd:ee:01",
                meshUplinkBand: RadioBand.Band5GHz, meshUplinkChannel: 36)
        };

        var graph = _service.BuildInterferenceGraph(aps, RadioBand.Band5GHz, null, null, null);

        graph.MeshConstraints.Should().HaveCount(1);
        graph.MeshConstraints[0].ParentIndex.Should().Be(0);
        graph.MeshConstraints[0].ChildIndex.Should().Be(1);
    }

    [Fact]
    public void BuildInterferenceGraph_ExternalLoad_FromScanResults()
    {
        var aps = new List<AccessPointSnapshot>
        {
            CreateAp("aa:bb:cc:dd:ee:01", "AP-1", RadioBand.Band5GHz, 36)
        };

        var scans = new List<ChannelScanResult>
        {
            new()
            {
                ApMac = "aa:bb:cc:dd:ee:01",
                Band = RadioBand.Band5GHz,
                Neighbors = new()
                {
                    new NeighborNetwork { Bssid = "ff:ff:ff:00:00:01", Channel = 36, Signal = -60, IsOwnNetwork = false },
                    new NeighborNetwork { Bssid = "ff:ff:ff:00:00:02", Channel = 36, Signal = -70, IsOwnNetwork = false }
                }
            }
        };

        var graph = _service.BuildInterferenceGraph(aps, RadioBand.Band5GHz, null, scans, null);

        graph.ExternalLoad[0].Should().ContainKey(36);
        graph.ExternalLoad[0][36].Should().BeGreaterThan(0);
    }

    [Fact]
    public void BuildInterferenceGraph_OwnNetworkNeighbors_Excluded()
    {
        var aps = new List<AccessPointSnapshot>
        {
            CreateAp("aa:bb:cc:dd:ee:01", "AP-1", RadioBand.Band5GHz, 36)
        };

        var scans = new List<ChannelScanResult>
        {
            new()
            {
                ApMac = "aa:bb:cc:dd:ee:01",
                Band = RadioBand.Band5GHz,
                Neighbors = new()
                {
                    new NeighborNetwork { Bssid = "ff:ff:ff:00:00:01", Channel = 36, Signal = -60, IsOwnNetwork = true }
                }
            }
        };

        var graph = _service.BuildInterferenceGraph(aps, RadioBand.Band5GHz, null, scans, null);

        graph.ExternalLoad[0].Should().BeEmpty();
    }

    // --- Scoring ---

    [Fact]
    public void ScoreAssignment_CoChannelAps_HigherScore()
    {
        var aps = new List<AccessPointSnapshot>
        {
            CreateAp("aa:bb:cc:dd:ee:01", "AP-1", RadioBand.Band5GHz, 36),
            CreateAp("aa:bb:cc:dd:ee:02", "AP-2", RadioBand.Band5GHz, 36)
        };
        var graph = _service.BuildInterferenceGraph(aps, RadioBand.Band5GHz, null, null, null);

        var coChannelScore = _service.ScoreAssignment(
            graph, new[] { (36, 80), (36, 80) }, RadioBand.Band5GHz);
        var separatedScore = _service.ScoreAssignment(
            graph, new[] { (36, 80), (149, 80) }, RadioBand.Band5GHz);

        coChannelScore.Should().BeGreaterThan(separatedScore);
    }

    [Fact]
    public void ScoreAssignment_UtilizationDropsButNotToZero_WhenCoChannelResolved()
    {
        // An AP's channel utilization is part co-channel airtime (which vacates when a neighbor
        // moves off-channel) and part its own serving traffic (which persists). So when the move
        // resolves the co-channel pair, the utilization stress must drop - but not to zero.
        // Comparing a busy AP against an idle one cancels the co-channel term and the unobserved
        // penalty, isolating just the utilization contribution.
        double UtilContribution((int Channel, int Width)[] assignment)
        {
            var busy = CreateAp("aa:bb:cc:dd:ee:01", "Busy", RadioBand.Band5GHz, 36);
            busy.Radios[0].ChannelUtilization = 40;
            var idle = CreateAp("aa:bb:cc:dd:ee:01", "Idle", RadioBand.Band5GHz, 36);
            idle.Radios[0].ChannelUtilization = 0;
            var neighbor = CreateAp("aa:bb:cc:dd:ee:02", "Neighbor", RadioBand.Band5GHz, 36);

            var busyGraph = _service.BuildInterferenceGraph(
                new List<AccessPointSnapshot> { busy, neighbor }, RadioBand.Band5GHz, null, null, null);
            var idleGraph = _service.BuildInterferenceGraph(
                new List<AccessPointSnapshot> { idle, neighbor }, RadioBand.Band5GHz, null, null, null);

            return _service.ScoreAssignment(busyGraph, assignment, RadioBand.Band5GHz)
                 - _service.ScoreAssignment(idleGraph, assignment, RadioBand.Band5GHz);
        }

        // Neighbor stays co-channel (utilization fully retained) vs neighbor moved off-block.
        var full = UtilContribution(new[] { (36, 80), (36, 80) });
        var resolved = UtilContribution(new[] { (36, 80), (149, 80) });

        resolved.Should().BeGreaterThan(0, "the AP's own-traffic utilization persists after the neighbor moves");
        resolved.Should().BeLessThan(full, "the co-channel-attributable share of utilization still drops");
    }

    [Fact]
    public void ScoreAssignment_MeshPair_ExcludedFromScore()
    {
        var aps = new List<AccessPointSnapshot>
        {
            CreateAp("aa:bb:cc:dd:ee:01", "AP-Parent", RadioBand.Band5GHz, 36),
            CreateAp("aa:bb:cc:dd:ee:02", "AP-Child", RadioBand.Band5GHz, 36,
                isMeshChild: true, meshParentMac: "aa:bb:cc:dd:ee:01",
                meshUplinkBand: RadioBand.Band5GHz, meshUplinkChannel: 36)
        };
        var graph = _service.BuildInterferenceGraph(aps, RadioBand.Band5GHz, null, null, null);

        // Mesh pair on same channel should have score 0 (interference excluded)
        var score = _service.ScoreAssignment(
            graph, new[] { (36, 80), (36, 80) }, RadioBand.Band5GHz);

        score.Should().Be(0);
    }

    // --- Unobserved-channel uncertainty (confidence-weighted) ---

    // Builds a 2-AP graph where the sibling sits on a non-overlapping channel, so the
    // subject AP (index 0) is the only contributor to the score - isolating its external
    // + unobserved-uncertainty terms.
    private InterferenceGraph BuildSubjectGraph(int siblingChannel = 100)
    {
        var aps = new List<AccessPointSnapshot>
        {
            CreateAp("aa:bb:cc:dd:ee:01", "Subject", RadioBand.Band5GHz, 149),
            CreateAp("aa:bb:cc:dd:ee:02", "Sibling", RadioBand.Band5GHz, siblingChannel)
        };
        var graph = _service.BuildInterferenceGraph(aps, RadioBand.Band5GHz, null, null, null);
        graph.DirectlyObservedChannels[0] = new HashSet<int> { 36 };
        graph.ExternalLoad[0] = new Dictionary<int, double> { { 36, 1.0 }, { 149, 0.5 } };
        return graph;
    }

    [Fact]
    public void ScoreAssignment_HistoricOccupancy_SoftensUnobservedPenalty()
    {
        // ch149 is triangulated-only (not directly scanned), so it normally carries the full
        // uncertainty penalty. But if the AP has measured historic occupancy of ch149, we have
        // real data for it and the penalty should be mostly waived (confidence 0.85).
        var graph = BuildSubjectGraph();
        var assignment = new[] { (149, 80), (100, 80) };

        graph.Nodes[0].HistoricalStress = null;
        var scoreNoHistory = _service.ScoreAssignment(graph, assignment, RadioBand.Band5GHz);

        graph.Nodes[0].HistoricalStress = new Dictionary<int, (double, double, double)>
        {
            { 149, (0, 0, 0) } // benign measured airtime - isolates the unobserved term
        };
        var scoreWithHistory = _service.ScoreAssignment(graph, assignment, RadioBand.Band5GHz);

        scoreWithHistory.Should().BeLessThan(scoreNoHistory);
        // History keeps only 15% of the cliff, so the gap is 0.85 of the full penalty. The floor
        // now carries the band uncertainty multiplier (x2.0 on 5 GHz), so the base penalty is 2.0.
        (scoreNoHistory - scoreWithHistory).Should().BeApproximately(1.7, 0.05);
    }

    [Fact]
    public void ScoreAssignment_UnobservedChannelNoEvidence_StillPenalized()
    {
        // Soundness guard: a channel with no direct scan, no historic occupancy, and no resident
        // sibling must still be taxed, so genuinely-unknown channels can't win by looking empty.
        var graph = BuildSubjectGraph();
        graph.Nodes[0].HistoricalStress = null;

        var onObserved = _service.ScoreAssignment(
            graph, new[] { (36, 80), (100, 80) }, RadioBand.Band5GHz);
        var onUnobserved = _service.ScoreAssignment(
            graph, new[] { (149, 80), (100, 80) }, RadioBand.Band5GHz);

        onUnobserved.Should().BeGreaterThan(onObserved);
    }

    [Fact]
    public void ScoreAssignment_UnobservedPenalty_IndependentOfApCurrentChannel()
    {
        // Anti-oscillation invariant: a candidate channel must score the same whether the AP is
        // currently resident on it or moving onto it. Confidence is drawn from stable signals
        // (historic occupancy), not the AP's current position, so the score doesn't swing on moves.
        var graph = BuildSubjectGraph();
        graph.Nodes[0].HistoricalStress = new Dictionary<int, (double, double, double)>
        {
            { 149, (0, 0, 0) }
        };
        var assignment = new[] { (149, 80), (100, 80) };

        graph.Nodes[0].CurrentChannel = 36;  // ch149 is a candidate the AP is moving to
        var asCandidate = _service.ScoreAssignment(graph, assignment, RadioBand.Band5GHz);

        graph.Nodes[0].CurrentChannel = 149; // AP is resident on ch149
        var asResident = _service.ScoreAssignment(graph, assignment, RadioBand.Band5GHz);

        asCandidate.Should().BeApproximately(asResident, 0.001);
    }

    [Fact]
    public void ScoreAssignment_UnobservedPenalty_LowerForBandsWithBetterPropagation()
    {
        // Scan completeness tracks propagation: 2.4 GHz sees nearly every neighbor, so its
        // unobserved channels are less uncertain than 6 GHz, which dies fastest through walls.
        // The uncertainty multiplier must therefore order 2.4 < 5 < 6.
        double UnobservedScore(RadioBand band, int observedCh, int unobservedCh)
        {
            var aps = new List<AccessPointSnapshot>
            {
                CreateAp("aa:bb:cc:dd:ee:01", "Subject", band, observedCh, width: 20)
            };
            var graph = _service.BuildInterferenceGraph(aps, band, null, null, null);
            graph.DirectlyObservedChannels[0] = new HashSet<int> { observedCh };
            graph.ExternalLoad[0] = new Dictionary<int, double> { { observedCh, 0.1 }, { unobservedCh, 1.0 } };
            graph.Nodes[0].HistoricalStress = null;
            return _service.ScoreAssignment(graph, new[] { (unobservedCh, 20) }, band);
        }

        var score2_4 = UnobservedScore(RadioBand.Band2_4GHz, 1, 11);
        var score5 = UnobservedScore(RadioBand.Band5GHz, 36, 149);
        var score6 = UnobservedScore(RadioBand.Band6GHz, 5, 213);

        score2_4.Should().BeLessThan(score5);
        score5.Should().BeLessThan(score6);
    }

    // --- Directional interference (EIRP-aware) ---

    // Two placed APs ~45 m apart, same channel, with the given TX powers. Returns the graph.
    private InterferenceGraph BuildPlacedPair(int txPowerA, int txPowerB)
    {
        var aps = new List<AccessPointSnapshot>
        {
            CreateAp("aa:bb:cc:dd:ee:01", "AP-A", RadioBand.Band5GHz, 36, txPower: txPowerA),
            CreateAp("aa:bb:cc:dd:ee:02", "AP-B", RadioBand.Band5GHz, 36, txPower: txPowerB)
        };
        var propCtx = new ApPropagationContext
        {
            ApsByMac = new Dictionary<string, PropagationAp>
            {
                ["aa:bb:cc:dd:ee:01"] = new()
                {
                    Mac = "aa:bb:cc:dd:ee:01",
                    Model = "U6-Pro",
                    Latitude = 36.0000,
                    Longitude = -94.0000,
                    Floor = 1,
                    TxPowerDbm = txPowerA,
                    AntennaGainDbi = 3,
                    MountType = "ceiling"
                },
                ["aa:bb:cc:dd:ee:02"] = new()
                {
                    Mac = "aa:bb:cc:dd:ee:02",
                    Model = "U6-Pro",
                    Latitude = 36.0004,
                    Longitude = -94.0000,
                    Floor = 1,
                    TxPowerDbm = txPowerB,
                    AntennaGainDbi = 3,
                    MountType = "ceiling"
                }
            },
            WallsByFloor = new Dictionary<int, List<PropagationWall>>(),
            Buildings = null
        };
        return _service.BuildInterferenceGraph(aps, RadioBand.Band5GHz, propCtx, null, null);
    }

    [Fact]
    public void BuildInterferenceGraph_AsymmetricEirp_DirectionalWeightLowerForLowerPowerAggressor()
    {
        // AP-A full power (20 dBm), AP-B intentionally low (5 dBm).
        var graph = BuildPlacedPair(txPowerA: 20, txPowerB: 5);

        // Directional [aggressor, victim]: the low-power AP-B interferes with AP-A LESS than the
        // full-power AP-A interferes with AP-B (B transmits weaker, so its signal at A is lower).
        graph.DirectionalWeights[1, 0].Should().BeLessThan(graph.DirectionalWeights[0, 1]);

        // The symmetric weight is the worst case of both directions - it equals the stronger one,
        // so it does NOT credit B's low power (which is exactly why the degradation guard needs
        // the directional weight instead).
        graph.InternalWeights[0, 1].Should().Be(graph.InternalWeights[1, 0]);
        graph.InternalWeights[0, 1].Should().BeApproximately(graph.DirectionalWeights[0, 1], 0.001);
    }

    [Fact]
    public void BuildInterferenceGraph_EqualEirp_DirectionalWeightsSymmetric()
    {
        // With equal TX power, the two directions are the same - confirms the asymmetry above is
        // driven by EIRP, not by anything else.
        var graph = BuildPlacedPair(txPowerA: 20, txPowerB: 20);

        graph.DirectionalWeights[0, 1].Should().BeApproximately(graph.DirectionalWeights[1, 0], 0.001);
    }

    // --- Optimization ---

    [Fact]
    public void Optimize_ThreeApsOnSameChannel_RecommendsSeparation()
    {
        // Three APs on the same channel gives each AP a score > MinApScoreToMove (2.0)
        // since each has two co-channel neighbors: 2 × 0.625 × 3.0 = 3.75
        var aps = new List<AccessPointSnapshot>
        {
            CreateAp("aa:bb:cc:dd:ee:01", "AP-1", RadioBand.Band5GHz, 36),
            CreateAp("aa:bb:cc:dd:ee:02", "AP-2", RadioBand.Band5GHz, 36),
            CreateAp("aa:bb:cc:dd:ee:03", "AP-3", RadioBand.Band5GHz, 36)
        };
        var graph = _service.BuildInterferenceGraph(aps, RadioBand.Band5GHz, null, null, null);

        var plan = _service.Optimize(graph, RadioBand.Band5GHz, null);

        plan.Recommendations.Should().HaveCount(3);
        plan.RecommendedNetworkScore.Should().BeLessThanOrEqualTo(plan.CurrentNetworkScore);

        // At least one AP should be moved to a different channel
        var channels = plan.Recommendations.Select(r => r.RecommendedChannel).Distinct().ToList();
        channels.Count.Should().BeGreaterThan(1);
    }

    [Fact]
    public void Optimize_SingleAp_NoChange()
    {
        var aps = new List<AccessPointSnapshot>
        {
            CreateAp("aa:bb:cc:dd:ee:01", "AP-1", RadioBand.Band5GHz, 36)
        };
        var graph = _service.BuildInterferenceGraph(aps, RadioBand.Band5GHz, null, null, null);

        var plan = _service.Optimize(graph, RadioBand.Band5GHz, null);

        plan.Recommendations.Should().HaveCount(1);
        plan.CurrentNetworkScore.Should().Be(0);
    }

    [Fact]
    public void Optimize_MeshPair_SharesChannel()
    {
        var aps = new List<AccessPointSnapshot>
        {
            CreateAp("aa:bb:cc:dd:ee:01", "AP-Parent", RadioBand.Band5GHz, 36),
            CreateAp("aa:bb:cc:dd:ee:02", "AP-Child", RadioBand.Band5GHz, 36,
                isMeshChild: true, meshParentMac: "aa:bb:cc:dd:ee:01",
                meshUplinkBand: RadioBand.Band5GHz, meshUplinkChannel: 36),
            CreateAp("aa:bb:cc:dd:ee:03", "AP-Other", RadioBand.Band5GHz, 36)
        };
        var graph = _service.BuildInterferenceGraph(aps, RadioBand.Band5GHz, null, null, null);

        var plan = _service.Optimize(graph, RadioBand.Band5GHz, null);

        // Mesh pair must stay on same channel
        var parentRec = plan.Recommendations.First(r => r.ApMac == "aa:bb:cc:dd:ee:01");
        var childRec = plan.Recommendations.First(r => r.ApMac == "aa:bb:cc:dd:ee:02");
        parentRec.RecommendedChannel.Should().Be(childRec.RecommendedChannel);
        childRec.IsMeshConstrained.Should().BeTrue();
    }

    [Fact]
    public void Optimize_MeshLeaderForcedToMove_ChildFollowsAndIsNotStranded()
    {
        // The leader is jammed on ch36 by two pinned co-channel neighbors, so it must relocate.
        // Its mesh child must move with it - never left stranded on the old channel, which is what
        // happened when the per-AP filter reverted a low-scoring child independently of its leader.
        var aps = new List<AccessPointSnapshot>
        {
            CreateAp("aa:bb:cc:dd:ee:01", "Pinned-1", RadioBand.Band5GHz, 36),
            CreateAp("aa:bb:cc:dd:ee:02", "Pinned-2", RadioBand.Band5GHz, 36),
            CreateAp("aa:bb:cc:dd:ee:03", "Leader", RadioBand.Band5GHz, 36),
            CreateAp("aa:bb:cc:dd:ee:04", "Child", RadioBand.Band5GHz, 36,
                isMeshChild: true, meshParentMac: "aa:bb:cc:dd:ee:03",
                meshUplinkBand: RadioBand.Band5GHz, meshUplinkChannel: 36)
        };
        var options = new RecommendationOptions
        {
            PinnedApMacs = new HashSet<string> { "aa:bb:cc:dd:ee:01", "aa:bb:cc:dd:ee:02" }
        };
        var graph = _service.BuildInterferenceGraph(aps, RadioBand.Band5GHz, null, null, null, options);

        var plan = _service.Optimize(graph, RadioBand.Band5GHz, null, options);

        var leaderRec = plan.Recommendations.First(r => r.ApMac == "aa:bb:cc:dd:ee:03");
        var childRec = plan.Recommendations.First(r => r.ApMac == "aa:bb:cc:dd:ee:04");

        leaderRec.RecommendedChannel.Should().NotBe(36, "the leader is forced off the jammed channel");
        childRec.RecommendedChannel.Should().Be(leaderRec.RecommendedChannel, "the child must follow its leader");
        childRec.RecommendedWidth.Should().Be(leaderRec.RecommendedWidth);
        childRec.IsMeshConstrained.Should().BeTrue();
    }

    [Fact]
    public void Optimize_MeshUplinkOn5GHz_ChildIsIndependentOn2_4GHz()
    {
        // The backhaul runs on 5 GHz, so on the 2.4 GHz plan the child is a free, independent
        // radio - no mesh constraint should be created and it must not be marked constrained.
        var aps = new List<AccessPointSnapshot>
        {
            CreateAp("aa:bb:cc:dd:ee:01", "Leader", RadioBand.Band2_4GHz, 1),
            CreateAp("aa:bb:cc:dd:ee:02", "Child", RadioBand.Band2_4GHz, 1,
                isMeshChild: true, meshParentMac: "aa:bb:cc:dd:ee:01",
                meshUplinkBand: RadioBand.Band5GHz, meshUplinkChannel: 36)
        };
        var graph = _service.BuildInterferenceGraph(aps, RadioBand.Band2_4GHz, null, null, null);

        var plan = _service.Optimize(graph, RadioBand.Band2_4GHz, null);

        graph.MeshConstraints.Should().BeEmpty("the backhaul band (5 GHz) is not the band being optimized");
        var childRec = plan.Recommendations.First(r => r.ApMac == "aa:bb:cc:dd:ee:02");
        childRec.IsMeshConstrained.Should().BeFalse();
    }

    [Fact]
    public void Optimize_DfsExclude_NoDfsChannelsRecommended()
    {
        var aps = new List<AccessPointSnapshot>
        {
            CreateAp("aa:bb:cc:dd:ee:01", "AP-1", RadioBand.Band5GHz, 100, hasDfs: true),
            CreateAp("aa:bb:cc:dd:ee:02", "AP-2", RadioBand.Band5GHz, 100, hasDfs: true)
        };
        var options = new RecommendationOptions { DfsPreference = DfsPreference.Exclude };
        var graph = _service.BuildInterferenceGraph(aps, RadioBand.Band5GHz, null, null, null, options);
        var plan = _service.Optimize(graph, RadioBand.Band5GHz, null, options);

        // DFS channels: 52-64, 100-144
        foreach (var rec in plan.Recommendations)
        {
            var ch = rec.RecommendedChannel;
            var isDfs = (ch >= 52 && ch <= 64) || (ch >= 100 && ch <= 144);
            isDfs.Should().BeFalse($"Channel {ch} is DFS but DFS was excluded");
        }
    }

    [Fact]
    public void Optimize_PinnedAp_ChannelUnchanged()
    {
        var aps = new List<AccessPointSnapshot>
        {
            CreateAp("aa:bb:cc:dd:ee:01", "AP-Pinned", RadioBand.Band5GHz, 36),
            CreateAp("aa:bb:cc:dd:ee:02", "AP-Movable", RadioBand.Band5GHz, 36)
        };
        var options = new RecommendationOptions
        {
            PinnedApMacs = new HashSet<string> { "aa:bb:cc:dd:ee:01" }
        };
        var graph = _service.BuildInterferenceGraph(aps, RadioBand.Band5GHz, null, null, null, options);
        var plan = _service.Optimize(graph, RadioBand.Band5GHz, null, options);

        var pinnedRec = plan.Recommendations.First(r => r.ApMac == "aa:bb:cc:dd:ee:01");
        pinnedRec.RecommendedChannel.Should().Be(36);
    }

    [Fact]
    public void Optimize_EmptyGraph_ReturnsEmptyPlan()
    {
        var graph = new InterferenceGraph();
        var plan = _service.Optimize(graph, RadioBand.Band5GHz, null);

        plan.Recommendations.Should().BeEmpty();
        plan.CurrentNetworkScore.Should().Be(0);
    }

    [Fact]
    public void Optimize_2_4GHz_UsesNonOverlappingChannels()
    {
        var aps = new List<AccessPointSnapshot>
        {
            CreateAp("aa:bb:cc:dd:ee:01", "AP-1", RadioBand.Band2_4GHz, 6, width: 20),
            CreateAp("aa:bb:cc:dd:ee:02", "AP-2", RadioBand.Band2_4GHz, 6, width: 20),
            CreateAp("aa:bb:cc:dd:ee:03", "AP-3", RadioBand.Band2_4GHz, 6, width: 20)
        };
        var graph = _service.BuildInterferenceGraph(aps, RadioBand.Band2_4GHz, null, null, null);
        var plan = _service.Optimize(graph, RadioBand.Band2_4GHz, null);

        // Should recommend 1, 6, 11 (non-overlapping)
        var channels = plan.Recommendations.Select(r => r.RecommendedChannel).OrderBy(c => c).ToList();
        channels.Should().OnlyContain(c => c == 1 || c == 6 || c == 11);
        channels.Distinct().Count().Should().Be(3);
    }

    [Fact]
    public void Optimize_ScoreImproves()
    {
        // Three APs all on channel 36 - optimizer should separate them
        var aps = new List<AccessPointSnapshot>
        {
            CreateAp("aa:bb:cc:dd:ee:01", "AP-1", RadioBand.Band5GHz, 36),
            CreateAp("aa:bb:cc:dd:ee:02", "AP-2", RadioBand.Band5GHz, 36),
            CreateAp("aa:bb:cc:dd:ee:03", "AP-3", RadioBand.Band5GHz, 36)
        };
        var graph = _service.BuildInterferenceGraph(aps, RadioBand.Band5GHz, null, null, null);
        var plan = _service.Optimize(graph, RadioBand.Band5GHz, null);

        plan.RecommendedNetworkScore.Should().BeLessThan(plan.CurrentNetworkScore);
        plan.ImprovementPercent.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Optimize_AlreadyOptimal_NoChange()
    {
        // APs already on different channels
        var aps = new List<AccessPointSnapshot>
        {
            CreateAp("aa:bb:cc:dd:ee:01", "AP-1", RadioBand.Band5GHz, 36),
            CreateAp("aa:bb:cc:dd:ee:02", "AP-2", RadioBand.Band5GHz, 149)
        };
        var graph = _service.BuildInterferenceGraph(aps, RadioBand.Band5GHz, null, null, null);
        var plan = _service.Optimize(graph, RadioBand.Band5GHz, null);

        plan.CurrentNetworkScore.Should().Be(0);
        plan.RecommendedNetworkScore.Should().Be(0);
    }

    [Fact]
    public void Optimize_ReportsUnplacedCount()
    {
        var aps = new List<AccessPointSnapshot>
        {
            CreateAp("aa:bb:cc:dd:ee:01", "AP-1", RadioBand.Band5GHz, 36),
            CreateAp("aa:bb:cc:dd:ee:02", "AP-2", RadioBand.Band5GHz, 36)
        };
        var graph = _service.BuildInterferenceGraph(aps, RadioBand.Band5GHz, null, null, null);
        var plan = _service.Optimize(graph, RadioBand.Band5GHz, null);

        plan.UnplacedApCount.Should().Be(2); // Neither is placed
    }

    [Fact]
    public void Optimize_MeshChildMarkedConstrained()
    {
        var aps = new List<AccessPointSnapshot>
        {
            CreateAp("aa:bb:cc:dd:ee:01", "AP-Parent", RadioBand.Band5GHz, 36),
            CreateAp("aa:bb:cc:dd:ee:02", "AP-Child", RadioBand.Band5GHz, 36,
                isMeshChild: true, meshParentMac: "aa:bb:cc:dd:ee:01",
                meshUplinkBand: RadioBand.Band5GHz, meshUplinkChannel: 36)
        };
        var graph = _service.BuildInterferenceGraph(aps, RadioBand.Band5GHz, null, null, null);
        var plan = _service.Optimize(graph, RadioBand.Band5GHz, null);

        plan.Recommendations.First(r => r.ApMac == "aa:bb:cc:dd:ee:02")
            .IsMeshConstrained.Should().BeTrue();
    }

    [Fact]
    public void Optimize_ZeroInterference_PreservesCurrentChannels()
    {
        // APs already on different non-overlapping channels (score = 0)
        // Optimizer should NOT swap them around pointlessly
        // Using non-DFS channels that are in the default valid set
        var aps = new List<AccessPointSnapshot>
        {
            CreateAp("aa:bb:cc:dd:ee:01", "AP-1", RadioBand.Band5GHz, 36),
            CreateAp("aa:bb:cc:dd:ee:02", "AP-2", RadioBand.Band5GHz, 149)
        };
        var graph = _service.BuildInterferenceGraph(aps, RadioBand.Band5GHz, null, null, null);
        var plan = _service.Optimize(graph, RadioBand.Band5GHz, null);

        // No changes should be recommended
        foreach (var rec in plan.Recommendations)
        {
            rec.IsChanged.Should().BeFalse(
                $"AP {rec.ApName} was moved from {rec.CurrentChannel} to {rec.RecommendedChannel} with no improvement");
        }
    }

    [Fact]
    public void Optimize_6GHz_NoInterference_KeepsCurrentChannels()
    {
        // 6 GHz APs on different 160 MHz bonding groups with zero interference
        // Using channels from default valid set: 1, 5, 9, 13, 17, 21, 25, 29, 33, 37, 41, 45, 49, 53, 57, 61
        // Ch 5/160 → span (1,29), Ch 37/160 → span (33,61) — non-overlapping
        var aps = new List<AccessPointSnapshot>
        {
            CreateAp("aa:bb:cc:dd:ee:01", "AP-1", RadioBand.Band6GHz, 5, width: 160),
            CreateAp("aa:bb:cc:dd:ee:02", "AP-2", RadioBand.Band6GHz, 37, width: 160)
        };
        var graph = _service.BuildInterferenceGraph(aps, RadioBand.Band6GHz, null, null, null);
        var plan = _service.Optimize(graph, RadioBand.Band6GHz, null);

        // Should not swap channels when there's no improvement
        foreach (var rec in plan.Recommendations)
        {
            rec.IsChanged.Should().BeFalse(
                $"AP {rec.ApName} was moved from Ch {rec.CurrentChannel} to Ch {rec.RecommendedChannel} with no improvement");
        }
    }

    [Fact]
    public void Optimize_2_4GHz_AlwaysUsesOnly_1_6_11()
    {
        // Even with regulatory data that includes other channels, should only use 1/6/11
        var aps = new List<AccessPointSnapshot>
        {
            CreateAp("aa:bb:cc:dd:ee:01", "AP-1", RadioBand.Band2_4GHz, 3, width: 20),
            CreateAp("aa:bb:cc:dd:ee:02", "AP-2", RadioBand.Band2_4GHz, 9, width: 20)
        };
        var graph = _service.BuildInterferenceGraph(aps, RadioBand.Band2_4GHz, null, null, null);
        var plan = _service.Optimize(graph, RadioBand.Band2_4GHz, null);

        foreach (var rec in plan.Recommendations)
        {
            rec.RecommendedChannel.Should().BeOneOf(new[] { 1, 6, 11 },
                $"2.4 GHz should only recommend 1/6/11 but got {rec.RecommendedChannel}");
        }
    }

    [Fact]
    public void Optimize_80MHz_DoesNotRecommendSameBondingGroup()
    {
        // Three APs on same 80 MHz channel ensures scores > MinApScoreToMove (2.0)
        // and verifies separation into different bonding groups
        var aps = new List<AccessPointSnapshot>
        {
            CreateAp("aa:bb:cc:dd:ee:01", "AP-1", RadioBand.Band5GHz, 36, width: 80),
            CreateAp("aa:bb:cc:dd:ee:02", "AP-2", RadioBand.Band5GHz, 36, width: 80),
            CreateAp("aa:bb:cc:dd:ee:03", "AP-3", RadioBand.Band5GHz, 36, width: 80)
        };
        var graph = _service.BuildInterferenceGraph(aps, RadioBand.Band5GHz, null, null, null);
        var plan = _service.Optimize(graph, RadioBand.Band5GHz, null);

        // At least two APs should be on different bonding groups
        var movedRecs = plan.Recommendations.Where(r => r.IsChanged).ToList();
        movedRecs.Should().NotBeEmpty("at least one AP should be moved off the shared channel");

        foreach (var rec in movedRecs)
        {
            var otherRecs = plan.Recommendations.Where(r => r.ApMac != rec.ApMac);
            foreach (var other in otherRecs)
            {
                var span1 = ChannelSpanHelper.GetChannelSpan(RadioBand.Band5GHz, rec.RecommendedChannel, 80);
                var span2 = ChannelSpanHelper.GetChannelSpan(RadioBand.Band5GHz, other.RecommendedChannel, 80);
                if (rec.RecommendedChannel != other.RecommendedChannel)
                {
                    ChannelSpanHelper.SpansOverlap(span1, span2).Should().BeFalse(
                        $"APs should be on different 80 MHz blocks but got ch{rec.RecommendedChannel} ({span1}) and ch{other.RecommendedChannel} ({span2})");
                }
            }
        }
    }

    // --- Neighbor Triangulation ---

    [Fact]
    public void BuildExternalLoad_TriangulatedNeighborApplied()
    {
        // AP-1 on ch36, AP-2 on ch149. AP-2 sees a neighbor on ch36.
        // AP-1 should get a triangulated external load entry on ch36 (scaled by internal weight).
        var aps = new List<AccessPointSnapshot>
        {
            CreateAp("aa:bb:cc:dd:ee:01", "AP-1", RadioBand.Band5GHz, 36),
            CreateAp("aa:bb:cc:dd:ee:02", "AP-2", RadioBand.Band5GHz, 149)
        };

        var scans = new List<ChannelScanResult>
        {
            new()
            {
                ApMac = "aa:bb:cc:dd:ee:02",
                Band = RadioBand.Band5GHz,
                Neighbors = new()
                {
                    new NeighborNetwork { Bssid = "ff:ff:ff:00:00:01", Channel = 36, Signal = -55, Width = 80, IsOwnNetwork = false }
                }
            }
        };

        var graph = _service.BuildInterferenceGraph(aps, RadioBand.Band5GHz, null, scans, null);

        // AP-1 (index 0) should have triangulated external load on ch36
        graph.ExternalLoad[0].Should().ContainKey(36);
        // Unplaced APs have internal weight 0.531. Neighbor at -55 dBm → weight 0.844 (CCA-anchored).
        // Width matches AP (80=80), no width scaling.
        // Triangulated weight = 0.844 * 0.531 = 0.448
        graph.ExternalLoad[0][36].Should().BeApproximately(0.448, 0.05);

        // AP-2 (index 1) should also have direct external load on ch36
        graph.ExternalLoad[1].Should().ContainKey(36);
        graph.ExternalLoad[1][36].Should().BeApproximately(0.844, 0.05);
    }

    [Fact]
    public void BuildExternalLoad_DirectObservationUnchanged()
    {
        // Same scenario as BuildInterferenceGraph_ExternalLoad_FromScanResults
        // but with BSSID - verifies direct observation behavior is preserved
        var aps = new List<AccessPointSnapshot>
        {
            CreateAp("aa:bb:cc:dd:ee:01", "AP-1", RadioBand.Band5GHz, 36)
        };

        var scans = new List<ChannelScanResult>
        {
            new()
            {
                ApMac = "aa:bb:cc:dd:ee:01",
                Band = RadioBand.Band5GHz,
                Neighbors = new()
                {
                    new NeighborNetwork { Bssid = "ff:ff:ff:00:00:01", Channel = 36, Signal = -60, Width = 80, IsOwnNetwork = false },
                    new NeighborNetwork { Bssid = "ff:ff:ff:00:00:02", Channel = 36, Signal = -70, Width = 80, IsOwnNetwork = false }
                }
            }
        };

        var graph = _service.BuildInterferenceGraph(aps, RadioBand.Band5GHz, null, scans, null);

        graph.ExternalLoad[0].Should().ContainKey(36);
        // -60 dBm → 0.6875, -70 dBm → 0.375, sum = 1.0625 (CCA-anchored)
        graph.ExternalLoad[0][36].Should().BeApproximately(1.0625, 0.05);
    }

    [Fact]
    public void BuildExternalLoad_OwnNetworkExcludedFromTriangulation()
    {
        // AP-1 on ch36, AP-2 on ch149. AP-2 sees an own-network BSSID on ch36.
        // Own-network should NOT be triangulated to AP-1.
        var aps = new List<AccessPointSnapshot>
        {
            CreateAp("aa:bb:cc:dd:ee:01", "AP-1", RadioBand.Band5GHz, 36),
            CreateAp("aa:bb:cc:dd:ee:02", "AP-2", RadioBand.Band5GHz, 149)
        };

        var scans = new List<ChannelScanResult>
        {
            new()
            {
                ApMac = "aa:bb:cc:dd:ee:02",
                Band = RadioBand.Band5GHz,
                Neighbors = new()
                {
                    new NeighborNetwork { Bssid = "ff:ff:ff:00:00:01", Channel = 36, Signal = -55, IsOwnNetwork = true }
                }
            }
        };

        var graph = _service.BuildInterferenceGraph(aps, RadioBand.Band5GHz, null, scans, null);

        // Neither AP should have external load - own-network is excluded
        graph.ExternalLoad[0].Should().BeEmpty();
        graph.ExternalLoad[1].Should().BeEmpty();
    }

    [Fact]
    public void BuildExternalLoad_MultipleObserversTakeMax()
    {
        // Three APs. AP-1 and AP-2 both see the same neighbor BSSID.
        // AP-3 should get the triangulated weight from the closer observer.
        var aps = new List<AccessPointSnapshot>
        {
            CreateAp("aa:bb:cc:dd:ee:01", "AP-1", RadioBand.Band5GHz, 36),
            CreateAp("aa:bb:cc:dd:ee:02", "AP-2", RadioBand.Band5GHz, 149),
            CreateAp("aa:bb:cc:dd:ee:03", "AP-3", RadioBand.Band5GHz, 161)
        };

        var scans = new List<ChannelScanResult>
        {
            new()
            {
                ApMac = "aa:bb:cc:dd:ee:01",
                Band = RadioBand.Band5GHz,
                Neighbors = new()
                {
                    // AP-1 sees neighbor at -75 dBm (weak)
                    new NeighborNetwork { Bssid = "ff:ff:ff:00:00:01", Channel = 100, Signal = -75, Width = 80, IsOwnNetwork = false }
                }
            },
            new()
            {
                ApMac = "aa:bb:cc:dd:ee:02",
                Band = RadioBand.Band5GHz,
                Neighbors = new()
                {
                    // AP-2 sees same neighbor at -55 dBm (strong)
                    new NeighborNetwork { Bssid = "ff:ff:ff:00:00:01", Channel = 100, Signal = -55, Width = 80, IsOwnNetwork = false }
                }
            }
        };

        var graph = _service.BuildInterferenceGraph(aps, RadioBand.Band5GHz, null, scans, null);

        // AP-3 (index 2) should have external load on ch100 from the best estimate
        graph.ExternalLoad[2].Should().ContainKey(100);

        // The stronger sighting (-55 dBm → weight 0.844) × proximity 0.531 = 0.448
        // beats the weaker sighting (-75 dBm → weight 0.219) × proximity 0.531 = 0.116
        graph.ExternalLoad[2][100].Should().BeApproximately(0.448, 0.05);
    }

    // --- Re-validation after per-AP filtering ---

    [Fact]
    public void Optimize_VetoedMoveInvalidatesOtherMoves_RevertsToAvoidWorsening()
    {
        // Regression: optimizer plans a coordinated swap (A,B move to ch6; C moves off ch6).
        // Per-AP filter vetoes C's move (score too low), so A and B land on ch6
        // alongside C - worse than before. Re-validation should catch this.
        //
        // Setup: 4 APs on 2.4 GHz with 3 channels (1, 6, 11).
        // Two APs on ch11 (high co-channel = high scores, will want to move),
        // one AP on ch6 (low score, will be vetoed), one AP on ch1 (low score, vetoed).
        // Without re-validation, the two ch11 APs would both move to ch6,
        // creating 3 APs on ch6.
        var aps = new List<AccessPointSnapshot>
        {
            CreateAp("aa:bb:cc:dd:ee:01", "AP-1", RadioBand.Band2_4GHz, 11, width: 20),
            CreateAp("aa:bb:cc:dd:ee:02", "AP-2", RadioBand.Band2_4GHz, 11, width: 20),
            CreateAp("aa:bb:cc:dd:ee:03", "AP-3", RadioBand.Band2_4GHz, 6, width: 20),
            CreateAp("aa:bb:cc:dd:ee:04", "AP-4", RadioBand.Band2_4GHz, 1, width: 20)
        };

        var graph = _service.BuildInterferenceGraph(aps, RadioBand.Band2_4GHz, null, null, null);
        var plan = _service.Optimize(graph, RadioBand.Band2_4GHz, null);

        // Count how many APs end up recommended on each channel
        var channelCounts = plan.Recommendations
            .GroupBy(r => r.RecommendedChannel)
            .ToDictionary(g => g.Key, g => g.Count());

        // No channel should have 3+ APs when only 3 non-overlapping channels exist for 4 APs
        foreach (var (channel, count) in channelCounts)
        {
            count.Should().BeLessThan(3,
                $"channel {channel} has {count} APs - re-validation should prevent " +
                "piling APs onto a channel when the coordinated swap was partially vetoed");
        }

        // Every changed AP should genuinely improve vs current
        foreach (var rec in plan.Recommendations.Where(r => r.IsChanged))
        {
            rec.RecommendedScore.Should().BeLessThan(rec.CurrentScore,
                $"{rec.ApName} recommended score {rec.RecommendedScore:F3} should be better " +
                $"(lower) than current {rec.CurrentScore:F3}");
        }
    }
}
