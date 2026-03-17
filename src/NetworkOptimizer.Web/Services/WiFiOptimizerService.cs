using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Audit.Analyzers;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.WiFi;
using NetworkOptimizer.WiFi.Analyzers;
using NetworkOptimizer.WiFi.Models;
using NetworkOptimizer.WiFi.Providers;
using NetworkOptimizer.WiFi.Rules;
using NetworkOptimizer.WiFi.Services;
using AuditNetworkInfo = NetworkOptimizer.Audit.Models.NetworkInfo;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Service layer for Wi-Fi Optimizer feature.
/// Coordinates data providers and analyzers.
/// </summary>
public class WiFiOptimizerService
{
    private readonly UniFiConnectionService _connectionService;
    private readonly ISystemSettingsService _settingsService;
    private readonly ILogger<WiFiOptimizerService> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly SiteHealthScorer _healthScorer;
    private readonly WiFiOptimizerEngine _optimizerEngine;
    private readonly VlanAnalyzer _vlanAnalyzer;
    private readonly HeatmapDataCache _heatmapCache;
    private readonly FloorPlanService _floorPlanService;
    private readonly IServiceProvider _serviceProvider;
    private readonly PlannedApService _plannedApService;
    private readonly ChannelRecommendationService _channelRecommendationService;

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
        ISystemSettingsService settingsService,
        HeatmapDataCache heatmapCache,
        FloorPlanService floorPlanService,
        IServiceProvider serviceProvider,
        PlannedApService plannedApService,
        ChannelRecommendationService channelRecommendationService,
        ILogger<WiFiOptimizerService> logger,
        ILoggerFactory loggerFactory)
    {
        _connectionService = connectionService;
        _optimizerEngine = optimizerEngine;
        _vlanAnalyzer = vlanAnalyzer;
        _settingsService = settingsService;
        _heatmapCache = heatmapCache;
        _floorPlanService = floorPlanService;
        _serviceProvider = serviceProvider;
        _plannedApService = plannedApService;
        _channelRecommendationService = channelRecommendationService;
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
                var context = await BuildOptimizerContextAsync(onlineAps, _cachedClients, _cachedWlanConfigs, _cachedNetworks);
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

            // Fetch data in parallel - use Task.WhenAll to start all tasks,
            // but handle individual failures so one bad task doesn't block everything
            var apsTask = provider.GetAccessPointsAsync();
            var clientsTask = provider.GetWirelessClientsAsync();
            var roamingTask = provider.GetRoamingTopologyAsync();
            var wlanTask = provider.GetWlanConfigurationsAsync();
            var networkTask = _connectionService.Client.GetNetworkConfigsAsync();

            // Wait for all tasks, even if some fail
            await Task.WhenAll(
                apsTask.ContinueWith(_ => { }, TaskContinuationOptions.ExecuteSynchronously),
                clientsTask.ContinueWith(_ => { }, TaskContinuationOptions.ExecuteSynchronously),
                roamingTask.ContinueWith(_ => { }, TaskContinuationOptions.ExecuteSynchronously),
                wlanTask.ContinueWith(_ => { }, TaskContinuationOptions.ExecuteSynchronously),
                networkTask.ContinueWith(_ => { }, TaskContinuationOptions.ExecuteSynchronously));

            // Extract results, logging failures individually
            if (apsTask.IsCompletedSuccessfully)
            {
                _cachedAps = WiFiAnalysisHelpers.SortByIp(apsTask.Result);
            }
            else if (apsTask.IsFaulted)
            {
                _logger.LogError(apsTask.Exception?.InnerException, "Failed to fetch access points");
            }

            if (clientsTask.IsCompletedSuccessfully)
            {
                _cachedClients = clientsTask.Result;
            }
            else if (clientsTask.IsFaulted)
            {
                _logger.LogError(clientsTask.Exception?.InnerException, "Failed to fetch wireless clients");
            }

            if (roamingTask.IsCompletedSuccessfully)
            {
                _cachedRoamingData = roamingTask.Result;
            }
            else if (roamingTask.IsFaulted)
            {
                _logger.LogWarning(roamingTask.Exception?.InnerException, "Failed to fetch roaming topology");
            }

            if (wlanTask.IsCompletedSuccessfully)
            {
                _cachedWlanConfigs = wlanTask.Result;
            }
            else if (wlanTask.IsFaulted)
            {
                _logger.LogWarning(wlanTask.Exception?.InnerException, "Failed to fetch WLAN configurations");
            }

            // Convert UniFi network configs to classified NetworkInfo using VlanAnalyzer
            if (networkTask.IsCompletedSuccessfully)
            {
                var networkConfigs = networkTask.Result;
                _cachedNetworks = networkConfigs
                    .Where(n => !n.Purpose.Equals("wan", StringComparison.OrdinalIgnoreCase))
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

                // Apply user purpose overrides (same overrides used by Security Audit)
                var overridesJson = await _settingsService.GetAsync("audit:networkPurposeOverrides");
                if (!string.IsNullOrEmpty(overridesJson))
                {
                    try
                    {
                        var overrides = JsonSerializer.Deserialize<Dictionary<string, string>>(overridesJson);
                        _vlanAnalyzer.ApplyPurposeOverrides(_cachedNetworks, overrides);
                    }
                    catch (JsonException ex)
                    {
                        _logger.LogWarning(ex, "Failed to parse network purpose overrides");
                    }
                }
            }
            else if (networkTask.IsFaulted)
            {
                _logger.LogWarning(networkTask.Exception?.InnerException, "Failed to fetch network configs");
            }

            _lastRefresh = DateTimeOffset.UtcNow;

            // Enrich roaming topology with proper model names from AP data
            if (_cachedRoamingData != null && _cachedAps is { Count: > 0 })
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
                _cachedAps?.Count ?? 0, _cachedClients?.Count ?? 0);
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
    private async Task<WiFiOptimizerContext> BuildOptimizerContextAsync(
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

        // Load propagation data for spatial interference checking
        ApPropagationContext? propCtx = null;
        try
        {
            // Resolve ApMapService lazily to avoid circular dependency
            // (ApMapService -> WiFiOptimizerService -> ApMapService)
            var apMapService = _serviceProvider.GetRequiredService<ApMapService>();
            var cached = await _heatmapCache.GetOrLoadAsync(_floorPlanService, apMapService, _plannedApService);
            var placedAps = cached.ApMarkers
                .Where(a => a.Latitude.HasValue && a.Longitude.HasValue)
                .ToList();

            if (placedAps.Count > 0)
            {
                propCtx = new ApPropagationContext
                {
                    ApsByMac = placedAps.ToDictionary(
                        a => a.Mac.ToLowerInvariant(),
                        a => new PropagationAp
                        {
                            Mac = a.Mac,
                            Model = a.Model,
                            Latitude = a.Latitude!.Value,
                            Longitude = a.Longitude!.Value,
                            Floor = a.Floor ?? 1,
                            OrientationDeg = a.OrientationDeg,
                            MountType = a.MountType
                        }),
                    WallsByFloor = cached.WallsByFloor,
                    Buildings = cached.BuildingFloorInfos
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to load propagation data for interference checking");
        }

        return new WiFiOptimizerContext
        {
            Wlans = wlans,
            Networks = networks,
            AccessPoints = aps,
            Clients = clients,
            LegacyClients = legacyClients,
            SteerableClients = steerableClients,
            PropagationContext = propCtx
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
    /// Get regulatory channel availability data for the site's country.
    /// Cached for 30 minutes since regulatory data rarely changes.
    /// </summary>
    public async Task<RegulatoryChannelData?> GetRegulatoryChannelsAsync()
    {
        if (!_connectionService.IsConnected || _connectionService.Client == null)
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
            using var doc = await _connectionService.Client.GetCurrentChannelDataAsync();
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
    /// Get AP channel change events from the system log (v2 API).
    /// Returns empty list on failure - never throws.
    /// </summary>
    public async Task<List<WiFi.Models.ChannelChangeEvent>> GetChannelChangeEventsAsync(
        DateTimeOffset start,
        DateTimeOffset end,
        string? apMac = null)
    {
        if (!_connectionService.IsConnected || _connectionService.Client == null)
            return new List<WiFi.Models.ChannelChangeEvent>();

        try
        {
            var provider = CreateProvider();
            return await provider.GetChannelChangeEventsAsync(start, end, apMac);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get channel change events");
            return new List<WiFi.Models.ChannelChangeEvent>();
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

    /// <summary>
    /// Get channel recommendations for a specific band.
    /// Coordinates data loading and calls the recommendation engine for all bands.
    /// </summary>
    public async Task<Dictionary<RadioBand, ChannelPlan>> GetAllChannelRecommendationsAsync(
        RecommendationOptions? options = null)
    {
        var results = new Dictionary<RadioBand, ChannelPlan>();

        if (!_connectionService.IsConnected)
        {
            _logger.LogDebug("Cannot get channel recommendations - not connected to UniFi");
            return results;
        }

        try
        {
            // Load all required data once (shared across all bands)
            var apsTask = GetAccessPointsAsync();
            var regulatoryTask = GetRegulatoryChannelsAsync();
            var scanTask = GetChannelScanResultsAsync(
                startTime: DateTimeOffset.UtcNow.AddHours(-ChannelRecommendationService.ScanLookbackHours));

            await Task.WhenAll(apsTask, regulatoryTask, scanTask);

            var aps = apsTask.Result;
            var regulatoryData = regulatoryTask.Result;
            var scanResults = scanTask.Result;

            if (aps.Count == 0)
            {
                _logger.LogDebug("No APs available for channel recommendations");
                return results;
            }

            // Load propagation context once (same pattern as BuildOptimizerContextAsync)
            ApPropagationContext? propCtx = null;
            bool hasBuildingData = false;
            try
            {
                var apMapService = _serviceProvider.GetRequiredService<ApMapService>();
                var cached = await _heatmapCache.GetOrLoadAsync(_floorPlanService, apMapService, _plannedApService);
                var placedAps = cached.ApMarkers
                    .Where(a => a.Latitude.HasValue && a.Longitude.HasValue)
                    .ToList();

                hasBuildingData = placedAps.Count > 0 && cached.BuildingFloorInfos.Count > 0;

                if (placedAps.Count > 0)
                {
                    propCtx = new ApPropagationContext
                    {
                        ApsByMac = placedAps.ToDictionary(
                            a => a.Mac.ToLowerInvariant(),
                            a => new PropagationAp
                            {
                                Mac = a.Mac,
                                Model = a.Model,
                                Latitude = a.Latitude!.Value,
                                Longitude = a.Longitude!.Value,
                                Floor = a.Floor ?? 1,
                                OrientationDeg = a.OrientationDeg,
                                MountType = a.MountType
                            }),
                        WallsByFloor = cached.WallsByFloor,
                        Buildings = cached.BuildingFloorInfos
                    };
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to load propagation data for channel recommendations");
            }

            // Fetch 30-day historical radio stats paired with channel change events
            var historicalStress = await GetHistoricalStressAsync(aps);

            // Generate recommendations for each band that has APs
            var bands = new[] { RadioBand.Band2_4GHz, RadioBand.Band5GHz, RadioBand.Band6GHz };
            foreach (var band in bands)
            {
                var bandAps = aps.Where(ap =>
                    ap.IsOnline && ap.Radios.Any(r => r.Band == band && r.Channel.HasValue)).ToList();
                if (bandAps.Count == 0) continue;

                try
                {
                    var bandStress = historicalStress?.GetValueOrDefault(band);
                    var graph = _channelRecommendationService.BuildInterferenceGraph(
                        aps, band, propCtx, scanResults, regulatoryData, options, bandStress);

                    var plan = _channelRecommendationService.Optimize(
                        graph, band, regulatoryData, options, hasBuildingData);

                    results[band] = plan;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to get channel recommendations for {Band}", band);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get channel recommendations");
        }

        return results;
    }

    /// <summary>
    /// Fetch per-AP historical radio stats (30 days, daily granularity) and pair with
    /// channel change events to build per-channel stress maps. Returns stress data keyed
    /// by band → AP MAC → channel → (avg util, avg interf, avg txRetry).
    /// </summary>
    private async Task<Dictionary<RadioBand, Dictionary<string, Dictionary<int, (double Utilization, double Interference, double TxRetryPct)>>>?>
        GetHistoricalStressAsync(List<AccessPointSnapshot> aps)
    {
        if (!_connectionService.IsConnected || _connectionService.Client == null)
            return null;

        try
        {
            var end = DateTimeOffset.UtcNow;
            var start = end.AddDays(-7);
            var onlineAps = aps.Where(ap => ap.IsOnline).ToList();
            if (onlineAps.Count == 0) return null;

            // Fetch 7-day hourly + 1-day 5-min metrics and channel change events concurrently
            var recentStart = end.AddDays(-1);
            var tasks = onlineAps.Select(async ap =>
            {
                var metricsTask = GetApMetricsAsync(
                    new[] { ap.Mac }, start, end, MetricGranularity.Hourly);
                var recentMetricsTask = GetApMetricsAsync(
                    new[] { ap.Mac }, recentStart, end, MetricGranularity.FiveMinutes);
                var eventsTask = GetChannelChangeEventsAsync(start, end, ap.Mac);

                await Task.WhenAll(metricsTask, recentMetricsTask, eventsTask);

                return (ap.Mac, Metrics: metricsTask.Result, RecentMetrics: recentMetricsTask.Result, Events: eventsTask.Result);
            });

            var allResults = await Task.WhenAll(tasks);

            var bands = new[] { RadioBand.Band2_4GHz, RadioBand.Band5GHz, RadioBand.Band6GHz };
            var result = new Dictionary<RadioBand, Dictionary<string, Dictionary<int, (double, double, double)>>>();
            foreach (var band in bands)
                result[band] = new Dictionary<string, Dictionary<int, (double, double, double)>>(StringComparer.OrdinalIgnoreCase);

            foreach (var (mac, metrics, recentMetrics, events) in allResults)
            {
                if (metrics.Count == 0) continue;
                var macLower = mac.ToLowerInvariant();

                // Find the current channel for each band from the AP snapshot
                var ap = onlineAps.First(a => a.Mac.Equals(mac, StringComparison.OrdinalIgnoreCase));

                foreach (var band in bands)
                {
                    var radio = ap.Radios.FirstOrDefault(r => r.Band == band && r.Channel.HasValue);
                    if (radio == null) continue;

                    // Build channel timeline from change events (sorted chronologically)
                    var bandEvents = events
                        .Where(e => e.Band == band)
                        .OrderBy(e => e.Timestamp)
                        .ToList();

                    _logger.LogDebug("[ChannelRec] {ApName} {Band}: {EventCount} channel events, current=ch{CurrentCh}, events=[{Events}]",
                        ap.Name, band, bandEvents.Count, radio.Channel,
                        string.Join(", ", bandEvents.Select(e => $"{e.Timestamp:MM/dd} ch{e.PreviousChannel}→ch{e.NewChannel}")));

                    // 7-day hourly: average per channel
                    var channelMetrics = new Dictionary<int, List<(double Util, double Interf, double TxRetry)>>();
                    foreach (var metric in metrics)
                    {
                        if (!metric.ByBand.TryGetValue(band, out var bandData) ||
                            !bandData.ChannelUtilization.HasValue)
                            continue;

                        var channel = GetChannelAtTime(metric.Timestamp, bandEvents, radio.Channel!.Value);
                        if (!channelMetrics.ContainsKey(channel))
                            channelMetrics[channel] = new List<(double, double, double)>();
                        channelMetrics[channel].Add((
                            bandData.ChannelUtilization ?? 0,
                            bandData.Interference ?? 0,
                            bandData.TxRetryPct ?? 0));
                    }

                    // 1-day 5-min: average for current channel only (higher resolution recent data)
                    var recentCurrentChannel = new List<(double Util, double Interf, double TxRetry)>();
                    foreach (var metric in recentMetrics)
                    {
                        if (!metric.ByBand.TryGetValue(band, out var bandData) ||
                            !bandData.ChannelUtilization.HasValue)
                            continue;

                        var channel = GetChannelAtTime(metric.Timestamp, bandEvents, radio.Channel!.Value);
                        if (channel == radio.Channel!.Value)
                        {
                            recentCurrentChannel.Add((
                                bandData.ChannelUtilization ?? 0,
                                bandData.Interference ?? 0,
                                bandData.TxRetryPct ?? 0));
                        }
                    }

                    if (channelMetrics.Count > 0)
                    {
                        var perChannel = new Dictionary<int, (double, double, double)>();
                        foreach (var (ch, dataPoints) in channelMetrics)
                        {
                            var avg = (
                                dataPoints.Average(d => d.Util),
                                dataPoints.Average(d => d.Interf),
                                dataPoints.Average(d => d.TxRetry));

                            // For current channel: use max of 7-day avg and 1-day avg
                            // so recent deterioration isn't diluted by older data
                            if (ch == radio.Channel!.Value && recentCurrentChannel.Count > 0)
                            {
                                var recentAvg = (
                                    recentCurrentChannel.Average(d => d.Util),
                                    recentCurrentChannel.Average(d => d.Interf),
                                    recentCurrentChannel.Average(d => d.TxRetry));

                                avg = (
                                    Math.Max(avg.Item1, recentAvg.Item1),
                                    Math.Max(avg.Item2, recentAvg.Item2),
                                    Math.Max(avg.Item3, recentAvg.Item3));

                                _logger.LogDebug("[ChannelRec] {ApName} {Band} ch{Ch}: 7d avg u={U7:F1}% i={I7:F1}% tx={T7:F1}%, " +
                                    "1d avg u={U1:F1}% i={I1:F1}% tx={T1:F1}% ({Count} samples), using max",
                                    ap.Name, band, ch,
                                    dataPoints.Average(d => d.Util), dataPoints.Average(d => d.Interf), dataPoints.Average(d => d.TxRetry),
                                    recentAvg.Item1, recentAvg.Item2, recentAvg.Item3, recentCurrentChannel.Count);
                            }

                            perChannel[ch] = avg;
                        }
                        result[band][macLower] = perChannel;
                    }
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to fetch historical stress metrics, falling back to snapshot");
            return null;
        }
    }

    /// <summary>
    /// Determine which channel an AP was on at a given timestamp by walking
    /// the channel change event timeline backwards.
    /// </summary>
    private static int GetChannelAtTime(
        DateTimeOffset timestamp,
        List<ChannelChangeEvent> events,
        int currentChannel)
    {
        // Walk events in reverse to find the most recent change before this timestamp
        for (int i = events.Count - 1; i >= 0; i--)
        {
            if (events[i].Timestamp <= timestamp)
                return events[i].NewChannel;
        }

        // Before any recorded change: use the first event's PreviousChannel if available
        if (events.Count > 0)
            return events[0].PreviousChannel;

        // No change events at all: assume current channel
        return currentChannel;
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
