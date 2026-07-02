using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkOptimizer.Core.Models;
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
            Status = new(DeviceStatusKind.Online, "Online"),
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
                Mac = "aa:bb:cc:dd:ee:02", Name = "AP-Offline", Status = new(DeviceStatusKind.Offline, "Offline"),
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

    [Fact]
    public void BuildInterferenceGraph_RememberedNeighbor_WeightScaledByConfidence()
    {
        var aps = new List<AccessPointSnapshot>
        {
            CreateAp("aa:bb:cc:dd:ee:01", "AP-1", RadioBand.Band5GHz, 36)
        };

        // Identical neighbors except confidence: the remembered one (0.5) must carry half
        // the live one's weight, land in HistoricallyObservedChannels instead of
        // DirectlyObservedChannels, and never grant the full "directly observed" status.
        var scans = new List<ChannelScanResult>
        {
            new()
            {
                ApMac = "aa:bb:cc:dd:ee:01",
                Band = RadioBand.Band5GHz,
                Neighbors = new()
                {
                    new NeighborNetwork { Bssid = "ff:ff:ff:00:00:01", Channel = 36, Signal = -60, IsOwnNetwork = false },
                    new NeighborNetwork { Bssid = "ff:ff:ff:00:00:02", Channel = 149, Signal = -60, IsOwnNetwork = false, Confidence = 0.5 }
                }
            }
        };

        var graph = _service.BuildInterferenceGraph(aps, RadioBand.Band5GHz, null, scans, null);

        graph.ExternalLoad[0][149].Should().BeApproximately(graph.ExternalLoad[0][36] * 0.5, 0.001);
        graph.DirectlyObservedChannels[0].Should().Contain(36);
        graph.DirectlyObservedChannels[0].Should().NotContain(149);
        graph.HistoricallyObservedChannels[0].Should().ContainKey(149)
            .WhoseValue.Should().BeApproximately(0.5, 0.001);
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
        // History keeps only 15% of the cliff, so the gap is 0.85 of the full penalty. ch149 has a
        // triangulated sighting (0.5), which is trusted as-is (no floor) and carries the band
        // uncertainty multiplier (x2.0 on 5 GHz), so the base penalty is 1.0 and the gap is 0.85.
        (scoreNoHistory - scoreWithHistory).Should().BeApproximately(0.85, 0.05);
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

    // Standard US 5 GHz regulatory channel set (80 MHz bonding groups + DFS list).
    private static RegulatoryChannelData StdUsRegulatory() => new()
    {
        Channels5GHz = new Dictionary<int, int[]>
        {
            { 20, new[] { 36, 40, 44, 48, 52, 56, 60, 64, 100, 104, 108, 112, 116, 120, 124, 128, 132, 136, 140, 144, 149, 153, 157, 161, 165 } },
            { 80, new[] { 36, 52, 100, 116, 132, 149 } }
        },
        DfsChannels = new[] { 52, 56, 60, 64, 100, 104, 108, 112, 116, 120, 124, 128, 132, 136, 140, 144 }
    };

    // A single 5 GHz AP on the given channel, with the given per-channel external load and the set
    // of channels it has directly observed. Mirrors the live-graph shape the optimizer consumes.
    private InterferenceGraph SingleApGraph(
        int currentChannel,
        Dictionary<int, double> externalLoad,
        HashSet<int> directlyObserved,
        RegulatoryChannelData reg,
        RecommendationOptions options)
    {
        var aps = new List<AccessPointSnapshot>
        {
            CreateAp("aa:bb:cc:dd:ee:01", "Subject", RadioBand.Band5GHz, currentChannel, hasDfs: true)
        };
        var graph = _service.BuildInterferenceGraph(aps, RadioBand.Band5GHz, null, null, reg, options);
        graph.ExternalLoad[0] = externalLoad;
        graph.DirectlyObservedChannels[0] = directlyObserved;
        return graph;
    }

    [Fact]
    public void Optimize_DfsToUnobservedNonDfs_FrictionKeepsApOnDfs()
    {
        // The reported case (Mac site): an AP healthy on a DFS channel with one known neighbor, and
        // a non-DFS channel it has NO scan data for. Without friction the engine jumps to the
        // non-DFS channel because it "looks" empty - but that emptiness is just missing data.
        var reg = StdUsRegulatory();
        var options = new RecommendationOptions { DfsPreference = DfsPreference.IncludeWithPenalty };
        var graph = SingleApGraph(52,
            externalLoad: new() { { 52, 0.55 } },
            directlyObserved: new(), // only triangulated data, like the live Mac AP
            reg, options);

        var plan = _service.Optimize(graph, RadioBand.Band5GHz, reg, options);

        var subject = plan.Recommendations.Single();
        subject.RecommendedChannel.Should().Be(52,
            "leaving a working DFS channel for a non-DFS channel with no scan data is a blind bet");
        subject.IsChanged.Should().BeFalse();
    }

    [Fact]
    public void Optimize_DfsToObservedCleanNonDfs_MoveAllowed_BadgeNotDfs()
    {
        // Friction is waived when the non-DFS destination has real scan evidence it's clean. A
        // congested DFS AP should still move - and the DFS badge must reflect the final non-DFS
        // channel, not the optimizer's first pick (the stale-badge bug).
        var reg = StdUsRegulatory();
        var options = new RecommendationOptions { DfsPreference = DfsPreference.IncludeWithPenalty };
        var graph = SingleApGraph(52,
            externalLoad: new() { { 52, 2.5 }, { 36, 0.0 } }, // current DFS busy, ch36 scanned clean
            directlyObserved: new() { 52, 36 },
            reg, options);

        var plan = _service.Optimize(graph, RadioBand.Band5GHz, reg, options);

        var subject = plan.Recommendations.Single();
        subject.RecommendedChannel.Should().Be(36);
        subject.IsChanged.Should().BeTrue();
        subject.IsCurrentDfsChannel.Should().BeTrue("ch52 (UNII-2) is a DFS channel");
        subject.IsRecommendedDfsChannel.Should().BeFalse("ch36 (UNII-1) is not a DFS channel");
    }

    [Fact]
    public void Optimize_RecommendsDfsChannel_BadgeIsDfs()
    {
        // A congested non-DFS AP with a clean, observed DFS channel available: the move is to DFS
        // (no departure friction - it's moving onto DFS, not off it) and the badge must show DFS.
        var reg = StdUsRegulatory();
        var options = new RecommendationOptions { DfsPreference = DfsPreference.IncludeWithPenalty };
        var graph = SingleApGraph(149,
            externalLoad: new() { { 149, 3.0 }, { 100, 0.5 }, { 116, 2.0 }, { 132, 2.0 } },
            directlyObserved: new() { 149, 100, 116, 132 },
            reg, options);

        var plan = _service.Optimize(graph, RadioBand.Band5GHz, reg, options);

        var subject = plan.Recommendations.Single();
        subject.RecommendedChannel.Should().Be(100);
        subject.IsChanged.Should().BeTrue();
        subject.IsCurrentDfsChannel.Should().BeFalse("ch149 (UNII-3) is not a DFS channel");
        subject.IsRecommendedDfsChannel.Should().BeTrue("ch100 (UNII-2C) is a DFS channel");
    }

    [Fact]
    public void Optimize_TriangulatedCleanNonDfs_PreferredOverObservedDfs()
    {
        // The reported case: a congested DFS AP, a non-DFS channel that a sibling scanned and found
        // clean (triangulated, not in this AP's own direct set). Triangulation is real evidence, so
        // the clean non-DFS channel should win - the engine must NOT floor or friction it into
        // losing to a directly-observed-but-occupied DFS channel. The current channel is loaded
        // above the per-AP move threshold so the move routes through the normal per-AP path.
        var reg = StdUsRegulatory();
        var options = new RecommendationOptions { DfsPreference = DfsPreference.IncludeWithPenalty };
        var graph = SingleApGraph(52,
            externalLoad: new() { { 52, 2.5 }, { 36, 0.2 } }, // ch52 directly heard busy; ch36 triangulated clean
            directlyObserved: new() { 52 },                   // ch36 NOT directly observed by this AP
            reg, options);

        var plan = _service.Optimize(graph, RadioBand.Band5GHz, reg, options);

        var subject = plan.Recommendations.Single();
        subject.RecommendedChannel.Should().Be(36,
            "a sibling-scanned clean channel is real evidence and beats a directly-observed occupied DFS channel");
        subject.IsCurrentDfsChannel.Should().BeTrue("ch52 (UNII-2) is a DFS channel");
        subject.IsRecommendedDfsChannel.Should().BeFalse("ch36 (UNII-1) is not a DFS channel");
    }

    [Fact]
    public void Optimize_AltruisticRelocation_DoesNotMoveHealthyApWhenNoNeighborBenefits()
    {
        // The reported Mac case: two APs that share no spectrum (ch36 and ch149). The ch36 AP is
        // healthy (below the per-AP move threshold) and a cleaner channel exists, but relocating it
        // only lowers ITS OWN score - it cannot declutter the ch149 neighbor. The altruistic pass
        // must NOT move it. Before the fix the gate measured total network score, which the mover's
        // own drop inflates, so it relocated the AP onto an unshared channel for no one's benefit.
        var reg = StdUsRegulatory();
        var options = new RecommendationOptions { DfsPreference = DfsPreference.IncludeWithPenalty };
        var aps = new List<AccessPointSnapshot>
        {
            CreateAp("aa:bb:cc:dd:ee:01", "Mover", RadioBand.Band5GHz, 36, width: 20),
            CreateAp("aa:bb:cc:dd:ee:02", "Bystander", RadioBand.Band5GHz, 149, width: 20)
        };
        var graph = _service.BuildInterferenceGraph(aps, RadioBand.Band5GHz, null, null, reg, options);
        // Mover sits on a lightly-loaded ch36 with a clean, directly-observed non-DFS ch161 nearby.
        graph.ExternalLoad[0] = new() { { 36, 0.8 } };
        graph.DirectlyObservedChannels[0] = new() { 36, 161 };
        // Bystander is healthy on ch149 and overlaps neither ch36 nor the mover's alternatives.
        graph.ExternalLoad[1] = new() { { 149, 0.3 } };
        graph.DirectlyObservedChannels[1] = new() { 149 };

        var plan = _service.Optimize(graph, RadioBand.Band5GHz, reg, options);

        var mover = plan.Recommendations.First(r => r.ApMac == "aa:bb:cc:dd:ee:01");
        mover.RecommendedChannel.Should().Be(36,
            "a healthy AP must not relocate when the move helps no neighbor - that just chases its own score");
        mover.IsChanged.Should().BeFalse();
    }

    [Fact]
    public void Optimize_AltruisticRelocation_StillMovesHealthyApThatDecluttersCoChannelNeighbor()
    {
        // Regression guard for the legitimate altruistic path: a healthy AP that interferes with a
        // co-channel neighbor SHOULD relocate, because the neighbor genuinely benefits. The neighbor
        // suffers from the shared channel but stays below the move threshold, so it can't rescue
        // itself - only the mover relocating helps it. The per-AP path won't move the mover either
        // (it too is below the threshold); only the altruistic pass can, and it must still fire when
        // the OTHER AP's score actually drops.
        var reg = StdUsRegulatory();
        var options = new RecommendationOptions { DfsPreference = DfsPreference.IncludeWithPenalty };
        var aps = new List<AccessPointSnapshot>
        {
            CreateAp("aa:bb:cc:dd:ee:01", "Mover", RadioBand.Band5GHz, 36, width: 20),
            CreateAp("aa:bb:cc:dd:ee:02", "Victim", RadioBand.Band5GHz, 36, width: 20)
        };
        var graph = _service.BuildInterferenceGraph(aps, RadioBand.Band5GHz, null, null, reg, options);
        // Asymmetric coupling: the mover blasts the victim (0.5 -> 1.5 co-channel pain, real but
        // under the 2.0 move threshold so the victim can't escape on its own), but barely hears it
        // back (0.1 -> 0.3), so the mover stays comfortably healthy on the shared channel.
        graph.InternalWeights[0, 1] = graph.InternalWeights[1, 0] = 0.5;
        graph.DirectionalWeights[0, 1] = 0.5; // mover -> victim (strong)
        graph.DirectionalWeights[1, 0] = 0.1; // victim -> mover (weak)
        graph.ExternalLoad[0] = new() { { 36, 0.0 }, { 161, 0.0 } };
        graph.DirectlyObservedChannels[0] = new() { 36, 161 };
        graph.ExternalLoad[1] = new() { { 36, 0.0 } };
        graph.DirectlyObservedChannels[1] = new() { 36 };

        var plan = _service.Optimize(graph, RadioBand.Band5GHz, reg, options);

        var mover = plan.Recommendations.First(r => r.ApMac == "aa:bb:cc:dd:ee:01");
        var victim = plan.Recommendations.First(r => r.ApMac == "aa:bb:cc:dd:ee:02");
        mover.RecommendedChannel.Should().NotBe(36,
            "the mover should relocate to declutter the co-channel victim that genuinely benefits");
        victim.RecommendedChannel.Should().Be(36,
            "the victim is below the move threshold and is decluttered by the mover leaving, not by moving itself");
    }

    [Fact]
    public void ScoreAssignment_BlindApUnobservedChannel_FlooredAgainstTriangulatedLoad()
    {
        // Fix for the deceptive 0.0: an AP with NO direct scan data on the band (fully blind, only
        // triangulated neighbor sightings) used to short-circuit the unobserved penalty to 0, so an
        // unscanned channel read as a perfect 0.0 and won relocations. It must instead floor against
        // the AP's known (triangulated) load so a blind channel carries real uncertainty.
        var reg = StdUsRegulatory();
        var options = new RecommendationOptions { DfsPreference = DfsPreference.IncludeWithPenalty };
        var graph = SingleApGraph(36,
            externalLoad: new() { { 36, 0.2 } }, // a single triangulated sighting; nothing on ch100
            directlyObserved: new(),             // fully blind - no direct scans on this band
            reg, options);

        var onUnobserved = _service.ScoreAssignment(graph, new[] { (100, 80) }, RadioBand.Band5GHz);

        onUnobserved.Should().BeGreaterThan(0, "an unobserved channel must never score a deceptive 0.0");
        onUnobserved.Should().BeApproximately(0.4, 0.05,
            "a blind AP floors an unscanned channel at its triangulated load (0.2) x the 5 GHz uncertainty multiplier (2.0)");
    }

    [Fact]
    public void Optimize_Crowded24GHz_SuppressesMarginalCollisionMove()
    {
        // In a saturated 2.4 GHz band, the optimizer was shoving a congested AP onto a neighbor's
        // channel for a tiny net gain (the unobserved floor over-priced the one empty lane). The
        // crowding friction must hold it put: a move that nets little once both co-channel victims
        // are counted isn't worth the churn when the whole band is already busy.
        var reg = StdUsRegulatory();
        var options = new RecommendationOptions { DfsPreference = DfsPreference.IncludeWithPenalty };
        var aps = new List<AccessPointSnapshot>
        {
            CreateAp("aa:bb:cc:dd:ee:01", "Living Room", RadioBand.Band2_4GHz, 1, width: 20),
            CreateAp("aa:bb:cc:dd:ee:02", "Downstairs", RadioBand.Band2_4GHz, 11, width: 20)
        };
        var graph = _service.BuildInterferenceGraph(aps, RadioBand.Band2_4GHz, null, null, reg, options);
        graph.InternalWeights[0, 1] = graph.InternalWeights[1, 0] = 1.0;
        graph.DirectionalWeights[0, 1] = graph.DirectionalWeights[1, 0] = 1.0;
        // Every channel is busy. ch1 is the least-bad, so the search wants to pile Downstairs onto
        // Living Room's ch1 - a co-channel collision whose net site benefit is negative.
        graph.ExternalLoad[0] = new() { { 1, 4.0 }, { 6, 8.0 }, { 11, 9.0 } };
        graph.DirectlyObservedChannels[0] = new() { 1, 6, 11 };
        graph.ExternalLoad[1] = new() { { 1, 4.0 }, { 6, 8.0 }, { 11, 9.0 } };
        graph.DirectlyObservedChannels[1] = new() { 1, 6, 11 };

        var plan = _service.Optimize(graph, RadioBand.Band2_4GHz, reg, options);

        var downstairs = plan.Recommendations.First(r => r.ApMac == "aa:bb:cc:dd:ee:02");
        downstairs.RecommendedChannel.Should().Be(11,
            "in a crowded 2.4 GHz band a move onto a neighbor's channel isn't worth the churn");
        downstairs.RecommendedChannel.Should().NotBe(1, "it must not be shoved into a co-channel collision");
    }

    [Fact]
    public void Optimize_Crowded24GHz_StillSpreadsClusteredAps()
    {
        // The crowding friction must not stop us spreading APs apart: three APs piled on ch1 hurt
        // each other badly, and splitting them across 1/6/11 is a large net win (each resolved
        // collision frees both victims), so it clears the crowded-band bar with room to spare.
        var reg = StdUsRegulatory();
        var options = new RecommendationOptions { DfsPreference = DfsPreference.IncludeWithPenalty };
        var aps = new List<AccessPointSnapshot>
        {
            CreateAp("aa:bb:cc:dd:ee:01", "AP-1", RadioBand.Band2_4GHz, 1, width: 20),
            CreateAp("aa:bb:cc:dd:ee:02", "AP-2", RadioBand.Band2_4GHz, 1, width: 20),
            CreateAp("aa:bb:cc:dd:ee:03", "AP-3", RadioBand.Band2_4GHz, 1, width: 20)
        };
        var graph = _service.BuildInterferenceGraph(aps, RadioBand.Band2_4GHz, null, null, reg, options);
        for (int a = 0; a < 3; a++)
            for (int b = 0; b < 3; b++)
                if (a != b) { graph.InternalWeights[a, b] = 1.0; graph.DirectionalWeights[a, b] = 1.0; }
        for (int a = 0; a < 3; a++)
        {
            graph.ExternalLoad[a] = new() { { 1, 2.0 }, { 6, 2.0 }, { 11, 2.0 } };
            graph.DirectlyObservedChannels[a] = new() { 1, 6, 11 };
        }

        var plan = _service.Optimize(graph, RadioBand.Band2_4GHz, reg, options);

        var channels = plan.Recommendations.Select(r => r.RecommendedChannel).Distinct().ToList();
        channels.Should().HaveCountGreaterThan(1,
            "clustered APs must still be spread across 2.4 GHz channels despite the crowding friction");
    }

    [Fact]
    public void ScoreAssignment_WideNeighbor_StepsOnAdjacentChannel()
    {
        // A 40 MHz neighbor on 2.4 GHz ch11 spectrally covers ch6 too (span 7-14 vs ch6's 4-8), so
        // its load must count against a candidate ch6, not just its control channel. A 20 MHz
        // neighbor on ch11 does not reach ch6. And crucially, 20 MHz neighbors that share the wide
        // neighbor's control channel must NOT be dragged into spilling along with it.
        var reg = StdUsRegulatory();
        var options = new RecommendationOptions();
        var aps = new List<AccessPointSnapshot>
        {
            CreateAp("aa:bb:cc:dd:ee:01", "AP", RadioBand.Band2_4GHz, 11, width: 20)
        };

        InterferenceGraph Build(params (int Channel, int Width, double Weight)[] neighbors)
        {
            var g = _service.BuildInterferenceGraph(aps, RadioBand.Band2_4GHz, null, null, reg, options);
            g.ExternalLoad[0] = new();
            g.ExternalNeighbors[0] = new();
            foreach (var (ch, w, weight) in neighbors)
            {
                g.ExternalLoad[0][ch] = g.ExternalLoad[0].GetValueOrDefault(ch) + weight;
                g.ExternalNeighbors[0][(ch, w)] = g.ExternalNeighbors[0].GetValueOrDefault((ch, w)) + weight;
            }
            g.DirectlyObservedChannels[0] = new() { 11 };
            return g;
        }

        var ch6Narrow = _service.ScoreAssignment(Build((11, 20, 1.0)), new[] { (6, 20) }, RadioBand.Band2_4GHz);
        var ch6Wide = _service.ScoreAssignment(Build((11, 40, 1.0)), new[] { (6, 20) }, RadioBand.Band2_4GHz);

        ch6Wide.Should().BeGreaterThan(ch6Narrow,
            "a 40 MHz neighbor on ch11 steps on ch6, so it raises ch6's score; a 20 MHz one does not");
        (ch6Wide - ch6Narrow).Should().BeApproximately(1.0, 0.01,
            "only the wide neighbor's weight reaches ch6 once spectral width is accounted for");

        // The fix for the over-spill: a pile of 20 MHz neighbors on ch11 plus ONE 40 MHz neighbor
        // there must spill only the 40 MHz neighbor's weight into ch6 - the 20 MHz weight stays put.
        var ch6Mixed = _service.ScoreAssignment(
            Build((11, 20, 5.0), (11, 40, 1.0)), new[] { (6, 20) }, RadioBand.Band2_4GHz);
        (ch6Mixed - ch6Narrow).Should().BeApproximately(1.0, 0.01,
            "only the 40 MHz neighbor (1.0) spills to ch6; the 5.0 of 20 MHz neighbors on ch11 do not");
    }

    [Fact]
    public void ScoreAssignment_MeasuredFloor_RaisesUnderstatedCandidate_ButNeverDiscountsKnownBssids()
    {
        // The measured floor applies to CANDIDATE channels (a move-to). AP currently on ch36; we
        // score it on the candidate ch40. Floor: the scan barely registers ch40 (0.5 proxy) but the
        // radio measures it 50% busy, so congestion is RAISED to the measurement. Not a cap: a
        // candidate with many KNOWN BSSIDs (proxy 8.0) that scans idle (5%) keeps its proxy -
        // detected BSSIDs are real and the scan is only a reference.
        var reg = StdUsRegulatory();
        var options = new RecommendationOptions();

        InterferenceGraph Build(double externalLoad, int scanUtil)
        {
            var g = SingleApGraph(36, externalLoad: new() { { 40, externalLoad } }, directlyObserved: new(), reg, options);
            g.ScanChannelData[0] = new Dictionary<(int Channel, int Width), (int Utilization, int? NoiseFloor)> { { (40, 80), (scanUtil, (int?)null) } };
            return g;
        }

        var understated = _service.ScoreAssignment(Build(0.5, 50), new[] { (40, 80) }, RadioBand.Band5GHz);
        var knownButIdle = _service.ScoreAssignment(Build(8.0, 5), new[] { (40, 80) }, RadioBand.Band5GHz);

        understated.Should().BeGreaterThan(2.0,
            "an under-stated candidate is floored up to the measured 50% airtime (~2.5 at the 0.05 scale), not left at the 0.5 proxy");
        knownButIdle.Should().BeGreaterThan(7.0,
            "the 8.0 of known BSSIDs is kept - a one-shot idle scan never discounts detected APs");
    }

    [Fact]
    public void MeasuredFloor_AppliesToCandidateNotCurrentChannel_OwnUtilizationBiasRemoved()
    {
        // The own-scan utilization floor must NOT inflate the AP's CURRENT channel - its scan radio
        // also hears its own serving traffic, which follows it anywhere. Same measured 60% util: on a
        // candidate it floors the score up; on the current channel it does not.
        var reg = StdUsRegulatory();
        var options = new RecommendationOptions();

        InterferenceGraph Build(int scanChannel)
        {
            var g = SingleApGraph(36, externalLoad: new() { { 36, 0.5 }, { 40, 0.5 } }, directlyObserved: new(), reg, options);
            g.ScanChannelData[0] = new Dictionary<(int Channel, int Width), (int Utilization, int? NoiseFloor)> { { (scanChannel, 80), (60, (int?)null) } };
            return g;
        }

        var current = _service.ScoreAssignment(Build(36), new[] { (36, 80) }, RadioBand.Band5GHz);
        var candidate = _service.ScoreAssignment(Build(40), new[] { (40, 80) }, RadioBand.Band5GHz);

        candidate.Should().BeGreaterThan(current + 1.5,
            "60% measured util floors a candidate channel (~3.0) but not the current channel, whose airtime is the AP's own traffic");
    }

    [Fact]
    public void ScoreAssignment_NoiseFloor_PenalizesNoisyChannelOverQuietOne()
    {
        // Two candidates, identical low utilization, differing only in measured noise floor: the
        // noisy one (-50 dBm) must score worse than the pristine one (-95 dBm). Utilization alone
        // would tie them - the noise floor is the RF-energy signal that breaks the tie.
        var reg = StdUsRegulatory();
        var options = new RecommendationOptions();

        InterferenceGraph Build(int noiseFloor)
        {
            var g = SingleApGraph(36, externalLoad: new() { { 40, 0.5 } }, directlyObserved: new(), reg, options);
            g.ScanChannelData[0] = new Dictionary<(int Channel, int Width), (int Utilization, int? NoiseFloor)> { { (40, 80), (5, (int?)noiseFloor) } };
            return g;
        }

        var noisy = _service.ScoreAssignment(Build(-50), new[] { (40, 80) }, RadioBand.Band5GHz);
        var quiet = _service.ScoreAssignment(Build(-95), new[] { (40, 80) }, RadioBand.Band5GHz);

        noisy.Should().BeGreaterThan(quiet + 1.5,
            "a high noise floor (RF energy that utilization misses) penalizes the channel even at equal airtime");
    }

    [Fact]
    public void ScanOverSpan_AggregatesSubChannels_CatchesNoiseAnywhereInTheSpan()
    {
        // A 160 MHz channel's badness can sit on a non-control 20 MHz sub-channel. With BW20 buckets
        // the scorer must aggregate across the whole span (noise floor = worst sub-channel), not read
        // only the control bucket - else a clean control channel would hide a noisy bonded sub-channel.
        var reg = StdUsRegulatory();
        var options = new RecommendationOptions();

        InterferenceGraph Build(int noisySubChannel)
        {
            var g = SingleApGraph(100, externalLoad: new(), directlyObserved: new(), reg, options);
            var scan = new Dictionary<(int Channel, int Width), (int Utilization, int? NoiseFloor)>();
            foreach (var ch in new[] { 36, 40, 44, 48, 52, 56, 60, 64 })
                scan[(ch, 20)] = (0, ch == noisySubChannel ? -50 : -96);
            g.ScanChannelData[0] = scan;
            return g;
        }

        var noisyOnControl = _service.ScoreAssignment(Build(36), new[] { (36, 160) }, RadioBand.Band5GHz);
        var noisyOnSubChannel = _service.ScoreAssignment(Build(56), new[] { (36, 160) }, RadioBand.Band5GHz);

        noisyOnSubChannel.Should().BeApproximately(noisyOnControl, 0.01,
            "noise floor aggregates as the worst sub-channel across the 160 span, so it's caught whether on the control channel or a bonded sub-channel");
        noisyOnSubChannel.Should().BeGreaterThan(1.5, "a -50 dBm sub-channel meaningfully penalizes the 160 MHz channel");
    }

    [Fact]
    public void CurrentChannel_ScoredFromSiblingVantage_NotSelfContaminatedReading()
    {
        // An AP's own scan of its CURRENT channel is contaminated by traffic that follows it (serving
        // load / a mesh uplink). The scorer must read the current channel from a clean off-channel
        // sibling instead. Here Subject (ch36) reads its own ch36 noisy (-40, self), but Sibling (on
        // ch149, off ch36) sees ch36 clean (-90). Subject's score should reflect the clean -90.
        var reg = StdUsRegulatory();
        var options = new RecommendationOptions();
        var aps = new List<AccessPointSnapshot>
        {
            CreateAp("aa:bb:cc:dd:ee:01", "Subject", RadioBand.Band5GHz, 36),
            CreateAp("aa:bb:cc:dd:ee:02", "Sibling", RadioBand.Band5GHz, 149)
        };
        var assignment = new[] { (36, 80), (149, 80) };

        var withSiblingView = _service.BuildInterferenceGraph(aps, RadioBand.Band5GHz, null, null, reg, options);
        withSiblingView.ScanChannelData[0] = new() { { (36, 80), (0, (int?)(-40)) } }; // self-contaminated
        withSiblingView.ScanChannelData[1] = new() { { (36, 80), (0, (int?)(-90)) } }; // sibling's clean view of ch36
        var crossVantage = _service.ScoreAssignment(withSiblingView, assignment, RadioBand.Band5GHz);

        var noSiblingView = _service.BuildInterferenceGraph(aps, RadioBand.Band5GHz, null, null, reg, options);
        noSiblingView.ScanChannelData[0] = new() { { (36, 80), (0, (int?)(-40)) } }; // only the self read exists
        var selfContaminated = _service.ScoreAssignment(noSiblingView, assignment, RadioBand.Band5GHz);

        crossVantage.Should().BeLessThan(selfContaminated - 1.0,
            "the current channel is scored from the sibling's clean -90 view; only when no sibling has a view does it fall back to the self-contaminated -40");
    }

    [Fact]
    public void Optimize_RecommendedNetworkScore_NeverWorseThanCurrent()
    {
        // Invariant: the engine must never recommend a plan that RAISES (worsens) the network score.
        // A per-AP fallback move can help the mover but collide with a sibling and worsen the network
        // (the Front Yard ch1->ch6 case, where sum-of-ScoreAp called a net-worsening move "positive").
        // The fallback's network-objective net check + the global guardrail must prevent that.
        var reg = StdUsRegulatory();
        var options = new RecommendationOptions();
        var aps = new List<AccessPointSnapshot>
        {
            CreateAp("aa:bb:cc:dd:ee:01", "A", RadioBand.Band2_4GHz, 1),
            CreateAp("aa:bb:cc:dd:ee:02", "B", RadioBand.Band2_4GHz, 6),
            CreateAp("aa:bb:cc:dd:ee:03", "C", RadioBand.Band2_4GHz, 11)
        };
        var graph = _service.BuildInterferenceGraph(aps, RadioBand.Band2_4GHz, null, null, reg, options);
        graph.ExternalLoad[0] = new() { { 1, 4.0 } };  // A's current channel is loaded - it wants to move
        graph.ExternalLoad[1] = new() { { 6, 1.0 } };
        graph.ExternalLoad[2] = new() { { 11, 1.0 } };

        var plan = _service.Optimize(graph, RadioBand.Band2_4GHz, reg, options);

        plan.RecommendedNetworkScore.Should().BeLessThanOrEqualTo(plan.CurrentNetworkScore + 1e-6,
            "the engine must never recommend a plan that raises the network score");
    }

    [Fact]
    public void Optimize_MeasuredComfortableCurrentChannel_NotChurnedForOwnBenefit()
    {
        // The reported class: the external neighbor scan inflates a channel that the AP's own radio
        // measures as externally quiet (low 1d/7d interference). Without the measured-comfort anchor
        // the optimizer moves the AP off it; with it, a lone AP (no sibling to declutter) stays put.
        var reg = StdUsRegulatory();
        var options = new RecommendationOptions { DfsPreference = DfsPreference.IncludeWithPenalty };
        var graph = SingleApGraph(52,
            externalLoad: new() { { 52, 6.0 }, { 36, 0.0 } }, // scan says ch52 busy, ch36 clean
            directlyObserved: new() { 52, 36 },
            reg, options);
        // ...but the radio measured ch52 at only 10% external interference (< the comfortable bar).
        graph.Nodes[0].HistoricalStress = new Dictionary<int, (double Utilization, double Interference, double TxRetryPct)>
        {
            { 52, (15.0, 10.0, 2.0) }
        };

        var plan = _service.Optimize(graph, RadioBand.Band5GHz, reg, options);

        var subject = plan.Recommendations.Single();
        subject.RecommendedChannel.Should().Be(52,
            "the radio measures ch52 as externally quiet, so it must not be churned off it on the neighbor scan alone");
        subject.IsChanged.Should().BeFalse();
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

    // --- Soak-period suppression ---

    /// <summary>
    /// A single 5 GHz AP with heavy measured stress on its current channel. Historical stress
    /// of X% yields roughly X/100 * (3.0 + 1.5 + 1.0) on the current channel, so ~60% lands
    /// the score in the move window (2.0-4.0) while staying below the measured external
    /// interference threshold (70%) that lifts soak; 90% trips it.
    /// </summary>
    private InterferenceGraph BuildStressedSingleApGraph(double stressPct, ChannelSoakInfo? soak)
    {
        var aps = new List<AccessPointSnapshot>
        {
            CreateAp("aa:bb:cc:dd:ee:01", "AP-1", RadioBand.Band5GHz, 36)
        };
        var stress = new Dictionary<string, Dictionary<int, (double, double, double)>>
        {
            ["aa:bb:cc:dd:ee:01"] = new() { [36] = (stressPct, stressPct, stressPct) }
        };
        var soakInfo = soak != null
            ? new Dictionary<string, ChannelSoakInfo> { ["aa:bb:cc:dd:ee:01"] = soak }
            : null;

        return _service.BuildInterferenceGraph(
            aps, RadioBand.Band5GHz, null, null, null, null, stress, soakInfo);
    }

    [Fact]
    public void Optimize_SoakedChannels_NotRecommended()
    {
        // Leave exactly one non-soaked escape channel; the stressed AP must move there and
        // report what was suppressed. 60% interference sits in the move window while staying
        // below the 70% escape threshold, so suppression stays active.
        var probeGraph = BuildStressedSingleApGraph(60, soak: null);
        var validChannels = probeGraph.Nodes[0].ValidChannels;
        // The escape must not share ch36's bonding block, or the measured stress (applied by
        // span overlap) follows the AP there and no move clears the improvement gates.
        var currentSpan = ChannelSpanHelper.GetChannelSpan(RadioBand.Band5GHz, 36, 80);
        var escapeChannel = validChannels.First(ch =>
            !ChannelSpanHelper.SpansOverlap(currentSpan, ChannelSpanHelper.GetChannelSpan(RadioBand.Band5GHz, ch, 80)));
        var soakedValidChannels = validChannels.Where(ch => ch != 36 && ch != escapeChannel).ToHashSet();
        var soak = new ChannelSoakInfo
        {
            // 9999 is not a valid candidate - it must not be reported as soak-suppressed
            // (it was never removed by the soak filter).
            SoakedChannels = soakedValidChannels.Append(9999).ToHashSet(),
            LastChangeAt = DateTimeOffset.UtcNow.AddDays(-1),
            SoakEndsAt = DateTimeOffset.UtcNow.AddDays(6)
        };

        var graph = BuildStressedSingleApGraph(60, soak);
        var plan = _service.Optimize(graph, RadioBand.Band5GHz, null);

        var rec = plan.Recommendations[0];
        rec.CurrentScore.Should().BeInRange(2.0, 4.0,
            "the test relies on a stressed-but-not-catastrophic AP so suppression stays active");
        rec.IsChanged.Should().BeTrue("heavy measured stress on ch36 justifies a move");
        rec.RecommendedChannel.Should().Be(escapeChannel,
            "every other channel is soaking and must not be recommended");
        rec.IsSoaking.Should().BeTrue();
        rec.SoakSuppressedChannels.Should().BeEquivalentTo(soakedValidChannels,
            "only channels actually removed from the candidate set are reported");
        rec.SoakEndsAt.Should().Be(soak.SoakEndsAt);
    }

    [Fact]
    public void Optimize_SoakSuppression_LiftedForCatastrophicAp()
    {
        // Soak EVERY alternative channel. An AP whose MEASURED external interference on the new
        // channel is catastrophic (90%, past the 70% threshold) must still escape - soak
        // prevents churn, not rescue.
        var probeGraph = BuildStressedSingleApGraph(90, soak: null);
        var soak = new ChannelSoakInfo
        {
            SoakedChannels = probeGraph.Nodes[0].ValidChannels.Where(ch => ch != 36).ToHashSet(),
            LastChangeAt = DateTimeOffset.UtcNow.AddDays(-1),
            SoakEndsAt = DateTimeOffset.UtcNow.AddDays(6)
        };

        var graph = BuildStressedSingleApGraph(90, soak);
        var plan = _service.Optimize(graph, RadioBand.Band5GHz, null);

        var rec = plan.Recommendations[0];
        rec.IsChanged.Should().BeTrue("a measurably suffering AP must be allowed to leave despite soak");
        rec.IsSoaking.Should().BeFalse("lifted suppression is not reported as active");
    }

    [Fact]
    public void Optimize_SoakHolds_WhenScoreInflatedButMeasuredAirtimeFine()
    {
        // The dense-band failure mode the escape is anchored against: idle-neighbor external
        // load can push the INFERRED score far past any absolute ceiling on every AP forever,
        // while the radio's own measured airtime is fine. Soak must hold - an inferred-score
        // escape would make soak a permanent no-op exactly where it matters most.
        var probe = BuildStressedSingleApGraph(30, soak: null);
        var soakedChannel = probe.Nodes[0].ValidChannels.First(ch => ch != 36);
        var soak = new ChannelSoakInfo
        {
            SoakedChannels = new HashSet<int> { soakedChannel },
            LastChangeAt = DateTimeOffset.UtcNow.AddHours(-2),
            SoakEndsAt = DateTimeOffset.UtcNow.AddHours(14)
        };

        var graph = BuildStressedSingleApGraph(30, soak);
        foreach (var ch in graph.Nodes[0].ValidChannels)
            graph.ExternalLoad[0][ch] = 5.0;

        var plan = _service.Optimize(graph, RadioBand.Band5GHz, null);

        var rec = plan.Recommendations[0];
        rec.CurrentScore.Should().BeGreaterThanOrEqualTo(4.0,
            "the test relies on an inferred score past the old absolute ceiling");
        rec.IsSoaking.Should().BeTrue(
            "measured airtime is fine, so an inflated inferred score must not lift the soak");
        rec.SoakSuppressedChannels.Should().Contain(soakedChannel);
    }

    [Fact]
    public void Optimize_SoakWouldEmptyCandidates_InvalidChannelApStillMoves()
    {
        // A 2.4 GHz AP on non-standard ch3 must always move to 1/6/11. Even if all three are
        // soaking, the empty-candidate guard keeps them available.
        var aps = new List<AccessPointSnapshot>
        {
            CreateAp("aa:bb:cc:dd:ee:01", "AP-1", RadioBand.Band2_4GHz, 3, width: 20)
        };
        var soakInfo = new Dictionary<string, ChannelSoakInfo>
        {
            ["aa:bb:cc:dd:ee:01"] = new ChannelSoakInfo
            {
                SoakedChannels = new HashSet<int> { 1, 6, 11 },
                LastChangeAt = DateTimeOffset.UtcNow.AddDays(-1),
                SoakEndsAt = DateTimeOffset.UtcNow.AddDays(6)
            }
        };

        var graph = _service.BuildInterferenceGraph(
            aps, RadioBand.Band2_4GHz, null, null, null, null, null, soakInfo);
        var plan = _service.Optimize(graph, RadioBand.Band2_4GHz, null);

        var rec = plan.Recommendations[0];
        rec.RecommendedChannel.Should().BeOneOf(new[] { 1, 6, 11 },
            "an AP on an invalid channel must still be moved somewhere valid");
    }

    [Fact]
    public void Optimize_MeshChildSoak_GatesLeaderCandidates()
    {
        // A mesh group moves as one: the leader is stressed enough to want a move, but the
        // CHILD is soaking on every alternative channel. The leader must stay put - otherwise
        // the child-sync would hop the child straight back onto a channel it just left.
        // In the move window, below the 70% external interference escape threshold.
        var leaderStress = new Dictionary<string, Dictionary<int, (double, double, double)>>
        {
            ["aa:bb:cc:dd:ee:01"] = new() { [36] = (60d, 60d, 60d) }
        };
        List<AccessPointSnapshot> BuildAps() => new()
        {
            CreateAp("aa:bb:cc:dd:ee:01", "Leader", RadioBand.Band5GHz, 36),
            CreateAp("aa:bb:cc:dd:ee:02", "Child", RadioBand.Band5GHz, 36,
                isMeshChild: true, meshParentMac: "aa:bb:cc:dd:ee:01",
                meshUplinkBand: RadioBand.Band5GHz, meshUplinkChannel: 36)
        };

        var probeGraph = _service.BuildInterferenceGraph(
            BuildAps(), RadioBand.Band5GHz, null, null, null, null, leaderStress);
        var leaderChannels = probeGraph.Nodes.First(nd => nd.Name == "Leader").ValidChannels;

        var soakInfo = new Dictionary<string, ChannelSoakInfo>
        {
            ["aa:bb:cc:dd:ee:02"] = new ChannelSoakInfo
            {
                SoakedChannels = leaderChannels.Where(ch => ch != 36).ToHashSet(),
                LastChangeAt = DateTimeOffset.UtcNow.AddDays(-1),
                SoakEndsAt = DateTimeOffset.UtcNow.AddDays(6)
            }
        };

        var graph = _service.BuildInterferenceGraph(
            BuildAps(), RadioBand.Band5GHz, null, null, null, null, leaderStress, soakInfo);
        var plan = _service.Optimize(graph, RadioBand.Band5GHz, null);

        var leaderRec = plan.Recommendations.First(r => r.ApName == "Leader");
        var childRec = plan.Recommendations.First(r => r.ApName == "Child");
        leaderRec.RecommendedChannel.Should().Be(36,
            "every alternative is soaked by the mesh child, and the group moves as one");
        childRec.RecommendedChannel.Should().Be(36);
        leaderRec.IsSoaking.Should().BeTrue("the child's soak gated the leader's candidates");
    }

    [Fact]
    public void Optimize_NoSoakInfo_BehaviorUnchanged()
    {
        var withSoakNull = BuildStressedSingleApGraph(60, soak: null);
        var plan = _service.Optimize(withSoakNull, RadioBand.Band5GHz, null);

        plan.Recommendations[0].IsSoaking.Should().BeFalse();
        plan.Recommendations[0].SoakEndsAt.Should().BeNull();
    }
}
