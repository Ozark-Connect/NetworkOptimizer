using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Gates;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// History drawn behind a reading as a sparkline: the last 24 hours of each Dashboard signal hero
/// (ONT Rx power, cable modem downstream SNR, cellular signal quality and RSRP), and the last hour
/// of Live View's tiles. Read from InfluxDB history; null when there is none.
///
/// Site-scoped, Viewer: the Dashboard and Live View cards that show these are open to any role.
/// </summary>
[MutatingService(SiteScoped = true)]
public interface ISignalSparklineService
{
    /// <summary>Rx optical power of an SFP ONT module, in dBm.</summary>
    [RequireRole(Roles.Viewer)]
    Task<IReadOnlyList<SparkPoint>?> GetSfpRxAsync(string deviceMac, string portName);

    /// <summary>Rx optical power of a standalone ONT, in dBm.</summary>
    [RequireRole(Roles.Viewer)]
    Task<IReadOnlyList<SparkPoint>?> GetOntRxAsync(int ontId);

    /// <summary>Average downstream SNR of a cable modem, in dB.</summary>
    [RequireRole(Roles.Viewer)]
    Task<IReadOnlyList<SparkPoint>?> GetCmSnrAsync(int cmId);

    /// <summary>RSRP of a cellular modem's 5G NR leg (<paramref name="nr"/>) or LTE leg, in dBm.</summary>
    [RequireRole(Roles.Viewer)]
    Task<IReadOnlyList<SparkPoint>?> GetCellularRsrpAsync(int modemId, bool nr);

    /// <summary>Signal quality of a cellular modem, 0 to 100.</summary>
    [RequireRole(Roles.Viewer)]
    Task<IReadOnlyList<SparkPoint>?> GetCellularQualityAsync(int modemId);

    /// <summary>
    /// The last hour of every Live View tile at once, from two queries: one for all the latency tiles
    /// (Gateway, ISP, Transit; RTT and loss) and one for the gateway's health. Every tile passes the
    /// same request, and callers asking at the same time share one fetch.
    /// </summary>
    [RequireRole(Roles.Viewer)]
    Task<LiveViewSparklines> GetLiveViewAsync(LiveViewSparkRequest request);
}

/// <summary>
/// What Live View's tiles read: the gateway, its fabric target, and the ISP and Transit targets in the
/// current WAN scope.
/// </summary>
public sealed record LiveViewSparkRequest(
    string? GatewayMac,
    string? GatewayTargetId,
    IReadOnlyList<string> IspTargetIds,
    IReadOnlyList<string> TransitTargetIds)
{
    /// <summary>Identity for caching: the same tiles in the same scope ask for the same thing.</summary>
    public string Key =>
        $"{GatewayMac}|{GatewayTargetId}|{string.Join(",", IspTargetIds.Order(StringComparer.Ordinal))}|{string.Join(",", TransitTargetIds.Order(StringComparer.Ordinal))}";
}

/// <summary>The Live View tiles that carry a sparkline.</summary>
public enum LiveViewSpark
{
    GatewayRtt, GatewayLoss, GatewayCpu, GatewayMemory, GatewayTemperature,
    IspRtt, IspLoss, TransitRtt, TransitLoss,
}

/// <summary>Each Live View tile's last hour; a tile with no history has no entry.</summary>
public sealed record LiveViewSparklines(IReadOnlyDictionary<LiveViewSpark, IReadOnlyList<SparkPoint>> Series)
{
    /// <summary>No history for any tile.</summary>
    public static readonly LiveViewSparklines Empty = new(new Dictionary<LiveViewSpark, IReadOnlyList<SparkPoint>>());

    /// <summary>The tile's points, or null.</summary>
    public IReadOnlyList<SparkPoint>? For(LiveViewSpark tile) => Series.TryGetValue(tile, out var v) ? v : null;
}

/// <summary>One sparkline sample: the mean of a reading over one window.</summary>
public readonly record struct SparkPoint(DateTime Time, double Value);
