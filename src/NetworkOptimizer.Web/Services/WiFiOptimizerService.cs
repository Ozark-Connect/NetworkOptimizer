using Microsoft.Extensions.Logging;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.WiFi;
using NetworkOptimizer.WiFi.Analyzers;
using NetworkOptimizer.WiFi.Models;
using NetworkOptimizer.WiFi.Providers;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Service layer for Wi-Fi Optimizer feature.
/// Coordinates data providers and analyzers.
/// </summary>
public class WiFiOptimizerService
{
    private readonly UniFiConnectionService _connectionService;
    private readonly ILogger<WiFiOptimizerService> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly SiteHealthScorer _healthScorer;

    // Cached data (refreshed on demand)
    private List<AccessPointSnapshot>? _cachedAps;
    private List<WirelessClientSnapshot>? _cachedClients;
    private List<WlanConfiguration>? _cachedWlanConfigs;
    private RoamingTopology? _cachedRoamingData;
    private SiteHealthScore? _cachedHealthScore;
    private DateTimeOffset _lastRefresh = DateTimeOffset.MinValue;
    private readonly TimeSpan _cacheExpiry = TimeSpan.FromSeconds(30);

    public WiFiOptimizerService(
        UniFiConnectionService connectionService,
        ILogger<WiFiOptimizerService> logger,
        ILoggerFactory loggerFactory)
    {
        _connectionService = connectionService;
        _logger = logger;
        _loggerFactory = loggerFactory;
        _healthScorer = new SiteHealthScorer();
    }

    /// <summary>
    /// Creates a UniFiLiveDataProvider with required dependencies.
    /// </summary>
    private UniFiLiveDataProvider CreateProvider()
    {
        var discovery = new UniFiDiscovery(
            _connectionService.Client!,
            _loggerFactory.CreateLogger<UniFiDiscovery>());
        return new UniFiLiveDataProvider(
            _connectionService.Client!,
            discovery,
            _loggerFactory.CreateLogger<UniFiLiveDataProvider>());
    }

    /// <summary>
    /// Get current site health score
    /// </summary>
    public async Task<SiteHealthScore?> GetSiteHealthScoreAsync(bool forceRefresh = false)
    {
        if (!_connectionService.IsConnected)
        {
            _logger.LogDebug("Cannot get health score - not connected to UniFi");
            return null;
        }

        if (!forceRefresh && _cachedHealthScore != null && DateTimeOffset.UtcNow - _lastRefresh < _cacheExpiry)
        {
            return _cachedHealthScore;
        }

        try
        {
            await RefreshDataAsync();
            if (_cachedAps == null || _cachedClients == null)
            {
                return null;
            }

            _cachedHealthScore = _healthScorer.Calculate(_cachedAps, _cachedClients, _cachedRoamingData);

            // Add MLO issue if enabled on any WLAN
            if (_cachedWlanConfigs?.Any(w => w.Enabled && w.MloEnabled) == true)
            {
                _cachedHealthScore.Issues.Add(new HealthIssue
                {
                    Severity = HealthIssueSeverity.Info,
                    Dimension = "Airtime Efficiency",
                    Title = "MLO enabled",
                    Description = "Multi-Link Operation is enabled on one or more SSIDs. MLO can improve speeds for Wi-Fi 7 devices but has been observed to reduce 5 GHz and 6 GHz throughput for non-MLO clients.",
                    Recommendation = "Consider disabling MLO if you have many non-Wi-Fi 7 devices experiencing reduced throughput"
                });
            }

            return _cachedHealthScore;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to calculate site health score");
            return null;
        }
    }

    /// <summary>
    /// Get all access points with current Wi-Fi data
    /// </summary>
    public async Task<List<AccessPointSnapshot>> GetAccessPointsAsync(bool forceRefresh = false)
    {
        if (!_connectionService.IsConnected)
        {
            return new List<AccessPointSnapshot>();
        }

        if (!forceRefresh && _cachedAps != null && DateTimeOffset.UtcNow - _lastRefresh < _cacheExpiry)
        {
            return _cachedAps;
        }

        await RefreshDataAsync();
        return _cachedAps ?? new List<AccessPointSnapshot>();
    }

