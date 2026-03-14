namespace NetworkOptimizer.WiFi.Models;

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
    public bool IsDfsChannel { get; set; }
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
    public double ImprovementPercent => CurrentNetworkScore > 0
        ? (CurrentNetworkScore - RecommendedNetworkScore) / CurrentNetworkScore * 100 : 0;
    public int UnplacedApCount { get; set; }
    public bool HasScanData { get; set; }
    public bool HasBuildingData { get; set; }
}

/// <summary>
/// Interference graph representing pairwise AP interference and external loads.
/// Public for testability.
/// </summary>
public class InterferenceGraph
{
    public List<ApNode> Nodes { get; set; } = new();

    /// <summary>Pairwise internal interference weights [i,j]</summary>
    public double[,] InternalWeights { get; set; } = new double[0, 0];

    /// <summary>Per-AP external load by channel number. ExternalLoad[apIndex][channel] = weight</summary>
    public Dictionary<int, double>[] ExternalLoad { get; set; } = [];

    /// <summary>Per-AP channel scan metrics (utilization/interference). ScanChannelData[apIndex][channel] = (util, interf)</summary>
    public Dictionary<int, (int Utilization, int Interference)>[] ScanChannelData { get; set; } = [];

    public List<MeshConstraint> MeshConstraints { get; set; } = new();
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
    /// Per-channel historical stress from 30-day metrics paired with channel change events.
    /// Key = channel number, Value = (avg utilization %, avg interference %, avg TX retry %).
    /// Null if historical data is unavailable.
    /// </summary>
    public Dictionary<int, (double Utilization, double Interference, double TxRetryPct)>? HistoricalStress { get; set; }

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
