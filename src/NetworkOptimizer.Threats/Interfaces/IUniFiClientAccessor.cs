using NetworkOptimizer.UniFi;

namespace NetworkOptimizer.Threats.Interfaces;

/// <summary>
/// Provides access to the UniFi API client without coupling to the Web project.
/// Implemented in the Web project using UniFiConnectionService.
/// </summary>
public interface IUniFiClientAccessor
{
    /// <summary>
    /// Gets the current UniFi API client, or null if not connected.
    /// </summary>
    UniFiApiClient? Client { get; }

    /// <summary>
    /// Whether the UniFi controller connection is established.
    /// </summary>
    bool IsConnected { get; }
}
