using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Gates;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// The last 24 hours of the reading each Dashboard signal hero shows (ONT Rx power, cable modem
/// downstream SNR, cellular signal quality), drawn behind it as a sparkline. Read from InfluxDB
/// history; null when there is none.
///
/// Site-scoped, Viewer: the Dashboard cards that show these are open to any role.
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

    /// <summary>Signal quality of a cellular modem, 0 to 100.</summary>
    [RequireRole(Roles.Viewer)]
    Task<IReadOnlyList<SparkPoint>?> GetCellularQualityAsync(int modemId);
}

/// <summary>One sparkline sample: the mean of a reading over one window.</summary>
public readonly record struct SparkPoint(DateTime Time, double Value);
