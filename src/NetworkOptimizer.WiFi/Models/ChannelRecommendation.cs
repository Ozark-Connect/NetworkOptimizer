namespace NetworkOptimizer.WiFi.Models;

/// <summary>
/// An (AP, band) that has no recent per-channel spectrum-scan measurement, so its channel
/// recommendation falls back to the neighbor (external) scan. A quick-scan can fill it.
/// <paramref name="HasDedicatedScanRadio"/> = the AP scans on a dedicated radio (no client impact);
/// otherwise scanning briefly interrupts that band. <paramref name="IsMeshParent"/> = scanning this
/// AP (when it lacks a dedicated scan radio) can also briefly drop devices meshed through it.
/// </summary>
public record SpectrumScanGap(
    string ApMac, string ApName, RadioBand Band, string BandCode, bool HasDedicatedScanRadio, bool IsMeshParent);

/// <summary>
/// Per-AP channel recommendation: current vs recommended (channel, width) tuple.
/// </summary>
public class ApChannelRecommendation
{
    public string ApMac { get; set; } = string.Empty;
    public string ApName { get; set; } = string.Empty;
    public RadioBand Band { get; set; }

    // Current state
    public int CurrentChannel { get; set; }
    public int CurrentWidth { get; set; }

    // Recommendation (width = current for now, ready for width optimization)
    public int RecommendedChannel { get; set; }
    public int RecommendedWidth { get; set; }

    public double CurrentScore { get; set; }
    public double RecommendedScore { get; set; }

    public bool IsChanged => CurrentChannel != RecommendedChannel || CurrentWidth != RecommendedWidth;
    public bool IsMeshConstrained { get; set; }
    public bool IsUnplaced { get; set; }

    /// <summary>Whether the AP's current channel span is subject to DFS.</summary>
    public bool IsCurrentDfsChannel { get; set; }

    /// <summary>Whether the recommended channel span is subject to DFS.</summary>
    public bool IsRecommendedDfsChannel { get; set; }
}

/// <summary>
/// Network-wide channel plan result for a single band.
/// </summary>
public class ChannelPlan
{
    public RadioBand Band { get; set; }
    public List<ApChannelRecommendation> Recommendations { get; set; } = new();
    public double CurrentNetworkScore { get; set; }
    public double RecommendedNetworkScore { get; set; }
    public double ImprovementPercent
    {
        get
        {
            if (CurrentNetworkScore <= 0) return 0;
            var pct = (CurrentNetworkScore - RecommendedNetworkScore) / CurrentNetworkScore * 100;
            // Cap percentage when absolute improvement is small - "90% less interference"
            // is misleading when going from 0.8 to 0.1. Scale cap by absolute improvement.
            var absoluteImprovement = CurrentNetworkScore - RecommendedNetworkScore;
            if (absoluteImprovement < 2.0)
                pct = Math.Min(pct, absoluteImprovement / 2.0 * 100);
            return Math.Max(pct, 0);
        }
    }
    public int UnplacedApCount { get; set; }
    public bool HasScanData { get; set; }
    public bool HasNeighborNetworks { get; set; }

    /// <summary>
    /// True when we have per-channel spectrum measurements (utilization / noise floor) for this band,
    /// so the recommendation is grounded in measured airtime and RF noise - not just neighbor scans or
    /// internal AP interference - even when no neighbor networks were detected.
    /// </summary>
    public bool HasMeasuredChannelData { get; set; }
    public bool HasBuildingData { get; set; }

    /// <summary>
    /// True when DFS avoidance was requested but isn't possible at the current channel width
    /// (e.g. 160 MHz where all bonding groups include DFS channels).
    /// </summary>
    public bool DfsAvoidanceNotPossible { get; set; }
}

/// <summary>
/// Interference graph representing pairwise AP interference and external loads.
/// Public for testability.
/// </summary>
public class InterferenceGraph
{
    public List<ApNode> Nodes { get; set; } = new();

    /// <summary>True when DFS avoidance was requested but at least one AP had to fall back to DFS channels</summary>
    public bool DfsAvoidanceFallback { get; set; }

    /// <summary>
    /// Pairwise internal interference weights [i,j], symmetric (worst case of both directions).
    /// Models mutual co-channel contention - used for the global objective and proximity.
    /// </summary>
    public double[,] InternalWeights { get; set; } = new double[0, 0];

    /// <summary>
    /// Directional internal interference weights [aggressor, victim] = how much the aggressor AP
    /// interferes AT the victim, based on the aggressor's signal reaching the victim (its EIRP).
    /// Used where interference is one-directional: an AP's own suffered interference and the
    /// degradation guard. A low-power AP correctly interferes with neighbors less than the
    /// symmetric worst case implies.
    /// </summary>
    public double[,] DirectionalWeights { get; set; } = new double[0, 0];

    /// <summary>Per-AP external load by channel number. ExternalLoad[apIndex][channel] = total weight</summary>
    public Dictionary<int, double>[] ExternalLoad { get; set; } = [];

