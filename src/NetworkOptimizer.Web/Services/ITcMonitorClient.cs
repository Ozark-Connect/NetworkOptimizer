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
    /// <param name="siteSlug">
    /// Site the gateway belongs to. For a non-default site reached through its
    /// agent, the host:port is routed over the tunnel before polling. Null or the
    /// default site polls the gateway directly.
    /// </param>
    /// <returns>TC monitor response with interface rates, or null if unreachable.</returns>
    Task<TcMonitorResponse?> GetTcStatsAsync(string host, int port = TcMonitorClient.DefaultPort, bool forceRefresh = false, string? siteSlug = null);

    /// <summary>
    /// Check if a gateway has the tc-monitor script running.
    /// </summary>
    /// <param name="host">Gateway IP or hostname.</param>
    /// <param name="port">Port number (default 8088).</param>
    /// <param name="siteSlug">Site the gateway belongs to (routes via the agent when set and agent-backed).</param>
    /// <returns>True if the monitor is available and responding.</returns>
    Task<bool> IsMonitorAvailableAsync(string host, int port = TcMonitorClient.DefaultPort, string? siteSlug = null);

    /// <summary>
    /// Get the primary WAN rate (first interface with active status).
    /// </summary>
    /// <param name="host">Gateway IP or hostname.</param>
    /// <param name="port">Port number (default 8088).</param>
    /// <param name="siteSlug">Site the gateway belongs to (routes via the agent when set and agent-backed).</param>
    /// <returns>The rate in Mbps, or null if unavailable.</returns>
    Task<double?> GetPrimaryWanRateAsync(string host, int port = TcMonitorClient.DefaultPort, string? siteSlug = null);
}