    /// <summary>
    /// Get all wireless clients with current connection data
    /// </summary>
    public async Task<List<WirelessClientSnapshot>> GetWirelessClientsAsync(bool forceRefresh = false)
    {
        if (!_connectionService.IsConnected)
        {
            return new List<WirelessClientSnapshot>();
        }

        if (!forceRefresh && _cachedClients != null && DateTimeOffset.UtcNow - _lastRefresh < _cacheExpiry)
        {
            return _cachedClients;
        }

        await RefreshDataAsync();
        return _cachedClients ?? new List<WirelessClientSnapshot>();
    }

    /// <summary>
    /// Get roaming topology data
    /// </summary>
    public async Task<RoamingTopology?> GetRoamingTopologyAsync(bool forceRefresh = false)
    {
        if (!_connectionService.IsConnected)
        {
            return null;
        }

        if (!forceRefresh && _cachedRoamingData != null && DateTimeOffset.UtcNow - _lastRefresh < _cacheExpiry)
        {
            return _cachedRoamingData;
        }

        await RefreshDataAsync();
        return _cachedRoamingData;
    }

    /// <summary>
    /// Get WLAN configurations with band steering settings
    /// </summary>
    public async Task<List<WlanConfiguration>> GetWlanConfigurationsAsync(bool forceRefresh = false)
    {
        if (!_connectionService.IsConnected)
        {
            return new List<WlanConfiguration>();
        }

        if (!forceRefresh && _cachedWlanConfigs != null && DateTimeOffset.UtcNow - _lastRefresh < _cacheExpiry)
        {
            return _cachedWlanConfigs;
        }

        await RefreshDataAsync();
        return _cachedWlanConfigs ?? new List<WlanConfiguration>();
    }

    /// <summary>
    /// Get summary statistics for dashboard display
    /// </summary>
    public async Task<WiFiSummary> GetSummaryAsync()
    {
        var summary = new WiFiSummary();

        if (!_connectionService.IsConnected)
        {
            return summary;
        }

        try
        {
            var aps = await GetAccessPointsAsync();
            var clients = await GetWirelessClientsAsync();
            var healthScore = await GetSiteHealthScoreAsync();

            summary.TotalAps = aps.Count;
            summary.TotalClients = clients.Count;
            summary.ClientsOn2_4GHz = clients.Count(c => c.Band == RadioBand.Band2_4GHz);
            summary.ClientsOn5GHz = clients.Count(c => c.Band == RadioBand.Band5GHz);
            summary.ClientsOn6GHz = clients.Count(c => c.Band == RadioBand.Band6GHz);
            summary.HealthScore = healthScore?.OverallScore;
            summary.HealthGrade = healthScore?.Grade;

            if (clients.Any(c => c.Satisfaction.HasValue))
            {
                summary.AvgSatisfaction = (int)clients
                    .Where(c => c.Satisfaction.HasValue)
                    .Average(c => c.Satisfaction!.Value);
            }

            if (clients.Any(c => c.Signal.HasValue))
            {
                summary.AvgSignal = (int)clients
                    .Where(c => c.Signal.HasValue)
                    .Average(c => c.Signal!.Value);
            }

            summary.WeakSignalClients = clients.Count(c => c.Signal.HasValue && c.Signal.Value < -70);

            // Check if MLO is enabled on any enabled WLAN
            var wlanConfigs = await GetWlanConfigurationsAsync();
            summary.MloEnabled = wlanConfigs.Any(w => w.Enabled && w.MloEnabled);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get Wi-Fi summary");
        }

        return summary;
    }

