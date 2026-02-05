using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.WiFi.Models;

namespace NetworkOptimizer.WiFi.Rules;

/// <summary>
/// Context containing all data needed by WiFi Optimizer rules.
/// </summary>
public class WiFiOptimizerContext
{
    /// <summary>
    /// WLAN (SSID) configurations.
    /// </summary>
    public required List<WlanConfiguration> Wlans { get; init; }

    /// <summary>
    /// Network configurations (classified by VlanAnalyzer).
    /// </summary>
    public required List<NetworkInfo> Networks { get; init; }

    /// <summary>
    /// Access point snapshots.
    /// </summary>
    public required List<AccessPointSnapshot> AccessPoints { get; init; }

    /// <summary>
    /// All wireless clients.
    /// </summary>
    public required List<WirelessClientSnapshot> Clients { get; init; }

    /// <summary>
    /// Legacy clients (2.4 GHz only, cannot be steered to higher bands).
    /// </summary>
    public required List<WirelessClientSnapshot> LegacyClients { get; init; }

    /// <summary>
    /// Clients that could be on a higher band than they're currently on.
    /// </summary>
    public required List<WirelessClientSnapshot> SteerableClients { get; init; }

    // Convenience accessors

    /// <summary>
    /// All IoT networks (by VlanAnalyzer classification).
    /// </summary>
    public IEnumerable<NetworkInfo> IoTNetworks => Networks.Where(n => n.Enabled && n.Purpose == NetworkPurpose.IoT);

    /// <summary>
    /// First Security/Camera network found.
    /// </summary>
    public NetworkInfo? SecurityNetwork => Networks.FirstOrDefault(n => n.Enabled && n.Purpose == NetworkPurpose.Security);

    /// <summary>
    /// Main networks (Home or Corporate purpose).
    /// </summary>
    public IEnumerable<NetworkInfo> MainNetworks => Networks.Where(n =>
        n.Enabled && n.Purpose is NetworkPurpose.Home or NetworkPurpose.Corporate);

    /// <summary>
    /// Whether any APs have 5 GHz radios active.
    /// </summary>
    public bool Has5GHzCoverage => AccessPoints.Any(ap =>
        ap.Radios.Any(r => r.Band == RadioBand.Band5GHz && r.Channel.HasValue));

    /// <summary>
    /// Whether any APs have 6 GHz radios active.
    /// </summary>
    public bool Has6GHzCoverage => AccessPoints.Any(ap =>
        ap.Radios.Any(r => r.Band == RadioBand.Band6GHz && r.Channel.HasValue));
}
