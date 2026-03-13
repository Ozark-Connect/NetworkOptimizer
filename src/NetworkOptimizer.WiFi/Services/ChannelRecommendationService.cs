using Microsoft.Extensions.Logging;
using NetworkOptimizer.WiFi.Helpers;
using NetworkOptimizer.WiFi.Models;

namespace NetworkOptimizer.WiFi.Services;

/// <summary>
/// Core engine for network-wide channel plan optimization.
/// Builds an interference graph from live data and optimizes channel assignments
/// using greedy + local search to minimize total weighted interference.
/// </summary>
public class ChannelRecommendationService
{
    private readonly PropagationService _propagationService;
    private readonly ILogger<ChannelRecommendationService> _logger;

    /// <summary>Default assumed signal for unplaced AP pairs (dBm)</summary>
    private const int DefaultUnplacedSignalDbm = -65;

    /// <summary>DFS penalty base (equivalent to a moderate neighbor)</summary>
    private const double DfsPenaltyBase = 0.5;

    /// <summary>Number of random restarts for optimization</summary>
    private const int RandomRestarts = 8;

    public ChannelRecommendationService(
        PropagationService propagationService,
        ILogger<ChannelRecommendationService> logger)
    {
        _propagationService = propagationService;
        _logger = logger;
    }

    /// <summary>
    /// Build the interference graph from live AP data, propagation context, and RF scan results.
    /// </summary>
    public InterferenceGraph BuildInterferenceGraph(
        List<AccessPointSnapshot> aps,
        RadioBand band,
        ApPropagationContext? propContext,
        List<ChannelScanResult>? scanResults,
        RegulatoryChannelData? regulatoryData,
        RecommendationOptions? options = null)
    {
        var opts = options ?? new RecommendationOptions();

        // Filter to APs with a radio on this band
        var bandAps = aps
            .Where(ap => ap.IsOnline && ap.Radios.Any(r => r.Band == band && r.Channel.HasValue))
            .ToList();

        var n = bandAps.Count;
        var graph = new InterferenceGraph
        {
            Nodes = new List<ApNode>(n),
            InternalWeights = new double[n, n],
            ExternalLoad = new Dictionary<int, double>[n],
            MeshConstraints = new List<MeshConstraint>()
        };

        // Build nodes
        for (int i = 0; i < n; i++)
        {
            var ap = bandAps[i];
            var radio = ap.Radios.First(r => r.Band == band && r.Channel.HasValue);
            var isPlaced = propContext?.ApsByMac.ContainsKey(ap.Mac.ToLowerInvariant()) == true;

            var validChannels = GetValidChannels(band, radio, regulatoryData, opts.DfsPreference);
            var currentWidth = radio.ChannelWidth ?? 20;

            graph.Nodes.Add(new ApNode
            {
                Mac = ap.Mac,
                Name = ap.Name,
                CurrentChannel = radio.Channel!.Value,
                CurrentWidth = currentWidth,
                ValidChannels = validChannels,
                ValidWidths = new[] { currentWidth }, // Locked to current for now
                IsPlaced = isPlaced,
                HasDfs = radio.HasDfs
            });

            graph.ExternalLoad[i] = new Dictionary<int, double>();
        }

        // Build pairwise internal interference weights
        var bandStr = band.ToPropagationBand();
        for (int i = 0; i < n; i++)
        {
            for (int j = i + 1; j < n; j++)
            {
                var weight = ComputeInternalWeight(
                    bandAps[i], bandAps[j], band, bandStr, propContext);
                graph.InternalWeights[i, j] = weight;
                graph.InternalWeights[j, i] = weight;
            }
        }

        // Build external load from RF scan data
        if (scanResults != null)
        {
            BuildExternalLoad(graph, bandAps, band, scanResults);
        }

        // Identify mesh constraints
        BuildMeshConstraints(graph, bandAps, band);

        _logger.LogDebug(
            "Built interference graph for {Band}: {NodeCount} APs, {MeshCount} mesh constraints",
            band, n, graph.MeshConstraints.Count);

        return graph;
    }

