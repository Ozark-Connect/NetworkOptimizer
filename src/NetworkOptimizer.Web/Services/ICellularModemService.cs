using NetworkOptimizer.Monitoring.Models;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Gates;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Service for polling cellular modem stats.
/// Delegates transport-specific polling to ICellularModemProvider implementations.
/// Auto-discovers UniFi modems from the controller device list.
/// </summary>
[MutatingService(SiteScoped = true)]
public interface ICellularModemService : IDisposable
{

    /// <summary>
    /// Get cached stats for a specific modem without polling.
    /// Returns null if no cached stats exist for this modem.
    /// </summary>
    /// <param name="modemId">The modem configuration ID.</param>
    /// <returns>Cached stats or null.</returns>
    [RequireRole(Roles.Viewer)]
    Task<CellularModemStats?> GetCachedStatsAsync(int modemId);

    /// <summary>
    /// Auto-discover UniFi cellular modems from the controller device list.
    /// </summary>
    /// <returns>A list of discovered modems.</returns>
    [RequireRole(Roles.Admin)]
    Task<List<DiscoveredModem>> DiscoverModemsAsync();

    /// <summary>
    /// Provider-aware probe. Resolves the provider for the configuration
    /// and asks it to verify reachability and (where applicable) auth.
    /// </summary>
    /// <param name="modem">The modem configuration to probe.</param>
    /// <returns>A tuple containing success status and message.</returns>
    [RequireRole(Roles.Admin)]
    Task<(bool success, string message)> ProbeModemAsync(ModemConfiguration modem);

    /// <summary>
    /// Poll a modem - fetches stats via the resolved provider and updates LastPolled timestamp.
    /// </summary>
    /// <param name="modem">The modem configuration to poll.</param>
    /// <returns>A tuple containing success status and message.</returns>
    [RequireRole(Roles.Viewer)]
    Task<(bool success, string message)> PollModemAsync(ModemConfiguration modem);

    /// <summary>
    /// Power-cycle a modem's radio to force a fresh cell selection, then re-poll.
    /// Drops the cellular connection for several seconds, so callers must confirm
    /// with the user first. Fails for providers without the capability.
    /// </summary>
    /// <param name="modemId">The modem configuration ID.</param>
    /// <returns>A tuple containing success status and message.</returns>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.CellularRadioReset, TargetType = "cellular_modem")]
    Task<(bool success, string message)> ResetRadioAsync(int modemId);

    /// <summary>
    /// Get all configured modems.
    /// </summary>
    /// <returns>A list of all modem configurations.</returns>
    [RequireRole(Roles.Viewer)]
    Task<List<ModemConfiguration>> GetModemsAsync();

    /// <summary>
    /// Enable or disable polling for one modem while retaining its configuration.
    /// </summary>
    /// <param name="id">The modem configuration ID.</param>
    /// <param name="enabled">Whether polling is enabled.</param>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.MonitoringSetupChanged, TargetType = "cellular_modem")]
    Task SetModemEnabledAsync(int id, bool enabled);

    /// <summary>
    /// Add or update a modem configuration.
    /// </summary>
    /// <param name="config">The modem configuration to save.</param>
    /// <returns>The saved modem configuration.</returns>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.MonitoringSetupChanged, TargetType = "cellular_modem")]
    Task<ModemConfiguration> SaveModemAsync(ModemConfiguration config);

    /// <summary>
    /// Delete a modem configuration.
    /// </summary>
    /// <param name="id">The ID of the modem configuration to delete.</param>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.MonitoringSetupChanged, TargetType = "cellular_modem")]
    Task DeleteModemAsync(int id);
}
