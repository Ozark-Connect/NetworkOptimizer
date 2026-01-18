using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Service for managing shared SSH credentials and executing SSH commands on UniFi devices.
/// All UniFi network devices (APs, switches) share the same SSH credentials.
/// </summary>
public interface IUniFiSshService
{
    /// <summary>
    /// Get the shared SSH settings (creates default if none exist).
    /// </summary>
    /// <param name="siteId">The site ID.</param>
    /// <returns>The SSH settings.</returns>
    Task<UniFiSshSettings> GetSettingsAsync(int siteId);

    /// <summary>
    /// Save SSH settings.
    /// </summary>
    /// <param name="siteId">The site ID.</param>
    /// <param name="settings">The settings to save.</param>
    /// <returns>The saved settings.</returns>
    Task<UniFiSshSettings> SaveSettingsAsync(int siteId, UniFiSshSettings settings);

    /// <summary>
    /// Test SSH connection to a specific host using shared credentials.
    /// </summary>
    /// <param name="siteId">The site ID.</param>
    /// <param name="host">The host to test connection to.</param>
    /// <returns>A tuple containing success status and message.</returns>
    Task<(bool success, string message)> TestConnectionAsync(int siteId, string host);

    /// <summary>
    /// Test SSH connection to a device using device-specific credentials if configured.
    /// </summary>
    /// <param name="siteId">The site ID.</param>
    /// <param name="device">The device configuration containing credentials.</param>
    /// <returns>A tuple containing success status and message.</returns>
    Task<(bool success, string message)> TestConnectionAsync(int siteId, DeviceSshConfiguration device);

    /// <summary>
    /// Run an SSH command on a device using shared credentials.
    /// </summary>
    /// <param name="siteId">The site ID.</param>
    /// <param name="host">The host to run the command on.</param>
    /// <param name="command">The command to execute.</param>
    /// <param name="portOverride">Optional port override.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <returns>A tuple containing success status and output.</returns>
    Task<(bool success, string output)> RunCommandAsync(int siteId, string host, string command, int? portOverride = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Run an SSH command on a device with optional per-device credential overrides.
    /// If override values are null/empty, falls back to global settings.
    /// </summary>
    /// <param name="siteId">The site ID.</param>
    /// <param name="host">The host to run the command on.</param>
    /// <param name="command">The command to execute.</param>
    /// <param name="portOverride">Optional port override.</param>
    /// <param name="usernameOverride">Optional username override.</param>
    /// <param name="passwordOverride">Optional password override.</param>
    /// <param name="privateKeyPathOverride">Optional private key path override.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <returns>A tuple containing success status and output.</returns>
    Task<(bool success, string output)> RunCommandAsync(
        int siteId,
        string host,
        string command,
        int? portOverride,
        string? usernameOverride,
        string? passwordOverride,
        string? privateKeyPathOverride,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Run an SSH command using device-specific credentials if configured, falling back to global settings.
    /// </summary>
    /// <param name="siteId">The site ID.</param>
    /// <param name="device">The device configuration containing host and credentials.</param>
    /// <param name="command">The command to execute.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <returns>A tuple containing success status and output.</returns>
    Task<(bool success, string output)> RunCommandWithDeviceAsync(int siteId, DeviceSshConfiguration device, string command, CancellationToken cancellationToken = default);

    /// <summary>
    /// Check if a tool (like iperf3) is available on a device using global credentials.
    /// </summary>
    /// <param name="siteId">The site ID.</param>
    /// <param name="host">The host to check.</param>
    /// <param name="toolName">The name of the tool to check for.</param>
    /// <returns>A tuple containing availability status and version string.</returns>
    Task<(bool available, string version)> CheckToolAvailableAsync(int siteId, string host, string toolName);

    /// <summary>
    /// Check if a tool (like iperf3) is available on a device using device-specific credentials if configured.
    /// </summary>
    /// <param name="siteId">The site ID.</param>
    /// <param name="device">The device configuration containing credentials.</param>
    /// <param name="toolName">The name of the tool to check for.</param>
    /// <returns>A tuple containing availability status and version string.</returns>
    Task<(bool available, string version)> CheckToolAvailableAsync(int siteId, DeviceSshConfiguration device, string toolName);

    /// <summary>
    /// Get all configured devices.
    /// </summary>
    /// <param name="siteId">The site ID.</param>
    /// <returns>A list of all device SSH configurations.</returns>
    Task<List<DeviceSshConfiguration>> GetDevicesAsync(int siteId);

    /// <summary>
    /// Save a device configuration.
    /// </summary>
    /// <param name="siteId">The site ID.</param>
    /// <param name="device">The device configuration to save.</param>
    /// <returns>The saved device configuration.</returns>
    Task<DeviceSshConfiguration> SaveDeviceAsync(int siteId, DeviceSshConfiguration device);

    /// <summary>
    /// Delete a device configuration.
    /// </summary>
    /// <param name="siteId">The site ID.</param>
    /// <param name="id">The ID of the device configuration to delete.</param>
    Task DeleteDeviceAsync(int siteId, int id);
}
