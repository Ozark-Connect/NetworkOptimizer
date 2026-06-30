using NetworkOptimizer.WiFi.Models;

namespace NetworkOptimizer.WiFi;

/// <summary>
/// Abstraction layer for Wi-Fi data access.
/// Implementations can source data from UniFi API (live) or InfluxDB (historical).
/// This protects against UniFi API changes and enables time-series analysis.
/// </summary>
public interface IWiFiDataProvider
{
    /// <summary>
    /// Get current snapshot of all access points with Wi-Fi metrics.
    /// Pass <paramref name="useCache"/> = false to force a live device read, bypassing any
    /// short-lived device-list cache (used by explicit user refresh and post-re-pair refresh).
    /// </summary>
    Task<List<AccessPointSnapshot>> GetAccessPointsAsync(CancellationToken cancellationToken = default, bool useCache = true);

    /// <summary>
    /// Get current snapshot of all wireless clients with connection details
    /// </summary>
    Task<List<WirelessClientSnapshot>> GetWirelessClientsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get time-series Wi-Fi metrics for site-wide analysis
    /// </summary>
    /// <param name="start">Start of time range</param>
    /// <param name="end">End of time range</param>
    /// <param name="granularity">Data point interval</param>
    Task<List<SiteWiFiMetrics>> GetSiteMetricsAsync(
        DateTimeOffset start,
        DateTimeOffset end,
        MetricGranularity granularity = MetricGranularity.FiveMinutes,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get time-series Wi-Fi metrics for a specific client
    /// </summary>
    /// <param name="clientMac">Client MAC address</param>
    /// <param name="start">Start of time range</param>
    /// <param name="end">End of time range</param>
    /// <param name="granularity">Data point interval</param>
    Task<List<ClientWiFiMetrics>> GetClientMetricsAsync(
        string clientMac,
        DateTimeOffset start,
        DateTimeOffset end,
        MetricGranularity granularity = MetricGranularity.FiveMinutes,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get WLAN (SSID) configurations with current statistics
    /// </summary>
    Task<List<WlanConfiguration>> GetWlanConfigurationsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get roaming events for analysis
    /// </summary>
    /// <param name="start">Start of time range</param>
    /// <param name="end">End of time range</param>
    /// <param name="clientMac">Optional: filter to specific client</param>
    Task<List<RoamingEvent>> GetRoamingEventsAsync(
        DateTimeOffset start,
        DateTimeOffset end,
        string? clientMac = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get channel scan results (neighboring networks, interference)
    /// </summary>
    /// <param name="apMac">Optional: filter to specific AP</param>
    /// <param name="startTime">Optional: filter to networks seen since this time</param>
    /// <param name="endTime">Optional: filter to networks seen until this time</param>
    Task<List<ChannelScanResult>> GetChannelScanResultsAsync(
        string? apMac = null,
        DateTimeOffset? startTime = null,
        DateTimeOffset? endTime = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Trigger a quick RF spectrum scan on an AP's band. A quick scan does NOT disconnect clients
    /// (unlike a full scan), but takes time per band - intended for on-demand or background refresh,
    /// never inline with a recommendation. Read results later via the scan APIs.
    /// </summary>
    /// <param name="apMac">AP MAC address.</param>
    /// <param name="bandCode">UniFi band code: "ng" (2.4), "na" (5), "6e" (6).</param>
    /// <param name="bandwidthMhz">Scan bandwidth - should match the radio's CURRENT operating width
    /// so the radio doesn't reconfigure (less disruption) and the result buckets line up with the
    /// operating channel.</param>
    Task<bool> TriggerQuickScanAsync(string apMac, string bandCode, int bandwidthMhz, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether a quick-scan is still running on the given AP (results not yet final).
    /// </summary>
    Task<bool> IsQuickScanInProgressAsync(string apMac, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether this provider supports historical/time-series queries
    /// </summary>
    bool SupportsHistoricalData { get; }

    /// <summary>
    /// Provider name for logging/diagnostics
    /// </summary>
    string ProviderName { get; }
}

/// <summary>
/// Time granularity for metrics queries
/// </summary>
public enum MetricGranularity
{
    /// <summary>5-minute intervals - highest resolution</summary>
    FiveMinutes,

    /// <summary>Hourly intervals</summary>
    Hourly,

    /// <summary>Daily intervals</summary>
    Daily
}
