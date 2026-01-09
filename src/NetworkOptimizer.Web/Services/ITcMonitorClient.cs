namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Client for polling TC (Traffic Control) statistics from UniFi gateways.
/// The gateway must have the tc-monitor script deployed, which exposes
/// SQM/FQ_CoDel rates via a simple HTTP endpoint.
/// </summary>
public interface ITcMonitorClient
{
    /// <summary>
    /// Poll TC statistics from a gateway running the tc-monitor script.
    /// </summary>
    /// <param name="host">Gateway IP or hostname.</param>
    /// <param name="port">Port number (default 8088).</param>
    /// <param name="forceRefresh">Bypass cache and fetch fresh data.</param>
    /// <returns>TC monitor response with interface rates, or null if unreachable.</returns>
    Task<TcMonitorResponse?> GetTcStatsAsync(string host, int port = TcMonitorClient.DefaultPort, bool forceRefresh = false);

    /// <summary>
    /// Check if a gateway has the tc-monitor script running.
    /// </summary>
    /// <param name="host">Gateway IP or hostname.</param>
    /// <param name="port">Port number (default 8088).</param>
    /// <returns>True if the monitor is available and responding.</returns>
    Task<bool> IsMonitorAvailableAsync(string host, int port = TcMonitorClient.DefaultPort);

    /// <summary>
    /// Get the primary WAN rate (first interface with active status).
    /// </summary>
    /// <param name="host">Gateway IP or hostname.</param>
    /// <param name="port">Port number (default 8088).</param>
    /// <returns>The rate in Mbps, or null if unavailable.</returns>
    Task<double?> GetPrimaryWanRateAsync(string host, int port = TcMonitorClient.DefaultPort);
}
