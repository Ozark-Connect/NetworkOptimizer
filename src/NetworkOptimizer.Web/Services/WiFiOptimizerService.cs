using System.Text.Json;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Audit.Analyzers;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.WiFi;
using NetworkOptimizer.WiFi.Analyzers;
using NetworkOptimizer.WiFi.Models;
using NetworkOptimizer.WiFi.Providers;
using NetworkOptimizer.WiFi.Rules;
using AuditNetworkInfo = NetworkOptimizer.Audit.Models.NetworkInfo;

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
    private readonly WiFiOptimizerEngine _optimizerEngine;
    private readonly VlanAnalyzer _vlanAnalyzer;

    // Cached data (refreshed on demand)
    private List<AccessPointSnapshot>? _cachedAps;
    private List<WirelessClientSnapshot>? _cachedClients;
    private List<WlanConfiguration>? _cachedWlanConfigs;
    private List<AuditNetworkInfo>? _cachedNetworks;
    private RoamingTopology? _cachedRoamingData;
    private SiteHealthScore? _cachedHealthScore;
    private DateTimeOffset _lastRefresh = DateTimeOffset.MinValue;
    private readonly TimeSpan _cacheExpiry = TimeSpan.FromSeconds(30);

    public WiFiOptimizerService(
        UniFiConnectionService connectionService,
        WiFiOptimizerEngine optimizerEngine,
        VlanAnalyzer vlanAnalyzer,
        ILogger<WiFiOptimizerService> logger,
        ILoggerFactory loggerFactory)
    {
        _connectionService = connectionService;
        _optimizerEngine = optimizerEngine;
        _vlanAnalyzer = vlanAnalyzer;
        _logger = logger;
        _loggerFactory = loggerFactory;
        _healthScorer = new SiteHealthScorer();
    }

    /// <summary>
    /// Creates a UniFiLiveDataProvider with required dependencies.
    /// </summary>
    private UniFiLiveDataProvider CreateProvider(int siteId)
    {
        var discovery = new UniFiDiscovery(
            _connectionService.GetClient(siteId)!,
            _loggerFactory.CreateLogger<UniFiDiscovery>());
        return new UniFiLiveDataProvider(
            _connectionService.GetClient(siteId)!,
            discovery,
            _loggerFactory.CreateLogger<UniFiLiveDataProvider>());
    }

    /// <summary>
    /// Get current site health score
    /// </summary>
    public async Task<SiteHealthScore?> GetSiteHealthScoreAsync(int siteId, bool forceRefresh = false)
    {
        if (!_connectionService.IsConnected(siteId))
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
            await RefreshDataAsync(siteId);
            if (_cachedAps == null || _cachedClients == null)
            {
                return null;
            }

            _cachedHealthScore = _healthScorer.Calculate(_cachedAps, _cachedClients, _cachedRoamingData);

            // Only consider online APs for additional issue checks
            var onlineAps = _cachedAps.Where(ap => ap.IsOnline).ToList();

            // Add MLO issue if enabled on Wi-Fi 7 capable APs (affects airtime efficiency)
            var hasWifi7Aps = onlineAps.Any(ap => ap.Radios.Any(r => r.Is11Be));
            var hasMloEnabledWlan = _cachedWlanConfigs?.Any(w => w.Enabled && w.MloEnabled) == true;
            if (hasWifi7Aps && hasMloEnabledWlan)
            {
                _cachedHealthScore.Issues.Add(new HealthIssue
                {
                    Severity = HealthIssueSeverity.Info,
                    Dimensions = { HealthDimension.AirtimeEfficiency },
                    Title = "MLO enabled",
                    Description = "Multi-Link Operation is enabled on one or more SSIDs. MLO allows Wi-Fi 7 devices to aggregate multiple bands simultaneously. Non-Wi-Fi 7 devices may see reduced throughput on 5 GHz and 6 GHz bands.",
                    Recommendation = "Consider disabling MLO if you have many non-Wi-Fi 7 devices experiencing slow speeds on 5 GHz or 6 GHz."
                });
            }

            // Check for 6 GHz capable APs with 6 GHz disabled
            var hasAps6GHz = onlineAps.Any(ap => ap.Radios.Any(r => r.Band == RadioBand.Band6GHz));
            var hasWlan6GHz = _cachedWlanConfigs?.Any(w => w.Enabled && w.EnabledBands.Contains(RadioBand.Band6GHz)) == true;
            if (hasAps6GHz && !hasWlan6GHz)
            {
                var aps6GHzCount = onlineAps.Count(ap => ap.Radios.Any(r => r.Band == RadioBand.Band6GHz));
                _cachedHealthScore.Issues.Add(new HealthIssue
                {
                    Severity = HealthIssueSeverity.Info,
                    Dimensions = { HealthDimension.ChannelHealth, HealthDimension.AirtimeEfficiency },
                    Title = "6 GHz disabled",
                    Description = $"You have {aps6GHzCount} access point{(aps6GHzCount > 1 ? "s" : "")} with 6 GHz radios, but no SSIDs are broadcasting on 6 GHz. Enabling 6 GHz can offload Wi-Fi 6E/7 capable devices from congested 2.4 GHz and 5 GHz bands.",
                    Recommendation = "Enable 6 GHz on your SSIDs in UniFi Network: Settings > WiFi > (SSID) > Radio Band."
                });
            }

            // Run WiFi Optimizer rules for IoT SSID separation, band steering recommendations, etc.
            if (_cachedWlanConfigs != null && _cachedNetworks != null)
            {
                var context = BuildOptimizerContext(onlineAps, _cachedClients, _cachedWlanConfigs, _cachedNetworks);
                _optimizerEngine.EvaluateRules(_cachedHealthScore, context);
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
    public async Task<List<AccessPointSnapshot>> GetAccessPointsAsync(int siteId, bool forceRefresh = false)
    {
        if (!_connectionService.IsConnected(siteId))
        {
            return new List<AccessPointSnapshot>();
        }

        if (!forceRefresh && _cachedAps != null && DateTimeOffset.UtcNow - _lastRefresh < _cacheExpiry)
        {
            return _cachedAps;
        }

        await RefreshDataAsync(siteId);
        return _cachedAps ?? new List<AccessPointSnapshot>();
    }

    /// <summary>
    /// Get all wireless clients with current connection data
    /// </summary>
    public async Task<List<WirelessClientSnapshot>> GetWirelessClientsAsync(int siteId, bool forceRefresh = false)
    {
        if (!_connectionService.IsConnected(siteId))
        {
            return new List<WirelessClientSnapshot>();
        }

        if (!forceRefresh && _cachedClients != null && DateTimeOffset.UtcNow - _lastRefresh < _cacheExpiry)
        {
            return _cachedClients;
        }

        await RefreshDataAsync(siteId);
        return _cachedClients ?? new List<WirelessClientSnapshot>();
    }

    /// <summary>
    /// Get roaming topology data
    /// </summary>
    public async Task<RoamingTopology?> GetRoamingTopologyAsync(int siteId, bool forceRefresh = false)
    {
        if (!_connectionService.IsConnected(siteId))
        {
            return null;
        }

        if (!forceRefresh && _cachedRoamingData != null && DateTimeOffset.UtcNow - _lastRefresh < _cacheExpiry)
        {
            return _cachedRoamingData;
        }

        await RefreshDataAsync(siteId);
        return _cachedRoamingData;
    }

    /// <summary>
    /// Get WLAN configurations with band steering settings
    /// </summary>
    public async Task<List<WlanConfiguration>> GetWlanConfigurationsAsync(int siteId, bool forceRefresh = false)
    {
        if (!_connectionService.IsConnected(siteId))
        {
            return new List<WlanConfiguration>();
        }

        if (!forceRefresh && _cachedWlanConfigs != null && DateTimeOffset.UtcNow - _lastRefresh < _cacheExpiry)
        {
            return _cachedWlanConfigs;
        }

        await RefreshDataAsync(siteId);
        return _cachedWlanConfigs ?? new List<WlanConfiguration>();
    }

    /// <summary>
    /// Get summary statistics for dashboard display
    /// </summary>
    public async Task<WiFiSummary> GetSummaryAsync(int siteId)
    {
        var summary = new WiFiSummary();

        if (!_connectionService.IsConnected(siteId))
        {
            return summary;
        }

        try
        {
            var aps = await GetAccessPointsAsync(siteId);
            var clients = await GetWirelessClientsAsync(siteId);
            var healthScore = await GetSiteHealthScoreAsync(siteId);

            summary.TotalAps = aps.Count;
            var onlineClients = clients.Where(c => c.IsOnline).ToList();
            summary.TotalClients = onlineClients.Count;
            summary.ClientsOn2_4GHz = onlineClients.Count(c => c.Band == RadioBand.Band2_4GHz);
            summary.ClientsOn5GHz = onlineClients.Count(c => c.Band == RadioBand.Band5GHz);
            summary.ClientsOn6GHz = onlineClients.Count(c => c.Band == RadioBand.Band6GHz);
            summary.HealthScore = healthScore?.OverallScore;
            summary.HealthGrade = healthScore?.Grade;

            if (onlineClients.Any(c => c.Satisfaction.HasValue))
            {
                summary.AvgSatisfaction = (int)onlineClients
                    .Where(c => c.Satisfaction.HasValue)
                    .Average(c => c.Satisfaction!.Value);
            }

            if (onlineClients.Any(c => c.Signal.HasValue))
            {
                summary.AvgSignal = (int)onlineClients
                    .Where(c => c.Signal.HasValue)
                    .Average(c => c.Signal!.Value);
            }

            summary.WeakSignalClients = onlineClients.Count(c => c.Signal.HasValue && c.Signal.Value < -70);

            // Check if MLO is enabled on any enabled WLAN
            var wlanConfigs = await GetWlanConfigurationsAsync(siteId);
            summary.MloEnabled = wlanConfigs.Any(w => w.Enabled && w.MloEnabled);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get Wi-Fi summary");
        }

        return summary;
    }

    private async Task RefreshDataAsync(int siteId)
    {
        if (_connectionService.GetClient(siteId) == null)
        {
            _logger.LogWarning("UniFi client not available");
            return;
        }

        try
        {
            var provider = CreateProvider(siteId);

            // Fetch data in parallel
            var apsTask = provider.GetAccessPointsAsync();
            var clientsTask = provider.GetWirelessClientsAsync();
            var roamingTask = provider.GetRoamingTopologyAsync();
            var wlanTask = provider.GetWlanConfigurationsAsync();
            var networkTask = _connectionService.GetClient(siteId)!.GetNetworkConfigsAsync();

            await Task.WhenAll(apsTask, clientsTask, roamingTask, wlanTask, networkTask);

            // Sort APs by IP address for consistent display across all components
            _cachedAps = WiFiAnalysisHelpers.SortByIp(await apsTask);
            _cachedClients = await clientsTask;
            _cachedRoamingData = await roamingTask;
            _cachedWlanConfigs = await wlanTask;

            // Convert UniFi network configs to classified NetworkInfo using VlanAnalyzer
            var networkConfigs = await networkTask;
            _cachedNetworks = networkConfigs
                .Where(n => !n.Purpose.Equals("wan", StringComparison.OrdinalIgnoreCase)) // Exclude WAN networks
                .Select(n => new AuditNetworkInfo
                {
                    Id = n.Id,
                    Name = n.Name,
                    VlanId = n.Vlan ?? 1,
                    Purpose = _vlanAnalyzer.ClassifyNetwork(n.Name, n.Purpose, n.Vlan ?? 1,
                        n.DhcpdEnabled, null, n.InternetAccessEnabled, n.FirewallZoneId, null),
                    Subnet = n.IpSubnet,
                    DhcpEnabled = n.DhcpdEnabled,
                    InternetAccessEnabled = n.InternetAccessEnabled,
                    Enabled = n.Enabled,
                    FirewallZoneId = n.FirewallZoneId,
                    NetworkGroup = n.Networkgroup
                })
                .ToList();

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
        _cachedNetworks = null;
        _cachedRoamingData = null;
        _cachedHealthScore = null;
        _cachedScanResults = null;
        _lastRefresh = DateTimeOffset.MinValue;
    }

    /// <summary>
    /// Build the context for WiFi Optimizer rules evaluation.
    /// </summary>
    private WiFiOptimizerContext BuildOptimizerContext(
        List<AccessPointSnapshot> aps,
        List<WirelessClientSnapshot> clients,
        List<WlanConfiguration> wlans,
        List<AuditNetworkInfo> networks)
    {
        // Determine which APs have which bands available
        var has5gAps = aps.Any(ap => ap.Radios.Any(r => r.Band == RadioBand.Band5GHz && r.Channel.HasValue));
        var has6gAps = aps.Any(ap => ap.Radios.Any(r => r.Band == RadioBand.Band6GHz && r.Channel.HasValue));

        // Classify clients
        var legacyClients = new List<WirelessClientSnapshot>();
        var steerableClients = new List<WirelessClientSnapshot>();

        foreach (var client in clients)
        {
            var supports5g = client.Capabilities.Supports5GHz;
            var supports6g = client.Capabilities.Supports6GHz;

            if (client.Band == RadioBand.Band2_4GHz)
            {
                if (supports6g && has6gAps)
                    steerableClients.Add(client);
                else if (supports5g && has5gAps)
                    steerableClients.Add(client);
                else
                    legacyClients.Add(client); // 2.4 GHz only
            }
            else if (client.Band == RadioBand.Band5GHz && supports6g && has6gAps)
            {
                steerableClients.Add(client);
            }
        }

        return new WiFiOptimizerContext
        {
            Wlans = wlans,
            Networks = networks,
            AccessPoints = aps,
            Clients = clients,
            LegacyClients = legacyClients,
            SteerableClients = steerableClients
        };
    }

    // Cached regulatory channel data (rarely changes - per country/regulatory domain)
    private RegulatoryChannelData? _cachedRegulatoryChannels;
    private DateTimeOffset _regulatoryChannelsFetchTime = DateTimeOffset.MinValue;
    private static readonly TimeSpan RegulatoryChannelsCacheExpiry = TimeSpan.FromMinutes(30);

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
        int siteId,
        bool forceRefresh = false,
        DateTimeOffset? startTime = null,
        DateTimeOffset? endTime = null)
    {
        if (!_connectionService.IsConnected(siteId) || _connectionService.GetClient(siteId) == null)
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
            var provider = CreateProvider(siteId);

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
    /// Get regulatory channel availability data for the site's country.
    /// Cached for 30 minutes since regulatory data rarely changes.
    /// </summary>
    public async Task<RegulatoryChannelData?> GetRegulatoryChannelsAsync(int siteId)
    {
        if (!_connectionService.IsConnected(siteId) || _connectionService.GetClient(siteId) == null)
        {
            _logger.LogDebug("Cannot get regulatory channels - not connected to UniFi");
            return null;
        }

        if (_cachedRegulatoryChannels != null &&
            DateTimeOffset.UtcNow - _regulatoryChannelsFetchTime < RegulatoryChannelsCacheExpiry)
        {
            return _cachedRegulatoryChannels;
        }

        try
        {
            using var doc = await _connectionService.GetClient(siteId)!.GetCurrentChannelDataAsync();
            if (doc == null) return _cachedRegulatoryChannels; // Return stale cache if available

            if (doc.RootElement.TryGetProperty("data", out var data) &&
                data.ValueKind == JsonValueKind.Array &&
                data.GetArrayLength() > 0)
            {
                _cachedRegulatoryChannels = RegulatoryChannelData.Parse(data[0]);
                _regulatoryChannelsFetchTime = DateTimeOffset.UtcNow;
                _logger.LogInformation("Loaded regulatory channel data");
            }

            return _cachedRegulatoryChannels;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get regulatory channel data");
            return _cachedRegulatoryChannels; // Return stale cache on error
        }
    }

    /// <summary>
    /// Get site-wide Wi-Fi metrics time series for AirView charts
    /// </summary>
    public async Task<List<WiFi.Models.SiteWiFiMetrics>> GetSiteMetricsAsync(
        int siteId,
        DateTimeOffset start,
        DateTimeOffset end,
        WiFi.MetricGranularity granularity = WiFi.MetricGranularity.FiveMinutes)
    {
        if (!_connectionService.IsConnected(siteId) || _connectionService.GetClient(siteId) == null)
        {
            _logger.LogDebug("Cannot get site metrics - not connected to UniFi");
            return new List<WiFi.Models.SiteWiFiMetrics>();
        }

        try
        {
            var provider = CreateProvider(siteId);

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
        int siteId,
        string[] apMacs,
        DateTimeOffset start,
        DateTimeOffset end,
        WiFi.MetricGranularity granularity = WiFi.MetricGranularity.FiveMinutes)
    {
        if (!_connectionService.IsConnected(siteId) || _connectionService.GetClient(siteId) == null)
        {
            _logger.LogDebug("Cannot get AP metrics - not connected to UniFi");
            return new List<WiFi.Models.SiteWiFiMetrics>();
        }

        try
        {
            var provider = CreateProvider(siteId);

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
        int siteId,
        string clientMac,
        DateTimeOffset start,
        DateTimeOffset end,
        WiFi.MetricGranularity granularity = WiFi.MetricGranularity.FiveMinutes)
    {
        if (!_connectionService.IsConnected(siteId) || _connectionService.GetClient(siteId) == null)
        {
            _logger.LogDebug("Cannot get client metrics - not connected to UniFi");
            return new List<WiFi.Models.ClientWiFiMetrics>();
        }

        try
        {
            var provider = CreateProvider(siteId);

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
        int siteId,
        string clientMac,
        int limit = 200)
    {
        if (!_connectionService.IsConnected(siteId) || _connectionService.GetClient(siteId) == null)
        {
            _logger.LogDebug("Cannot get client events - not connected to UniFi");
            return new List<WiFi.Models.ClientConnectionEvent>();
        }

        try
        {
            var provider = CreateProvider(siteId);

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
