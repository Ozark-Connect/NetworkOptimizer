namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Service for managing SQM (Smart Queue Management) and polling TC stats.
/// SQM data is obtained by polling the tc-monitor endpoint on the UniFi gateway.
/// </summary>
public interface ISqmService
{
    /// <summary>
    /// Configure the TC monitor endpoint to poll.
    /// </summary>
    /// <param name="host">The hostname or IP address of the gateway running TC monitor.</param>
    /// <param name="port">The port number for TC monitor (default: 8088).</param>
    void ConfigureTcMonitor(string host, int port = 8088);

    /// <summary>
    /// Get current SQM status including live TC rates if available.
    /// Results are cached for 2 minutes to avoid repeated HTTP calls.
    /// </summary>
    /// <param name="forceRefresh">If true, bypasses the cache and fetches fresh data.</param>
    /// <returns>A <see cref="SqmStatusData"/> object containing current SQM status and TC rates.</returns>
    Task<SqmStatusData> GetSqmStatusAsync(bool forceRefresh = false);

    /// <summary>
    /// Check if TC monitor is reachable on the gateway.
    /// </summary>
    /// <param name="host">Optional hostname to test. If not provided, uses configured or controller host.</param>
    /// <param name="port">Optional port to test. If not provided, uses configured port.</param>
    /// <returns>A tuple indicating availability and any error message.</returns>
    Task<(bool Available, string? Error)> TestTcMonitorAsync(string? host = null, int? port = null);

    /// <summary>
    /// Get just the TC interface stats from the gateway.
    /// </summary>
    /// <returns>A list of <see cref="TcInterfaceStats"/> or null if unavailable.</returns>
    Task<List<TcInterfaceStats>?> GetTcInterfaceStatsAsync();

    /// <summary>
    /// Get WAN interface configurations from the UniFi controller.
    /// Returns a mapping of interface name to friendly name (e.g., "eth4" -> "Yelcot").
    /// </summary>
    /// <returns>A list of <see cref="WanInterfaceInfo"/> objects with WAN interface details.</returns>
    Task<List<WanInterfaceInfo>> GetWanInterfacesFromControllerAsync();

    /// <summary>
    /// Generate the tc-monitor configuration content based on controller WAN settings.
    /// This can be used to deploy the correct interface mapping to gateways.
    /// </summary>
    /// <returns>Configuration string in the format expected by tc-monitor (e.g., "ifbeth4:Yelcot ifbeth0:Starlink").</returns>
    Task<string> GenerateTcMonitorConfigAsync();

    /// <summary>
    /// Deploy SQM configuration to the gateway.
    /// </summary>
    /// <param name="config">The SQM configuration to deploy.</param>
    /// <returns>True if deployment succeeded, false otherwise.</returns>
    Task<bool> DeploySqmAsync(SqmConfiguration config);

    /// <summary>
    /// Generate SQM scripts for the specified configuration.
    /// </summary>
    /// <param name="config">The SQM configuration to generate scripts for.</param>
    /// <returns>The path to the generated scripts archive.</returns>
    Task<string> GenerateSqmScriptsAsync(SqmConfiguration config);

    /// <summary>
    /// Disable SQM on the gateway.
    /// </summary>
    /// <returns>True if SQM was successfully disabled, false otherwise.</returns>
    Task<bool> DisableSqmAsync();
}
