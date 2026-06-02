using NetworkOptimizer.Monitoring.Models;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Service for polling cellular modem stats.
/// Delegates transport-specific polling to ICellularModemProvider implementations.
/// Auto-discovers UniFi modems from the controller device list.
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
    /// Auto-discover UniFi cellular modems from the controller device list.
    /// </summary>
    /// <returns>A list of discovered modems.</returns>
    Task<List<DiscoveredModem>> DiscoverModemsAsync();

    /// <summary>
    /// Provider-aware probe. Resolves the provider for the configuration
    /// and asks it to verify reachability and (where applicable) auth.
    /// </summary>
    /// <param name="modem">The modem configuration to probe.</param>
    /// <returns>A tuple containing success status and message.</returns>
    Task<(bool success, string message)> ProbeModemAsync(ModemConfiguration modem);

    /// <summary>
    /// Poll a modem - fetches stats via the resolved provider and updates LastPolled timestamp.
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
