using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.Audit.Models;

/// <summary>
/// Wrapper for a wireless client with detection results for audit analysis
/// </summary>
public class WirelessClientInfo
{
    /// <summary>
    /// The UniFi client response data
    /// </summary>
    public required UniFiClientResponse Client { get; init; }

    /// <summary>
    /// The network this client is connected to
    /// </summary>
    public NetworkInfo? Network { get; init; }

    /// <summary>
    /// Device type detection result
    /// </summary>
    public required DeviceDetectionResult Detection { get; init; }

    /// <summary>
    /// Name of the access point this client is connected to
    /// </summary>
    public string? AccessPointName { get; init; }

    /// <summary>
    /// MAC address of the access point
    /// </summary>
    public string? AccessPointMac { get; init; }

    /// <summary>
    /// Display name for the client (name, hostname, or MAC)
    /// </summary>
    public string DisplayName => Client.Name ?? Client.Hostname ?? Client.Mac ?? "Unknown";

    /// <summary>
    /// MAC address of the client
    /// </summary>
    public string? Mac => Client.Mac;
}
