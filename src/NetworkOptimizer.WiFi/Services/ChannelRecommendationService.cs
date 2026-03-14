using System.Text;
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

    /// <summary>Weight multiplier for channel scan utilization in scoring (0-1 scale)</summary>
    private const double ScanUtilizationWeight = 0.02;

    /// <summary>Weight multiplier for channel scan interference in scoring (0-1 scale)</summary>
    private const double ScanInterferenceWeight = 0.03;

    /// <summary>
    /// Weight for TX retry stress penalty. High TX retries indicate the external load
    /// score is underestimating real interference on the current channel.
    /// Applied to channels overlapping the AP's current channel span.
    /// </summary>
    private const double TxRetryStressWeight = 3.0;

    /// <summary>
    /// Weight for channel utilization stress penalty.
    /// High utilization means the channel is congested.
    /// </summary>
    private const double UtilizationStressWeight = 1.0;

    /// <summary>
    /// Weight for interference stress penalty.
    /// High interference from radio stats means non-WiFi interference on channel.
    /// </summary>
    private const double InterferenceStressWeight = 1.5;

    /// <summary>
    /// Minimum radio stat threshold to be considered "stressed".
    /// Values below this (e.g., 1% utilization) are noise, not real stress.
    /// </summary>
    private const double StressMinThreshold = 5.0;

    /// <summary>
    /// Minimum average score improvement per AP to recommend changes.
    /// Scales with network size: a 4-AP network needs 1.0 total improvement,
    /// a 50-AP network needs 12.5. Prevents recommending changes when
    /// interference is already negligible.
    /// </summary>
    private const double MinAvgImprovementPerAp = 0.25;

    /// <summary>
    /// Minimum current score for an AP to be worth moving. APs with scores
    /// below this have negligible interference and shouldn't be disrupted.
    /// After optimization, APs below this threshold are reverted to their
    /// current channel assignment.
    /// </summary>
    private const double MinApScoreToMove = 0.5;

    /// <summary>
    /// Penalty for channels with no historical data. Unknown channels carry more
    /// risk than channels we have measured data for, so they shouldn't score as
    /// perfect (0.0). Applied per-AP when historical stress data exists for the AP
    /// but not for the candidate channel.
    /// </summary>
    private const double UnknownChannelPenalty = 0.15;

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
        RecommendationOptions? options = null,
        Dictionary<string, Dictionary<int, (double Utilization, double Interference, double TxRetryPct)>>? historicalStress = null)
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
            ScanChannelData = new Dictionary<int, (int Utilization, int Interference)>[n],
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

            var macLower = ap.Mac.ToLowerInvariant();

            // Per-channel historical stress from 30-day metrics + channel change events
            Dictionary<int, (double, double, double)>? apHistStress = null;
            if (historicalStress != null)
                historicalStress.TryGetValue(macLower, out apHistStress);

            graph.Nodes.Add(new ApNode
            {
                Mac = ap.Mac,
                Name = ap.Name,
                CurrentChannel = radio.Channel!.Value,
                CurrentWidth = currentWidth,
                ValidChannels = validChannels,
                ValidWidths = new[] { currentWidth }, // Locked to current for now
                IsPlaced = isPlaced,
                HasDfs = radio.HasDfs,
                ChannelUtilization = radio.ChannelUtilization ?? 0,
                Interference = radio.Interference ?? 0,
                TxRetriesPct = radio.TxRetriesPct ?? 0,
                HistoricalStress = apHistStress
            });

            graph.ExternalLoad[i] = new Dictionary<int, double>();
            graph.ScanChannelData[i] = new Dictionary<int, (int, int)>();
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
            graph.HasScanData = scanResults.Any(s => s.Band == band);
            BuildExternalLoad(graph, bandAps, band, scanResults);
            BuildScanChannelData(graph, bandAps, band, scanResults);
        }

        // Identify mesh constraints
        BuildMeshConstraints(graph, bandAps, band);

        // Propagate historical stress to nearby APs using propagation weights.
        // If Back Yard had 28% TX retries on ch36, nearby Front Yard would likely
        // experience similar issues on ch36, scaled by their proximity.
        PropagateHistoricalStress(graph, band);

        // Log the full graph for debugging
        LogGraphDetails(graph, band, bandAps);

        return graph;
    }

    /// <summary>
    /// Optimize channel plan for a given band using greedy + local search.
    /// </summary>
    public ChannelPlan Optimize(
        InterferenceGraph graph,
        RadioBand band,
        RegulatoryChannelData? regulatoryData,
        RecommendationOptions? options = null,
        bool hasBuildingData = false)
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
        currentNetworkScore = AddDfsPenalty(graph, currentAssignment, band, opts.DfsPreference, currentNetworkScore);

        // Log per-AP per-channel score breakdown BEFORE optimization
        LogPerApChannelScores(graph, currentAssignment, band, "PRE-OPTIMIZATION");

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

        // Log per-AP per-channel score breakdown AFTER optimization
        LogPerApChannelScores(graph, bestAssignment, band, "POST-OPTIMIZATION");

        // If average improvement per AP is negligible, keep the current assignment.
        // Scales with network size: 4 APs need 1.0 total, 50 APs need 12.5.
        var improvement = currentNetworkScore - bestScore;
        var avgImprovement = n > 0 ? improvement / n : 0;
        if (improvement > 0 && avgImprovement < MinAvgImprovementPerAp)
        {
            _logger.LogDebug(
                "[ChannelRec] Avg improvement per AP {AvgImprovement:F3} below threshold {Threshold:F3} " +
                "(total {Improvement:F3} across {N} APs), keeping current assignment",
                avgImprovement, MinAvgImprovementPerAp, improvement, n);
            bestAssignment = currentAssignment;
            bestScore = currentNetworkScore;
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
            HasScanData = graph.HasScanData,
            HasNeighborNetworks = graph.ExternalLoad.Any(d => d.Count > 0),
            HasBuildingData = hasBuildingData
        };

        for (int i = 0; i < n; i++)
        {
            var node = graph.Nodes[i];
            var currentApScore = ScoreAp(graph, currentAssignment, i, band);
            var recommendedApScore = ScoreAp(graph, bestAssignment, i, band);

            // Don't recommend moving APs with negligible current interference,
            // unless the AP is on a non-valid channel (e.g., 2.4 GHz ch3 should always be moved to 1/6/11)
            var recommendedChannel = bestAssignment[i].Channel;
            var recommendedWidth = bestAssignment[i].Width;
            var isOnValidChannel = node.ValidChannels.Contains(node.CurrentChannel);
            if (isOnValidChannel && currentApScore < MinApScoreToMove &&
                (recommendedChannel != node.CurrentChannel || recommendedWidth != node.CurrentWidth))
            {
                _logger.LogDebug(
                    "[ChannelRec] {ApName} current score {Score:F3} below per-AP threshold {Threshold:F3}, " +
                    "keeping current ch{Channel}/{Width} MHz",
                    node.Name, currentApScore, MinApScoreToMove, node.CurrentChannel, node.CurrentWidth);
                recommendedChannel = node.CurrentChannel;
                recommendedWidth = node.CurrentWidth;
                recommendedApScore = currentApScore;
            }

            plan.Recommendations.Add(new ApChannelRecommendation
            {
                ApMac = node.Mac,
                ApName = node.Name,
                Band = band,
                CurrentChannel = node.CurrentChannel,
                CurrentWidth = node.CurrentWidth,
                RecommendedChannel = recommendedChannel,
                RecommendedWidth = recommendedWidth,
                CurrentScore = currentApScore,
                RecommendedScore = recommendedApScore,
                IsMeshConstrained = node.MeshGroupLeader >= 0 && node.MeshGroupLeader != i,
                IsUnplaced = !node.IsPlaced,
                IsDfsChannel = band == RadioBand.Band5GHz && dfsSet.Contains(recommendedChannel)
            });
        }

        // Rebuild the final recommended assignment after per-AP filtering
        // and re-score with ScoreAssignment (counts each pair once, no double-counting)
        var finalAssignment = new (int Channel, int Width)[n];
        for (int i = 0; i < n; i++)
        {
            var rec = plan.Recommendations[i];
            finalAssignment[i] = (rec.RecommendedChannel, rec.RecommendedWidth);
        }
        plan.RecommendedNetworkScore = ScoreAssignment(graph, finalAssignment, band);
        plan.RecommendedNetworkScore = AddDfsPenalty(graph, finalAssignment, band, opts.DfsPreference, plan.RecommendedNetworkScore);

        // Log final recommendation summary
        LogRecommendationSummary(plan, currentAssignment, bestAssignment);

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

        // External interference (neighbor networks)
        for (int i = 0; i < n; i++)
        {
            var apSpan = ChannelSpanHelper.GetChannelSpan(band, assignment[i].Channel, assignment[i].Width);
            foreach (var (extChannel, extWeight) in graph.ExternalLoad[i])
            {
                var extSpan = (Low: extChannel, High: extChannel);
                if (ChannelSpanHelper.SpansOverlap(apSpan, extSpan))
                    score += extWeight;
            }
        }

        // Channel scan data (utilization/interference from RF environment scan)
        for (int i = 0; i < n; i++)
        {
            if (graph.ScanChannelData[i].Count == 0) continue;

            var ch = assignment[i].Channel;
            if (graph.ScanChannelData[i].TryGetValue(ch, out var scanData))
            {
                score += scanData.Utilization * ScanUtilizationWeight;
                score += scanData.Interference * ScanInterferenceWeight;
            }
        }

        // Historical channel stress: penalize channels where this AP historically experienced
        // high utilization, interference, or TX retries. Uses 30-day metrics paired with channel
        // change events for per-channel stress data. Falls back to current radio stats if
        // historical data is unavailable.
        for (int i = 0; i < n; i++)
        {
            score += ComputeStressPenalty(graph, band, i, assignment);
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

        // Channel scan data
        var ch = assignment[apIndex].Channel;
        if (graph.ScanChannelData[apIndex].TryGetValue(ch, out var scanData))
        {
            score += scanData.Utilization * ScanUtilizationWeight;
            score += scanData.Interference * ScanInterferenceWeight;
        }

        // Historical channel stress
        score += ComputeStressPenalty(graph, band, apIndex, assignment);

        return score;
    }

    /// <summary>
    /// Compute stress penalty for an AP in a given assignment.
    /// Uses per-channel historical stress if available (from 30-day metrics + channel change events).
    /// Falls back to current radio stats applied to the current channel span.
    /// Co-channel resolution scaling applies in both cases.
    /// </summary>
    private double ComputeStressPenalty(
        InterferenceGraph graph,
        RadioBand band,
        int apIndex,
        (int Channel, int Width)[] assignment)
    {
        var node = graph.Nodes[apIndex];
        var assignedSpan = ChannelSpanHelper.GetChannelSpan(band, assignment[apIndex].Channel, assignment[apIndex].Width);

        if (node.HistoricalStress != null && node.HistoricalStress.Count > 0)
        {
            // Per-channel historical stress: check each historically stressed channel
            // and apply its penalty if the assigned channel overlaps its span.
            // No co-channel resolution scaling - historical stress is measured data
            // reflecting the real RF environment, not just internal AP interference.
            double penalty = 0;
            bool hasDataForAssignedChannel = false;

            foreach (var (histChannel, stress) in node.HistoricalStress)
            {
                var histSpan = ChannelSpanHelper.GetChannelSpan(band, histChannel, node.CurrentWidth);
                if (ChannelSpanHelper.SpansOverlap(assignedSpan, histSpan))
                {
                    hasDataForAssignedChannel = true;

                    if (stress.TxRetryPct < StressMinThreshold &&
                        stress.Utilization < StressMinThreshold &&
                        stress.Interference < StressMinThreshold)
                        continue;

                    penalty += (stress.TxRetryPct / 100.0) * TxRetryStressWeight
                        + (stress.Utilization / 100.0) * UtilizationStressWeight
                        + (stress.Interference / 100.0) * InterferenceStressWeight;
                }
            }

            // Unknown channels carry more risk than measured ones
            if (!hasDataForAssignedChannel)
                penalty += UnknownChannelPenalty;

            return penalty;
        }

        // Fallback: use current radio stats on current channel span
        if (node.TxRetriesPct < StressMinThreshold &&
            node.ChannelUtilization < StressMinThreshold &&
            node.Interference < StressMinThreshold)
            return 0;

        var currentSpan = ChannelSpanHelper.GetChannelSpan(band, node.CurrentChannel, node.CurrentWidth);
        if (!ChannelSpanHelper.SpansOverlap(currentSpan, assignedSpan))
            return 0;

        var fallbackScale = ComputeStressScale(graph, band, apIndex, currentSpan, assignment);
        return fallbackScale * ((node.TxRetriesPct / 100.0) * TxRetryStressWeight
            + (node.ChannelUtilization / 100.0) * UtilizationStressWeight
            + (node.Interference / 100.0) * InterferenceStressWeight);
    }

    /// <summary>
    /// Compute how much of the stress penalty to apply when an AP stays on its current channel.
    /// If internal co-channel APs are moving away in the proposed assignment, their contribution
    /// to the stress is being resolved, so we scale down proportionally.
    /// Returns 1.0 (full penalty) when no co-channel APs are being resolved,
    /// 0.0 when all co-channel APs are moving away (stress fully resolved).
    /// If stress is purely external (no internal co-channel APs), returns 1.0.
    /// </summary>
    private double ComputeStressScale(
        InterferenceGraph graph,
        RadioBand band,
        int apIndex,
        (int Low, int High) currentSpan,
        (int Channel, int Width)[] assignment)
    {
        int currentCoChannel = 0;
        int remainingCoChannel = 0;
        var n = graph.Nodes.Count;

        for (int j = 0; j < n; j++)
        {
            if (j == apIndex) continue;
            if (AreMeshPair(graph, apIndex, j)) continue;

            // Is this other AP currently co-channel with the stressed AP?
            var otherCurrentSpan = ChannelSpanHelper.GetChannelSpan(band,
                graph.Nodes[j].CurrentChannel, graph.Nodes[j].CurrentWidth);

            if (!ChannelSpanHelper.SpansOverlap(currentSpan, otherCurrentSpan))
                continue;

            currentCoChannel++;

            // Does this other AP stay co-channel in the proposed assignment?
            var otherAssignedSpan = ChannelSpanHelper.GetChannelSpan(band,
                assignment[j].Channel, assignment[j].Width);

            if (ChannelSpanHelper.SpansOverlap(currentSpan, otherAssignedSpan))
                remainingCoChannel++;
        }

        // No internal co-channel APs - stress is purely external, keep full penalty
        if (currentCoChannel == 0)
            return 1.0;

        // Scale by fraction of co-channel APs remaining
        return (double)remainingCoChannel / currentCoChannel;
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

            _logger.LogDebug(
                "[ChannelRec] Internal weight {AP1} <-> {AP2}: signal {S1to2:F0}/{S2to1:F0} dBm, worst={Worst} dBm, weight={Weight:F3} (propagation)",
                ap1.Name, ap2.Name, signal1to2, signal2to1, worstSignal,
                ChannelSpanHelper.SignalToInterferenceWeight(worstSignal));

            return ChannelSpanHelper.SignalToInterferenceWeight(worstSignal);
        }

        // One or both unplaced - use conservative default
        _logger.LogDebug(
            "[ChannelRec] Internal weight {AP1} <-> {AP2}: weight={Weight:F3} (default, unplaced)",
            ap1.Name, ap2.Name, ChannelSpanHelper.SignalToInterferenceWeight(DefaultUnplacedSignalDbm));

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

            var apWidth = graph.Nodes[apIndex].CurrentWidth;

            foreach (var neighbor in scan.Neighbors.Where(n => !n.IsOwnNetwork && n.Signal.HasValue))
            {
                var weight = ChannelSpanHelper.SignalToInterferenceWeight(neighbor.Signal!.Value);

                // Scale by width ratio: a 20 MHz neighbor only impacts a fraction of a 160 MHz channel
                var neighborWidth = neighbor.Width ?? 20;
                if (neighborWidth < apWidth)
                    weight *= (double)neighborWidth / apWidth;

                var channel = neighbor.Channel;

                if (!graph.ExternalLoad[apIndex].ContainsKey(channel))
                    graph.ExternalLoad[apIndex][channel] = 0;
                graph.ExternalLoad[apIndex][channel] += weight;
            }
        }
    }

    /// <summary>
    /// Propagate historical stress from nearby APs using propagation weights.
    /// If AP A had high stress on a channel and AP B is nearby (high internal weight),
    /// AP B gets that channel's stress added, scaled by the proximity weight.
    /// Only propagates between placed APs (where we have real propagation data).
    /// </summary>
    private static void PropagateHistoricalStress(InterferenceGraph graph, RadioBand band)
    {
        var n = graph.Nodes.Count;

        // Collect propagated stress separately to avoid order-dependent accumulation
        var propagated = new Dictionary<int, Dictionary<int, (double Util, double Interf, double TxRetry)>>();

        for (int i = 0; i < n; i++)
        {
            var source = graph.Nodes[i];
            if (source.HistoricalStress == null || source.HistoricalStress.Count == 0)
                continue;

            for (int j = 0; j < n; j++)
            {
                if (j == i) continue;

                var target = graph.Nodes[j];
                // Only propagate between placed APs with real propagation weights
                if (!source.IsPlaced || !target.IsPlaced) continue;

                var weight = graph.InternalWeights[i, j];
                if (weight < 0.3) continue; // Only nearby APs (signal > ~-78 dBm)

                foreach (var (histChannel, stress) in source.HistoricalStress)
                {
                    // Scale stress by proximity weight, dampened by 50%.
                    // Even at weight 1.0, only inherit half the neighbor's stress.
                    // Without dampening, 2.4 GHz (where all weights are high) gets
                    // uniform stress across all channels, preventing any improvements.
                    var scale = weight * 0.5;
                    var scaledUtil = stress.Utilization * scale;
                    var scaledInterf = stress.Interference * scale;
                    var scaledTxRetry = stress.TxRetryPct * scale;

                    if (!propagated.ContainsKey(j))
                        propagated[j] = new Dictionary<int, (double, double, double)>();

                    if (propagated[j].TryGetValue(histChannel, out var existing))
                    {
                        // Take the max from multiple sources
                        propagated[j][histChannel] = (
                            Math.Max(existing.Util, scaledUtil),
                            Math.Max(existing.Interf, scaledInterf),
                            Math.Max(existing.TxRetry, scaledTxRetry));
                    }
                    else
                    {
                        propagated[j][histChannel] = (scaledUtil, scaledInterf, scaledTxRetry);
                    }
                }
            }
        }

        // Merge propagated stress into each node's historical stress
        foreach (var (nodeIdx, channels) in propagated)
        {
            var node = graph.Nodes[nodeIdx];
            node.HistoricalStress ??= new Dictionary<int, (double, double, double)>();

            foreach (var (ch, stress) in channels)
            {
                if (node.HistoricalStress.TryGetValue(ch, out var own))
                {
                    // AP has its own data for this channel - take the max
                    node.HistoricalStress[ch] = (
                        Math.Max(own.Utilization, stress.Util),
                        Math.Max(own.Interference, stress.Interf),
                        Math.Max(own.TxRetryPct, stress.TxRetry));
                }
                else
                {
                    // AP has no data for this channel - add the propagated data
                    node.HistoricalStress[ch] = (stress.Util, stress.Interf, stress.TxRetry);
                }
            }
        }
    }

    private static void BuildScanChannelData(
        InterferenceGraph graph,
        List<AccessPointSnapshot> bandAps,
        RadioBand band,
        List<ChannelScanResult> scanResults)
    {
        var macToIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < bandAps.Count; i++)
            macToIndex[bandAps[i].Mac] = i;

        foreach (var scan in scanResults.Where(s => s.Band == band))
        {
            if (!macToIndex.TryGetValue(scan.ApMac, out var apIndex))
                continue;

            foreach (var chInfo in scan.Channels)
            {
                if (chInfo.Utilization.HasValue || chInfo.Interference.HasValue)
                {
                    graph.ScanChannelData[apIndex][chInfo.Channel] = (
                        chInfo.Utilization ?? 0,
                        chInfo.Interference ?? 0);
                }
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
        // 2.4 GHz: ALWAYS restrict to 1, 6, 11 regardless of regulatory data.
        // Co-channel interference (managed by CSMA/CA) is far better than
        // adjacent channel overlap which cannot be mitigated.
        if (band == RadioBand.Band2_4GHz)
            return [1, 6, 11];

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
            RadioBand.Band5GHz => [36, 40, 44, 48, 149, 153, 157, 161, 165],
            RadioBand.Band6GHz => [1, 5, 9, 13, 17, 21, 25, 29, 33, 37, 41, 45, 49, 53, 57, 61],
            _ => []
        };
    }

    private static int GetMaxValidChannels(InterferenceGraph graph) =>
        graph.Nodes.Max(n => n.ValidChannels.Length);

    /// <summary>
    /// Count how many APs have a different channel/width vs the original assignment.
    /// Used for tie-breaking: prefer fewer changes when scores are equal.
    /// </summary>
    private static int CountChanges(
        (int Channel, int Width)[] assignment,
        (int Channel, int Width)[] original)
    {
        int changes = 0;
        for (int i = 0; i < assignment.Length && i < original.Length; i++)
        {
            if (assignment[i].Channel != original[i].Channel || assignment[i].Width != original[i].Width)
                changes++;
        }
        return changes;
    }

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
        long evaluations = 0;

        // Initialize with current
        for (int i = 0; i < n; i++)
        {
            bestAssignment[i] = (graph.Nodes[i].CurrentChannel, graph.Nodes[i].CurrentWidth);
            currentAssignment[i] = bestAssignment[i];
        }

        // Get ordered indices (mesh leaders first, then non-mesh, skip mesh children)
        var searchIndices = GetSearchIndices(graph, pinnedIndices);

        // Track current assignment for tie-breaking
        var originalAssignment = new (int Channel, int Width)[n];
        for (int i = 0; i < n; i++)
            originalAssignment[i] = (graph.Nodes[i].CurrentChannel, graph.Nodes[i].CurrentWidth);

        void Search(int depth)
        {
            if (depth >= searchIndices.Count)
            {
                evaluations++;

                // Apply mesh constraints
                var withMesh = ((int Channel, int Width)[])currentAssignment.Clone();
                ApplyMeshConstraints(graph, withMesh);

                var score = ScoreAssignment(graph, withMesh, band);
                score = AddDfsPenalty(graph, withMesh, band, opts.DfsPreference, score);

                if (score < bestScore ||
                    (score == bestScore && CountChanges(withMesh, originalAssignment) < CountChanges(bestAssignment, originalAssignment)))
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

        _logger.LogDebug(
            "[ChannelRec] Exhaustive search for {Band}: evaluated {Count} assignments, best score {Score:F3}",
            band, evaluations, bestScore);

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

        // Track original assignment for tie-breaking (prefer fewer changes)
        var originalAssignment = new (int Channel, int Width)[n];
        for (int i = 0; i < n; i++)
            originalAssignment[i] = (graph.Nodes[i].CurrentChannel, graph.Nodes[i].CurrentWidth);

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
                        // Prefer current channel when scores are equal (avoid pointless swaps)
                        if (score < bestLocal ||
                            (score == bestLocal && ch == node.CurrentChannel && w == node.CurrentWidth))
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

            // Local search (hill climbing) - only accept strict improvements
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

            if (finalScore < bestScore ||
                (finalScore == bestScore && CountChanges(assignment, originalAssignment) < CountChanges(bestAssignment, originalAssignment)))
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

    // ============ Debug Logging ============

    private void LogGraphDetails(InterferenceGraph graph, RadioBand band, List<AccessPointSnapshot> bandAps)
    {
        var n = graph.Nodes.Count;
        if (n == 0) return;

        var sb = new StringBuilder();
        sb.AppendLine($"[ChannelRec] === Interference Graph for {band} ({n} APs) ===");

        // Node summary
        for (int i = 0; i < n; i++)
        {
            var node = graph.Nodes[i];
            var radio = bandAps[i].Radios.First(r => r.Band == band && r.Channel.HasValue);
            var histStr = "";
            if (node.HistoricalStress != null && node.HistoricalStress.Count > 0)
            {
                var parts = node.HistoricalStress
                    .OrderBy(kv => kv.Key)
                    .Select(kv => $"ch{kv.Key}(u={kv.Value.Utilization:F0}%,i={kv.Value.Interference:F0}%,tx={kv.Value.TxRetryPct:F1}%)");
                histStr = $", histStress=[{string.Join(", ", parts)}]";
            }
            sb.AppendLine($"  [{i}] {node.Name}: ch{node.CurrentChannel}/{node.CurrentWidth} MHz, " +
                $"placed={node.IsPlaced}, validCh=[{string.Join(",", node.ValidChannels)}], " +
                $"util={radio.ChannelUtilization}%, interf={radio.Interference}%, txRetry={radio.TxRetriesPct:F1}%{histStr}");
        }

        // Internal weight matrix
        sb.AppendLine("  Internal weights (propagation-modeled signal → weight):");
        for (int i = 0; i < n; i++)
        {
            for (int j = i + 1; j < n; j++)
            {
                var w = graph.InternalWeights[i, j];
                if (w > 0)
                    sb.AppendLine($"    {graph.Nodes[i].Name} <-> {graph.Nodes[j].Name}: {w:F3}");
            }
        }

        // External load per AP per channel
        sb.AppendLine("  External load (neighbor RSSI → weight, by channel):");
        for (int i = 0; i < n; i++)
        {
            if (graph.ExternalLoad[i].Count == 0)
            {
                sb.AppendLine($"    {graph.Nodes[i].Name}: (no scan data)");
                continue;
            }
            var loads = graph.ExternalLoad[i]
                .OrderBy(kv => kv.Key)
                .Select(kv => $"ch{kv.Key}={kv.Value:F3}");
            sb.AppendLine($"    {graph.Nodes[i].Name}: {string.Join(", ", loads)}");
        }

        // Scan channel data per AP
        sb.AppendLine("  Scan channel metrics (utilization/interference):");
        for (int i = 0; i < n; i++)
        {
            if (graph.ScanChannelData[i].Count == 0)
            {
                sb.AppendLine($"    {graph.Nodes[i].Name}: (no scan channel data)");
                continue;
            }
            var metrics = graph.ScanChannelData[i]
                .OrderBy(kv => kv.Key)
                .Select(kv => $"ch{kv.Key}=util:{kv.Value.Utilization}%/interf:{kv.Value.Interference}%");
            sb.AppendLine($"    {graph.Nodes[i].Name}: {string.Join(", ", metrics)}");
        }

        // Mesh constraints
        if (graph.MeshConstraints.Count > 0)
        {
            sb.AppendLine("  Mesh constraints:");
            foreach (var mc in graph.MeshConstraints)
                sb.AppendLine($"    {graph.Nodes[mc.ChildIndex].Name} → parent {graph.Nodes[mc.ParentIndex].Name}");
        }

        _logger.LogDebug("{GraphDetails}", sb.ToString());
    }

    private void LogPerApChannelScores(
        InterferenceGraph graph,
        (int Channel, int Width)[] currentAssignment,
        RadioBand band,
        string phase)
    {
        var n = graph.Nodes.Count;
        if (n == 0) return;

        var sb = new StringBuilder();
        sb.AppendLine($"[ChannelRec] === {phase}: Per-AP channel scores ({band}) ===");
        sb.AppendLine($"  Current assignment: {string.Join(", ", Enumerable.Range(0, n).Select(i => $"{graph.Nodes[i].Name}=ch{currentAssignment[i].Channel}"))}");

        var totalScore = ScoreAssignment(graph, currentAssignment, band);
        sb.AppendLine($"  Total network score: {totalScore:F3}");

        // For each AP, score every valid channel
        for (int i = 0; i < n; i++)
        {
            var node = graph.Nodes[i];
            sb.AppendLine($"  {node.Name} (current: ch{currentAssignment[i].Channel}):");

            foreach (var ch in node.ValidChannels)
            {
                // Temporarily change this AP's channel to compute its score
                var testAssignment = ((int Channel, int Width)[])currentAssignment.Clone();
                testAssignment[i] = (ch, currentAssignment[i].Width);

                // Compute per-AP score breakdown
                double internalScore = 0;
                double externalScore = 0;
                double scanScore = 0;

                for (int j = 0; j < n; j++)
                {
                    if (j == i) continue;
                    if (AreMeshPair(graph, i, j)) continue;
                    var overlap = ChannelSpanHelper.ComputeOverlapFactor(
                        band, ch, currentAssignment[i].Width,
                        testAssignment[j].Channel, testAssignment[j].Width);
                    internalScore += graph.InternalWeights[i, j] * overlap;
                }

                var apSpan = ChannelSpanHelper.GetChannelSpan(band, ch, currentAssignment[i].Width);
                foreach (var (extCh, extW) in graph.ExternalLoad[i])
                {
                    if (ChannelSpanHelper.SpansOverlap(apSpan, (extCh, extCh)))
                        externalScore += extW;
                }

                if (graph.ScanChannelData[i].TryGetValue(ch, out var scanData))
                {
                    scanScore = scanData.Utilization * ScanUtilizationWeight
                              + scanData.Interference * ScanInterferenceWeight;
                }

                // Historical channel stress penalty
                double stressScore = 0;
                var testSpan = ChannelSpanHelper.GetChannelSpan(band, ch, currentAssignment[i].Width);

                if (node.HistoricalStress != null && node.HistoricalStress.Count > 0)
                {
                    foreach (var (histCh, stress) in node.HistoricalStress)
                    {
                        if (stress.TxRetryPct < StressMinThreshold &&
                            stress.Utilization < StressMinThreshold &&
                            stress.Interference < StressMinThreshold)
                            continue;
                        var histSpan = ChannelSpanHelper.GetChannelSpan(band, histCh, node.CurrentWidth);
                        if (ChannelSpanHelper.SpansOverlap(testSpan, histSpan))
                        {
                            stressScore += (stress.TxRetryPct / 100.0) * TxRetryStressWeight
                                + (stress.Utilization / 100.0) * UtilizationStressWeight
                                + (stress.Interference / 100.0) * InterferenceStressWeight;
                        }
                    }
                }
                else if (node.TxRetriesPct >= StressMinThreshold ||
                    node.ChannelUtilization >= StressMinThreshold ||
                    node.Interference >= StressMinThreshold)
                {
                    var currentSpan = ChannelSpanHelper.GetChannelSpan(band, node.CurrentChannel, node.CurrentWidth);
                    if (ChannelSpanHelper.SpansOverlap(currentSpan, testSpan))
                    {
                        stressScore = (node.TxRetriesPct / 100.0) * TxRetryStressWeight
                                    + (node.ChannelUtilization / 100.0) * UtilizationStressWeight
                                    + (node.Interference / 100.0) * InterferenceStressWeight;
                    }
                }

                var total = internalScore + externalScore + scanScore + stressScore;
                var marker = ch == currentAssignment[i].Channel ? " <<<" : "";
                var stressStr = stressScore > 0 ? $" + stress={stressScore:F3}(raw)" : "";
                sb.AppendLine($"    ch{ch,3}: internal={internalScore:F3} + external={externalScore:F3} + scan={scanScore:F3}{stressStr} = {total:F3}{marker}");
            }
        }

        _logger.LogDebug("{PerApScores}", sb.ToString());
    }

    private void LogRecommendationSummary(
        ChannelPlan plan,
        (int Channel, int Width)[] currentAssignment,
        (int Channel, int Width)[] bestAssignment)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[ChannelRec] === RECOMMENDATION SUMMARY ({plan.Band}) ===");
        sb.AppendLine($"  Network score: {plan.CurrentNetworkScore:F3} → {plan.RecommendedNetworkScore:F3} ({plan.ImprovementPercent:F1}% improvement)");

        foreach (var rec in plan.Recommendations)
        {
            var change = rec.IsChanged ? "CHANGE" : "keep";
            var mesh = rec.IsMeshConstrained ? " [MESH]" : "";
            var unplaced = rec.IsUnplaced ? " [UNPLACED]" : "";
            sb.AppendLine($"  {rec.ApName}: ch{rec.CurrentChannel}/{rec.CurrentWidth} MHz (score {rec.CurrentScore:F3}) → " +
                $"ch{rec.RecommendedChannel}/{rec.RecommendedWidth} MHz (score {rec.RecommendedScore:F3}) [{change}]{mesh}{unplaced}");
        }

        _logger.LogDebug("{RecommendationSummary}", sb.ToString());
    }
}
