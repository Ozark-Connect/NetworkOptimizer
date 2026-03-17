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

        // -65 dBm → weight 0.625
        graph.InternalWeights[0, 1].Should().BeApproximately(0.625, 0.01);
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
        // Unplaced APs have internal weight 0.625. Neighbor at -55 dBm → weight 0.875.
        // Width matches AP (80=80), no width scaling.
        // Triangulated weight = 0.875 * 0.625 = 0.547
        graph.ExternalLoad[0][36].Should().BeApproximately(0.547, 0.05);

        // AP-2 (index 1) should also have direct external load on ch36
        graph.ExternalLoad[1].Should().ContainKey(36);
        graph.ExternalLoad[1][36].Should().BeApproximately(0.875, 0.05);
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
        // -60 dBm → 0.75, -70 dBm → 0.5, sum = 1.25
        graph.ExternalLoad[0][36].Should().BeApproximately(1.25, 0.05);
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

        // The stronger sighting (-55 dBm → weight 0.875) × proximity 0.625 = 0.547
        // beats the weaker sighting (-75 dBm → weight 0.375) × proximity 0.625 = 0.234
        graph.ExternalLoad[2][100].Should().BeApproximately(0.547, 0.05);
    }
}
