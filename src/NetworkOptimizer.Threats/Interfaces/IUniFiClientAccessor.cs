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

    /// <summary>
    /// The API client for a specific site (null/empty slug = the default/main site), or null
    /// if that site isn't connected. Used by per-site background collection.
    /// </summary>
    UniFiApiClient? GetClient(string? siteSlug);

    /// <summary>Whether the given site's UniFi connection is established (null/empty = default site).</summary>
    bool GetIsConnected(string? siteSlug);
}