    private async Task RefreshDataAsync()
    {
        if (_connectionService.Client == null)
        {
            _logger.LogWarning("UniFi client not available");
            return;
        }

        try
        {
            var provider = CreateProvider();

            // Fetch data in parallel
            var apsTask = provider.GetAccessPointsAsync();
            var clientsTask = provider.GetWirelessClientsAsync();
            var roamingTask = provider.GetRoamingTopologyAsync();
            var wlanTask = provider.GetWlanConfigurationsAsync();

            await Task.WhenAll(apsTask, clientsTask, roamingTask, wlanTask);

            // Sort APs by IP address for consistent display across all components
            _cachedAps = Components.Shared.WiFi.WiFiAnalysisHelpers.SortByIp(await apsTask);
            _cachedClients = await clientsTask;
            _cachedRoamingData = await roamingTask;
            _cachedWlanConfigs = await wlanTask;
            _lastRefresh = DateTimeOffset.UtcNow;

            // Enrich roaming topology with proper model names from AP data
            if (_cachedRoamingData != null && _cachedAps.Count > 0)
            {
                foreach (var vertex in _cachedRoamingData.Vertices)
                {
                    var ap = _cachedAps.FirstOrDefault(a =>
                        string.Equals(a.Mac, vertex.Mac, StringComparison.OrdinalIgnoreCase));
                    if (ap != null)
                    {
                        vertex.Model = ap.Model; // Use the friendly model name
                    }
                }
            }

            _logger.LogDebug("Refreshed Wi-Fi data: {ApCount} APs, {ClientCount} clients",
                _cachedAps.Count, _cachedClients.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh Wi-Fi data from UniFi");
        }
    }

    /// <summary>
    /// Clear cached data to force refresh on next request
    /// </summary>
    public void ClearCache()
    {
        _cachedAps = null;
        _cachedClients = null;
        _cachedWlanConfigs = null;
        _cachedRoamingData = null;
        _cachedHealthScore = null;
        _cachedScanResults = null;
        _lastRefresh = DateTimeOffset.MinValue;
    }

    // Cached channel scan results (keyed by time range)
    private List<ChannelScanResult>? _cachedScanResults;
    private string? _cachedScanResultsTimeKey;

    /// <summary>
    /// Get RF environment channel scan results from APs
    /// </summary>
    /// <param name="forceRefresh">Force refresh even if cached</param>
    /// <param name="startTime">Optional: filter to networks seen since this time</param>
    /// <param name="endTime">Optional: filter to networks seen until this time</param>
    public async Task<List<ChannelScanResult>> GetChannelScanResultsAsync(
        bool forceRefresh = false,
        DateTimeOffset? startTime = null,
        DateTimeOffset? endTime = null)
    {
        if (!_connectionService.IsConnected || _connectionService.Client == null)
        {
            _logger.LogDebug("Cannot get scan results - not connected to UniFi");
            return new List<ChannelScanResult>();
        }

        // Create cache key based on time range
        var timeKey = $"{startTime?.ToUnixTimeSeconds()}_{endTime?.ToUnixTimeSeconds()}";
        var cacheValid = !forceRefresh
            && _cachedScanResults != null
            && _cachedScanResultsTimeKey == timeKey
            && DateTimeOffset.UtcNow - _lastRefresh < _cacheExpiry;

        if (cacheValid)
        {
            return _cachedScanResults!;
        }

        try
        {
            var provider = CreateProvider();

            _cachedScanResults = await provider.GetChannelScanResultsAsync(
                apMac: null,
                startTime: startTime,
                endTime: endTime);
            _cachedScanResultsTimeKey = timeKey;
            return _cachedScanResults;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get channel scan results");
            return new List<ChannelScanResult>();
        }
    }

    /// <summary>
    /// Get site-wide Wi-Fi metrics time series for AirView charts
    /// </summary>
    public async Task<List<WiFi.Models.SiteWiFiMetrics>> GetSiteMetricsAsync(
        DateTimeOffset start,
        DateTimeOffset end,
        WiFi.MetricGranularity granularity = WiFi.MetricGranularity.FiveMinutes)
    {
        if (!_connectionService.IsConnected || _connectionService.Client == null)
        {
            _logger.LogDebug("Cannot get site metrics - not connected to UniFi");
            return new List<WiFi.Models.SiteWiFiMetrics>();
        }

        try
        {
            var provider = CreateProvider();

            return await provider.GetSiteMetricsAsync(start, end, granularity);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get site metrics");
            return new List<WiFi.Models.SiteWiFiMetrics>();
        }
    }

    /// <summary>
    /// Get per-AP Wi-Fi metrics time series (filtered by AP MAC)
    /// </summary>
    public async Task<List<WiFi.Models.SiteWiFiMetrics>> GetApMetricsAsync(
        string[] apMacs,
        DateTimeOffset start,
        DateTimeOffset end,
        WiFi.MetricGranularity granularity = WiFi.MetricGranularity.FiveMinutes)
    {
        if (!_connectionService.IsConnected || _connectionService.Client == null)
        {
            _logger.LogDebug("Cannot get AP metrics - not connected to UniFi");
            return new List<WiFi.Models.SiteWiFiMetrics>();
        }

        try
        {
            var provider = CreateProvider();

            return await provider.GetApMetricsAsync(apMacs, start, end, granularity);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get AP metrics for {ApMacs}", string.Join(",", apMacs));
            return new List<WiFi.Models.SiteWiFiMetrics>();
        }
    }

    /// <summary>
    /// Get per-client Wi-Fi metrics time series
    /// </summary>
    public async Task<List<WiFi.Models.ClientWiFiMetrics>> GetClientMetricsAsync(
        string clientMac,
        DateTimeOffset start,
        DateTimeOffset end,
        WiFi.MetricGranularity granularity = WiFi.MetricGranularity.FiveMinutes)
    {
        if (!_connectionService.IsConnected || _connectionService.Client == null)
        {
            _logger.LogDebug("Cannot get client metrics - not connected to UniFi");
            return new List<WiFi.Models.ClientWiFiMetrics>();
        }

        try
        {
            var provider = CreateProvider();

            return await provider.GetClientMetricsAsync(clientMac, start, end, granularity);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get client metrics for {ClientMac}", clientMac);
            return new List<WiFi.Models.ClientWiFiMetrics>();
        }
    }

    /// <summary>
    /// Get client connection events (connects, disconnects, roams)
    /// </summary>
    public async Task<List<WiFi.Models.ClientConnectionEvent>> GetClientConnectionEventsAsync(
        string clientMac,
        int limit = 200)
    {
        if (!_connectionService.IsConnected || _connectionService.Client == null)
        {
            _logger.LogDebug("Cannot get client events - not connected to UniFi");
            return new List<WiFi.Models.ClientConnectionEvent>();
        }

        try
        {
            var provider = CreateProvider();

            return await provider.GetClientConnectionEventsAsync(clientMac, limit);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get client events for {ClientMac}", clientMac);
            return new List<WiFi.Models.ClientConnectionEvent>();
        }
    }
}

/// <summary>
/// Summary data for dashboard display
/// </summary>
public class WiFiSummary
{
    public int TotalAps { get; set; }
    public int TotalClients { get; set; }
    public int ClientsOn2_4GHz { get; set; }
    public int ClientsOn5GHz { get; set; }
    public int ClientsOn6GHz { get; set; }
    public int? HealthScore { get; set; }
    public string? HealthGrade { get; set; }
    public int? AvgSatisfaction { get; set; }
    public int? AvgSignal { get; set; }
    public int WeakSignalClients { get; set; }

    /// <summary>
    /// Whether MLO (Multi-Link Operation) is enabled on any enabled WLAN.
    /// When true, may impact throughput for non-MLO devices on 5 GHz and 6 GHz bands.
    /// </summary>
    public bool MloEnabled { get; set; }
}
