using NetworkOptimizer.Core.Models;

namespace NetworkOptimizer.Core.Interfaces;

/// <summary>
/// Interface for interacting with the UniFi Controller API.
/// Provides methods for retrieving device information, configuration, and network status.
/// </summary>
public interface IUniFiApiClient
{
    /// <summary>
    /// Authenticates with the UniFi controller.
    /// </summary>
    /// <param name="username">Controller username.</param>
    /// <param name="password">Controller password.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if authentication was successful.</returns>
    Task<bool> AuthenticateAsync(string username, string password, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves all sites from the UniFi controller.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of site information.</returns>
    Task<List<UniFiSite>> GetSitesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves all devices from a specific site.
    /// </summary>
    /// <param name="siteId">Site identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of UniFi devices.</returns>
    Task<List<UniFiDevice>> GetDevicesAsync(string siteId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves detailed information for a specific device.
    /// </summary>
    /// <param name="siteId">Site identifier.</param>
    /// <param name="deviceId">Device identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Device details.</returns>
    Task<UniFiDevice?> GetDeviceAsync(string siteId, string deviceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves the network configuration for a site.
    /// </summary>
    /// <param name="siteId">Site identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Network configuration including VLANs, firewall rules, and port configs.</returns>
    Task<NetworkConfiguration> GetNetworkConfigurationAsync(string siteId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves SQM configuration for a device.
    /// </summary>
    /// <param name="siteId">Site identifier.</param>
    /// <param name="deviceId">Device identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>SQM configuration if available.</returns>
    Task<SqmConfiguration?> GetSqmConfigurationAsync(string siteId, string deviceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates SQM configuration for a device.
    /// </summary>
    /// <param name="siteId">Site identifier.</param>
    /// <param name="deviceId">Device identifier.</param>
    /// <param name="configuration">SQM configuration to apply.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the update was successful.</returns>
    Task<bool> UpdateSqmConfigurationAsync(string siteId, string deviceId, SqmConfiguration configuration, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves all wireless networks for a site.
    /// </summary>
    /// <param name="siteId">Site identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of wireless network configurations.</returns>
    Task<List<WirelessNetwork>> GetWirelessNetworksAsync(string siteId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves all firewall rules for a site.
    /// </summary>
    /// <param name="siteId">Site identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of firewall rules.</returns>
    Task<List<FirewallRule>> GetFirewallRulesAsync(string siteId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves port configurations for a specific switch.
    /// </summary>
    /// <param name="siteId">Site identifier.</param>
    /// <param name="deviceId">Switch device identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of port configurations.</returns>
    Task<List<PortConfiguration>> GetPortConfigurationsAsync(string siteId, string deviceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Performs a health check on the API connection.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the connection is healthy.</returns>
    Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Represents a UniFi site.
/// </summary>
public class UniFiSite
{
    /// <summary>
    /// Site identifier.
    /// </summary>
    public string SiteId { get; set; } = string.Empty;

    /// <summary>
    /// Site name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Site description.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Number of devices in the site.
    /// </summary>
    public int DeviceCount { get; set; }

    /// <summary>
    /// Number of clients connected to the site.
    /// </summary>
    public int ClientCount { get; set; }
}
