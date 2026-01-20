using NetworkOptimizer.Monitoring.Models;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Service for polling cellular modem stats via SSH.
/// Uses shared UniFiSshService for SSH operations.
/// Auto-discovers U5G-Max modems from UniFi device list.
/// </summary>
public interface ICellularModemService : IDisposable
{
    /// <summary>
    /// Get the most recent stats for all modems.
    /// </summary>
    /// <returns>The last collected modem stats, or null if none available.</returns>
    CellularModemStats? GetLastStats();

    /// <summary>
    /// Get cached stats for a specific modem without polling.
    /// Returns null if no cached stats exist for this modem.
    /// </summary>
    /// <param name="modemId">The modem configuration ID.</param>
    /// <returns>Cached stats or null.</returns>
    CellularModemStats? GetCachedStats(int modemId);

    /// <summary>
    /// Auto-discover U5G-Max modems from UniFi device list.
    /// </summary>
    /// <returns>A list of discovered modems.</returns>
    Task<List<DiscoveredModem>> DiscoverModemsAsync();

    /// <summary>
    /// Test SSH connection to a modem using shared credentials.
    /// </summary>
    /// <param name="host">The host address of the modem.</param>
    /// <returns>A tuple containing success status and message.</returns>
    Task<(bool success, string message)> TestConnectionAsync(string host);

    /// <summary>
    /// Poll a modem - fetches stats via SSH and updates LastPolled timestamp.
    /// </summary>
    /// <param name="modem">The modem configuration to poll.</param>
    /// <returns>A tuple containing success status and message.</returns>
    Task<(bool success, string message)> PollModemAsync(ModemConfiguration modem);

    /// <summary>
    /// Get all configured modems.
    /// </summary>
    /// <returns>A list of all modem configurations.</returns>
    Task<List<ModemConfiguration>> GetModemsAsync();

    /// <summary>
    /// Add or update a modem configuration.
    /// </summary>
    /// <param name="config">The modem configuration to save.</param>
    /// <returns>The saved modem configuration.</returns>
    Task<ModemConfiguration> SaveModemAsync(ModemConfiguration config);

    /// <summary>
    /// Delete a modem configuration.
    /// </summary>
    /// <param name="id">The ID of the modem configuration to delete.</param>
    Task DeleteModemAsync(int id);
}
