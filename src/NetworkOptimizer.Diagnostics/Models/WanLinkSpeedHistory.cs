namespace NetworkOptimizer.Diagnostics.Models;

/// <summary>
/// How often one gateway WAN port negotiated below its usual speed over the lookback window,
/// read from the recorded link speed history. Gathered by the Web layer, which owns InfluxDB.
/// </summary>
public sealed record WanLinkSpeedHistory
{
    /// <summary>Gateway MAC.</summary>
    public required string DeviceMac { get; init; }

    /// <summary>Kernel interface name of the WAN port, e.g. "eth4".</summary>
    public required string IfName { get; init; }

    /// <summary>The fastest speed the port held over the window, in Mbps: what it normally reaches.</summary>
    public required int UsualMbps { get; init; }

    /// <summary>The slowest speed it dropped to, in Mbps.</summary>
    public required int LowestMbps { get; init; }

    /// <summary>Separate stretches spent below <see cref="UsualMbps"/>.</summary>
    public required int DegradedEpisodes { get; init; }

    /// <summary>Approximate total time spent below <see cref="UsualMbps"/>.</summary>
    public required TimeSpan DegradedTime { get; init; }

    /// <summary>How far back the history reaches: the lookback, or less while the history is still filling.</summary>
    public required TimeSpan Lookback { get; init; }
}
