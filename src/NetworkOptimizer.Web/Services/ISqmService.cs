using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Gates;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Service for managing SQM (Smart Queue Management) and polling TC stats.
/// SQM data is obtained by polling the tc-monitor endpoint on the UniFi gateway.
/// </summary>
[MutatingService(SiteScoped = true)]
public interface ISqmService
{
    /// <summary>
    /// Get current SQM status including live TC rates if available.
    /// Results are cached for 2 minutes to avoid repeated HTTP calls.
    /// </summary>
    /// <param name="forceRefresh">If true, bypasses the cache and fetches fresh data.</param>
    /// <returns>A <see cref="SqmStatusData"/> object containing current SQM status and TC rates.</returns>
    [RequireRole(Roles.Viewer)]
    Task<SqmStatusData> GetSqmStatusAsync(bool forceRefresh = false);

    /// <summary>
    /// Check if TC monitor is reachable on the gateway.
    /// </summary>
    /// <param name="host">Optional hostname to test. If not provided, uses configured or controller host.</param>
    /// <param name="port">Optional port to test. If not provided, uses configured port.</param>
    /// <returns>A tuple indicating availability and any error message.</returns>
    [RequireRole(Roles.Viewer)]
    Task<(bool Available, string? Error)> TestTcMonitorAsync(string? host = null, int? port = null);

    /// <summary>
    /// Get just the TC interface stats from the gateway.
    /// </summary>
    /// <returns>A list of <see cref="TcInterfaceStats"/> or null if unavailable.</returns>
    [RequireRole(Roles.Viewer)]
    Task<List<TcInterfaceStats>?> GetTcInterfaceStatsAsync();

    /// <summary>
    /// Get WAN interface configurations from the UniFi controller.
    /// Returns a mapping of interface name to friendly name (e.g., "eth4" -> "Comcast").
    /// </summary>
    /// <returns>A list of <see cref="WanInterfaceInfo"/> objects with WAN interface details.</returns>
    [RequireRole(Roles.Viewer)]
    Task<List<WanInterfaceInfo>> GetWanInterfacesFromControllerAsync();

}