    /// <summary>
    /// Optimize channel plan for a given band using greedy + local search.
    /// </summary>
    public ChannelPlan Optimize(
        InterferenceGraph graph,
        RadioBand band,
        RegulatoryChannelData? regulatoryData,
        RecommendationOptions? options = null)
    {
        var opts = options ?? new RecommendationOptions();
        var n = graph.Nodes.Count;

        if (n == 0)
        {
            return new ChannelPlan { Band = band };
        }

        // Score current assignment
        var currentAssignment = new (int Channel, int Width)[n];
        for (int i = 0; i < n; i++)
            currentAssignment[i] = (graph.Nodes[i].CurrentChannel, graph.Nodes[i].CurrentWidth);

        var currentNetworkScore = ScoreAssignment(graph, currentAssignment, band);

        // Resolve mesh groups: mesh children get their leader's index
        ResolveMeshGroups(graph);

        // Find pinned AP indices
        var pinnedIndices = new HashSet<int>();
        if (opts.PinnedApMacs != null)
        {
            for (int i = 0; i < n; i++)
            {
                if (opts.PinnedApMacs.Contains(graph.Nodes[i].Mac))
                    pinnedIndices.Add(i);
            }
        }

        // Optimize
        (int Channel, int Width)[] bestAssignment;
        double bestScore;

        if (n <= 6 && GetMaxValidChannels(graph) <= 24)
        {
            // Small network: exhaustive search with pruning
            (bestAssignment, bestScore) = ExhaustiveSearch(graph, band, pinnedIndices, opts);
        }
        else
        {
            // Greedy + local search with random restarts
            (bestAssignment, bestScore) = GreedyLocalSearch(graph, band, pinnedIndices, opts);
        }

        // Build result
        var dfsChannels = regulatoryData?.DfsChannels ?? [];
        var dfsSet = new HashSet<int>(dfsChannels);

        var plan = new ChannelPlan
        {
            Band = band,
            CurrentNetworkScore = currentNetworkScore,
            RecommendedNetworkScore = bestScore,
            UnplacedApCount = graph.Nodes.Count(node => !node.IsPlaced),
            HasScanData = graph.ExternalLoad.Any(d => d.Count > 0)
        };

        for (int i = 0; i < n; i++)
        {
            var node = graph.Nodes[i];
            var currentApScore = ScoreAp(graph, currentAssignment, i, band);
            var recommendedApScore = ScoreAp(graph, bestAssignment, i, band);

            plan.Recommendations.Add(new ApChannelRecommendation
            {
                ApMac = node.Mac,
                ApName = node.Name,
                Band = band,
                CurrentChannel = node.CurrentChannel,
                CurrentWidth = node.CurrentWidth,
                RecommendedChannel = bestAssignment[i].Channel,
                RecommendedWidth = bestAssignment[i].Width,
                CurrentScore = currentApScore,
                RecommendedScore = recommendedApScore,
                IsMeshConstrained = node.MeshGroupLeader >= 0,
                IsUnplaced = !node.IsPlaced,
                IsDfsChannel = band == RadioBand.Band5GHz && dfsSet.Contains(bestAssignment[i].Channel)
            });
        }

        _logger.LogInformation(
            "Channel optimization for {Band}: score {Current:F2} -> {Recommended:F2} ({Improvement:F1}% improvement)",
            band, currentNetworkScore, bestScore, plan.ImprovementPercent);

        return plan;
    }

