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

    /// <summary>
    /// Friction for moving an AP off a DFS channel onto a non-DFS channel we have no direct
    /// neighbor observation of. DFS channels are typically underused, so abandoning a working one
    /// for a channel we have zero scan data on risks trading a known-decent channel for a hidden
    /// mess - the apparent "improvement" may be nothing more than the absence of data. This fixed
    /// cost is added to the candidate in the search-decision scoring so the move only wins on a
    /// clearly substantial net gain (roughly double the normal <see cref="MinApAbsoluteImprovement"/>
    /// bar), not on missing data. A genuinely congested DFS channel still clears it. Tunable.
    /// </summary>
    private const double DfsDepartureFrictionPenalty = 0.6;

    /// <summary>Number of random restarts for optimization</summary>
    private const int RandomRestarts = 8;

    /// <summary>Weight multiplier for channel scan utilization in scoring (0-1 scale)</summary>
    private const double ScanUtilizationWeight = 0.02;

    /// <summary>
    /// Spectrum-scan noise floor (dBm) at or below which a channel is treated as RF-clean; only the
    /// energy ABOVE this reference is penalized. Near -90 dBm a channel is quiet; higher (less
    /// negative) readings mean non-Wi-Fi energy (radar, microwaves, etc.) or a strong interferer
    /// raising the floor and hurting SNR - a real signal that utilization alone misses (a channel can
    /// be idle yet noisy). Tunable; verify against logged live values.
    /// </summary>
    private const double ScanNoiseFloorReferenceDbm = -90.0;

    /// <summary>Score penalty per dB of scan noise floor above the clean reference. -70 dBm (20 dB over)
    /// adds ~1.8 before the band-stress multiplier. Tunable.</summary>
    private const double ScanNoiseFloorWeight = 0.09;

    /// <summary>Cap on noise-floor excess (dB) so an out-of-range/garbage reading can't dominate.</summary>
    private const double ScanNoiseFloorMaxExcessDb = 45.0;

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
    /// Floor for how far an AP's channel-utilization stress can be scaled down when a proposed
    /// move resolves its co-channel pairs. Measured utilization is part co-channel airtime (which
    /// vacates the channel when a neighbor moves) and part the AP's own serving traffic (which
    /// does not). So utilization drops as co-channel resolves, but never below this fraction -
    /// otherwise a busy AP whose only co-channel neighbor relocates would read as perfectly idle.
    /// TX-retry and interference are pure contention (already counted by the internal co-channel
    /// term) and are not floored - they scale all the way down. Heuristic midpoint; tunable.
    /// </summary>
    private const double OwnLoadUtilizationFloor = 0.5;

    /// <summary>
    /// Minimum average score improvement per AP to recommend changes.
    /// Scales with network size: a 4-AP network needs 1.0 total improvement,
    /// a 50-AP network needs 12.5. Prevents recommending changes when
    /// interference is already negligible.
    /// </summary>
    private const double MinAvgImprovementPerAp = 0.15;

    /// <summary>
    /// Minimum current score for an AP to be worth moving. APs scoring below
    /// this are performing well enough to leave alone - the risk and disruption
    /// of a channel change isn't justified. A score of 1.3 means light interference
    /// that clients can handle; 2.0+ means real problems worth fixing.
    /// </summary>
    private const double MinApScoreToMove = 2.0;

    /// <summary>
    /// Minimum absolute score improvement for an individual AP to justify a move.
    /// Prevents churn when the gain is negligible (e.g., 0.7 → 0.1 = 0.6 gain).
    /// Both this AND MinApImprovementPercent must be met.
    /// </summary>
    // Tuned to 0.6 (from 1.0): surfaces moderate real gains (e.g. a quieter co-channel) now that
    // scoring is position-independent, so loosening cannot oscillate. Both gates AND'd.
    private const double MinApAbsoluteImprovement = 0.6;

    /// <summary>
    /// Minimum percentage score improvement for an individual AP to justify a move.
    /// Prevents moving APs where the improvement is small relative to current score
    /// (e.g., 3.0 → 2.8 = 7%). Both this AND MinApAbsoluteImprovement must be met.
    /// </summary>
    // Tuned to 0.15 (from 0.30): 30% missed genuinely-better channels whose gain was real but
    // moderate (~15-25%). Both this AND MinApAbsoluteImprovement must be met.
    private const double MinApImprovementPercent = 0.15;

    /// <summary>
    /// Penalty for channels with no historical data. Unknown channels carry more
    /// risk than channels we have measured data for, so they shouldn't score as
    /// perfect (0.0). Applied per-AP when historical stress data exists for the AP
    /// but not for the candidate channel.
    /// </summary>
    private const double UnknownChannelPenalty = 0.15;

    /// <summary>
    /// 2.4 GHz crowding friction baseline. 2.4 GHz has only three non-overlapping channels, so when
    /// the whole band is congested a channel change buys little and risks shuffling APs onto each
    /// other. Once the mean current per-AP score on 2.4 GHz exceeds this baseline, the optimizer
    /// raises the net-benefit a move must clear, scaled by how far past the baseline the band is
    /// (capped at <see cref="MaxCrowdingFriction"/>). Below the baseline, and on every other band,
    /// there is no friction. 4.0 is roughly where a 2.4 GHz AP is clearly contended on the
    /// CCA-anchored scale, so friction only engages once the band is genuinely busy.
    /// </summary>
    private const double CrowdingFrictionScoreBaseline = 4.0;

    /// <summary>Maximum 2.4 GHz crowding friction multiplier (see <see cref="CrowdingFrictionScoreBaseline"/>).</summary>
    private const double MaxCrowdingFriction = 3.0;

    /// <summary>
    /// Minimum whole-site improvement (as a percent of the current network score) required before ANY
    /// 2.4 GHz channel move is recommended. 2.4 GHz is low-value and inherently congested - three
    /// usable channels, legacy and IoT clients - so a marginal site gain just shuffles interference
    /// between APs and isn't worth the churn. The other bands recommend on any net improvement (still
    /// subject to the per-AP move gates); only 2.4 GHz must clear this higher whole-site bar. Applied
    /// as a final guardrail after all optimization passes, and skipped when any AP sits on an invalid
    /// channel (those always move to 1/6/11 regardless). Tunable.
    /// </summary>
    private const double MinBand24NetworkImprovementPercent = 8.0;

    /// <summary>
    /// A channel is "measurably comfortable" when the AP's own radio reports time-averaged (1d/7d)
    /// EXTERNAL-network interference on it (airtime from other people's networks) below this percent.
    /// The external neighbor scan counts visible-but-idle BSSIDs and can inflate a fine channel into a
    /// bogus move recommendation; the radio's measured interference is the ground truth for the
    /// channel it sits on. We gate on interference (not utilization, which is partly the AP's own
    /// serving traffic that follows it to any channel). Below ~20% external airtime a 2.4/5 GHz
    /// channel is genuinely usable, so we don't churn off it for the AP's own benefit. Tunable.
    /// </summary>
    private const double ComfortableInterferencePct = 20.0;

    /// <summary>
    /// Measured-worse guard: minimum dB by which a candidate channel's spectrum-scan noise floor must
    /// exceed (be louder than) the AP's current channel's before the channel counts as measurably
    /// worse on the noise-floor signal. A margin, not a hair-trigger - small floor wobble between two
    /// otherwise-fine channels shouldn't hold an AP put. Noise floor is ambient RF (not the AP's own
    /// traffic), so the AP's own read of both channels is directly comparable. Tunable.
    /// </summary>
    private const double MeasurablyWorseNoiseFloorMarginDb = 6.0;

    /// <summary>
    /// Measured-worse guard: a candidate channel's noise floor only counts as "worse" when it is at
    /// least this loud in absolute terms - i.e. a real interferer is present, not two quiet floors a
    /// few dB apart. Keeps the guard from blocking a move between two clean channels (common on 5/6
    /// GHz), where floors sit far below this. -70 dBm is well above a clean floor (~-90) yet only
    /// reached when a genuine emitter occupies the channel. Tunable.
    /// </summary>
    private const double ElevatedNoiseFloorDbm = -70.0;

    /// <summary>
    /// Measured-worse guard: minimum percentage-point margin by which a candidate channel's measured
    /// external interference must exceed the AP's current channel's to count as measurably worse on the
    /// interference signal. Paired with an absolute floor of <see cref="ComfortableInterferencePct"/>
    /// on the candidate, so the destination must be both meaningfully worse AND genuinely
    /// not-comfortable - never trips on the low-single-digit interference typical of clean 5/6 GHz
    /// bands. Tunable.
    /// </summary>
    private const double MeasurablyWorseInterferencePct = 5.0;

    /// <summary>
    /// Scan-materiality diagnostics: minimum pooled external-neighbor weight on a channel for its
    /// elevated noise floor to count as "corroborated" by the (fresher) neighbor scan. Below this, a
    /// loud floor with no Wi-Fi neighbors to explain it is EITHER a stale spectrum reading OR genuine
    /// non-Wi-Fi energy (radar, microwave) - the log flags it "uncorroborated" so we can tell the two
    /// apart on live sites. Diagnostic threshold only; never gates a recommendation.
    /// </summary>
    private const double CorroborationMinExternalWeight = 1.0;

    /// <summary>
    /// How old a spectrum scan must be before a re-scan is worth SUGGESTING (never before it's also
    /// material - see <see cref="ApChannelRecommendation.ScanRescanRecommended"/>). Deliberately in
    /// days, not hours: a fixed interferer's noise floor and the neighbor-network picture are stable
    /// over hours, so re-scanning a same-day reading rarely changes anything and just churns clients on
    /// APs without a dedicated scan radio. The materiality gate does the real filtering; this is only a
    /// coarse "enough time has passed that the RF picture plausibly moved." Tunable.
    /// </summary>
    private static readonly TimeSpan SpectrumScanStaleAfter = TimeSpan.FromHours(72);

    /// <summary>
    /// Converts a measured channel occupancy percentage (0-100) to the per-AP score scale used by
    /// the absolute gates, for the measured-congestion floor (#2). Anchored to those gates rather
    /// than to the (over-stated) external proxy: ~40% airtime lands near <see cref="MinApScoreToMove"/>
    /// (2.0) and ~80% near <see cref="CatastrophicAbsoluteScore"/> (4.0), so a moderately busy channel
    /// reads as moderate - not catastrophic. Tunable.
    /// </summary>
    private const double MeasuredCongestionToLoadScale = 0.05;

    /// <summary>
    /// Maximum allowed score degradation for any individual AP in a recommended plan.
    /// 1.5 = AP's score can increase by up to 50%. Prevents sacrificing one AP
    /// too heavily for network-wide improvement.
    /// </summary>
    private const double MaxApScoreDegradation = 1.5;

    /// <summary>
    /// Absolute score a per-AP fallback move may never push another AP to, regardless of net
    /// benefit. A net-positive standalone move (the moving AP gains more than the total it degrades
    /// others) is allowed even past MaxApScoreDegradation, but never if it leaves a victim above
    /// this score - so we never put one AP into genuinely bad shape to help the site. Absolute
    /// (not a multiple of the victim's base) because "bad" is an absolute condition: a high score
    /// means heavy interference regardless of where the AP started. 4.0 ~ where an AP is clearly
    /// suffering on the CCA-anchored scale (the worst real APs sit ~4); a modest sacrifice like
    /// pushing a neighbor 1.2 -> 2.0 stays well clear of it.
    /// </summary>
    private const double CatastrophicAbsoluteScore = 4.0;

    /// <summary>
    /// Measured EXTERNAL interference (other networks' airtime) past which a soaking AP's current
    /// channel counts as too bad to keep holding, so the soak lock lifts and a move back into play
    /// (a "reasonable escape") is allowed. Anchored to the radio's own time-averaged interference -
    /// the same ground-truth channel-quality signal the comfort anchor uses (comfortable sits below
    /// <see cref="ComfortableInterferencePct"/> = 20% on every band).
    ///
    /// Per band, because "terrible" is band-relative: 2.4 GHz is congested by nature (only three
    /// non-overlapping channels, legacy devices, Bluetooth), so a high bar avoids constant churn on
    /// a band where a move rarely helps; 6 GHz is the cleanest band, so sustained foreign airtime is
    /// meaningful sooner. This mirrors <see cref="GetBandStressMultiplier"/>'s band philosophy.
    /// The lock only lifts - the improvement gates still require a meaningfully better destination
    /// before any move is actually recommended, so lifting on a band where nothing is better is a
    /// no-op.
    ///
    /// Deliberately NOT the inferred score (idle-neighbor external load inflates it past any
    /// absolute ceiling on dense bands, which would make soak a permanent no-op there), and
    /// deliberately NOT utilization or TX retries: both are contaminated by the AP's OWN
    /// serving traffic, which follows the radio to any channel, so a busy AP would keep
    /// escaping soak no matter where it moved - the exact self-induced false positive this
    /// feature exists to avoid. Tunable.
    /// </summary>
    private static double GetSoakEscapeInterferencePct(RadioBand band) => band switch
    {
        RadioBand.Band2_4GHz => 60.0, // Crowded by nature; bar stays high, and in a truly self-crowded
                                      // environment the crowding friction + global guardrail hold it put anyway
        RadioBand.Band5GHz => 50.0,   // Over half the airtime foreign is clearly bad on a wider band
        _ => 45.0                     // 6 GHz: clean band, sustained interference is meaningful sooner
    };

    /// <summary>
    /// Minimum neighbor signal to count as external interference. Matches the CCA
    /// (Clear Channel Assessment) threshold: below -82 dBm, radios don't defer
    /// transmission so the neighbor causes no real co-channel interference.
    /// </summary>
    private const int CcaThresholdDbm = -82;

    /// <summary>How far back to look for neighbor scan data (hours).</summary>
    public const double ScanLookbackHours = 1.0;

    /// <summary>
    /// Minimum triangulated weight to count as interference. After scaling a neighbor's
    /// signal weight by the observer→target proximity, weights below this threshold
    /// are too attenuated to cause real interference and are discarded.
    /// 0.2 ≈ a neighbor at -82 dBm observed by an AP with proximity weight 0.8.
    /// </summary>
    private const double MinTriangulatedWeight = 0.2;

    /// <summary>
    /// Uncertainty multipliers for external load on channels with no direct neighbor
    /// observations, by band. Triangulation discovers some neighbors but not all - the observer
    /// AP may miss neighbors visible from the target's location. The multiplier inflates the
    /// triangulated estimate to account for missing neighbors (base + base * multiplier).
    ///
    /// It is band-dependent because scan completeness tracks propagation range:
    /// - 2.4 GHz penetrates walls and travels far, so an observer hears nearly every neighbor
    ///   the target would, and those neighbors genuinely reach the target. The picture is close
    ///   to complete, so unobserved channels are barely uncertain (1.5).
    /// - 5 GHz: the ~3x underestimate (2.0) calibrated in testing.
    /// - 6 GHz dies fastest through walls, so observers miss the most near-target neighbors and
    ///   uncertainty is highest (2.5). Tempered by 6 GHz being sparsely populated today.
    ///
    /// This is the right caution for a genuinely unknown channel; channels we DO have evidence
    /// for are scaled down separately via <see cref="ObservationConfidence"/>, which is where
    /// the softening belongs. 2.4/6 GHz values are physically motivated; only 5 GHz is calibrated.
    /// </summary>
    private const double UnobservedChannelMultiplier2_4GHz = 1.5;
    private const double UnobservedChannelMultiplier5GHz = 2.0;
    private const double UnobservedChannelMultiplier6GHz = 2.5;

    /// <summary>
    /// Observation confidence for a candidate channel this AP has measured historic occupancy
    /// of (within the metrics window). We have real airtime data for it, so the uncertainty
    /// penalty is mostly - but not entirely (data can be stale) - waived.
    /// </summary>
    private const double HistoricOccupancyConfidence = 0.85;

    /// <summary>
    /// Observation confidence for a candidate channel a sibling AP is currently resident on
    /// and this AP hears well. The sibling is a live observer of that channel's conditions,
    /// though from its own location, so confidence is high but below historic/direct.
    /// </summary>
    private const double SiblingResidentConfidence = 0.70;

    /// <summary>
    /// Observation confidence from spectrum-scan coverage of a channel: the radio directly measured
    /// the channel's airtime, so it isn't "unknown" - but it's a one-shot sample, so it reduces the
    /// uncertainty penalty without erasing it (the scan is a reference, not the authority for a
    /// move-to channel). Between sibling-resident and historic. Tunable.
    /// </summary>
    private const double ScanCoverageConfidence = 0.80;

    /// <summary>
    /// Observation confidence for a candidate channel this radio saw a neighbor on via the
    /// long-term neighbor memory (a remembered sighting, not a live scan). Real evidence the
    /// radio itself gathered, but dated - the neighborhood may have changed since - so it
    /// ranks below every live tier. Scaled by the sighting's age-decayed confidence, so a
    /// week-old memory softens the uncertainty penalty more than a month-old one. Tunable.
    /// </summary>
    private const double RememberedSightingConfidence = 0.6;

    /// <summary>
    /// Minimum internal (propagation) weight for a resident sibling AP to count as an observer
    /// of a channel. Below this the sibling is too far to characterize this AP's RF environment.
    /// </summary>
    private const double SiblingObserverMinWeight = 0.4;

    /// <summary>
    /// Multiplier for internal (own AP) co-channel interference. Co-channeling your
    /// own APs is worse than external neighbors: your APs are permanent, always-on,
    /// high-duty-cycle, and you control them. A 3x multiplier ensures the engine
    /// avoids co-channeling APs that can hear each other well.
    /// </summary>
    private const double InternalCoChannelMultiplier = 3.0;

    /// <summary>
    /// Band-specific multiplier for ambient RF stress (utilization, interference, TX retries)
    /// and scan channel data. Lower bands have higher baseline noise that's normal for
    /// the RF environment and shouldn't drive aggressive channel changes.
    /// Internal co-channel and external neighbor signal scores are NOT scaled by this -
    /// strong neighbors still steer recommendations equally on all bands.
    /// </summary>
    private static double GetBandStressMultiplier(RadioBand band) => band switch
    {
        RadioBand.Band2_4GHz => 0.3, // Crowded by nature: 3 non-overlapping channels, legacy devices, Bluetooth
        RadioBand.Band5GHz => 0.7,   // More channels but still shared spectrum, DFS complicates things
        _ => 1.0                     // 6 GHz: clean band, any interference is meaningful
    };

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
        Dictionary<string, Dictionary<int, (double Utilization, double Interference, double TxRetryPct)>>? historicalStress = null,
        Dictionary<string, ChannelSoakInfo>? soakInfo = null)
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
            DirectionalWeights = new double[n, n],
            ExternalLoad = new Dictionary<int, double>[n],
            ExternalNeighbors = new Dictionary<(int Channel, int Width), double>[n],
            DirectlyObservedChannels = new HashSet<int>[n],
            HistoricallyObservedChannels = new Dictionary<int, double>[n],
            ScanChannelData = new Dictionary<(int Channel, int Width), (int Utilization, int? NoiseFloor)>[n],
            MeshConstraints = new List<MeshConstraint>(),
            DfsChannels = new HashSet<int>(regulatoryData?.DfsChannels ?? [])
        };

        // Build nodes
        for (int i = 0; i < n; i++)
        {
            var ap = bandAps[i];
            var radio = ap.Radios.First(r => r.Band == band && r.Channel.HasValue);
            var isPlaced = propContext?.ApsByMac.ContainsKey(ap.Mac.ToLowerInvariant()) == true;

            var (validChannels, effectiveWidth, hadDfsFallback) = GetValidChannelsWithWidth(band, radio, regulatoryData, opts.DfsPreference, radio.Channel!.Value);
            if (hadDfsFallback) graph.DfsAvoidanceFallback = true;
            var currentWidth = radio.ChannelWidth ?? 20;

            var macLower = ap.Mac.ToLowerInvariant();

            // Per-channel historical stress from 30-day metrics + channel change events
            Dictionary<int, (double, double, double)>? apHistStress = null;
            if (historicalStress != null)
                historicalStress.TryGetValue(macLower, out apHistStress);

            // Visibility: if an AP has no time-averaged channel metrics, the stress term falls back
            // to the AP's live (instantaneous) radio stats for its current channel - which a burst
            // of client activity can inflate. Log it so we can confirm both sites are using the
            // historical (1d/7d) data wherever it exists.
            if (apHistStress == null || apHistStress.Count == 0)
                _logger.LogDebug(
                    "[ChannelRec] {ApName} {Band}: no historical channel metrics - stress falls back " +
                    "to live radio stats for the current channel only",
                    ap.Name, band);

            graph.Nodes.Add(new ApNode
            {
                Mac = ap.Mac,
                Name = ap.Name,
                CurrentChannel = radio.Channel!.Value,
                CurrentWidth = currentWidth,
                ValidChannels = validChannels,
                ValidWidths = new[] { currentWidth }, // Width changes are a future feature
                IsPlaced = isPlaced,
                HasDfs = radio.HasDfs,
                ChannelUtilization = radio.ChannelUtilization ?? 0,
                Interference = radio.Interference ?? 0,
                TxRetriesPct = radio.TxRetriesPct ?? 0,
                HistoricalStress = apHistStress,
                SoakInfo = soakInfo?.GetValueOrDefault(macLower)
            });

            graph.ExternalLoad[i] = new Dictionary<int, double>();
            graph.ExternalNeighbors[i] = new Dictionary<(int Channel, int Width), double>();
            graph.DirectlyObservedChannels[i] = new HashSet<int>();
            graph.HistoricallyObservedChannels[i] = new Dictionary<int, double>();
            graph.ScanChannelData[i] = new Dictionary<(int, int), (int, int?)>();
        }

        // Build pairwise internal interference weights
        var bandStr = band.ToPropagationBand();
        for (int i = 0; i < n; i++)
        {
            for (int j = i + 1; j < n; j++)
            {
                var w = ComputeInternalWeight(
                    bandAps[i], bandAps[j], band, bandStr, propContext);
                graph.InternalWeights[i, j] = w.Symmetric;
                graph.InternalWeights[j, i] = w.Symmetric;
                // Directional [aggressor, victim]: Forward is i's signal at j, Reverse is j's at i.
                graph.DirectionalWeights[i, j] = w.Forward;
                graph.DirectionalWeights[j, i] = w.Reverse;
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
        LogGraphDetails(graph, band, bandAps, options);

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

        // Apply DFS-departure friction on 5 GHz except in Avoid-DFS mode (where leaving DFS is the
        // user's explicit goal). The scorer reads this flag, so the friction reaches every
        // move-decision path - search, per-AP gates, fallback and altruistic relocation alike.
        graph.ApplyDfsDepartureFriction =
            band == RadioBand.Band5GHz && opts.DfsPreference != DfsPreference.Exclude;

        // Score current assignment
        var currentAssignment = new (int Channel, int Width)[n];
        for (int i = 0; i < n; i++)
            currentAssignment[i] = (graph.Nodes[i].CurrentChannel, graph.Nodes[i].CurrentWidth);

        var currentNetworkScore = ScoreAssignment(graph, currentAssignment, band);
        // DFS penalty is used for optimization decisions (comparing current vs recommended),
        // but the displayed current score should be consistent across DFS modes.
        var currentWithDfsPenalty = AddDfsPenalty(graph, currentAssignment, band, opts.DfsPreference, currentNetworkScore);

        // Log DFS mode and per-AP per-channel score breakdown BEFORE optimization
        var dfsLabel = opts.DfsPreference switch
        {
            DfsPreference.IncludeWithPenalty => "Include DFS",
            DfsPreference.Exclude => "Avoid DFS",
            DfsPreference.Prefer => "Prefer DFS",
            _ => "Unknown"
        };
        _logger.LogDebug("[ChannelRec] Running {Band} optimization with DFS mode: {DfsMode}", band, dfsLabel);
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

        // Compute per-AP current scores for degradation constraint
        var currentApScores = new double[n];
        for (int i = 0; i < n; i++)
            currentApScores[i] = ScoreAp(graph, currentAssignment, i, band);

        // Soak-period suppression: an AP whose channel changed recently HOLDS that channel long
        // enough to measure it - its candidate set is locked to the current channel/width, so the
        // search, per-AP fallback and altruistic passes (all of which iterate ValidChannels) leave
        // it put. Holding beats the earlier "remove only the channels it left" filter, which let a
        // soaking AP hop onto any not-recently-left channel (e.g. one newly opened by a DFS-mode
        // change) and produced the exact churn soak exists to prevent. Applies to every band.
        // A mesh group moves as one (children mirror the leader's channel), so the leader's soak is
        // driven by the union of the whole group's soaked channels and the child follows the held
        // leader. A group that is MEASURABLY suffering on the new channel (any member's measured
        // airtime past the band's soak-escape threshold) is exempt: soak prevents churn, not rescue,
        // so a genuinely terrible channel can still escape. The escape requires the radio's own
        // measured stress rather than the inferred
        // score - on a dense band the score is inflated by idle-neighbor external load and can sit
        // permanently above any absolute ceiling, which would make soak a no-op exactly where it
        // matters most. A soaking AP is only held if its current channel is itself valid, so the
        // invalid-channel handling below (an AP stranded on a non-standard channel) is unaffected.
        var soakRemoved = new List<int>?[n];
        var soakEnds = new DateTimeOffset?[n];
        // Channels currently held by a soaking group. Other APs must not be moved onto them
        // (see the second pass below) - stacking onto a radio that is mid-soak corrupts the very
        // measurement the soak exists to gather, and the soaking radio is locked and can't escape.
        var soakingChannels = new HashSet<int>();
        for (int i = 0; i < n; i++)
        {
            var node = graph.Nodes[i];
            // Children have no independent move decision - their soak is enforced on the leader.
            if (node.MeshGroupLeader >= 0 && node.MeshGroupLeader != i) continue;

            var soaked = new HashSet<int>(node.SoakInfo?.SoakedChannels ?? Enumerable.Empty<int>());
            var soakEnd = node.SoakInfo?.SoakEndsAt ?? DateTimeOffset.MinValue;
            var sufferingIndex = IsCurrentChannelMeasurablySuffering(graph, band, i) ? i : -1;
            for (int j = 0; j < n; j++)
            {
                if (j == i || graph.Nodes[j].MeshGroupLeader != i) continue;
                if (graph.Nodes[j].SoakInfo is { } childSoak)
                {
                    soaked.UnionWith(childSoak.SoakedChannels);
                    if (childSoak.SoakEndsAt > soakEnd) soakEnd = childSoak.SoakEndsAt;
                }
                if (sufferingIndex < 0 && IsCurrentChannelMeasurablySuffering(graph, band, j))
                    sufferingIndex = j;
            }
            if (soaked.Count == 0) continue;

            if (sufferingIndex >= 0)
            {
                _logger.LogDebug(
                    "[ChannelRec] {ApName} {Band}: soak lock lifted for a reasonable escape - " +
                    "{SufferingAp} measures foreign airtime past the {Escape:F0}% escape threshold " +
                    "on its current channel, all channels stay in play",
                    node.Name, band, graph.Nodes[sufferingIndex].Name, GetSoakEscapeInterferencePct(band));
                continue;
            }

            // Report only the recently-left channels that are still real candidates - a channel
            // excluded for another reason (the user's DFS setting, a non-standard channel) must
            // not be presented as merely soaking. The current channel is never "left".
            var removed = node.ValidChannels
                .Where(ch => ch != node.CurrentChannel && soaked.Contains(ch))
                .OrderBy(ch => ch)
                .ToList();
            if (removed.Count == 0) continue;

            // A radio that changed onto its current channel recently HOLDS that channel for the
            // whole soak window - not just the channels it left. A freshly-changed radio commits
            // to its new channel until it has measured data on it, even when a channel that was
            // newly added to the running (e.g. DFS just switched on) now scores better. Locking
            // the whole config (channel + width) is stronger than the earlier "block hop-back"
            // filter, which let a soaking AP jump to any not-recently-left channel and produced
            // exactly the churn soak exists to prevent. The catastrophic-suffering escape above
            // still overrides this - soak prevents churn, not rescue.
            if (node.ValidChannels.Contains(node.CurrentChannel))
            {
                node.ValidChannels = new[] { node.CurrentChannel };
                node.ValidWidths = new[] { node.CurrentWidth };
                soakingChannels.Add(node.CurrentChannel);
            }
            else
            {
                // Current channel isn't valid (e.g. a non-standard 2.4 GHz channel) - the AP must
                // move SOMEWHERE, so only drop the recently-left channels rather than strand it.
                var filtered = node.ValidChannels.Where(ch => !soaked.Contains(ch)).ToArray();
                if (filtered.Length == 0) continue;
                node.ValidChannels = filtered;
            }

            _logger.LogDebug(
                "[ChannelRec] {ApName} {Band}: soaking until {SoakEnd:MM/dd HH:mm} - holding current " +
                "channel; recently-left channel(s) [{Channels}] suppressed",
                node.Name, band, soakEnd, string.Join(", ", removed));
            soakRemoved[i] = removed;
            soakEnds[i] = soakEnd;
        }

        // Second pass: keep every other AP off the channels a soaking group is holding. A move onto
        // a soaking radio's channel is sometimes the lowest-interference option, but it is the wrong
        // call while that radio is mid-soak - it corrupts the measurement and the soaking radio is
        // locked, so it can't move out of the way. An AP already resident on such a channel keeps it
        // (grandfathered by the current-channel clause); we only prevent NEW collisions. Guarded so a
        // filter that would empty an AP's candidate set is skipped rather than stranding the search.
        if (soakingChannels.Count > 0)
        {
            for (int i = 0; i < n; i++)
            {
                var node = graph.Nodes[i];
                if (soakRemoved[i] != null) continue; // this AP is itself soak-locked
                if (node.MeshGroupLeader >= 0 && node.MeshGroupLeader != i) continue; // child follows leader
                var filtered = node.ValidChannels
                    .Where(ch => ch == node.CurrentChannel || !soakingChannels.Contains(ch))
                    .ToArray();
                if (filtered.Length == node.ValidChannels.Length || filtered.Length == 0) continue;
                var reserved = node.ValidChannels.Except(filtered).OrderBy(ch => ch).ToList();
                _logger.LogDebug(
                    "[ChannelRec] {ApName} {Band}: excluding channel(s) [{Channels}] held by a soaking AP",
                    node.Name, band, string.Join(", ", reserved));
                node.ValidChannels = filtered;
            }
        }

        // Optimize
        (int Channel, int Width)[] bestAssignment;
        double bestScore;

        // Use exhaustive search when the search space is manageable.
        // 2.4 GHz (3 channels): up to ~12 APs (531K). 5/6 GHz: fewer APs due to more channels.
        var maxChannels = GetMaxValidChannels(graph);
        var searchSpace = Math.Pow(maxChannels, n);
        if (searchSpace <= 1_000_000)
        {
            (bestAssignment, bestScore) = ExhaustiveSearch(graph, band, pinnedIndices, opts, currentApScores);
        }
        else
        {
            // Greedy + local search with random restarts
            (bestAssignment, bestScore) = GreedyLocalSearch(graph, band, pinnedIndices, opts, currentApScores);
        }

        // Log per-AP per-channel score breakdown AFTER optimization
        LogPerApChannelScores(graph, bestAssignment, band, "POST-OPTIMIZATION");

        // If average improvement per AP is negligible, keep the current assignment.
        // Exception: if any AP is on a non-valid channel (e.g. 2.4 GHz ch3 instead of
        // 1/6/11), always use the optimized assignment so those APs get moved.
        // Compare raw scores (without DFS penalty) so the threshold decision is consistent
        // across all DFS modes. The DFS penalty only influences the optimizer's channel search.
        var bestRawScore = ScoreAssignment(graph, bestAssignment, band);
        var improvement = currentNetworkScore - bestRawScore;
        var avgImprovement = n > 0 ? improvement / n : 0;
        var hasInvalidChannelAps = graph.Nodes.Any(node =>
            !node.ValidChannels.Contains(node.CurrentChannel));

        if (!hasInvalidChannelAps)
        {
            if (improvement <= 0)
            {
                _logger.LogDebug(
                    "[ChannelRec] No improvement found (current {Current:F3} vs best {Best:F3}), keeping current assignment",
                    currentNetworkScore, bestRawScore);
                bestAssignment = currentAssignment;
            }
            else if (avgImprovement < MinAvgImprovementPerAp)
            {
                _logger.LogDebug(
                    "[ChannelRec] Avg improvement per AP {AvgImprovement:F3} below threshold {Threshold:F3} " +
                    "(total {Improvement:F3} across {N} APs), keeping current assignment",
                    avgImprovement, MinAvgImprovementPerAp, improvement, n);
                bestAssignment = currentAssignment;
            }
        }

        // Build result
        var plan = new ChannelPlan
        {
            Band = band,
            CurrentNetworkScore = currentNetworkScore,
            RecommendedNetworkScore = bestScore,
            UnplacedApCount = graph.Nodes.Count(node => !node.IsPlaced),
            HasScanData = graph.HasScanData,
            HasNeighborNetworks = graph.ExternalLoad.Any(d => d.Count > 0),
            HasMeasuredChannelData = graph.ScanChannelData.Any(d => d.Count > 0),
            HasBuildingData = hasBuildingData,
            DfsAvoidanceNotPossible = graph.DfsAvoidanceFallback
        };

        for (int i = 0; i < n; i++)
        {
            var node = graph.Nodes[i];
            var currentApScore = ScoreAp(graph, currentAssignment, i, band);
            var recommendedApScore = ScoreAp(graph, bestAssignment, i, band);

            // Don't recommend moving APs unless the improvement justifies the disruption,
            // unless the AP is on a non-valid channel (e.g., 2.4 GHz ch3 should always be moved to 1/6/11)
            var recommendedChannel = bestAssignment[i].Channel;
            var recommendedWidth = bestAssignment[i].Width;
            var isOnValidChannel = node.ValidChannels.Contains(node.CurrentChannel);
            var isChanged = recommendedChannel != node.CurrentChannel || recommendedWidth != node.CurrentWidth;

            // A mesh child has no independent move decision - its channel is dictated by its
            // leader (applied during the search). Never run the per-AP "is the move worth it?"
            // filter on it, or a low-scoring child gets pinned to its old channel while the
            // leader moves, splitting the backhaul across two channels. Children are synced to
            // the leader's final channel after reconciliation (see end of method).
            var isMeshChild = node.MeshGroupLeader >= 0 && node.MeshGroupLeader != i;

            if (!isMeshChild && isOnValidChannel && isChanged)
            {
                var absoluteImprovement = currentApScore - recommendedApScore;
                var percentImprovement = currentApScore > 0 ? absoluteImprovement / currentApScore : 0;

                if (currentApScore < MinApScoreToMove)
                {
                    _logger.LogDebug(
                        "[ChannelRec] {ApName} current score {Score:F3} below threshold {Threshold:F3}, " +
                        "keeping current ch{Channel}/{Width} MHz",
                        node.Name, currentApScore, MinApScoreToMove, node.CurrentChannel, node.CurrentWidth);
                    recommendedChannel = node.CurrentChannel;
                    recommendedWidth = node.CurrentWidth;
                    recommendedApScore = currentApScore;
                }
                else if (absoluteImprovement < MinApAbsoluteImprovement ||
                         percentImprovement < MinApImprovementPercent)
                {
                    _logger.LogDebug(
                        "[ChannelRec] {ApName} improvement too small (abs={Abs:F3}, pct={Pct:P0}), " +
                        "keeping current ch{Channel}/{Width} MHz (thresholds: abs>={AbsThresh:F1}, pct>={PctThresh:P0})",
                        node.Name, absoluteImprovement, percentImprovement,
                        node.CurrentChannel, node.CurrentWidth,
                        MinApAbsoluteImprovement, MinApImprovementPercent);
                    recommendedChannel = node.CurrentChannel;
                    recommendedWidth = node.CurrentWidth;
                    recommendedApScore = currentApScore;
                }
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
                IsCurrentDfsChannel = IsDfsAssignment(band, node.CurrentChannel, node.CurrentWidth, graph.DfsChannels),
                // Recommended DFS status is authoritatively recomputed against the final channel
                // after reconciliation (the per-AP fallback, altruistic relocation and mesh sync
                // can all rewrite the channel below). This initial value is the optimizer's raw pick.
                IsRecommendedDfsChannel = IsDfsAssignment(band, recommendedChannel, recommendedWidth, graph.DfsChannels),
                SoakSuppressedChannels = soakRemoved[i] ?? new List<int>(),
                SoakEndsAt = soakEnds[i]
            });
        }

        // Re-validate changed APs against the actual final assignment.
        // Per-AP filtering may have vetoed some moves, which invalidates the scores
        // used to approve other moves. For example, if the optimizer planned to swap
        // APs A and B between channels but B was vetoed (score too low to move),
        // A's move may now put it on the same channel as B - worse than before.
        // Iterate until stable: rebuild final assignment, re-score changed APs,
        // revert any that no longer meet thresholds.
        var finalAssignment = new (int Channel, int Width)[n];
        bool reverted;
        do
        {
            reverted = false;
            for (int i = 0; i < n; i++)
            {
                var rec = plan.Recommendations[i];
                finalAssignment[i] = (rec.RecommendedChannel, rec.RecommendedWidth);
            }
            // Keep mesh children on their leader's channel so every re-score below sees a
            // physically valid assignment (a backhaul pair can't sit on two channels).
            ApplyMeshConstraints(graph, finalAssignment);

            // Check changed APs still meet improvement thresholds
            for (int i = 0; i < n; i++)
            {
                var rec = plan.Recommendations[i];
                var node = graph.Nodes[i];
                // A mesh child follows its leader - it has no independent move to revert.
                if (node.MeshGroupLeader >= 0 && node.MeshGroupLeader != i) continue;
                var isChanged = rec.RecommendedChannel != node.CurrentChannel ||
                                rec.RecommendedWidth != node.CurrentWidth;
                if (!isChanged) continue;

                // Never revert APs on invalid channels (e.g., 2.4 GHz ch3 must move to 1/6/11)
                var isOnValidChannel = node.ValidChannels.Contains(node.CurrentChannel);
                if (!isOnValidChannel) continue;

                // Re-score this AP against the actual final assignment (not the optimizer's ideal)
                var actualScore = ScoreAp(graph, finalAssignment, i, band);
                var currentApScore = ScoreAp(graph, currentAssignment, i, band);
                var absoluteImprovement = currentApScore - actualScore;
                var percentImprovement = currentApScore > 0 ? absoluteImprovement / currentApScore : 0;

                if (absoluteImprovement <= 0 ||
                    absoluteImprovement < MinApAbsoluteImprovement ||
                    percentImprovement < MinApImprovementPercent)
                {
                    _logger.LogDebug(
                        "[ChannelRec] {ApName} re-scored against final assignment: {ActualScore:F3} " +
                        "(was {OriginalScore:F3} in optimizer plan), improvement {Abs:F3}/{Pct:P0} " +
                        "no longer meets thresholds, reverting to ch{Channel}/{Width} MHz",
                        node.Name, actualScore, rec.RecommendedScore,
                        absoluteImprovement, percentImprovement,
                        node.CurrentChannel, node.CurrentWidth);
                    rec.RecommendedChannel = node.CurrentChannel;
                    rec.RecommendedWidth = node.CurrentWidth;
                    rec.RecommendedScore = currentApScore;
                    finalAssignment[i] = (node.CurrentChannel, node.CurrentWidth);
                    reverted = true;
                }
                else
                {
                    // Update displayed score to reflect actual final assignment
                    rec.RecommendedScore = actualScore;
                }
            }

            // Check unchanged APs for excessive degradation caused by other APs' moves.
            // If moving AP-X onto an unchanged AP's channel makes it significantly worse,
            // revert the move that caused the most degradation.
            if (!reverted)
            {
                int worstDegradedBy = -1;
                double worstDegradation = 0;

                for (int i = 0; i < n; i++)
                {
                    var node = graph.Nodes[i];
                    var isChanged = plan.Recommendations[i].RecommendedChannel != node.CurrentChannel ||
                                    plan.Recommendations[i].RecommendedWidth != node.CurrentWidth;
                    if (isChanged) continue;

                    var currentApScore = currentApScores[i];
                    var actualScore = ScoreAp(graph, finalAssignment, i, band);
                    var degradation = actualScore - currentApScore;

                    // Degradation exceeds MaxApScoreDegradation (50% increase)
                    if (currentApScore > 0 && actualScore / currentApScore > MaxApScoreDegradation)
                    {
                        if (degradation > worstDegradation)
                        {
                            worstDegradation = degradation;

                            // Find which changed AP contributes most to this degradation
                            // by checking co-channel overlap with the degraded AP
                            double maxContribution = 0;
                            for (int j = 0; j < n; j++)
                            {
                                if (j == i) continue;
                                // Skip mesh pairs - their co-channel is expected and excluded from
                                // scoring, so a mesh partner can't be the cause of i's degradation
                                // and must not be the AP we revert (mesh must share a channel).
                                if (AreMeshPair(graph, i, j)) continue;
                                var jNode = graph.Nodes[j];
                                var jChanged = plan.Recommendations[j].RecommendedChannel != jNode.CurrentChannel ||
                                               plan.Recommendations[j].RecommendedWidth != jNode.CurrentWidth;
                                if (!jChanged) continue;
                                if (!jNode.ValidChannels.Contains(jNode.CurrentChannel)) continue;

                                var contribution = graph.DirectionalWeights[j, i] *
                                    ChannelSpanHelper.ComputeOverlapFactor(band,
                                        finalAssignment[i].Channel, finalAssignment[i].Width,
                                        finalAssignment[j].Channel, finalAssignment[j].Width) *
                                    InternalCoChannelMultiplier;

                                if (contribution > maxContribution)
                                {
                                    maxContribution = contribution;
                                    worstDegradedBy = j;
                                }
                            }
                        }
                    }
                }

                if (worstDegradedBy >= 0)
                {
                    var rec = plan.Recommendations[worstDegradedBy];
                    var node = graph.Nodes[worstDegradedBy];
                    _logger.LogDebug(
                        "[ChannelRec] {ApName} move to ch{RecChannel} degrades unchanged AP beyond " +
                        "{MaxDeg:P0} threshold, reverting to ch{CurrentChannel}/{Width} MHz",
                        node.Name, rec.RecommendedChannel, MaxApScoreDegradation,
                        node.CurrentChannel, node.CurrentWidth);
                    rec.RecommendedChannel = node.CurrentChannel;
                    rec.RecommendedWidth = node.CurrentWidth;
                    rec.RecommendedScore = currentApScores[worstDegradedBy];
                    finalAssignment[worstDegradedBy] = (node.CurrentChannel, node.CurrentWidth);
                    reverted = true;
                }
            }
        } while (reverted);

        // Per-AP fallback: the optimizer's global plan may have fully collapsed during
        // re-validation (e.g., it wanted both APs to swap but only one could move).
        // For any AP still scoring above MinApScoreToMove with no recommended change,
        // try moving it to its best channel individually - checking that it doesn't
        // degrade any other AP beyond MaxApScoreDegradation.
        for (int i = 0; i < n; i++)
        {
            var node = graph.Nodes[i];
            var rec = plan.Recommendations[i];
            // A mesh child can't be moved on its own - it follows its leader's channel.
            if (node.MeshGroupLeader >= 0 && node.MeshGroupLeader != i) continue;
            var isChanged = rec.RecommendedChannel != node.CurrentChannel ||
                            rec.RecommendedWidth != node.CurrentWidth;
            if (isChanged) continue;

            // Use the AP's score in the final assignment, not the original current state.
            // Other APs may have already moved away, reducing this AP's interference.
            var scoreInFinal = ScoreAp(graph, finalAssignment, i, band);
            if (scoreInFinal < MinApScoreToMove) continue;
            if (!node.ValidChannels.Contains(node.CurrentChannel)) continue;

            // Try each valid channel and find the best that meets all constraints. Selection and the
            // net-benefit test are on the NETWORK objective so a fallback move can never raise it.
            int fallbackChannel = -1;
            int fallbackWidth = node.CurrentWidth;
            double fallbackScore = scoreInFinal;
            var netBefore = ScoreAssignment(graph, finalAssignment, band);
            var bestNet = netBefore;

            foreach (var candidateCh in node.ValidChannels)
            {
                if (candidateCh == node.CurrentChannel) continue;

                // Build trial assignment. Apply mesh constraints so that if this AP is a mesh
                // leader, its child moves with it - otherwise the net-benefit check below scores
                // the child on its old channel and undercounts the degradation a leader's move
                // actually causes (e.g. dragging the child onto a neighbor's channel too).
                var trial = new (int Channel, int Width)[n];
                Array.Copy(finalAssignment, trial, n);
                trial[i] = (candidateCh, node.CurrentWidth);
                ApplyMeshConstraints(graph, trial);

                var candidateScore = ScoreAp(graph, trial, i, band);
                var absImprovement = scoreInFinal - candidateScore;
                var pctImprovement = scoreInFinal > 0 ? absImprovement / scoreInFinal : 0;

                if (absImprovement < MinApAbsoluteImprovement ||
                    pctImprovement < MinApImprovementPercent)
                    continue;

                // Catastrophic guard: never push any single AP past the catastrophic cap. Measure each
                // victim against its score in the REALIZED plan (finalAssignment), the same baseline
                // the mover's gain uses.
                bool reject = false;
                for (int j = 0; j < n; j++)
                {
                    if (j == i) continue;
                    if (currentApScores[j] <= 0) continue;
                    var otherInFinal = ScoreAp(graph, finalAssignment, j, band);
                    var otherScore = ScoreAp(graph, trial, j, band);
                    if (otherScore > CatastrophicAbsoluteScore && otherScore > otherInFinal)
                    {
                        _logger.LogDebug(
                            "[ChannelRec] Per-AP fallback: {ApName} -> ch{Ch} would push {Victim} " +
                            "{From:F3}->{To:F3} above the {Cap:F1} ceiling, skipping",
                            node.Name, candidateCh, graph.Nodes[j].Name, otherInFinal, otherScore,
                            CatastrophicAbsoluteScore);
                        reject = true;
                        break;
                    }
                }
                if (reject) continue;

                // Net-benefit measured on the ACTUAL network objective (ScoreAssignment), NOT a sum of
                // per-AP scores. Summing ScoreAp double-counts a newly-created co-channel pair, so it
                // can call a net-WORSENING move "positive" - e.g. moving an AP onto a channel a sibling
                // already occupies (the Front Yard ch1->ch6 case that raised the network score). Pick
                // the candidate that lowers the network score the most; require a strict improvement.
                var netAfter = ScoreAssignment(graph, trial, band);
                if (netAfter >= bestNet)
                {
                    _logger.LogDebug(
                        "[ChannelRec] Per-AP fallback: {ApName} -> ch{Ch} network {Before:F3}->{After:F3} " +
                        "is not a net improvement, skipping",
                        node.Name, candidateCh, netBefore, netAfter);
                    continue;
                }

                bestNet = netAfter;
                fallbackScore = candidateScore;
                fallbackChannel = candidateCh;
            }

            if (fallbackChannel >= 0)
            {
                _logger.LogDebug(
                    "[ChannelRec] Per-AP fallback: {ApName} ch{Current} (score {CurrentScore:F3}) → " +
                    "ch{Best} (score {BestScore:F3}), net-positive for the site",
                    node.Name, node.CurrentChannel, scoreInFinal, fallbackChannel, fallbackScore);
                rec.RecommendedChannel = fallbackChannel;
                rec.RecommendedWidth = fallbackWidth;
                rec.RecommendedScore = fallbackScore;
                finalAssignment[i] = (fallbackChannel, fallbackWidth);
                // Keep the child aligned so later iterations score against a valid topology.
                ApplyMeshConstraints(graph, finalAssignment);
            }
        }

        // Altruistic relocation. The per-AP fallback above only relocates an AP to improve
        // ITSELF, and only once it is already suffering (>= MinApScoreToMove). That misses the
        // globally better move of relocating a still-healthy AP to declutter a worse neighbor
        // (e.g. moving a fine AP off a shared 160 MHz block so a congested neighbor stops sharing
        // it). The pruned search may never have tried that channel. Consider it here, but gate on
        // a real site-wide score improvement - not the mover's own score - and never sacrifice
        // the mover or push a victim into bad territory, so healthy networks see no churn.
        // The long-term fix is broader search candidate generation (see TODO.md); this is the
        // targeted, low-risk pass that complements the pruned search.
        var baselineNetworkScore = AddDfsPenalty(graph, finalAssignment, band, opts.DfsPreference,
            ScoreAssignment(graph, finalAssignment, band));
        for (int i = 0; i < n; i++)
        {
            if (pinnedIndices.Contains(i)) continue;
            var node = graph.Nodes[i];
            var rec = plan.Recommendations[i];
            // Children follow their leader; suffering APs are the selfish fallback's job.
            if (node.MeshGroupLeader >= 0 && node.MeshGroupLeader != i) continue;
            if (rec.RecommendedChannel != node.CurrentChannel || rec.RecommendedWidth != node.CurrentWidth)
                continue;
            if (!node.ValidChannels.Contains(node.CurrentChannel)) continue;
            var moverScore = ScoreAp(graph, finalAssignment, i, band);
            if (moverScore >= MinApScoreToMove) continue;

            // Interference borne by every OTHER AP today. The altruistic pass only fires when a
            // move reduces THIS - the mover's own score is deliberately excluded, because improving
            // the mover is the per-AP fallback's job and it already declined to move this healthy
            // AP. Gating on total network score instead would let a healthy AP relocate purely for
            // its own gain (e.g. chasing a quieter channel it shares with no neighbor), which is not
            // altruism and sneaks past the per-AP move threshold.
            double othersBaseline = 0;
            for (int j = 0; j < n; j++)
                if (j != i) othersBaseline += ScoreAp(graph, finalAssignment, j, band);

            int bestCh = -1;
            var bestNetwork = baselineNetworkScore;
            foreach (var candidateCh in node.ValidChannels)
            {
                if (candidateCh == node.CurrentChannel) continue;

                var trial = new (int Channel, int Width)[n];
                Array.Copy(finalAssignment, trial, n);
                trial[i] = (candidateCh, node.CurrentWidth);
                ApplyMeshConstraints(graph, trial);

                // Never sacrifice the mover: a healthy AP must stay healthy after the move.
                if (ScoreAp(graph, trial, i, band) > MinApScoreToMove) continue;

                // Never push another AP into genuinely bad territory.
                bool catastrophic = false;
                for (int j = 0; j < n; j++)
                {
                    if (j == i || currentApScores[j] <= 0) continue;
                    var otherScore = ScoreAp(graph, trial, j, band);
                    if (otherScore > CatastrophicAbsoluteScore && otherScore > currentApScores[j])
                    {
                        catastrophic = true;
                        break;
                    }
                }
                if (catastrophic) continue;

                // The neighbors must genuinely benefit: require a meaningful drop in the OTHER APs'
                // interference, not just a lower total (which the mover's own score change inflates).
                double othersTrial = 0;
                for (int j = 0; j < n; j++)
                    if (j != i) othersTrial += ScoreAp(graph, trial, j, band);
                if (othersBaseline - othersTrial < MinApAbsoluteImprovement) continue;

                // Among genuinely altruistic moves, pick the best site-wide outcome (DFS-aware,
                // like the search) and never accept one that makes the whole site worse.
                var trialNetwork = AddDfsPenalty(graph, trial, band, opts.DfsPreference,
                    ScoreAssignment(graph, trial, band));
                if (trialNetwork < bestNetwork)
                {
                    bestNetwork = trialNetwork;
                    bestCh = candidateCh;
                }
            }

            if (bestCh >= 0)
            {
                _logger.LogDebug(
                    "[ChannelRec] Altruistic relocation: {ApName} ch{Current} → ch{Best} to declutter " +
                    "neighbors (own score {Own:F3}, network {From:F3} → {To:F3})",
                    node.Name, node.CurrentChannel, bestCh, moverScore, baselineNetworkScore, bestNetwork);
                rec.RecommendedChannel = bestCh;
                rec.RecommendedWidth = node.CurrentWidth;
                finalAssignment[i] = (bestCh, node.CurrentWidth);
                ApplyMeshConstraints(graph, finalAssignment);
                baselineNetworkScore = bestNetwork;
            }
        }

        // 2.4 GHz crowding friction: in a broadly congested 2.4 GHz band, a channel change buys
        // little and can shuffle APs onto each other, so hold an AP on its current channel unless
        // its move clears a crowding-scaled net-site-benefit bar. The benefit sums EVERY AP's
        // interference, so a co-channel collision is counted against both victims, not netted away.
        // No-op on other bands and on an uncrowded 2.4 GHz band; an AP on an invalid channel still
        // moves. Runs before the mesh sync below so a reverted leader re-aligns its children.
        var crowdingFriction = ComputeBandCrowdingFriction(band, currentApScores);
        if (crowdingFriction > 1.0)
        {
            var netBenefitBar = MinApAbsoluteImprovement * (crowdingFriction - 1.0);
            bool revertedAny;
            do
            {
                revertedAny = false;
                for (int i = 0; i < n; i++)
                {
                    var node = graph.Nodes[i];
                    var rec = plan.Recommendations[i];
                    if (rec.RecommendedChannel == node.CurrentChannel && rec.RecommendedWidth == node.CurrentWidth)
                        continue;
                    // Mesh children follow their leader; an AP stuck on an invalid channel must move.
                    if (node.MeshGroupLeader >= 0 && node.MeshGroupLeader != i) continue;
                    if (!node.ValidChannels.Contains(node.CurrentChannel)) continue;

                    var revertedAssignment = ((int Channel, int Width)[])finalAssignment.Clone();
                    revertedAssignment[i] = (node.CurrentChannel, node.CurrentWidth);
                    ApplyMeshConstraints(graph, revertedAssignment);

                    double withMove = 0, withoutMove = 0;
                    for (int j = 0; j < n; j++)
                    {
                        withMove += ScoreAp(graph, finalAssignment, j, band);
                        withoutMove += ScoreAp(graph, revertedAssignment, j, band);
                    }
                    if (withoutMove - withMove >= netBenefitBar) continue; // the move earns its keep

                    _logger.LogDebug(
                        "[ChannelRec] 2.4 GHz crowding friction: reverting {ApName} ch{Rec} → ch{Cur}; net " +
                        "site benefit {Net:F3} below the crowded-band bar {Bar:F3} (friction {Friction:F2})",
                        node.Name, rec.RecommendedChannel, node.CurrentChannel,
                        withoutMove - withMove, netBenefitBar, crowdingFriction);

                    rec.RecommendedChannel = node.CurrentChannel;
                    rec.RecommendedWidth = node.CurrentWidth;
                    finalAssignment[i] = (node.CurrentChannel, node.CurrentWidth);
                    ApplyMeshConstraints(graph, finalAssignment);
                    revertedAny = true;
                }
            } while (revertedAny);
        }

        // Measured-comfort anchor (#1): don't churn an AP off a channel that its own radio measures
        // as quiet from OUTSIDE networks (low time-averaged external-network interference) unless the
        // move actually reduces co-channel interference to one of OUR OWN sibling APs. The external
        // neighbor scan counts visible-but-idle BSSIDs and can inflate an externally-clean channel
        // into a bogus move; the radio's 1d/7d interference measurement is ground truth for the
        // channel it sits on. Self-benefit moves off such a channel are reverted; a genuine move
        // that unsticks two of our APs sharing a channel still goes through.
        bool comfortReverted;
        do
        {
            comfortReverted = false;
            for (int i = 0; i < n; i++)
            {
                var node = graph.Nodes[i];
                var rec = plan.Recommendations[i];
                if (rec.RecommendedChannel == node.CurrentChannel && rec.RecommendedWidth == node.CurrentWidth)
                    continue;
                if (node.MeshGroupLeader >= 0 && node.MeshGroupLeader != i) continue;
                if (!node.ValidChannels.Contains(node.CurrentChannel)) continue;
                if (!IsCurrentChannelComfortable(graph, band, i)) continue;

                // Interference borne by our OTHER managed APs as recommended vs with this AP reverted
                // (moving this AP only changes the co-channel interference it imposes on them).
                var revertTrial = ((int Channel, int Width)[])finalAssignment.Clone();
                revertTrial[i] = (node.CurrentChannel, node.CurrentWidth);
                ApplyMeshConstraints(graph, revertTrial);

                double othersWithMove = 0, othersReverted = 0;
                for (int j = 0; j < n; j++)
                {
                    if (j == i) continue;
                    othersWithMove += ScoreAp(graph, finalAssignment, j, band);
                    othersReverted += ScoreAp(graph, revertTrial, j, band);
                }
                // Keep it put unless the move meaningfully reduces interference to our own sibling APs.
                if (othersReverted - othersWithMove >= MinApAbsoluteImprovement) continue;

                _logger.LogDebug(
                    "[ChannelRec] Measured-comfort: keeping {ApName} on ch{Cur} (radio measures it " +
                    "externally quiet; proposed ch{Rec} didn't help our own APs)",
                    node.Name, node.CurrentChannel, rec.RecommendedChannel);

                rec.RecommendedChannel = node.CurrentChannel;
                rec.RecommendedWidth = node.CurrentWidth;
                finalAssignment[i] = (node.CurrentChannel, node.CurrentWidth);
                ApplyMeshConstraints(graph, finalAssignment);
                comfortReverted = true;
            }
        } while (comfortReverted);

        // Measured "don't move onto a worse channel" guard. The external neighbor-scan proxy dominates
        // the score (~15-30) over every measured signal (~1-2), so it can steer an AP onto a channel
        // the AP's OWN radio measures as worse: fewer beaconing BSSIDs there, yet a louder noise floor
        // and/or more external-network airtime (the exact ch6->ch11 case where a -37 dBm interferer sat
        // on the "quieter" channel). Reject such a move when it buys no co-channel relief for our own
        // APs. Ground-truth only, at the AP's own vantage - spectrum-scan noise floor (ambient RF, not
        // the AP's own traffic) and time-averaged external interference - and only ever HOLDS an AP
        // put, so it can never create new churn. It complements the comfort anchor above: that fires
        // only when the CURRENT channel is already quiet (< ComfortableInterferencePct); this fires
        // whenever the DESTINATION measures worse, even from a middling current channel. Absolute
        // badness gates (see the guard constants) keep it inert on clean bands, where 5/6 GHz moves
        // between two quiet channels are the norm - it engages only when a real interferer or genuinely
        // high airtime sits on the destination, where suppressing the move is correct on any band.
        // Records each AP the guard held and why (rejected channel + which arm tripped), for the
        // scan-materiality diagnostics logged at the end - a guard hold is the strongest signal that
        // a possibly-stale scan is load-bearing in the plan.
        var measuredWorseHolds = new Dictionary<int, (int RejectedChannel, MeasuredWorseReason Reason)>();
        bool worseReverted;
        do
        {
            worseReverted = false;
            for (int i = 0; i < n; i++)
            {
                var node = graph.Nodes[i];
                var rec = plan.Recommendations[i];
                if (rec.RecommendedChannel == node.CurrentChannel && rec.RecommendedWidth == node.CurrentWidth)
                    continue;
                if (node.MeshGroupLeader >= 0 && node.MeshGroupLeader != i) continue;
                if (!node.ValidChannels.Contains(node.CurrentChannel)) continue;
                if (EvaluateMeasuredWorse(graph, band, i, rec.RecommendedChannel, rec.RecommendedWidth) is not { } worse)
                    continue;

                // Same internal-benefit escape as the comfort anchor: a move that genuinely unsticks a
                // co-channel pair among our OWN always-on APs still goes through despite the louder
                // ambient reading - resolving our own permanent collision can outweigh a strong neighbor.
                var revertTrial = ((int Channel, int Width)[])finalAssignment.Clone();
                revertTrial[i] = (node.CurrentChannel, node.CurrentWidth);
                ApplyMeshConstraints(graph, revertTrial);

                double othersWithMove = 0, othersReverted = 0;
                for (int j = 0; j < n; j++)
                {
                    if (j == i) continue;
                    othersWithMove += ScoreAp(graph, finalAssignment, j, band);
                    othersReverted += ScoreAp(graph, revertTrial, j, band);
                }
                if (othersReverted - othersWithMove >= MinApAbsoluteImprovement) continue;

                var rejectedChannel = rec.RecommendedChannel;
                _logger.LogDebug(
                    "[ChannelRec] Measured-worse guard: keeping {ApName} on ch{Cur} (proposed ch{Rec} " +
                    "measures worse at this AP [{Arms}] - and didn't reduce co-channel interference to " +
                    "our own APs)",
                    node.Name, node.CurrentChannel, rejectedChannel, DescribeMeasuredWorseArms(worse));

                measuredWorseHolds[i] = (rejectedChannel, worse);
                rec.RecommendedChannel = node.CurrentChannel;
                rec.RecommendedWidth = node.CurrentWidth;
                finalAssignment[i] = (node.CurrentChannel, node.CurrentWidth);
                ApplyMeshConstraints(graph, finalAssignment);
                worseReverted = true;
            }
        } while (worseReverted);

        // Sync mesh children to their leader's final channel. The leader may have moved or
        // been reverted during reconciliation; the child must mirror wherever it landed so the
        // displayed plan is physically valid (a backhaul pair shares one channel) and the
        // child's row shows the move it actually makes.
        for (int i = 0; i < n; i++)
        {
            var leader = graph.Nodes[i].MeshGroupLeader;
            if (leader < 0 || leader == i) continue;
            var leaderRec = plan.Recommendations[leader];
            plan.Recommendations[i].RecommendedChannel = leaderRec.RecommendedChannel;
            plan.Recommendations[i].RecommendedWidth = leaderRec.RecommendedWidth;
            // A child soaks with its leader: the whole backhaul holds the leader's channel, so the
            // child's row shows "Soaking" too (its channel changes only when the parent's does).
            plan.Recommendations[i].SoakSuppressedChannels = leaderRec.SoakSuppressedChannels;
            plan.Recommendations[i].SoakEndsAt = leaderRec.SoakEndsAt;
            finalAssignment[i] = (leaderRec.RecommendedChannel, leaderRec.RecommendedWidth);
        }

        // Global guardrail: never emit a plan that RAISES the network score. The post-passes
        // (per-AP fallback, altruistic) judge benefit per AP, and a per-AP proxy can disagree with the
        // network objective; if the final plan isn't a net improvement over current, drop every move
        // and keep the current assignment. (Higher ScoreAssignment = worse.)
        //
        // 2.4 GHz additionally requires the whole-site gain to clear MinBand24NetworkImprovementPercent:
        // the band is low-value and congested, so a marginal site improvement (e.g. a 3% drop that just
        // moves one AP onto a neighbor's channel) isn't worth the churn. Skipped when an AP is on an
        // invalid channel - those must always move to 1/6/11 no matter how small the site gain looks.
        var finalNetworkScore = ScoreAssignment(graph, finalAssignment, band);
        var networkImprovementPct = currentNetworkScore > 0
            ? (currentNetworkScore - finalNetworkScore) / currentNetworkScore * 100
            : 0;
        var band24GainTooSmall = band == RadioBand.Band2_4GHz && !hasInvalidChannelAps &&
                                 networkImprovementPct < MinBand24NetworkImprovementPercent;
        if (finalNetworkScore > currentNetworkScore || band24GainTooSmall)
        {
            _logger.LogDebug(
                "[ChannelRec] {Band}: final plan network {Final:F3} vs current {Current:F3} " +
                "({Pct:F1}% improvement) doesn't justify moves - reverting all",
                band, finalNetworkScore, currentNetworkScore, networkImprovementPct);
            for (int i = 0; i < n; i++)
            {
                var node = graph.Nodes[i];
                finalAssignment[i] = (node.CurrentChannel, node.CurrentWidth);
                plan.Recommendations[i].RecommendedChannel = node.CurrentChannel;
                plan.Recommendations[i].RecommendedWidth = node.CurrentWidth;
            }
        }

        // Re-score ALL APs against the final assignment for accurate display.
        // Unchanged APs may still be affected by other APs' moves (e.g., a neighbor
        // moved onto or off their channel), so their displayed score must reflect reality.
        // Also recompute the DFS badge here: the channel may have been rewritten by the
        // per-AP fallback, altruistic relocation or mesh sync since it was first set, so the
        // badge must reflect the channel actually shown, not the optimizer's original pick.
        for (int i = 0; i < n; i++)
        {
            var rec = plan.Recommendations[i];
            rec.RecommendedScore = ScoreAp(graph, finalAssignment, i, band);
            rec.IsRecommendedDfsChannel = IsDfsAssignment(band, rec.RecommendedChannel, rec.RecommendedWidth, graph.DfsChannels);
        }

        // Display scores without DFS penalty for consistency across modes.
        // DFS penalty only influences the optimizer's channel selection.
        plan.RecommendedNetworkScore = ScoreAssignment(graph, finalAssignment, band);

        // Log final recommendation summary
        LogRecommendationSummary(plan, currentAssignment, bestAssignment);

        // Record per-AP scan staleness/materiality onto the plan (drives the re-scan prompt) and, when
        // debug is on, log the full breakdown: how stale each AP's scan is, whether that staleness is
        // load-bearing (a measured-worse hold rests on it, or the scan term was decisive), and whether
        // the fresher neighbor scan still corroborates it.
        RecordAndLogScanMateriality(plan, graph, band, finalAssignment, measuredWorseHolds);

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

                score += graph.InternalWeights[i, j] * overlapFactor * InternalCoChannelMultiplier;
            }
        }

        // External interference (neighbor networks), floored by measured congestion (#2): the scan
        // can UNDER-state a channel (non-beaconing/hidden airtime the rogue scan never sees), so
        // raise it to what the radio measured. It is a floor only - never lower the proxy, because
        // the BSSIDs the scan DID detect are real and will transmit even if idle this instant.
        for (int i = 0; i < n; i++)
        {
            var apSpan = ChannelSpanHelper.GetChannelSpan(band, assignment[i].Channel, assignment[i].Width);
            double externalLoad = 0;
            foreach (var (extSpan, extWeight) in ExternalContributors(graph, band, i))
            {
                if (ChannelSpanHelper.SpansOverlap(apSpan, extSpan))
                    externalLoad += extWeight;
            }
            var measured = MeasuredCongestionLoad(graph, band, i, assignment);
            score += measured > externalLoad ? measured : externalLoad;
        }

        // Channel scan data (utilization/interference from RF environment scan)
        // Scaled by band multiplier - high utilization is normal on 2.4 GHz, concerning on 6 GHz
        var bandStress = GetBandStressMultiplier(band);
        for (int i = 0; i < n; i++)
        {
            if (graph.ScanChannelData[i].Count == 0) continue;

            if (ScanReadingForScoring(graph, band, i, assignment[i].Channel, assignment[i].Width) is { } scanData)
            {
                score += scanData.Utilization * ScanUtilizationWeight * bandStress;
                score += ScanNoiseFloorPenalty(scanData.NoiseFloor) * bandStress;
            }
        }

        // Historical channel stress and current radio stats.
        // Historical per-channel data is measured reality at each AP's location - the best
        // channel preference signal we have. NOT dampened by band multiplier.
        // Current radio stats fallback IS dampened (it's just ambient noise snapshot).
        for (int i = 0; i < n; i++)
        {
            var (historicalPenalty, fallbackPenalty) = ComputeStressPenalty(graph, band, i, assignment);
            score += historicalPenalty + fallbackPenalty * bandStress;
        }

        // Unobserved channel uncertainty (confidence-weighted, see ComputeUnobservedPenalty)
        for (int i = 0; i < n; i++)
            score += ComputeUnobservedPenalty(graph, band, i, assignment);

        // Friction against blindly leaving a DFS channel for an unobserved non-DFS one
        for (int i = 0; i < n; i++)
            score += ComputeDfsDepartureFriction(graph, band, i, assignment[i]);

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

        // Internal interference this AP SUFFERS from all other APs. Directional [j, apIndex]:
        // how much j's signal reaches this AP - so a low-EIRP neighbor interferes with it less.
        for (int j = 0; j < n; j++)
        {
            if (j == apIndex) continue;
            if (AreMeshPair(graph, apIndex, j)) continue;

            var overlapFactor = ChannelSpanHelper.ComputeOverlapFactor(
                band,
                assignment[apIndex].Channel, assignment[apIndex].Width,
                assignment[j].Channel, assignment[j].Width);

            score += graph.DirectionalWeights[j, apIndex] * overlapFactor * InternalCoChannelMultiplier;
        }

        // External interference, floored by measured congestion (#2): raise a channel the rogue scan
        // under-states up to what the radio measured, but never lower it - detected BSSIDs are real.
        var apSpan = ChannelSpanHelper.GetChannelSpan(band, assignment[apIndex].Channel, assignment[apIndex].Width);
        double externalLoad = 0;
        foreach (var (extSpan, extWeight) in ExternalContributors(graph, band, apIndex))
        {
            if (ChannelSpanHelper.SpansOverlap(apSpan, extSpan))
                externalLoad += extWeight;
        }
        var measuredLoad = MeasuredCongestionLoad(graph, band, apIndex, assignment);
        score += measuredLoad > externalLoad ? measuredLoad : externalLoad;

        // Channel scan data (scaled by band stress multiplier), aggregated over the channel's span.
        var bandStress = GetBandStressMultiplier(band);
        if (ScanReadingForScoring(graph, band, apIndex, assignment[apIndex].Channel, assignment[apIndex].Width) is { } scanData)
        {
            score += scanData.Utilization * ScanUtilizationWeight * bandStress;
            score += ScanNoiseFloorPenalty(scanData.NoiseFloor) * bandStress;
        }

        // Historical stress (undampened) + current stats fallback (dampened)
        var (histPenalty, fallbackPenalty) = ComputeStressPenalty(graph, band, apIndex, assignment);
        score += histPenalty + fallbackPenalty * bandStress;

        // Unobserved channel uncertainty (confidence-weighted)
        score += ComputeUnobservedPenalty(graph, band, apIndex, assignment);

        // Friction against blindly leaving a DFS channel for an unobserved non-DFS one
        score += ComputeDfsDepartureFriction(graph, band, apIndex, assignment[apIndex]);

        return score;
    }

    /// <summary>
    /// Unobserved-channel uncertainty penalty for an AP on its assigned channel. Triangulated
    /// external load under-counts neighbors the observing AP can't hear, so a channel with no
    /// direct observation would otherwise look artificially clean. This taxes that uncertainty,
    /// scaled by how much real evidence we already have for the channel (see
    /// <see cref="ObservationConfidence"/>): a channel we have historic occupancy of, or a
    /// resident sibling AP on, is mostly trusted. The penalty is computed identically whether
    /// the AP is resident on the channel or a candidate moving onto it, and draws confidence
    /// only from stable signals (direct scan, historic occupancy, sibling CURRENT channel) so
    /// it does not swing with transient scan coverage or the assignment being evaluated.
    /// </summary>
    private double ComputeUnobservedPenalty(
        InterferenceGraph graph,
        RadioBand band,
        int apIndex,
        (int Channel, int Width)[] assignment)
    {
        var directChannels = graph.DirectlyObservedChannels[apIndex];

        var apSpan = ChannelSpanHelper.GetChannelSpan(band, assignment[apIndex].Channel, assignment[apIndex].Width);

        var confidence = ObservationConfidence(graph, band, apIndex, apSpan, directChannels);
        if (confidence >= 1.0) return 0;

        double triangulatedLoad = 0;
        foreach (var (extSpan, extWeight) in ExternalContributors(graph, band, apIndex))
        {
            if (ChannelSpanHelper.SpansOverlap(apSpan, extSpan))
                triangulatedLoad += extWeight;
        }

        // Estimate the channel's load to apply the uncertainty premium to.
        //
        // If we have a triangulated sighting on this channel (a sibling AP scanned it and reported
        // a neighbor), that's a real measurement - trust it. It keeps the band uncertainty
        // multiplier below, since the observer isn't at this AP's exact spot, but it is NOT floored
        // up to our best observed channel: a channel a sibling scanned and found quiet should read
        // as quiet, not be assumed as busy as wherever we happen to sit.
        //
        // Only a channel with NO sighting at all (no direct, no triangulated) is a true blind spot.
        // Floor it at this AP's best KNOWN load so a blind channel never scores better than a
        // barely-seen one - otherwise the engine eagerly jumps onto channels it has zero data for.
        // Prefer directly-observed channels as the floor; an AP with NO direct scan data at all
        // (fully blind on this band) still floors against its triangulated neighbor load, so a
        // blind radio carries real uncertainty instead of reading a deceptive 0.
        double estimatedLoad;
        if (triangulatedLoad > 0)
        {
            estimatedLoad = triangulatedLoad;
        }
        else
        {
            double minKnownLoad = double.MaxValue;
            foreach (var dc in directChannels)
            {
                if (graph.ExternalLoad[apIndex].TryGetValue(dc, out var directWeight))
                    minKnownLoad = Math.Min(minKnownLoad, directWeight);
            }
            if (minKnownLoad == double.MaxValue)
            {
                foreach (var (_, extWeight) in graph.ExternalLoad[apIndex])
                    minKnownLoad = Math.Min(minKnownLoad, extWeight);
            }
            estimatedLoad = minKnownLoad == double.MaxValue ? 0 : minKnownLoad;
        }

        var basePenalty = estimatedLoad * GetUnobservedMultiplier(band);
        return (1.0 - confidence) * basePenalty;
    }

    /// <summary>
    /// A &gt;= 1.0 multiplier capturing how congested a 2.4 GHz band is, used to raise the net
    /// benefit a channel move must clear before it's worth recommending. Returns 1.0 (no friction)
    /// on every other band and when the mean current per-AP score is at or below
    /// <see cref="CrowdingFrictionScoreBaseline"/>; above the baseline it grows with the mean score,
    /// capped at <see cref="MaxCrowdingFriction"/>. 2.4 GHz only: with just three non-overlapping
    /// channels, once they are all busy no move meaningfully helps, so the optimizer should hold put.
    /// </summary>
    private static double ComputeBandCrowdingFriction(RadioBand band, double[] currentApScores)
    {
        if (band != RadioBand.Band2_4GHz || currentApScores.Length == 0) return 1.0;

        double sum = 0;
        foreach (var s in currentApScores) sum += s;
        var mean = sum / currentApScores.Length;

        if (mean <= CrowdingFrictionScoreBaseline) return 1.0;
        return Math.Min(mean / CrowdingFrictionScoreBaseline, MaxCrowdingFriction);
    }

    /// <summary>
    /// Whether the AP's current channel is "measurably comfortable" - its own radio reports
    /// time-averaged (1d/7d) EXTERNAL-network interference on it (airtime used by other people's
    /// networks) below <see cref="ComfortableInterferencePct"/>. Requires historical metrics (no
    /// averaged data → not claimed comfortable). Uses interference, not utilization, so the AP's own
    /// serving traffic (which follows it to any channel) doesn't make a fine channel read as busy.
    /// The measured-comfort anchor uses this to avoid churning an AP off a genuinely-clean channel
    /// just because the external neighbor scan sees a lot of idle BSSIDs.
    /// </summary>
    private bool IsCurrentChannelComfortable(InterferenceGraph graph, RadioBand band, int apIndex)
    {
        var node = graph.Nodes[apIndex];
        if (node.HistoricalStress == null || node.HistoricalStress.Count == 0) return false;

        var currentSpan = ChannelSpanHelper.GetChannelSpan(band, node.CurrentChannel, node.CurrentWidth);
        foreach (var (histChannel, stress) in node.HistoricalStress)
        {
            var histSpan = ChannelSpanHelper.GetChannelSpan(band, histChannel, node.CurrentWidth);
            if (ChannelSpanHelper.SpansOverlap(currentSpan, histSpan))
                return stress.Interference < ComfortableInterferencePct;
        }
        return false;
    }

    /// <summary>
    /// Whether a proposed channel measures WORSE than the AP's current channel at the AP's own
    /// location, on ground-truth signals the dominant neighbor-scan proxy ignores. Two independent
    /// arms, either of which trips the guard:
    /// <list type="bullet">
    /// <item>Spectrum-scan noise floor: the candidate's floor is at least
    /// <see cref="MeasurablyWorseNoiseFloorMarginDb"/> dB louder than the current channel's AND itself
    /// above <see cref="ElevatedNoiseFloorDbm"/> (a real interferer present, not two quiet floors).
    /// Noise floor is ambient RF, not the AP's own traffic, so the AP's own read of BOTH channels is
    /// directly comparable.</item>
    /// <item>Measured external interference (time-averaged, from measured history or, failing that, the
    /// neighbor-propagated estimate the scorer already uses): the candidate exceeds the current channel
    /// by at least <see cref="MeasurablyWorseInterferencePct"/> points AND is itself at or above
    /// <see cref="ComfortableInterferencePct"/> (the destination is genuinely not comfortable).</item>
    /// </list>
    /// The absolute gates keep the guard from blocking a move between two clean channels - the common
    /// 5/6 GHz case, where floors sit far below the elevated threshold and interference in the low
    /// single digits. Returns null when neither arm trips (nothing measurably worse - let the move
    /// stand); otherwise a <see cref="MeasuredWorseReason"/> naming the arm(s) and the compared values,
    /// so the caller can both act on it and log why (feeding the scan-materiality diagnostics).
    /// </summary>
    private MeasuredWorseReason? EvaluateMeasuredWorse(
        InterferenceGraph graph, RadioBand band, int apIndex, int recChannel, int recWidth)
    {
        var node = graph.Nodes[apIndex];

        // Noise-floor arm: the AP's OWN scan of each channel (ambient, uncontaminated by own traffic).
        // Higher (less negative) dBm = louder = worse.
        var curNf = ScanOverSpan(graph, band, apIndex, node.CurrentChannel, node.CurrentWidth)?.NoiseFloor;
        var recNf = ScanOverSpan(graph, band, apIndex, recChannel, recWidth)?.NoiseFloor;
        var noiseFloorArm = curNf is int cnf && recNf is int rnf &&
            rnf >= ElevatedNoiseFloorDbm &&
            rnf - cnf >= MeasurablyWorseNoiseFloorMarginDb;

        // Interference arm: measured external-network airtime, current vs candidate.
        double? curInt = TryGetMeasuredInterference(node, band, node.CurrentChannel, out var ci) ? ci : null;
        double? recInt = TryGetMeasuredInterference(node, band, recChannel, out var ri) ? ri : null;
        var interferenceArm = curInt is double c && recInt is double r &&
            r >= ComfortableInterferencePct &&
            r - c >= MeasurablyWorseInterferencePct;

        if (!noiseFloorArm && !interferenceArm) return null;
        return new MeasuredWorseReason(noiseFloorArm, interferenceArm, curNf, recNf, curInt, recInt);
    }

    /// <summary>
    /// Why the measured-worse guard considers a candidate channel worse than the AP's current one:
    /// which arm(s) tripped, and the values compared. Carried out of <see cref="EvaluateMeasuredWorse"/>
    /// so the hold can be logged with its rationale (scan-materiality diagnostics).
    /// </summary>
    private readonly record struct MeasuredWorseReason(
        bool NoiseFloorArm,
        bool InterferenceArm,
        int? CurrentNoiseFloor,
        int? RecommendedNoiseFloor,
        double? CurrentInterferencePct,
        double? RecommendedInterferencePct);

    /// <summary>
    /// Measured external interference (%) for a channel at this AP, preferring ground-truth measured
    /// history and falling back to the neighbor-propagated estimate (the same effective source
    /// <see cref="ComputeStressPenalty"/> scores) for a channel the AP never occupied. Returns false
    /// when neither has an overlapping entry.
    /// </summary>
    private static bool TryGetMeasuredInterference(ApNode node, RadioBand band, int channel, out double interferencePct)
    {
        interferencePct = 0;
        var span = ChannelSpanHelper.GetChannelSpan(band, channel, node.CurrentWidth);

        if (node.HistoricalStress != null)
            foreach (var (ch, stress) in node.HistoricalStress)
                if (ChannelSpanHelper.SpansOverlap(span, ChannelSpanHelper.GetChannelSpan(band, ch, node.CurrentWidth)))
                {
                    interferencePct = stress.Interference;
                    return true;
                }

        if (node.PropagatedStress != null)
            foreach (var (ch, stress) in node.PropagatedStress)
                if (ChannelSpanHelper.SpansOverlap(span, ChannelSpanHelper.GetChannelSpan(band, ch, node.CurrentWidth)))
                {
                    interferencePct = stress.Interference;
                    return true;
                }

        return false;
    }

    /// <summary>
    /// Whether the AP's own measured (1d/7d) record for its current channel shows EXTERNAL
    /// interference past the band's soak-escape threshold (<see cref="GetSoakEscapeInterferencePct"/>)
    /// - i.e. the channel is bad enough to lift the soak lock and allow a reasonable escape.
    /// It reads measured ground truth ONLY: never <see cref="ApNode.PropagatedStress"/> or the
    /// inferred score (idle-neighbor load inflates both, so on a dense 2.4 GHz band every AP would
    /// sit above any absolute ceiling forever, making soak a permanent no-op), and never utilization
    /// or TX retries (contaminated by the AP's own serving traffic, which follows it to any channel).
    /// No averaged data for the new channel yet (e.g. within the first hour after a change) means no
    /// escape - the soak is short, and rescue can wait for evidence.
    /// </summary>
    private static bool IsCurrentChannelMeasurablySuffering(InterferenceGraph graph, RadioBand band, int apIndex)
    {
        var node = graph.Nodes[apIndex];
        if (node.HistoricalStress == null || node.HistoricalStress.Count == 0) return false;

        var escapePct = GetSoakEscapeInterferencePct(band);
        var currentSpan = ChannelSpanHelper.GetChannelSpan(band, node.CurrentChannel, node.CurrentWidth);
        foreach (var (histChannel, stress) in node.HistoricalStress)
        {
            var histSpan = ChannelSpanHelper.GetChannelSpan(band, histChannel, node.CurrentWidth);
            if (ChannelSpanHelper.SpansOverlap(currentSpan, histSpan))
                return stress.Interference >= escapePct;
        }
        return false;
    }

    /// <summary>
    /// Measured-congestion FLOOR (#2) for the AP's assigned channel, on the external-load scale. The
    /// rogue/neighbor scan only sees beaconing BSSIDs, so it can UNDER-state a channel that's
    /// genuinely busy (the inverted ch1 case: low scan weight, but high measured airtime from
    /// non-beaconing or hidden sources). This raises the channel's congestion to what the radio
    /// actually measured, from the best signals we have: the AP's OWN spectrum-scan utilization for
    /// the channel (right vantage, all scanned channels), or a sibling AP's time-averaged external
    /// interference for a channel it currently occupies (averaged, and a real signal we'd also
    /// collide with our own AP there). Takes the higher of those. The caller uses it as a FLOOR only
    /// - it never LOWERS the proxy, because the BSSIDs the scan DID detect are real and will transmit
    /// even if idle this instant; the scan is a reference, not the authority. Returns -1 when no
    /// measurement exists.
    /// </summary>
    /// <summary>
    /// Score penalty from a channel's spectrum-scan noise floor (dBm): the energy ABOVE the clean
    /// reference, capped, times the per-dB weight. Returns 0 when there's no reading. Captures RF
    /// energy - non-Wi-Fi interference or a strong interferer - that raw utilization misses (a channel
    /// can be idle in airtime yet have a high noise floor, e.g. 2.4 GHz ch11). The noise floor is the
    /// ambient RF level, NOT the AP's own traffic (that shows up as utilization), so unlike the
    /// utilization floor it's safe to apply on the AP's current channel too. Caller scales by band
    /// stress.
    /// </summary>
    private static double ScanNoiseFloorPenalty(int? noiseFloorDbm)
    {
        if (noiseFloorDbm is not int nf) return 0;
        var excessDb = Math.Min(ScanNoiseFloorMaxExcessDb, Math.Max(0.0, nf - ScanNoiseFloorReferenceDbm));
        return excessDb * ScanNoiseFloorWeight;
    }

    /// <summary>
    /// The spectrum-scan reading for an operating (channel, width). Prefers a bucket measured at that
    /// exact width - UniFi already aggregated that channel - so we use its own number. Otherwise the
    /// scan is finer than the operating channel (e.g. BW20 buckets under a 160 MHz channel), and
    /// reading only the control bucket would judge a wide channel by one-eighth of its spectrum; so we
    /// aggregate the finest sub-channels across the span the way UniFi's wide-channel scan does
    /// (verified against a BW160-vs-BW20 capture): utilization = MEAN of the sub-channels, noise floor
    /// = MAX (worst) of them. Returns null when no bucket overlaps the span.
    /// </summary>
    private static (int Utilization, int? NoiseFloor)? ScanOverSpan(
        InterferenceGraph graph, RadioBand band, int apIndex, int channel, int width)
    {
        var buckets = graph.ScanChannelData[apIndex];
        if (buckets.Count == 0) return null;

        // 1) Exact operating-width bucket - trust UniFi's own aggregation for that channel.
        if (buckets.TryGetValue((channel, width), out var exact)) return exact;

        // 2) Aggregate the finest sub-channels overlapping the span (don't mix widths).
        var span = ChannelSpanHelper.GetChannelSpan(band, channel, width);
        var overlapping = buckets
            .Where(kv => ChannelSpanHelper.SpansOverlap(span, (kv.Key.Channel, kv.Key.Channel)))
            .ToList();
        if (overlapping.Count == 0) return null;

        var finestWidth = overlapping.Min(kv => kv.Key.Width);
        var fine = overlapping.Where(kv => kv.Key.Width == finestWidth).ToList();

        var util = (int)Math.Round(fine.Average(kv => (double)kv.Value.Utilization));
        int? worstNoise = null;
        foreach (var kv in fine)
            if (kv.Value.NoiseFloor is int nf)
                worstNoise = worstNoise is int n ? Math.Max(n, nf) : nf; // higher (less negative) dBm = worse
        return (util, worstNoise);
    }

    /// <summary>
    /// The scan reading to SCORE a channel with. For a candidate channel the AP's own scan is clean
    /// (it wasn't transmitting there when it scanned). For the AP's CURRENT channel its own scan is
    /// contaminated by traffic that FOLLOWS the AP - its serving load and, importantly, a mesh uplink
    /// carrying the same traffic (e.g. a camera feed) to whatever channel the AP moves to. Counting
    /// that against the current channel wrongly singles it out for an inescapable, self-induced load.
    /// So for the current channel we use the CLOSEST sibling that ISN'T on that channel - its read is
    /// a clean off-channel external scan of it - and fall back to the AP's own reading only when no
    /// sibling has a view (e.g. a single-AP site).
    /// </summary>
    private static (int Utilization, int? NoiseFloor)? ScanReadingForScoring(
        InterferenceGraph graph, RadioBand band, int apIndex, int channel, int width)
    {
        if (channel != graph.Nodes[apIndex].CurrentChannel)
            return ScanOverSpan(graph, band, apIndex, channel, width);

        var targetSpan = ChannelSpanHelper.GetChannelSpan(band, channel, width);
        double bestWeight = -1;
        (int Utilization, int? NoiseFloor)? best = null;
        var n = graph.Nodes.Count;
        for (int j = 0; j < n; j++)
        {
            if (j == apIndex) continue;
            var sibling = graph.Nodes[j];
            var siblingSpan = ChannelSpanHelper.GetChannelSpan(band, sibling.CurrentChannel, sibling.CurrentWidth);
            if (ChannelSpanHelper.SpansOverlap(targetSpan, siblingSpan)) continue; // on the channel - its read is self-contaminated too
            if (ScanOverSpan(graph, band, j, channel, width) is not { } reading) continue;
            var weight = graph.InternalWeights[apIndex, j];
            if (weight > bestWeight)
            {
                bestWeight = weight;
                best = reading;
            }
        }
        return best ?? ScanOverSpan(graph, band, apIndex, channel, width);
    }

    private double MeasuredCongestionLoad(InterferenceGraph graph, RadioBand band, int apIndex, (int Channel, int Width)[] assignment)
    {
        var node = graph.Nodes[apIndex];
        var assignedChannel = assignment[apIndex].Channel;
        var apSpan = ChannelSpanHelper.GetChannelSpan(band, assignedChannel, assignment[apIndex].Width);
        double measuredPct = -1;

        // The AP's own spectrum-scan utilization - only for a CANDIDATE channel. On its CURRENT
        // channel the scan radio also hears the AP's own serving traffic (which follows it to any
        // channel), so using it there would bias toward a move; the current channel's congestion is
        // governed by the external load + the interference-based comfort anchor (#1) instead.
        if (assignedChannel != node.CurrentChannel &&
            ScanOverSpan(graph, band, apIndex, assignedChannel, assignment[apIndex].Width) is { } scan)
            measuredPct = Math.Max(measuredPct, scan.Utilization);

        // A sibling AP's time-averaged external interference for a channel it currently sits on,
        // scaled by how strongly THIS AP hears that sibling (proximity) - a distant sibling's local
        // noise isn't our experience. Reads only real (measured) HistoricalStress, never propagated.
        var n = graph.Nodes.Count;
        for (int j = 0; j < n; j++)
        {
            if (j == apIndex) continue;
            var sibling = graph.Nodes[j];
            if (sibling.HistoricalStress == null) continue;
            var siblingSpan = ChannelSpanHelper.GetChannelSpan(band, sibling.CurrentChannel, sibling.CurrentWidth);
            if (!ChannelSpanHelper.SpansOverlap(apSpan, siblingSpan)) continue;
            if (sibling.HistoricalStress.TryGetValue(sibling.CurrentChannel, out var stress))
                measuredPct = Math.Max(measuredPct, stress.Interference * graph.InternalWeights[apIndex, j]);
        }

        return measuredPct < 0 ? -1 : measuredPct * MeasuredCongestionToLoadScale;
    }

    /// <summary>
    /// Each external contributor to an AP as a (channel span, weight) pair, accounting for the
    /// neighbor's width so a wide neighbor's load is tested against its full span (e.g. a 40 MHz
    /// neighbor on 2.4 GHz ch11 also covers ch6) - and ONLY that neighbor's weight spills, not the
    /// 20 MHz neighbors sharing its control channel. Falls back to treating each
    /// <see cref="InterferenceGraph.ExternalLoad"/> channel as a 20 MHz point when no per-width data
    /// exists (e.g. a unit test that populates ExternalLoad directly).
    /// </summary>
    private static IEnumerable<((int Low, int High) Span, double Weight)> ExternalContributors(
        InterferenceGraph graph, RadioBand band, int apIndex)
    {
        var byWidth = graph.ExternalNeighbors;
        if (byWidth != null && apIndex < byWidth.Length && byWidth[apIndex] is { Count: > 0 } neighbors)
        {
            foreach (var ((channel, width), weight) in neighbors)
                yield return (ChannelSpanHelper.GetChannelSpan(band, channel, width), weight);
            yield break;
        }

        foreach (var (channel, weight) in graph.ExternalLoad[apIndex])
            yield return ((channel, channel), weight);
    }

    /// <summary>
    /// Band-specific uncertainty multiplier for unobserved channels. Lower bands see a more
    /// complete scan picture (better propagation), so their unobserved channels are less
    /// uncertain. See the multiplier constants for rationale.
    /// </summary>
    private static double GetUnobservedMultiplier(RadioBand band) => band switch
    {
        RadioBand.Band2_4GHz => UnobservedChannelMultiplier2_4GHz,
        RadioBand.Band6GHz => UnobservedChannelMultiplier6GHz,
        _ => UnobservedChannelMultiplier5GHz
    };

    /// <summary>
    /// How confident we are in the external-load estimate for an AP's assigned channel span,
    /// in [0, 1]. 1.0 = a direct scan sighting on the channel (fully observed). Otherwise we
    /// fall back to weaker-but-real evidence: this AP's measured historic occupancy of the
    /// channel, or a sibling AP currently resident on it that this AP hears well. Uses each
    /// sibling's CURRENT channel (not the assignment under evaluation) so confidence is stable.
    /// </summary>
    private double ObservationConfidence(
        InterferenceGraph graph,
        RadioBand band,
        int apIndex,
        (int Low, int High) apSpan,
        HashSet<int> directChannels)
    {
        // Direct scan sighting on the assigned channel - fully observed, no uncertainty.
        if (directChannels.Any(dc => ChannelSpanHelper.SpansOverlap(apSpan, (dc, dc))))
            return 1.0;

        double confidence = 0;
        var node = graph.Nodes[apIndex];

        // Spectrum-scan coverage: the radio directly measured the channel's airtime, so it isn't
        // "unknown" even if it heard no beaconing neighbor there. But it's a one-shot sample, so it
        // only partially resolves the uncertainty - the scan informs the move, it doesn't crown it.
        if (graph.ScanChannelData[apIndex].Keys.Any(k => ChannelSpanHelper.SpansOverlap(apSpan, (k.Channel, k.Channel))))
            confidence = Math.Max(confidence, ScanCoverageConfidence);

        // Remembered neighbor sighting: this radio itself saw the channel's neighborhood within
        // the memory window - real evidence, decayed for age, at a reduced tier. Without this,
        // remembered neighbors would paradoxically make a remembered channel score WORSE than a
        // truly blind one (their load counts, plus the full uncertainty tax on top).
        if (apIndex < graph.HistoricallyObservedChannels.Length &&
            graph.HistoricallyObservedChannels[apIndex] is { Count: > 0 } histSightings)
        {
            foreach (var (histCh, sightingConf) in histSightings)
            {
                if (ChannelSpanHelper.SpansOverlap(apSpan, (histCh, histCh)))
                    confidence = Math.Max(confidence, RememberedSightingConfidence * sightingConf);
            }
        }

        // Historic occupancy: we have measured airtime for this AP on an overlapping channel.
        if (node.HistoricalStress != null)
        {
            foreach (var histChannel in node.HistoricalStress.Keys)
            {
                var histSpan = ChannelSpanHelper.GetChannelSpan(band, histChannel, node.CurrentWidth);
                if (ChannelSpanHelper.SpansOverlap(apSpan, histSpan))
                {
                    confidence = Math.Max(confidence, HistoricOccupancyConfidence);
                    break;
                }
            }
        }

        // Sibling AP currently resident on the channel that this AP hears well - a live observer.
        var n = graph.Nodes.Count;
        for (int j = 0; j < n; j++)
        {
            if (j == apIndex) continue;
            if (graph.InternalWeights[apIndex, j] < SiblingObserverMinWeight) continue;

            var siblingNode = graph.Nodes[j];
            var siblingSpan = ChannelSpanHelper.GetChannelSpan(band, siblingNode.CurrentChannel, siblingNode.CurrentWidth);
            if (ChannelSpanHelper.SpansOverlap(apSpan, siblingSpan))
            {
                confidence = Math.Max(confidence, SiblingResidentConfidence);
                break;
            }
        }

        return confidence;
    }

    /// <summary>
    /// Compute stress penalty for an AP in a given assignment.
    /// Returns (historicalPenalty, fallbackPenalty) so callers can apply band multiplier
    /// only to the fallback. Historical per-channel data is measured reality and should
    /// not be dampened - it's the best channel preference signal we have.
    /// </summary>
    private (double Historical, double Fallback) ComputeStressPenalty(
        InterferenceGraph graph,
        RadioBand band,
        int apIndex,
        (int Channel, int Width)[] assignment)
    {
        var node = graph.Nodes[apIndex];
        var assignedSpan = ChannelSpanHelper.GetChannelSpan(band, assignment[apIndex].Channel, assignment[apIndex].Width);

        // Combine measured history (ground truth) with neighbor-estimated (propagated) stress for
        // channels this AP never sat on; real measurements win where both have an entry. The
        // propagated estimates are legitimate here (a soft channel-preference signal) but stay out of
        // the ground-truth consumers (comfort anchor, measured floor's sibling lookup, confidence).
        var effectiveStress = node.HistoricalStress;
        if (node.PropagatedStress is { Count: > 0 })
        {
            effectiveStress = new Dictionary<int, (double Utilization, double Interference, double TxRetryPct)>(node.PropagatedStress);
            if (node.HistoricalStress != null)
                foreach (var (measuredCh, measuredStress) in node.HistoricalStress)
                    effectiveStress[measuredCh] = measuredStress;
        }

        if (effectiveStress != null && effectiveStress.Count > 0)
        {
            // Per-channel historical stress: check each historically stressed channel
            // and apply its penalty if the assigned channel overlaps its span.
            // Track contention (TX-retry + interference) separately from utilization. Contention
            // is the co-channel/CCA effect the internal weight term already captures, so it scales
            // down when a move resolves co-channel pairs. Utilization is dominated by the AP's own
            // serving traffic, which persists regardless of neighbors - it must NOT scale away, or
            // a busy AP whose only co-channel neighbor relocates would read as perfectly idle.
            double contentionPenalty = 0;
            double utilizationPenalty = 0;
            bool hasDataForAssignedChannel = false;

            foreach (var (histChannel, stress) in effectiveStress)
            {
                var histSpan = ChannelSpanHelper.GetChannelSpan(band, histChannel, node.CurrentWidth);
                if (ChannelSpanHelper.SpansOverlap(assignedSpan, histSpan))
                {
                    hasDataForAssignedChannel = true;

                    if (stress.TxRetryPct < StressMinThreshold &&
                        stress.Utilization < StressMinThreshold &&
                        stress.Interference < StressMinThreshold)
                        continue;

                    contentionPenalty += (stress.TxRetryPct / 100.0) * TxRetryStressWeight
                        + (stress.Interference / 100.0) * InterferenceStressWeight;
                    utilizationPenalty += (stress.Utilization / 100.0) * UtilizationStressWeight;
                }
            }

            // Unknown channels carry more risk than measured ones
            if (!hasDataForAssignedChannel)
                contentionPenalty += UnknownChannelPenalty;

            // Apply co-channel resolution scaling. TX-retry and interference are pure contention,
            // already counted by the internal weight term, so they scale all the way down to avoid
            // double-counting. Utilization is part co-channel airtime (drops with the neighbor) and
            // part own serving traffic (persists), so it scales too but is floored at the own-load
            // fraction rather than zeroing out.
            var histCurrentSpan = ChannelSpanHelper.GetChannelSpan(band, node.CurrentChannel, node.CurrentWidth);
            if (ChannelSpanHelper.SpansOverlap(assignedSpan, histCurrentSpan))
            {
                var scale = ComputeStressScale(graph, band, apIndex, histCurrentSpan, assignment);
                contentionPenalty *= scale;
                utilizationPenalty *= Math.Max(scale, OwnLoadUtilizationFloor);
            }

            return (contentionPenalty + utilizationPenalty, 0);
        }

        // Fallback: use current radio stats on current channel span
        if (node.TxRetriesPct < StressMinThreshold &&
            node.ChannelUtilization < StressMinThreshold &&
            node.Interference < StressMinThreshold)
            return (0, 0);

        var currentSpan = ChannelSpanHelper.GetChannelSpan(band, node.CurrentChannel, node.CurrentWidth);
        if (!ChannelSpanHelper.SpansOverlap(currentSpan, assignedSpan))
            return (0, 0);

        var fallbackScale = ComputeStressScale(graph, band, apIndex, currentSpan, assignment);
        // Contention (TX-retry + interference) scales all the way down with co-channel resolution;
        // utilization scales too but is floored at the own-load fraction (it never zeroes out).
        var fallbackContention = fallbackScale * ((node.TxRetriesPct / 100.0) * TxRetryStressWeight
            + (node.Interference / 100.0) * InterferenceStressWeight);
        var fallbackUtilization = Math.Max(fallbackScale, OwnLoadUtilizationFloor)
            * (node.ChannelUtilization / 100.0) * UtilizationStressWeight;
        return (0, fallbackContention + fallbackUtilization);
    }

    /// <summary>
    /// Compute how much of the stress penalty to apply when an AP stays on its current channel.
    /// If internal co-channel APs are moving away in the proposed assignment, their contribution
    /// to the stress is being resolved, so we scale down proportionally.
    /// The scale never drops below the external interference fraction - external neighbors
    /// aren't moving, so their contribution to stress persists regardless of internal changes.
    /// Returns 1.0 (full penalty) when no co-channel APs are being resolved.
    /// If stress is purely external (no internal co-channel APs), returns 1.0.
    /// </summary>
    private double ComputeStressScale(
        InterferenceGraph graph,
        RadioBand band,
        int apIndex,
        (int Low, int High) currentSpan,
        (int Channel, int Width)[] assignment)
    {
        double currentInternalLoad = 0;
        double proposedInternalLoad = 0;
        var n = graph.Nodes.Count;

        for (int j = 0; j < n; j++)
        {
            if (j == apIndex) continue;
            if (AreMeshPair(graph, apIndex, j)) continue;

            // Directional: co-channel load this AP suffers from j (j's signal reaching apIndex).
            var weight = graph.DirectionalWeights[j, apIndex];
            if (weight <= 0) continue;

            // Current internal co-channel load (weighted, not just count)
            var currentOverlap = ChannelSpanHelper.ComputeOverlapFactor(
                band, graph.Nodes[apIndex].CurrentChannel, graph.Nodes[apIndex].CurrentWidth,
                graph.Nodes[j].CurrentChannel, graph.Nodes[j].CurrentWidth);
            if (currentOverlap > 0)
                currentInternalLoad += weight * currentOverlap * InternalCoChannelMultiplier;

            // Proposed internal co-channel load
            var proposedOverlap = ChannelSpanHelper.ComputeOverlapFactor(
                band, graph.Nodes[apIndex].CurrentChannel, graph.Nodes[apIndex].CurrentWidth,
                assignment[j].Channel, assignment[j].Width);
            if (proposedOverlap > 0)
                proposedInternalLoad += weight * proposedOverlap * InternalCoChannelMultiplier;
        }

        // No internal co-channel APs in either current or proposed - stress is purely external
        if (currentInternalLoad <= 0)
            return 1.0;

        // If proposed has at least as much internal load, stress is not resolved
        if (proposedInternalLoad >= currentInternalLoad)
            return 1.0;

        // Compute external load on this channel span to set a floor
        double externalLoad = 0;
        foreach (var (extSpan, extWeight) in ExternalContributors(graph, band, apIndex))
        {
            if (ChannelSpanHelper.SpansOverlap(currentSpan, extSpan))
                externalLoad += extWeight;
        }

        // Internal resolution scale: fraction of internal load remaining
        double internalScale = proposedInternalLoad / currentInternalLoad;

        // Floor: external neighbors don't move, so their share of stress persists.
        // If external is 70% of total interference, stress can't drop below 70%.
        double totalLoad = currentInternalLoad + externalLoad;
        double externalFloor = totalLoad > 0 ? externalLoad / totalLoad : 0;

        return Math.Max(internalScale, externalFloor);
    }

    /// <summary>
    /// Compute internal interference weights between two APs. Returns the symmetric worst-case
    /// weight (for mutual contention), plus the two directional weights: Forward = ap1's signal
    /// reaching ap2 (ap1 as aggressor at ap2), Reverse = ap2's signal reaching ap1.
    /// </summary>
    private (double Symmetric, double Forward, double Reverse) ComputeInternalWeight(
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

            return (
                ChannelSpanHelper.SignalToInterferenceWeight(worstSignal),
                ChannelSpanHelper.SignalToInterferenceWeight((int)signal1to2),
                ChannelSpanHelper.SignalToInterferenceWeight((int)signal2to1));
        }

        // One or both unplaced - use conservative default (symmetric)
        var defaultWeight = ChannelSpanHelper.SignalToInterferenceWeight(DefaultUnplacedSignalDbm);
        _logger.LogDebug(
            "[ChannelRec] Internal weight {AP1} <-> {AP2}: weight={Weight:F3} (default, unplaced)",
            ap1.Name, ap2.Name, defaultWeight);

        return (defaultWeight, defaultWeight, defaultWeight);
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
        var n = bandAps.Count;

        // Map AP MAC to graph index
        var macToIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < n; i++)
            macToIndex[bandAps[i].Mac] = i;

        // Phase 1: Pool all neighbor sightings by BSSID across all observers
        // Each entry: (observerIndex, channel, width, signal, confidence). Confidence is 1.0
        // for live scan sightings; remembered (long-term memory) sightings carry an
        // age-decayed confidence that scales their weight down.
        var allNeighbors = new Dictionary<string, List<(int ObserverIndex, int Channel, int? Width, int Signal, double Confidence)>>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var scan in scanResults.Where(s => s.Band == band))
        {
            if (!macToIndex.TryGetValue(scan.ApMac, out var observerIndex))
                continue;

            foreach (var neighbor in scan.Neighbors.Where(nb =>
                !nb.IsOwnNetwork && nb.Signal.HasValue && nb.Signal.Value >= CcaThresholdDbm
                && !string.IsNullOrEmpty(nb.Bssid)))
            {
                if (!allNeighbors.TryGetValue(neighbor.Bssid, out var sightings))
                {
                    sightings = new List<(int, int, int?, int, double)>();
                    allNeighbors[neighbor.Bssid] = sightings;
                }
                sightings.Add((observerIndex, neighbor.Channel, neighbor.Width, neighbor.Signal!.Value, neighbor.Confidence));
            }
        }

        // Phase 2: For each unique BSSID, estimate its effect at every AP
        int directCount = 0, triangulatedCount = 0, filteredCount = 0;

        foreach (var (bssid, sightings) in allNeighbors)
        {
            for (int j = 0; j < n; j++)
            {
                var apWidth = graph.Nodes[j].CurrentWidth;
                double bestWeight = 0;
                int bestChannel = -1;
                int? bestWidth = null;
                bool isDirect = false;
                int bestObserverIndex = -1;
                double bestProximity = 0;
                double bestConfidence = 1.0;

                foreach (var (observerIndex, channel, width, signal, confidence) in sightings)
                {
                    var neighborWeight = ChannelSpanHelper.SignalToInterferenceWeight(signal) * confidence;
                    double effectiveWeight;
                    double proximity = 0;

                    if (observerIndex == j)
                    {
                        // Direct observation - identical to previous behavior
                        effectiveWeight = neighborWeight;
                    }
                    else
                    {
                        // Triangulated: scale by proximity between observer and target
                        proximity = graph.InternalWeights[observerIndex, j];
                        effectiveWeight = neighborWeight * proximity;
                    }

                    // Scale by width ratio: a 20 MHz neighbor only impacts a fraction of a wider channel
                    var neighborWidth = width ?? 20;
                    if (neighborWidth < apWidth)
                        effectiveWeight *= (double)neighborWidth / apWidth;

                    if (effectiveWeight > bestWeight)
                    {
                        bestWeight = effectiveWeight;
                        bestChannel = channel;
                        bestWidth = width;
                        isDirect = observerIndex == j;
                        bestObserverIndex = observerIndex;
                        bestProximity = proximity;
                        bestConfidence = confidence;
                    }
                }

                if (bestChannel < 0)
                    continue;

                // For triangulated entries, apply minimum weight threshold to avoid
                // noise from distant observers. Direct observations are always kept.
                if (!isDirect && bestWeight < MinTriangulatedWeight)
                {
                    filteredCount++;
                    continue;
                }

                if (!graph.ExternalLoad[j].ContainsKey(bestChannel))
                    graph.ExternalLoad[j][bestChannel] = 0;
                graph.ExternalLoad[j][bestChannel] += bestWeight;

                // Pool by (channel, width) so the scorer can account for each neighbor's true
                // spectral footprint (a 40 MHz neighbor on 2.4 GHz ch11 also steps on ch6) WITHOUT
                // dragging the 20 MHz neighbors that share its control channel into spilling too.
                var sightingWidth = bestWidth ?? 20;
                var key = (bestChannel, sightingWidth);
                graph.ExternalNeighbors[j][key] = graph.ExternalNeighbors[j].GetValueOrDefault(key) + bestWeight;

                if (isDirect)
                {
                    directCount++;
                    // Only a LIVE direct sighting fully observes the channel; a remembered one
                    // is real-but-dated evidence and lands in the reduced-confidence tier.
                    if (bestConfidence >= 1.0)
                    {
                        graph.DirectlyObservedChannels[j].Add(bestChannel);
                    }
                    else if (j < graph.HistoricallyObservedChannels.Length &&
                             graph.HistoricallyObservedChannels[j] is { } histObserved)
                    {
                        histObserved[bestChannel] = Math.Max(
                            histObserved.GetValueOrDefault(bestChannel), bestConfidence);
                    }
                }
                else
                {
                    triangulatedCount++;
                    _logger.LogDebug(
                        "Triangulated neighbor {Bssid} on ch{Channel}/{Width} → {ApName} weight={Weight:F3} (via {ObserverName}, proximity={Proximity:F3})",
                        bssid, bestChannel, bestWidth ?? 20, graph.Nodes[j].Name, bestWeight,
                        graph.Nodes[bestObserverIndex].Name, bestProximity);
                }
            }
        }

        if (allNeighbors.Count > 0 || directCount > 0 || triangulatedCount > 0)
        {
            _logger.LogDebug(
                "External load for {Band}: {BssidCount} unique BSSIDs pooled, {Direct} direct + {Triangulated} triangulated entries ({Filtered} filtered below threshold)",
                band, allNeighbors.Count, directCount, triangulatedCount, filteredCount);
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

        // Store propagated stress in its OWN field - never merged into measured HistoricalStress.
        // The stress penalty uses it as a soft estimate for channels this AP never sat on, but the
        // ground-truth consumers (comfort anchor, measured floor's sibling lookup, observation
        // confidence) read only the real HistoricalStress, so estimates can't masquerade as measured.
        foreach (var (nodeIdx, channels) in propagated)
            graph.Nodes[nodeIdx].PropagatedStress = channels;
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

            // Record the scan's true age for diagnostics (keep the freshest if several map here).
            if (scan.SpectrumTableTime is { } stt &&
                (graph.Nodes[apIndex].SpectrumScanTime is not { } existing || stt > existing))
                graph.Nodes[apIndex].SpectrumScanTime = stt;

            foreach (var chInfo in scan.Channels)
            {
                if (chInfo.Utilization.HasValue || chInfo.NoiseFloor.HasValue)
                {
                    // Key by (channel, width) so a channel's BW20 and BW160 readings coexist; the
                    // scorer (ScanOverSpan) picks the right one for the operating width.
                    var key = (chInfo.Channel, chInfo.Width ?? 20);
                    graph.ScanChannelData[apIndex][key] = (chInfo.Utilization ?? 0, chInfo.NoiseFloor);
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
            // TODO(MLO): MeshUplinkBand is a single RadioBand. No AP-to-AP MLO STR backhaul
            // hardware exists yet (today's MLO STR is client/bridge only), but when it ships a
            // backhaul can span multiple bands at once (e.g. 5 + 6 GHz). Make this a set and emit
            // one constraint per participating band once UniFi exposes per-link bands. The
            // reconciliation logic keys off MeshGroupLeader and needs no change - only this.
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

    /// <summary>
    /// Check if any AP in the assignment degrades more than the allowed threshold.
    /// Returns true if the assignment violates the constraint.
    /// </summary>
    private bool ViolatesApDegradation(
        InterferenceGraph graph,
        (int Channel, int Width)[] assignment,
        double[] currentApScores,
        RadioBand band)
    {
        for (int i = 0; i < graph.Nodes.Count; i++)
        {
            if (currentApScores[i] <= 0) continue;
            var newScore = ScoreAp(graph, assignment, i, band);
            if (newScore > currentApScores[i] * MaxApScoreDegradation)
                return true;
        }
        return false;
    }

    private static bool AreMeshPair(InterferenceGraph graph, int i, int j)
    {
        return graph.MeshConstraints.Any(c =>
            (c.ParentIndex == i && c.ChildIndex == j) ||
            (c.ParentIndex == j && c.ChildIndex == i));
    }

    /// <summary>
    /// Get valid channels for an AP. When DFS avoidance leaves no channels at the current
    /// width (e.g. 160 MHz where both groups include DFS), falls back to the full channel
    /// list so the engine can still produce valid recommendations.
    /// </summary>
    private (int[] Channels, int Width, bool DfsFallback) GetValidChannelsWithWidth(
        RadioBand band, RadioSnapshot radio,
        RegulatoryChannelData? regulatoryData,
        DfsPreference dfsPref,
        int currentChannel)
    {
        var width = radio.ChannelWidth ?? 20;
        var channels = GetValidChannels(band, radio, regulatoryData, dfsPref, width);

        // If avoiding DFS left us with zero channels at the current width,
        // fall back to including DFS channels. At 160 MHz both 5 GHz groups
        // (36-64, 100-128) include DFS, so avoidance isn't possible without
        // a width reduction (future feature).
        if (channels.Length == 0 && dfsPref == DfsPreference.Exclude &&
            band == RadioBand.Band5GHz)
        {
            channels = GetValidChannels(band, radio, regulatoryData, DfsPreference.IncludeWithPenalty, width);
            return (DeduplicateByBondingGroup(channels, band, width, currentChannel), width, true);
        }

        return (DeduplicateByBondingGroup(channels, band, width, currentChannel), width, false);
    }

    /// <summary>
    /// Deduplicate channels that map to the same bonding group at the given width.
    /// At 80 MHz, channels 149/153/157/161 all produce the same (149-161) block,
    /// so the optimizer should only see one of them. Keeps the lowest channel
    /// (bonding group start) as the representative.
    /// </summary>
    private static int[] DeduplicateByBondingGroup(int[] channels, RadioBand band, int width, int? currentChannel = null)
    {
        if (width <= 20 || band == RadioBand.Band2_4GHz)
            return channels;

        var deduped = channels
            .GroupBy(ch => ChannelSpanHelper.GetChannelSpan(band, ch, width))
            .Select(g =>
            {
                // If the AP's current channel is in this group, keep it as the
                // representative so the optimizer can correctly identify "no change"
                if (currentChannel.HasValue && g.Contains(currentChannel.Value))
                    return currentChannel.Value;
                return g.Min();
            })
            .OrderBy(ch => ch)
            .ToArray();

        return deduped;
    }

    private int[] GetValidChannels(
        RadioBand band, RadioSnapshot radio,
        RegulatoryChannelData? regulatoryData,
        DfsPreference dfsPref,
        int? widthOverride = null)
    {
        // 2.4 GHz: ALWAYS restrict to 1, 6, 11 regardless of regulatory data.
        // Co-channel interference (managed by CSMA/CA) is far better than
        // adjacent channel overlap which cannot be mitigated.
        if (band == RadioBand.Band2_4GHz)
            return [1, 6, 11];

        var width = widthOverride ?? radio.ChannelWidth ?? 20;

        if (regulatoryData != null)
        {
            bool includeDfs = dfsPref != DfsPreference.Exclude &&
                              (band != RadioBand.Band5GHz || radio.HasDfs);
            var channels = regulatoryData.GetChannels(band, width, includeDfs);
            if (channels.Length > 0)
                return channels;

            // If regulatory data exists but returned empty (e.g. DFS excluded at 160 MHz),
            // return empty so GetValidChannelsWithWidth can fall back to Include DFS.
            if (dfsPref == DfsPreference.Exclude && band == RadioBand.Band5GHz)
                return [];
        }

        // Fallback defaults (only when no regulatory data available)
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

        // IncludeWithPenalty: mild penalty for occupying a DFS channel (radar-interruption risk).
        // The DFS-departure friction (the inverse bias - don't blindly leave DFS) lives in
        // ScoreAp/ScoreAssignment instead, so it applies on every move-decision path, not just the
        // search/altruistic paths that route through here.
        double penalty = 0;
        for (int i = 0; i < assignment.Length; i++)
        {
            // Span-aware: an 80/160 MHz block whose bonding span includes any DFS sub-channel is a
            // DFS assignment even when its control channel isn't (e.g. 160 MHz at control ch36 spans
            // 36-64, covering DFS 52-64). Uses the regulatory DFS set, matching the DFS badge.
            if (IsDfsAssignment(band, assignment[i].Channel, assignment[i].Width, graph.DfsChannels))
            {
                // Conservative confidence for now (no DFS event history available)
                double confidence = 0.7;
                penalty += DfsPenaltyBase * (1 - confidence);
            }
        }

        return baseScore + penalty;
    }

    /// <summary>
    /// Friction for moving an AP off a DFS channel onto a non-DFS channel we have no neighbor data
    /// for. Returns <see cref="DfsDepartureFrictionPenalty"/> only when the AP currently sits on a
    /// DFS channel, the proposed channel is non-DFS and different, and we have NO sighting on it at
    /// all - neither a direct scan by this AP nor a triangulated neighbor from a sibling that
    /// scanned it. A DFS channel is typically underused, so abandoning a working one for a channel
    /// no one has any data on risks trading a known-decent channel for a hidden mess. A channel a
    /// sibling has already scanned is real evidence (its measured load drives the score normally)
    /// and earns no friction. Gated by <see cref="InterferenceGraph.ApplyDfsDepartureFriction"/>
    /// (off in Avoid-DFS mode, where leaving DFS is the user's explicit goal).
    /// </summary>
    private double ComputeDfsDepartureFriction(
        InterferenceGraph graph,
        RadioBand band,
        int apIndex,
        (int Channel, int Width) assigned)
    {
        if (!graph.ApplyDfsDepartureFriction) return 0;

        var node = graph.Nodes[apIndex];
        if (assigned.Channel == node.CurrentChannel) return 0; // not moving

        // Must currently be on DFS and proposing a non-DFS channel.
        if (!IsDfsAssignment(band, node.CurrentChannel, node.CurrentWidth, graph.DfsChannels)) return 0;
        if (IsDfsAssignment(band, assigned.Channel, assigned.Width, graph.DfsChannels)) return 0;

        // Only when we have no neighbor data for the destination at all. Any external-load entry
        // overlapping it - direct or triangulated from a sibling's scan - is real evidence, so its
        // measured load already drives the score and no friction applies.
        var span = ChannelSpanHelper.GetChannelSpan(band, assigned.Channel, assigned.Width);
        bool haveEvidence = ExternalContributors(graph, band, apIndex)
            .Any(c => ChannelSpanHelper.SpansOverlap(span, c.Span));
        if (haveEvidence) return 0;

        return DfsDepartureFrictionPenalty;
    }

    /// <summary>
    /// Whether a 5 GHz channel assignment is subject to DFS. Considers the full bonding span, not
    /// just the control channel: an 80/160 MHz block whose span includes any DFS sub-channel is a
    /// DFS assignment (the radio must perform radar detection across the whole block). Uses the
    /// site's regulatory DFS set when available, falling back to the standard UNII-2/2C ranges.
    /// Mirrors the bonding-group DFS logic in <see cref="RegulatoryChannelData.GetChannels"/>.
    /// </summary>
    private static bool IsDfsAssignment(RadioBand band, int channel, int width, HashSet<int> dfsSet)
    {
        if (band != RadioBand.Band5GHz) return false;
        var span = ChannelSpanHelper.GetChannelSpan(band, channel, width);
        for (int c = span.Low; c <= span.High; c += 4)
        {
            bool isDfs = dfsSet.Count > 0
                ? dfsSet.Contains(c)
                : (c >= 52 && c <= 64) || (c >= 100 && c <= 144);
            if (isDfs) return true;
        }
        return false;
    }

    private ((int Channel, int Width)[] Assignment, double Score) ExhaustiveSearch(
        InterferenceGraph graph,
        RadioBand band,
        HashSet<int> pinnedIndices,
        RecommendationOptions opts,
        double[] currentApScores)
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
                    // Reject if any AP degrades too much
                    if (ViolatesApDegradation(graph, withMesh, currentApScores, band))
                        return;

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
        RecommendationOptions opts,
        double[] currentApScores)
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

                            if (newScore < currentScore &&
                                !ViolatesApDegradation(graph, assignment, currentApScores, band))
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
                if (!ViolatesApDegradation(graph, assignment, currentApScores, band))
                {
                    bestScore = finalScore;
                    Array.Copy(assignment, bestAssignment, n);
                }
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

    private void LogGraphDetails(InterferenceGraph graph, RadioBand band, List<AccessPointSnapshot> bandAps, RecommendationOptions? options = null)
    {
        var n = graph.Nodes.Count;
        if (n == 0) return;

        var sb = new StringBuilder();
        var dfsMode = options?.DfsPreference switch
        {
            DfsPreference.IncludeWithPenalty => ", DFS=Include",
            DfsPreference.Exclude => ", DFS=Avoid",
            DfsPreference.Prefer => ", DFS=Prefer",
            _ => ""
        };
        sb.AppendLine($"[ChannelRec] === Interference Graph for {band} ({n} APs{dfsMode}) ===");

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

        // External load per AP per channel (includes direct observations + triangulated from other APs)
        sb.AppendLine("  External load (direct + triangulated neighbor weight, by channel):");
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

        // Scan channel data per AP (with the scan's true age, so a stale reading is visible here).
        var scanNow = DateTimeOffset.UtcNow;
        sb.AppendLine("  Scan channel metrics (utilization / noise floor):");
        for (int i = 0; i < n; i++)
        {
            var scanAge = FormatScanAge(graph.Nodes[i].SpectrumScanTime, scanNow);
            if (graph.ScanChannelData[i].Count == 0)
            {
                sb.AppendLine($"    {graph.Nodes[i].Name} ({scanAge}): (no scan channel data)");
                continue;
            }
            var metrics = graph.ScanChannelData[i]
                .OrderBy(kv => kv.Key.Channel).ThenBy(kv => kv.Key.Width)
                .Select(kv => $"ch{kv.Key.Channel}/{kv.Key.Width}=util:{kv.Value.Utilization}%/nf:{(kv.Value.NoiseFloor.HasValue ? kv.Value.NoiseFloor + "dBm" : "n/a")}");
            sb.AppendLine($"    {graph.Nodes[i].Name} ({scanAge}): {string.Join(", ", metrics)}");
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

        var bandStress = GetBandStressMultiplier(band);
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
                    internalScore += graph.DirectionalWeights[j, i] * overlap * InternalCoChannelMultiplier;
                }

                var apSpan = ChannelSpanHelper.GetChannelSpan(band, ch, currentAssignment[i].Width);
                foreach (var (extSpan, extW) in ExternalContributors(graph, band, i))
                {
                    if (ChannelSpanHelper.SpansOverlap(apSpan, extSpan))
                        externalScore += extW;
                }

                // #2 measured floor: a candidate channel's congestion is raised to what the radio
                // measured where that exceeds the neighbor-scan proxy (matches ScoreAp).
                var measuredFloor = MeasuredCongestionLoad(graph, band, i, testAssignment);
                if (measuredFloor > externalScore) externalScore = measuredFloor;

                // Scan: measured utilization + noise floor, band-weighted, span-aggregated, with the
                // current channel read cross-vantage (matches ScoreAp).
                double scanUtil = 0, scanNoise = 0;
                if (ScanReadingForScoring(graph, band, i, ch, currentAssignment[i].Width) is { } scanData)
                {
                    scanUtil = scanData.Utilization * ScanUtilizationWeight * bandStress;
                    scanNoise = ScanNoiseFloorPenalty(scanData.NoiseFloor) * bandStress;
                }
                scanScore = scanUtil + scanNoise;

                // Stress: reuse the real penalty so the breakdown reconciles with the score
                // (measured historical stress un-scaled, neighbor-propagated fallback band-weighted).
                var (histPenalty, fallbackPenalty) = ComputeStressPenalty(graph, band, i, testAssignment);
                var stressScore = histPenalty + fallbackPenalty * bandStress;

                // Unobserved channel uncertainty (confidence-weighted, matches ScoreAp/ScoreAssignment)
                double unobservedPenalty = ComputeUnobservedPenalty(graph, band, i, testAssignment);

                var total = internalScore + externalScore + scanScore + stressScore + unobservedPenalty;
                var marker = ch == currentAssignment[i].Channel ? " <<<" : "";
                var scanStr = scanScore > 0 ? $" + scan={scanScore:F3}(util {scanUtil:F2}/nf {scanNoise:F2})" : "";
                var stressStr = stressScore > 0 ? $" + stress={stressScore:F3}" : "";
                var unobsStr = unobservedPenalty > 0 ? $" + unobs={unobservedPenalty:F3}" : "";
                sb.AppendLine($"    ch{ch,3}: internal={internalScore:F3} + external={externalScore:F3}{scanStr}{stressStr}{unobsStr} = {total:F3}{marker}");
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

    /// <summary>
    /// Records per-AP scan staleness/materiality onto the plan and, when debug is on, logs the full
    /// breakdown. For each AP: how old its spectrum scan is; whether that age is load-bearing (the
    /// measured-worse guard held it on its noise-floor arm, or the scan term was decisive in the pick);
    /// and whether the fresher neighbor scan still corroborates the reading. Sets
    /// <see cref="ApChannelRecommendation.ScanRescanRecommended"/> when the scan is stale AND material
    /// AND uncorroborated - the only case where a re-scan could change the answer. The expensive
    /// decisive check only runs for stale APs (the only ones that can be prompt-material) unless debug
    /// is on and wants the full diagnostic. Changes no recommendation.
    /// </summary>
    private void RecordAndLogScanMateriality(
        ChannelPlan plan,
        InterferenceGraph graph,
        RadioBand band,
        (int Channel, int Width)[] finalAssignment,
        IReadOnlyDictionary<int, (int RejectedChannel, MeasuredWorseReason Reason)> measuredWorseHolds)
    {
        var n = graph.Nodes.Count;
        if (n == 0) return;

        var now = DateTimeOffset.UtcNow;
        var debug = _logger.IsEnabled(LogLevel.Debug);
        StringBuilder? sb = debug ? new StringBuilder() : null;
        sb?.AppendLine($"[ChannelRec] === SCAN MATERIALITY ({band}) ===");

        for (int i = 0; i < n; i++)
        {
            var node = graph.Nodes[i];
            var rec = plan.Recommendations[i];
            rec.SpectrumScanTime = node.SpectrumScanTime;
            var stale = node.SpectrumScanTime is { } t && (now - t) > SpectrumScanStaleAfter;
            var age = FormatScanAge(node.SpectrumScanTime, now);

            // Held-by-guard case: the scan (or measured interference) is directly load-bearing.
            if (measuredWorseHolds.TryGetValue(i, out var hold))
            {
                var (rejectedCh, reason) = hold;
                var ext = ExternalNeighborWeightOn(graph, band, i, rejectedCh, node.CurrentWidth);
                var corroborated = ext >= CorroborationMinExternalWeight;

                // A re-scan can only change a NOISE-FLOOR-arm hold on a STALE, UNCORROBORATED channel:
                // an interference-arm hold reads measured history (a spectrum re-scan won't refresh it),
                // and a corroborated floor is confirmed by the fresher neighbor scan.
                if (reason.NoiseFloorArm && stale && !corroborated)
                {
                    rec.ScanRescanRecommended = true;
                    rec.ScanRescanReason =
                        $"held off ch{rejectedCh} by a {age} spectrum reading the neighbor scan no longer corroborates";
                }

                sb?.AppendLine(
                    $"  {node.Name} (scan {age}): HELD off ch{rejectedCh} by measured-worse guard " +
                    $"[{DescribeMeasuredWorseArms(reason)}]; ch{rejectedCh} floor " +
                    $"{(corroborated ? "CORROBORATED" : "UNCORROBORATED")} by neighbors (ext={ext:F2})" +
                    $"{(rec.ScanRescanRecommended ? " -> RE-SCAN RECOMMENDED" : "")}");
                continue;
            }

            // A mesh child's channel is dictated by its leader, and the stale-target service excludes
            // it (it can't be re-scanned without dropping its uplink), so the decisive/re-scan analysis
            // doesn't apply - and its per-channel trials get reset by ApplyMeshConstraints anyway, which
            // would make the "decisive" verdict meaningless. Skip it.
            if (node.MeshGroupLeader >= 0 && node.MeshGroupLeader != i)
            {
                sb?.AppendLine($"  {node.Name} (scan {age}): mesh child, follows leader");
                continue;
            }

            // General case: only stale APs can be prompt-material, so skip the decisive check's cost
            // for fresh scans unless debug wants the full breakdown.
            if (!stale && !debug) continue;

            // Is the scan term what tips this AP's recommended channel over its best alternative? Strip
            // the scan term from both and see whether the ranking flips.
            var recCh = finalAssignment[i].Channel;
            var recWidth = finalAssignment[i].Width;
            var recTotal = ScoreAp(graph, finalAssignment, i, band);
            var recScan = ComputeScanScore(graph, band, i, recCh, recWidth);

            int altCh = -1;
            double altTotal = double.MaxValue, altScan = 0;
            foreach (var ch in node.ValidChannels)
            {
                if (ch == recCh) continue;
                var trial = ((int Channel, int Width)[])finalAssignment.Clone();
                trial[i] = (ch, recWidth);
                ApplyMeshConstraints(graph, trial);
                var t2 = ScoreAp(graph, trial, i, band);
                if (t2 < altTotal) { altTotal = t2; altCh = ch; altScan = ComputeScanScore(graph, band, i, ch, recWidth); }
            }

            var ago = ExternalNeighborWeightOn(graph, band, i, recCh, recWidth);
            // Decisive = rec currently wins, but with the scan term removed the alternative would.
            var decisive = altCh >= 0 && recTotal <= altTotal && (recTotal - recScan) > (altTotal - altScan);
            var recCorroborated = ago >= CorroborationMinExternalWeight;

            if (decisive && stale && !recCorroborated)
            {
                rec.ScanRescanRecommended = true;
                rec.ScanRescanReason =
                    $"a {age} spectrum reading the neighbor scan no longer corroborates is deciding ch{recCh}";
            }

            if (sb != null)
            {
                if (altCh < 0)
                    sb.AppendLine($"  {node.Name} (scan {age}): rec ch{recCh}, no alternative channel");
                else
                {
                    var verdict = decisive
                        ? $"scan term DECISIVE over alt ch{altCh} (ch{recCh} {recTotal:F2} vs ch{altCh} {altTotal:F2}; without scan the alt wins)"
                        : $"scan term not decisive (best alt ch{altCh})";
                    sb.AppendLine($"  {node.Name} (scan {age}): rec ch{recCh}, {verdict}; ch{recCh} ext={ago:F2}" +
                        $"{(rec.ScanRescanRecommended ? " -> RE-SCAN RECOMMENDED" : "")}");
                }
            }
        }

        if (sb != null) _logger.LogDebug("{ScanMateriality}", sb.ToString());
    }

    /// <summary>Short human label for which measured-worse arm(s) tripped, with the compared values.</summary>
    private static string DescribeMeasuredWorseArms(MeasuredWorseReason r)
    {
        var parts = new List<string>(2);
        if (r.NoiseFloorArm)
            parts.Add($"noise-floor {r.RecommendedNoiseFloor}dBm vs {r.CurrentNoiseFloor}dBm");
        if (r.InterferenceArm)
            parts.Add($"interference {r.RecommendedInterferencePct:F0}% vs {r.CurrentInterferencePct:F0}%");
        return parts.Count > 0 ? string.Join("; ", parts) : "no arm";
    }

    /// <summary>The scan-derived score term (utilization + noise floor, band-weighted) an AP would carry
    /// on a channel - the same computation <see cref="ScoreAp"/> adds, isolated so materiality can strip it.</summary>
    private double ComputeScanScore(InterferenceGraph graph, RadioBand band, int apIndex, int channel, int width)
    {
        var bandStress = GetBandStressMultiplier(band);
        if (ScanReadingForScoring(graph, band, apIndex, channel, width) is { } s)
            return s.Utilization * ScanUtilizationWeight * bandStress + ScanNoiseFloorPenalty(s.NoiseFloor) * bandStress;
        return 0;
    }

    /// <summary>Total pooled external-neighbor weight overlapping a channel span, for corroboration checks.</summary>
    private double ExternalNeighborWeightOn(InterferenceGraph graph, RadioBand band, int apIndex, int channel, int width)
    {
        var span = ChannelSpanHelper.GetChannelSpan(band, channel, width);
        double w = 0;
        foreach (var (extSpan, extWeight) in ExternalContributors(graph, band, apIndex))
            if (ChannelSpanHelper.SpansOverlap(span, extSpan))
                w += extWeight;
        return w;
    }

    /// <summary>Human-readable age of a scan timestamp relative to now, for debug logs.</summary>
    private static string FormatScanAge(DateTimeOffset? scanTime, DateTimeOffset now)
    {
        if (scanTime is not { } t) return "age unknown";
        var age = now - t;
        if (age < TimeSpan.Zero) return "just now";
        if (age.TotalMinutes < 90) return $"{age.TotalMinutes:F0}m old";
        if (age.TotalHours < 48) return $"{age.TotalHours:F1}h old";
        return $"{age.TotalDays:F1}d old";
    }
}
