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

    /// <summary>RSRP of a cellular modem's 5G NR leg (<paramref name="nr"/>) or LTE leg, in dBm.</summary>
    [RequireRole(Roles.Viewer)]
    Task<IReadOnlyList<SparkPoint>?> GetCellularRsrpAsync(int modemId, bool nr);

    /// <summary>Signal quality of a cellular modem, 0 to 100.</summary>
    [RequireRole(Roles.Viewer)]
    Task<IReadOnlyList<SparkPoint>?> GetCellularQualityAsync(int modemId);

    /// <summary>A device's health reading: CPU, memory, or temperature (Live View's gateway tiles).</summary>
    [RequireRole(Roles.Viewer)]
    Task<IReadOnlyList<SparkPoint>?> GetDeviceHealthAsync(string deviceMac, DeviceHealthField field);

    /// <summary>A monitoring target's average RTT (ms) or packet loss (%), across every vantage and WAN that probes it.</summary>
    [RequireRole(Roles.Viewer)]
    Task<IReadOnlyList<SparkPoint>?> GetTargetLatencyAsync(string targetId, LatencyField field);
}

/// <summary>The device_health fields a sparkline can draw.</summary>
public enum DeviceHealthField { Cpu, Memory, Temperature }

/// <summary>The latency fields a sparkline can draw.</summary>
public enum LatencyField { Rtt, Loss }

/// <summary>One sparkline sample: the mean of a reading over one window.</summary>
public readonly record struct SparkPoint(DateTime Time, double Value);