    /// <summary>
    /// Score a specific channel assignment. Lower is better.
    /// </summary>
    public double ScoreAssignment(
        InterferenceGraph graph,
        (int Channel, int Width)[] assignment,
        RadioBand band)
    {
        double score = 0;
        var n = graph.Nodes.Count;

        // Internal co-channel interference (count each pair once)
        for (int i = 0; i < n; i++)
        {
            for (int j = i + 1; j < n; j++)
            {
                // Skip mesh pairs - their co-channel interference is expected
                if (AreMeshPair(graph, i, j))
                    continue;

                var overlapFactor = ChannelSpanHelper.ComputeOverlapFactor(
                    band,
                    assignment[i].Channel, assignment[i].Width,
                    assignment[j].Channel, assignment[j].Width);

                score += graph.InternalWeights[i, j] * overlapFactor;
            }
        }

        // External interference
        for (int i = 0; i < n; i++)
        {
            var apSpan = ChannelSpanHelper.GetChannelSpan(band, assignment[i].Channel, assignment[i].Width);
            foreach (var (extChannel, extWeight) in graph.ExternalLoad[i])
            {
                // External neighbors are stored per primary channel with their width
                // For simplicity, treat external as 20 MHz and check overlap
                var extSpan = (Low: extChannel, High: extChannel);
                if (ChannelSpanHelper.SpansOverlap(apSpan, extSpan))
                    score += extWeight;
            }
        }

        return score;
    }

    /// <summary>
    /// Score a single AP's interference in a given assignment. Lower is better.
    /// </summary>
    private double ScoreAp(
        InterferenceGraph graph,
        (int Channel, int Width)[] assignment,
        int apIndex,
        RadioBand band)
    {
        double score = 0;
        var n = graph.Nodes.Count;

        // Internal interference from all other APs
        for (int j = 0; j < n; j++)
        {
            if (j == apIndex) continue;
            if (AreMeshPair(graph, apIndex, j)) continue;

            var overlapFactor = ChannelSpanHelper.ComputeOverlapFactor(
                band,
                assignment[apIndex].Channel, assignment[apIndex].Width,
                assignment[j].Channel, assignment[j].Width);

            score += graph.InternalWeights[apIndex, j] * overlapFactor;
        }

        // External interference
        var apSpan = ChannelSpanHelper.GetChannelSpan(band, assignment[apIndex].Channel, assignment[apIndex].Width);
        foreach (var (extChannel, extWeight) in graph.ExternalLoad[apIndex])
        {
            var extSpan = (Low: extChannel, High: extChannel);
            if (ChannelSpanHelper.SpansOverlap(apSpan, extSpan))
                score += extWeight;
        }

        return score;
    }

    private double ComputeInternalWeight(
        AccessPointSnapshot ap1, AccessPointSnapshot ap2,
        RadioBand band, string bandStr,
        ApPropagationContext? propContext)
    {
        var mac1 = ap1.Mac.ToLowerInvariant();
        var mac2 = ap2.Mac.ToLowerInvariant();

        if (propContext != null &&
            propContext.ApsByMac.TryGetValue(mac1, out var prop1) &&
            propContext.ApsByMac.TryGetValue(mac2, out var prop2))
        {
            // Both placed - use propagation model
            var radio1 = ap1.Radios.First(r => r.Band == band);
            var radio2 = ap2.Radios.First(r => r.Band == band);

            // Override TX power from live radio data
            var p1 = ClonePropAp(prop1, radio1);
            var p2 = ClonePropAp(prop2, radio2);

            // Pre-compute wall segments for relevant floors
            var segmentsByFloor = new Dictionary<int, List<PropagationService.WallSegment>>();
            foreach (var floor in new[] { p1.Floor, p2.Floor })
            {
                if (!segmentsByFloor.ContainsKey(floor) &&
                    propContext.WallsByFloor.TryGetValue(floor, out var walls))
                    segmentsByFloor[floor] = _propagationService.PrecomputeWallSegments(walls);
            }

            // Compute signal in both directions, use worst case
            var freqMhz = Data.MaterialAttenuation.GetCenterFrequencyMhz(bandStr);
            var signal1to2 = _propagationService.ComputeSignalAtPoint(
                p1, p2.Latitude, p2.Longitude, p2.Floor,
                bandStr, freqMhz, segmentsByFloor, propContext.Buildings);
            var signal2to1 = _propagationService.ComputeSignalAtPoint(
                p2, p1.Latitude, p1.Longitude, p1.Floor,
                bandStr, freqMhz, segmentsByFloor, propContext.Buildings);

            var worstSignal = (int)Math.Max(signal1to2, signal2to1);
            return ChannelSpanHelper.SignalToInterferenceWeight(worstSignal);
        }

        // One or both unplaced - use conservative default
        return ChannelSpanHelper.SignalToInterferenceWeight(DefaultUnplacedSignalDbm);
    }