    /// <summary>
    /// Per-AP external load pooled by (control channel, width). ExternalNeighbors[apIndex][(channel,
    /// width)] = weight. Keeping weight separated by width lets the scorer model each neighbor's true
    /// spectral footprint: a 40 MHz neighbor on 2.4 GHz ch11 steps on ch6, so its weight counts
    /// against any candidate channel its span overlaps - WITHOUT dragging the 20 MHz neighbors that
    /// share its control channel along with it (pooling everything to one width over-spilled them).
    /// When empty (e.g. a unit test that sets only <see cref="ExternalLoad"/>), the scorer falls back
    /// to treating each external-load channel as a 20 MHz point.
    /// </summary>
    public Dictionary<(int Channel, int Width), double>[] ExternalNeighbors { get; set; } = [];

    /// <summary>
    /// Per-AP set of channels with at least one direct neighbor observation.
    /// Channels NOT in this set have only triangulated (estimated) external load data,
    /// which is less reliable. Used by the scorer to penalize unobserved channels.
    /// </summary>
    public HashSet<int>[] DirectlyObservedChannels { get; set; } = [];

    /// <summary>
    /// Per-AP channel scan metrics, keyed by (control channel, scan bandwidth MHz) so multiple-width
    /// buckets for the same channel can coexist (e.g. a BW20 and a BW160 reading of ch36). Value =
    /// (utilization %, noise floor dBm); NoiseFloor is null when the scan reported no reading, lower
    /// (more negative) is cleaner. The scorer reads it via ScanOverSpan, which prefers an exact
    /// operating-width bucket and otherwise aggregates the finest sub-channels across the span.
    /// </summary>
    public Dictionary<(int Channel, int Width), (int Utilization, int? NoiseFloor)>[] ScanChannelData { get; set; } = [];

    public List<MeshConstraint> MeshConstraints { get; set; } = new();

    /// <summary>Whether scan results existed for this band (UniFi provided RF scan data)</summary>
    public bool HasScanData { get; set; }

    /// <summary>
    /// 5 GHz DFS channels for the site's regulatory domain (from <see cref="RegulatoryChannelData"/>).
    /// Empty when no regulatory data is available, in which case DFS reasoning falls back to the
    /// standard UNII-2/2C ranges. Used for the DFS badge and the DFS-departure friction.
    /// </summary>
    public HashSet<int> DfsChannels { get; set; } = new();

    /// <summary>
    /// When true, the scorer adds friction to moving an AP off a DFS channel onto a non-DFS
    /// channel it has no scan data for. Set by the optimizer for 5 GHz except in Avoid-DFS mode,
    /// where leaving DFS is the user's explicit goal. Off by default so direct scorer calls are
    /// unaffected.
    /// </summary>
    public bool ApplyDfsDepartureFriction { get; set; }
}

/// <summary>
/// A node in the interference graph representing one AP radio.
/// </summary>
public class ApNode
{
    public string Mac { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int CurrentChannel { get; set; }
    public int CurrentWidth { get; set; }
    public int[] ValidChannels { get; set; } = [];
    public int[] ValidWidths { get; set; } = [];
    public bool IsPlaced { get; set; }
    public bool HasDfs { get; set; }

    /// <summary>Current channel utilization % (0-100)</summary>
    public int ChannelUtilization { get; set; }

    /// <summary>Current interference % (0-100)</summary>
    public int Interference { get; set; }

    /// <summary>Current TX retry % (0-100)</summary>
    public double TxRetriesPct { get; set; }

    /// <summary>
    /// Per-channel historical stress from 30-day metrics paired with channel change events. This is
    /// the AP's OWN measured reality - "ground truth" for the channels it has actually sat on.
    /// Key = channel number, Value = (avg utilization %, avg interference %, avg TX retry %).
    /// Null if historical data is unavailable.
    /// </summary>
    public Dictionary<int, (double Utilization, double Interference, double TxRetryPct)>? HistoricalStress { get; set; }

    /// <summary>
    /// Per-channel stress ESTIMATED from nearby APs' measurements (proximity-scaled, dampened) for
    /// channels this AP has never sat on. Kept separate from <see cref="HistoricalStress"/> so the
    /// scorer can use it as a soft estimate for the stress penalty WITHOUT it masquerading as this
    /// AP's own measurement - the "ground-truth" consumers (comfort anchor, measured floor's sibling
    /// lookup, observation confidence) read only the real <see cref="HistoricalStress"/>.
    /// </summary>
    public Dictionary<int, (double Utilization, double Interference, double TxRetryPct)>? PropagatedStress { get; set; }

    /// <summary>Index of this AP's mesh group leader, or -1 if not in a mesh group</summary>
    public int MeshGroupLeader { get; set; } = -1;
}

/// <summary>
/// Mesh constraint: child and parent must share the same channel on the uplink band.
/// </summary>
public class MeshConstraint
{
    public int ParentIndex { get; set; }
    public int ChildIndex { get; set; }
    public RadioBand UplinkBand { get; set; }
}

/// <summary>
/// How to handle DFS channels in recommendations.
/// </summary>
public enum DfsPreference
{
    /// <summary>Include DFS channels with a penalty score</summary>
    IncludeWithPenalty,
    /// <summary>Exclude DFS channels entirely</summary>
    Exclude,
    /// <summary>Treat DFS same as non-DFS (no penalty)</summary>
    Prefer
}

/// <summary>
/// Options for the channel recommendation engine.
/// </summary>
public class RecommendationOptions
{
    public DfsPreference DfsPreference { get; set; } = DfsPreference.IncludeWithPenalty;
    public HashSet<string>? PinnedApMacs { get; set; }
    public bool OptimizeWidths { get; set; } = false;
}