    private static PropagationAp ClonePropAp(PropagationAp source, RadioSnapshot radio)
    {
        return new PropagationAp
        {
            Mac = source.Mac,
            Model = source.Model,
            Latitude = source.Latitude,
            Longitude = source.Longitude,
            Floor = source.Floor,
            TxPowerDbm = radio.TxPower ?? source.TxPowerDbm,
            AntennaGainDbi = radio.AntennaGain ?? source.AntennaGainDbi,
            OrientationDeg = source.OrientationDeg,
            MountType = source.MountType,
            AntennaMode = source.AntennaMode
        };
    }

    private void BuildExternalLoad(
        InterferenceGraph graph,
        List<AccessPointSnapshot> bandAps,
        RadioBand band,
        List<ChannelScanResult> scanResults)
    {
        // Map AP MAC to graph index
        var macToIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < bandAps.Count; i++)
            macToIndex[bandAps[i].Mac] = i;

        foreach (var scan in scanResults.Where(s => s.Band == band))
        {
            if (!macToIndex.TryGetValue(scan.ApMac, out var apIndex))
                continue;

            foreach (var neighbor in scan.Neighbors.Where(n => !n.IsOwnNetwork && n.Signal.HasValue))
            {
                var weight = ChannelSpanHelper.SignalToInterferenceWeight(neighbor.Signal!.Value);
                var channel = neighbor.Channel;

                if (!graph.ExternalLoad[apIndex].ContainsKey(channel))
                    graph.ExternalLoad[apIndex][channel] = 0;
                graph.ExternalLoad[apIndex][channel] += weight;
            }
        }
    }

    private static void BuildMeshConstraints(
        InterferenceGraph graph,
        List<AccessPointSnapshot> bandAps,
        RadioBand band)
    {
        var macToIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < bandAps.Count; i++)
            macToIndex[bandAps[i].Mac] = i;

        foreach (var ap in bandAps)
        {
            if (!ap.IsMeshChild || string.IsNullOrEmpty(ap.MeshParentMac))
                continue;
            if (ap.MeshUplinkBand != band)
                continue;

            if (macToIndex.TryGetValue(ap.Mac, out var childIdx) &&
                macToIndex.TryGetValue(ap.MeshParentMac, out var parentIdx))
            {
                graph.MeshConstraints.Add(new MeshConstraint
                {
                    ParentIndex = parentIdx,
                    ChildIndex = childIdx,
                    UplinkBand = band
                });
            }
        }
    }

    private static void ResolveMeshGroups(InterferenceGraph graph)
    {
        foreach (var constraint in graph.MeshConstraints)
        {
            // The parent is the group leader
            var leader = constraint.ParentIndex;

            // If parent already has a leader, use that (chain case)
            if (graph.Nodes[leader].MeshGroupLeader >= 0)
                leader = graph.Nodes[leader].MeshGroupLeader;

            graph.Nodes[constraint.ChildIndex].MeshGroupLeader = leader;

            // Ensure parent also knows it's a leader (mark with self-reference)
            if (graph.Nodes[leader].MeshGroupLeader < 0)
                graph.Nodes[leader].MeshGroupLeader = leader;
        }
    }

    private static bool AreMeshPair(InterferenceGraph graph, int i, int j)
    {
        return graph.MeshConstraints.Any(c =>
            (c.ParentIndex == i && c.ChildIndex == j) ||
            (c.ParentIndex == j && c.ChildIndex == i));
    }

    private int[] GetValidChannels(
        RadioBand band, RadioSnapshot radio,
        RegulatoryChannelData? regulatoryData,
        DfsPreference dfsPref)
    {
        var width = radio.ChannelWidth ?? 20;

        if (regulatoryData != null)
        {
            bool includeDfs = dfsPref != DfsPreference.Exclude &&
                              (band != RadioBand.Band5GHz || radio.HasDfs);
            var channels = regulatoryData.GetChannels(band, width, includeDfs);
            if (channels.Length > 0)
                return channels;
        }

        // Fallback defaults
        return band switch
        {
            RadioBand.Band2_4GHz => [1, 6, 11],
            RadioBand.Band5GHz => [36, 40, 44, 48, 149, 153, 157, 161, 165],
            RadioBand.Band6GHz => [1, 5, 9, 13, 17, 21, 25, 29, 33, 37, 41, 45, 49, 53, 57, 61],
            _ => []
        };
    }

    private static int GetMaxValidChannels(InterferenceGraph graph) =>
        graph.Nodes.Max(n => n.ValidChannels.Length);

    private (int Channel, int Width)[] ApplyMeshConstraints(
        InterferenceGraph graph,
        (int Channel, int Width)[] assignment)
    {
        foreach (var constraint in graph.MeshConstraints)
        {
            // Child gets parent's channel
            assignment[constraint.ChildIndex] = assignment[constraint.ParentIndex];
        }
        return assignment;
    }

    private double AddDfsPenalty(
        InterferenceGraph graph,
        (int Channel, int Width)[] assignment,
        RadioBand band,
        DfsPreference dfsPref,
        double baseScore)
    {
        if (band != RadioBand.Band5GHz || dfsPref == DfsPreference.Prefer)
            return baseScore;

        if (dfsPref == DfsPreference.Exclude)
            return baseScore; // DFS channels already excluded from valid set

        // IncludeWithPenalty
        double penalty = 0;
        for (int i = 0; i < assignment.Length; i++)
        {
            var ch = assignment[i].Channel;
            // DFS range: 52-64 (UNII-2), 100-144 (UNII-2C)
            if ((ch >= 52 && ch <= 64) || (ch >= 100 && ch <= 144))
            {
                // Conservative confidence for now (no DFS event history available)
                double confidence = 0.7;
                penalty += DfsPenaltyBase * (1 - confidence);
            }
        }

        return baseScore + penalty;
    }

    private (( int Channel, int Width)[] Assignment, double Score) ExhaustiveSearch(
        InterferenceGraph graph,
        RadioBand band,
        HashSet<int> pinnedIndices,
        RecommendationOptions opts)
    {
        var n = graph.Nodes.Count;
        var bestAssignment = new (int Channel, int Width)[n];
        var currentAssignment = new (int Channel, int Width)[n];
        var bestScore = double.MaxValue;

        // Initialize with current
        for (int i = 0; i < n; i++)
        {
            bestAssignment[i] = (graph.Nodes[i].CurrentChannel, graph.Nodes[i].CurrentWidth);
            currentAssignment[i] = bestAssignment[i];
        }

        // Get ordered indices (mesh leaders first, then non-mesh, skip mesh children)
        var searchIndices = GetSearchIndices(graph, pinnedIndices);

        void Search(int depth)
        {
            if (depth >= searchIndices.Count)
            {
                // Apply mesh constraints
                var withMesh = ((int Channel, int Width)[])currentAssignment.Clone();
                ApplyMeshConstraints(graph, withMesh);

                var score = ScoreAssignment(graph, withMesh, band);
                score = AddDfsPenalty(graph, withMesh, band, opts.DfsPreference, score);

                if (score < bestScore)
                {
                    bestScore = score;
                    Array.Copy(withMesh, bestAssignment, n);
                }
                return;
            }

            var apIdx = searchIndices[depth];
            var node = graph.Nodes[apIdx];

            foreach (var ch in node.ValidChannels)
            {
                foreach (var w in node.ValidWidths)
                {
                    currentAssignment[apIdx] = (ch, w);
                    Search(depth + 1);
                }
            }
        }

        Search(0);

        _logger.LogDebug("Exhaustive search for {Band}: best score {Score:F2}", band, bestScore);

        return (bestAssignment, bestScore);
    }

    private ((int Channel, int Width)[] Assignment, double Score) GreedyLocalSearch(
        InterferenceGraph graph,
        RadioBand band,
        HashSet<int> pinnedIndices,
        RecommendationOptions opts)
    {
        var n = graph.Nodes.Count;
        var bestAssignment = new (int Channel, int Width)[n];
        var bestScore = double.MaxValue;
        var rng = new Random(42); // Deterministic for reproducibility

        var searchIndices = GetSearchIndices(graph, pinnedIndices);

        for (int restart = 0; restart < RandomRestarts; restart++)
        {
            var assignment = new (int Channel, int Width)[n];

            // Initialize pinned APs
            for (int i = 0; i < n; i++)
                assignment[i] = (graph.Nodes[i].CurrentChannel, graph.Nodes[i].CurrentWidth);

            // Greedy phase: assign APs in shuffled order (first restart uses constraint order)
            var order = restart == 0
                ? searchIndices.ToList()
                : searchIndices.OrderBy(_ => rng.Next()).ToList();

            foreach (var apIdx in order)
            {
                var node = graph.Nodes[apIdx];
                var bestCh = node.CurrentChannel;
                var bestW = node.CurrentWidth;
                var bestLocal = double.MaxValue;

                foreach (var ch in node.ValidChannels)
                {
                    foreach (var w in node.ValidWidths)
                    {
                        assignment[apIdx] = (ch, w);
                        ApplyMeshConstraints(graph, assignment);
                        var score = ScoreAssignment(graph, assignment, band);
                        if (score < bestLocal)
                        {
                            bestLocal = score;
                            bestCh = ch;
                            bestW = w;
                        }
                    }
                }

                assignment[apIdx] = (bestCh, bestW);
                ApplyMeshConstraints(graph, assignment);
            }

            // Local search (hill climbing)
            bool improved = true;
            int iterations = 0;
            while (improved && iterations < 100)
            {
                improved = false;
                iterations++;

                foreach (var apIdx in searchIndices)
                {
                    var node = graph.Nodes[apIdx];
                    var currentScore = ScoreAssignment(graph, assignment, band);

                    foreach (var ch in node.ValidChannels)
                    {
                        foreach (var w in node.ValidWidths)
                        {
                            if (ch == assignment[apIdx].Channel && w == assignment[apIdx].Width)
                                continue;

                            var saved = assignment[apIdx];
                            assignment[apIdx] = (ch, w);
                            ApplyMeshConstraints(graph, assignment);
                            var newScore = ScoreAssignment(graph, assignment, band);

                            if (newScore < currentScore)
                            {
                                currentScore = newScore;
                                improved = true;
                            }
                            else
                            {
                                assignment[apIdx] = saved;
                                ApplyMeshConstraints(graph, assignment);
                            }
                        }
                    }
                }
            }

            var finalScore = ScoreAssignment(graph, assignment, band);
            finalScore = AddDfsPenalty(graph, assignment, band, opts.DfsPreference, finalScore);

            if (finalScore < bestScore)
            {
                bestScore = finalScore;
                Array.Copy(assignment, bestAssignment, n);
            }
        }

        _logger.LogDebug("Greedy+local search for {Band}: best score {Score:F2} over {Restarts} restarts",
            band, bestScore, RandomRestarts);

        return (bestAssignment, bestScore);
    }

    /// <summary>
    /// Get indices of APs that should be searched (excludes pinned and mesh children).
    /// Orders by most-constrained-first (most interference edges).
    /// </summary>
    private static List<int> GetSearchIndices(InterferenceGraph graph, HashSet<int> pinnedIndices)
    {
        var n = graph.Nodes.Count;
        var meshChildren = new HashSet<int>(
            graph.MeshConstraints.Select(c => c.ChildIndex));

        return Enumerable.Range(0, n)
            .Where(i => !pinnedIndices.Contains(i) && !meshChildren.Contains(i))
            .OrderByDescending(i =>
            {
                // Count total interference weight (most constrained first)
                double total = 0;
                for (int j = 0; j < n; j++)
                    if (j != i) total += graph.InternalWeights[i, j];
                return total;
            })
            .ToList();
    }
}
