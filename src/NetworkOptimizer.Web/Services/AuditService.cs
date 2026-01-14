using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using NetworkOptimizer.Audit;
using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.Audit.Rules;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Core.Models;
using NetworkOptimizer.Storage.Interfaces;
using NetworkOptimizer.Storage.Models;
using AuditModels = NetworkOptimizer.Audit.Models;
using StorageAuditResult = NetworkOptimizer.Storage.Models.AuditResult;

namespace NetworkOptimizer.Web.Services;

public class AuditService
{
    // Cache keys for IMemoryCache
    private const string CacheKeyLastAuditResult = "AuditService_LastAuditResult";
    private const string CacheKeyLastAuditTime = "AuditService_LastAuditTime";
    private const string CacheKeyDismissedIssues = "AuditService_DismissedIssues";
    private const string CacheKeyDismissedIssuesLoaded = "AuditService_DismissedIssuesLoaded";

    private readonly ILogger<AuditService> _logger;
    private readonly UniFiConnectionService _connectionService;
    private readonly ConfigAuditEngine _auditEngine;
    private readonly IAuditRepository _auditRepository;
    private readonly SystemSettingsService _settingsService;
    private readonly FingerprintDatabaseService _fingerprintService;
    private readonly IMemoryCache _cache;

    public AuditService(
        ILogger<AuditService> logger,
        UniFiConnectionService connectionService,
        ConfigAuditEngine auditEngine,
        IAuditRepository auditRepository,
        SystemSettingsService settingsService,
        FingerprintDatabaseService fingerprintService,
        IMemoryCache cache)
    {
        _logger = logger;
        _connectionService = connectionService;
        _auditEngine = auditEngine;
        _auditRepository = auditRepository;
        _settingsService = settingsService;
        _fingerprintService = fingerprintService;
        _cache = cache;
    }

    // Cache accessors using IMemoryCache
    private AuditResult? LastAuditResultCached
    {
        get => _cache.Get<AuditResult>(CacheKeyLastAuditResult);
        set
        {
            if (value != null)
                _cache.Set(CacheKeyLastAuditResult, value);
            else
                _cache.Remove(CacheKeyLastAuditResult);
        }
    }

    private DateTime? LastAuditTimeCached
    {
        get => _cache.Get<DateTime?>(CacheKeyLastAuditTime);
        set
        {
            if (value != null)
                _cache.Set(CacheKeyLastAuditTime, value);
            else
                _cache.Remove(CacheKeyLastAuditTime);
        }
    }

    private ConcurrentDictionary<string, byte> DismissedIssuesCache
    {
        get => _cache.GetOrCreate(CacheKeyDismissedIssues, _ => new ConcurrentDictionary<string, byte>())!;
    }

    private bool DismissedIssuesLoaded
    {
        get => _cache.Get<bool>(CacheKeyDismissedIssuesLoaded);
        set => _cache.Set(CacheKeyDismissedIssuesLoaded, value);
    }

    /// <summary>
    /// Clears all in-memory cached audit data.
    /// Call this after clearing audit data from the database.
    /// </summary>
    public void ClearCache()
    {
        _cache.Remove(CacheKeyLastAuditResult);
        _cache.Remove(CacheKeyLastAuditTime);
        _cache.Remove(CacheKeyDismissedIssues);
        _cache.Remove(CacheKeyDismissedIssuesLoaded);
        _logger.LogInformation("Audit cache cleared");
    }

    /// <summary>
    /// Ensure dismissed issues are loaded from database
    /// </summary>
    private async Task EnsureDismissedIssuesLoadedAsync()
    {
        if (DismissedIssuesLoaded) return;

        try
        {
            var dismissed = await _auditRepository.GetDismissedIssuesAsync();
            var cache = DismissedIssuesCache;
            foreach (var issue in dismissed)
            {
                cache.TryAdd(issue.IssueKey, 0);
            }
            DismissedIssuesLoaded = true;
            _logger.LogInformation("Loaded {Count} dismissed issues from database", dismissed.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load dismissed issues from database");
            DismissedIssuesLoaded = true; // Don't retry on every call
        }
    }

    /// <summary>
    /// Load audit settings from database into options
    /// </summary>
    private async Task LoadAuditSettingsAsync(AuditOptions options)
    {
        try
        {
            var appleStreaming = await _settingsService.GetAsync("audit:allowAppleStreamingOnMainNetwork");
            var allStreaming = await _settingsService.GetAsync("audit:allowAllStreamingOnMainNetwork");
            var nameBrandTVs = await _settingsService.GetAsync("audit:allowNameBrandTVsOnMainNetwork");
            var allTVs = await _settingsService.GetAsync("audit:allowAllTVsOnMainNetwork");
            var printers = await _settingsService.GetAsync("audit:allowPrintersOnMainNetwork");
            var dnatExcludedVlans = await _settingsService.GetAsync("audit:dnatExcludedVlans");
            var piholePort = await _settingsService.GetAsync("audit:piholeManagementPort");
            var unusedPortDays = await _settingsService.GetAsync("audit:unusedPortInactivityDays");
            var namedPortDays = await _settingsService.GetAsync("audit:namedPortInactivityDays");

            options.AllowAppleStreamingOnMainNetwork = appleStreaming?.ToLower() == "true";
            options.AllowAllStreamingOnMainNetwork = allStreaming?.ToLower() == "true";
            options.AllowNameBrandTVsOnMainNetwork = nameBrandTVs?.ToLower() == "true";
            options.AllowAllTVsOnMainNetwork = allTVs?.ToLower() == "true";
            // Printers default to true (allowed) if not set
            options.AllowPrintersOnMainNetwork = printers == null || printers.ToLower() == "true";
            // DNAT excluded VLANs (parse comma-separated VLAN IDs)
            options.DnatExcludedVlanIds = ParseVlanIds(dnatExcludedVlans);
            // Pi-hole port (null means auto-detect)
            options.PiholeManagementPort = int.TryParse(piholePort, out var port) && port > 0 ? port : null;
            // Unused port thresholds (defaults: 15 days unnamed, 45 days named)
            options.UnusedPortInactivityDays = int.TryParse(unusedPortDays, out var unusedDays) && unusedDays > 0 ? unusedDays : 15;
            options.NamedPortInactivityDays = int.TryParse(namedPortDays, out var namedDays) && namedDays > 0 ? namedDays : 45;

            _logger.LogDebug("Loaded audit settings: AllowApple={Apple}, AllowAllStreaming={AllStreaming}, AllowNameBrandTVs={NameBrandTVs}, AllowAllTVs={AllTVs}, AllowPrinters={Printers}",
                options.AllowAppleStreamingOnMainNetwork, options.AllowAllStreamingOnMainNetwork,
                options.AllowNameBrandTVsOnMainNetwork, options.AllowAllTVsOnMainNetwork, options.AllowPrintersOnMainNetwork);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load audit settings, using defaults");
        }
    }

    public AuditResult? LastAuditResult => LastAuditResultCached;
    public DateTime? LastAuditTime => LastAuditTimeCached;

    /// <summary>
    /// Get a unique key for an issue (for tracking dismissals)
    /// </summary>
    public static string GetIssueKey(AuditIssue issue) =>
        $"{issue.Title}|{issue.DeviceName}|{issue.Port}";

    /// <summary>
    /// Dismiss an issue (excludes it from counts, persisted to database)
    /// </summary>
    public async Task DismissIssueAsync(AuditIssue issue)
    {
        var key = GetIssueKey(issue);
        if (DismissedIssuesCache.TryAdd(key, 0))
        {
            try
            {
                await _auditRepository.SaveDismissedIssueAsync(new DismissedIssue
                {
                    IssueKey = key,
                    DismissedAt = DateTime.UtcNow
                });
                _logger.LogInformation("Dismissed and persisted issue: {Key}", key);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to persist dismissed issue: {Key}", key);
            }
        }
    }

    /// <summary>
    /// Check if an issue has been dismissed
    /// </summary>
    public bool IsIssueDismissed(AuditIssue issue) =>
        DismissedIssuesCache.ContainsKey(GetIssueKey(issue));

    /// <summary>
    /// Get active (non-dismissed) issues (synchronous - may not include dismissed filter if not yet loaded)
    /// Prefer using GetActiveIssuesAsync() for reliable results.
    /// </summary>
    public List<AuditIssue> GetActiveIssues()
    {
        // Return cached data only - don't block on loading dismissed issues
        // If dismissed issues haven't loaded yet, returns all issues
        return LastAuditResultCached?.Issues.Where(i => !IsIssueDismissed(i)).ToList() ?? new();
    }

    /// <summary>
    /// Get active (non-dismissed) issues (async version)
    /// </summary>
    public async Task<List<AuditIssue>> GetActiveIssuesAsync()
    {
        await EnsureDismissedIssuesLoadedAsync();
        return LastAuditResultCached?.Issues.Where(i => !IsIssueDismissed(i)).ToList() ?? new();
    }

    /// <summary>
    /// Get dismissed issues from the current audit result
    /// </summary>
    public async Task<List<AuditIssue>> GetDismissedIssuesAsync()
    {
        await EnsureDismissedIssuesLoadedAsync();
        return LastAuditResultCached?.Issues.Where(i => IsIssueDismissed(i)).ToList() ?? new();
    }

    /// <summary>
    /// Restore a dismissed issue (removes from dismissed list)
    /// </summary>
    public async Task RestoreIssueAsync(AuditIssue issue)
    {
        var key = GetIssueKey(issue);
        if (DismissedIssuesCache.TryRemove(key, out _))
        {
            try
            {
                await _auditRepository.DeleteDismissedIssueAsync(key);
                _logger.LogInformation("Restored issue: {Key}", key);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to remove dismissed issue from database: {Key}", key);
            }
        }
    }

    /// <summary>
    /// Get count of active critical issues
    /// </summary>
    public int ActiveCriticalCount =>
        GetActiveIssues().Count(i => i.Severity == AuditModels.AuditSeverity.Critical);

    /// <summary>
    /// Get count of active recommended issues
    /// </summary>
    public int ActiveRecommendedCount =>
        GetActiveIssues().Count(i => i.Severity == AuditModels.AuditSeverity.Recommended);

    /// <summary>
    /// Clear all dismissed issues (removes from database too)
    /// </summary>
    public async Task ClearDismissedIssuesAsync()
    {
        DismissedIssuesCache.Clear();
        try
        {
            await _auditRepository.ClearAllDismissedIssuesAsync();
            _logger.LogInformation("Cleared all dismissed issues from database");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear dismissed issues from database");
        }
    }

    /// <summary>
    /// Load the most recent audit result from the database
    /// </summary>
    public async Task<AuditResult?> LoadLastAuditFromDatabaseAsync()
    {
        try
        {
            var latestAudit = await _auditRepository.GetLatestAuditResultAsync();

            if (latestAudit == null)
                return null;

            // Parse the stored findings JSON
            var issues = new List<AuditIssue>();
            if (!string.IsNullOrEmpty(latestAudit.FindingsJson))
            {
                try
                {
                    issues = JsonSerializer.Deserialize<List<AuditIssue>>(latestAudit.FindingsJson) ?? new List<AuditIssue>();
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Failed to parse audit findings JSON");
                }
            }

            var result = new AuditResult
            {
                Score = (int)latestAudit.ComplianceScore,
                ScoreLabel = GetScoreLabel((int)latestAudit.ComplianceScore),
                ScoreClass = GetScoreClass((int)latestAudit.ComplianceScore),
                CriticalCount = latestAudit.FailedChecks,
                WarningCount = latestAudit.WarningChecks,
                InfoCount = latestAudit.PassedChecks,
                Issues = issues,
                CompletedAt = latestAudit.AuditDate
            };

            // Parse the stored report data JSON (for PDF generation)
            if (!string.IsNullOrEmpty(latestAudit.ReportDataJson))
            {
                try
                {
                    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    using var doc = JsonDocument.Parse(latestAudit.ReportDataJson);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("Statistics", out var statsEl) || root.TryGetProperty("statistics", out statsEl))
                    {
                        result.Statistics = JsonSerializer.Deserialize<AuditStatistics>(statsEl.GetRawText(), options);
                    }
                    if (root.TryGetProperty("HardeningMeasures", out var hardeningEl) || root.TryGetProperty("hardeningMeasures", out hardeningEl))
                    {
                        result.HardeningMeasures = JsonSerializer.Deserialize<List<string>>(hardeningEl.GetRawText(), options) ?? new();
                    }
                    if (root.TryGetProperty("Networks", out var networksEl) || root.TryGetProperty("networks", out networksEl))
                    {
                        result.Networks = JsonSerializer.Deserialize<List<NetworkReference>>(networksEl.GetRawText(), options) ?? new();
                    }
                    if (root.TryGetProperty("Switches", out var switchesEl) || root.TryGetProperty("switches", out switchesEl))
                    {
                        result.Switches = JsonSerializer.Deserialize<List<SwitchReference>>(switchesEl.GetRawText(), options) ?? new();
                    }
                    if (root.TryGetProperty("WirelessClients", out var wirelessEl) || root.TryGetProperty("wirelessClients", out wirelessEl))
                    {
                        result.WirelessClients = JsonSerializer.Deserialize<List<WirelessClientReference>>(wirelessEl.GetRawText(), options) ?? new();
                    }
                    if (root.TryGetProperty("OfflineClients", out var offlineEl) || root.TryGetProperty("offlineClients", out offlineEl))
                    {
                        result.OfflineClients = JsonSerializer.Deserialize<List<OfflineClientReference>>(offlineEl.GetRawText(), options) ?? new();
                    }
                    if (root.TryGetProperty("DnsSecurity", out var dnsEl) || root.TryGetProperty("dnsSecurity", out dnsEl))
                    {
                        result.DnsSecurity = JsonSerializer.Deserialize<DnsSecurityReference>(dnsEl.GetRawText(), options);
                    }
                    _logger.LogInformation("Restored report data: {Networks} networks, {Switches} switches, {Wireless} wireless clients, DNS={HasDns}",
                        result.Networks.Count, result.Switches.Count, result.WirelessClients.Count, result.DnsSecurity != null);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Failed to parse audit report data JSON");
                }
            }

            // Cache it
            LastAuditResultCached = result;
            LastAuditTimeCached = latestAudit.AuditDate;

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading last audit from database");
            return null;
        }
    }

    /// <summary>
    /// Get audit summary for dashboard display
    /// </summary>
    public async Task<AuditSummary> GetAuditSummaryAsync()
    {
        // Try memory cache first (use active counts to exclude dismissed issues)
        var cachedResult = LastAuditResultCached;
        var cachedTime = LastAuditTimeCached;
        if (cachedResult != null && cachedTime != null)
        {
            var activeIssues = GetActiveIssues();
            return new AuditSummary
            {
                Score = cachedResult.Score,
                CriticalCount = activeIssues.Count(i => i.Severity == AuditModels.AuditSeverity.Critical),
                WarningCount = activeIssues.Count(i => i.Severity == AuditModels.AuditSeverity.Recommended),
                LastAuditTime = cachedTime.Value,
                RecentIssues = activeIssues.Take(5).ToList()
            };
        }

        // Try to load from database
        var dbResult = await LoadLastAuditFromDatabaseAsync();
        var lastAuditTime = LastAuditTimeCached;
        if (dbResult != null && lastAuditTime != null)
        {
            return new AuditSummary
            {
                Score = dbResult.Score,
                CriticalCount = dbResult.CriticalCount,
                WarningCount = dbResult.WarningCount,
                LastAuditTime = lastAuditTime.Value,
                RecentIssues = dbResult.Issues.Take(5).ToList()
            };
        }

        // No audit data available
        return new AuditSummary
        {
            Score = 0,
            CriticalCount = 0,
            WarningCount = 0,
            LastAuditTime = null,
            RecentIssues = new List<AuditIssue>()
        };
    }

    private async Task PersistAuditResultAsync(AuditResult result)
    {
        try
        {
            // Serialize the full report data for PDF generation after page reload
            var reportData = new
            {
                Statistics = result.Statistics,
                HardeningMeasures = result.HardeningMeasures,
                Networks = result.Networks,
                Switches = result.Switches,
                WirelessClients = result.WirelessClients,
                OfflineClients = result.OfflineClients,
                DnsSecurity = result.DnsSecurity
            };
            var reportDataJson = JsonSerializer.Serialize(reportData);

            var storageResult = new StorageAuditResult
            {
                DeviceId = "network-audit",
                DeviceName = "Network Security Audit",
                AuditDate = result.CompletedAt,
                TotalChecks = result.CriticalCount + result.WarningCount + result.InfoCount,
                PassedChecks = result.InfoCount,
                FailedChecks = result.CriticalCount,
                WarningChecks = result.WarningCount,
                ComplianceScore = result.Score,
                FindingsJson = JsonSerializer.Serialize(result.Issues),
                ReportDataJson = reportDataJson,
                AuditVersion = "1.0",
                CreatedAt = DateTime.UtcNow
            };

            await _auditRepository.SaveAuditResultAsync(storageResult);

            _logger.LogInformation("Persisted audit result to database with {IssueCount} issues, {ReportSize} bytes report data",
                result.Issues.Count, reportDataJson.Length);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist audit result to database");
        }
    }

    private static string GetScoreLabel(int score) => score switch
    {
        >= 90 => "EXCELLENT",
        >= 75 => "GOOD",
        >= 60 => "FAIR",
        _ => "NEEDS ATTENTION"
    };

    private static string GetScoreClass(int score) => score switch
    {
        >= 90 => "excellent",
        >= 75 => "good",
        >= 60 => "fair",
        _ => "poor"
    };

    public async Task<AuditResult> RunAuditAsync(AuditOptions options)
    {
        _logger.LogInformation("Running security audit with options: {@Options}", options);

        // Invalidate device cache to ensure fresh data for audit
        _connectionService.InvalidateDeviceCache();

        if (!_connectionService.IsConnected || _connectionService.Client == null)
        {
            _logger.LogWarning("Cannot run audit: UniFi controller not connected");
            return new AuditResult
            {
                Score = 0,
                ScoreLabel = "UNAVAILABLE",
                ScoreClass = "poor",
                Issues = new List<AuditIssue>
                {
                    new AuditIssue
                    {
                        Severity = AuditModels.AuditSeverity.Critical,
                        Category = "Connection",
                        Title = "Controller Not Connected",
                        Description = "Cannot run security audit without an active connection to the UniFi controller.",
                        Recommendation = "Go to Settings and connect to your UniFi controller first."
                    }
                },
                CompletedAt = DateTime.UtcNow
            };
        }

        try
        {
            // Load streaming device settings from database
            await LoadAuditSettingsAsync(options);

            // Get raw device data from UniFi API
            var deviceDataJson = await _connectionService.Client.GetDevicesRawJsonAsync();

            if (string.IsNullOrEmpty(deviceDataJson))
            {
                throw new Exception("No device data returned from UniFi API");
            }

            // Fetch connected clients for enhanced device detection (fingerprint, MAC OUI)
            var clients = await _connectionService.Client.GetClientsAsync();
            _logger.LogInformation("Fetched {ClientCount} connected clients for device detection", clients?.Count ?? 0);

            // Fetch client history for offline device detection (30 days)
            List<NetworkOptimizer.UniFi.Models.UniFiClientHistoryResponse>? clientHistory = null;
            try
            {
                clientHistory = await _connectionService.Client.GetClientHistoryAsync(withinHours: 720);
                _logger.LogInformation("Fetched {HistoryCount} historical clients for offline device detection", clientHistory?.Count ?? 0);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch client history for offline device detection");
            }

            // Get fingerprint database for device name lookups
            var fingerprintDb = await _fingerprintService.GetDatabaseAsync();

            // Fetch settings for DNS security analysis (DoH configuration)
            System.Text.Json.JsonElement? settingsData = null;
            try
            {
                var settingsDoc = await _connectionService.Client.GetSettingsRawAsync();
                if (settingsDoc != null)
                {
                    settingsData = settingsDoc.RootElement;
                    _logger.LogInformation("Fetched site settings for DNS security analysis");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch site settings for DNS analysis");
            }

            // Fetch firewall policies for DNS leak prevention analysis
            System.Text.Json.JsonElement? firewallPoliciesData = null;
            try
            {
                var policiesDoc = await _connectionService.Client.GetFirewallPoliciesRawAsync();
                if (policiesDoc != null)
                {
                    firewallPoliciesData = policiesDoc.RootElement;
                    _logger.LogInformation("Fetched firewall policies for DNS security analysis");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch firewall policies for DNS analysis");
            }

            // Fetch firewall groups (port lists and IP lists) for flattening group references in rules
            List<NetworkOptimizer.UniFi.Models.UniFiFirewallGroup>? firewallGroups = null;
            try
            {
                firewallGroups = await _connectionService.Client.GetFirewallGroupsAsync();
                if (firewallGroups.Count > 0)
                {
                    _logger.LogInformation("Fetched {Count} firewall groups for rule flattening", firewallGroups.Count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch firewall groups");
            }

            // Fetch NAT rules for DNAT DNS detection
            System.Text.Json.JsonElement? natRulesData = null;
            try
            {
                var natDoc = await _connectionService.Client.GetNatRulesRawAsync();
                if (natDoc != null)
                {
                    natRulesData = natDoc.RootElement;
                    _logger.LogInformation("Fetched NAT rules for DNAT DNS analysis");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch NAT rules for DNAT DNS analysis");
            }

            _logger.LogInformation("Running audit engine on device data ({Length} bytes)", deviceDataJson.Length);

            // Fetch UniFi Protect cameras for 100% confidence detection
            ProtectCameraCollection? protectCameras = null;
            try
            {
                protectCameras = await _connectionService.Client.GetProtectCamerasAsync();
                if (protectCameras.Count > 0)
                {
                    _logger.LogInformation("Fetched {Count} UniFi Protect cameras for priority detection", protectCameras.Count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch UniFi Protect cameras (v2 API may not be available)");
            }

            // Fetch port profiles for resolving port configuration from profiles
            List<NetworkOptimizer.UniFi.Models.UniFiPortProfile>? portProfiles = null;
            try
            {
                portProfiles = await _connectionService.Client.GetPortProfilesAsync();
                if (portProfiles.Count > 0)
                {
                    _logger.LogInformation("Fetched {Count} port profiles for port configuration resolution", portProfiles.Count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch port profiles");
            }

            // Fetch UPnP status and port forwarding rules for UPnP security analysis
            bool? upnpEnabled = null;
            List<NetworkOptimizer.UniFi.Models.UniFiPortForwardRule>? portForwardRules = null;
            try
            {
                upnpEnabled = await _connectionService.Client.GetUpnpEnabledAsync();
                portForwardRules = await _connectionService.Client.GetPortForwardRulesAsync();
                var upnpRuleCount = portForwardRules?.Count(r => r.IsUpnp == 1) ?? 0;
                _logger.LogInformation("Fetched UPnP status (Enabled={Enabled}) and {Count} port forwarding rules ({UpnpCount} UPnP)",
                    upnpEnabled, portForwardRules?.Count ?? 0, upnpRuleCount);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch UPnP status or port forwarding rules");
            }

            // Convert options to allowance settings for the audit engine
            var allowanceSettings = new Audit.Models.DeviceAllowanceSettings
            {
                AllowAppleStreamingOnMainNetwork = options.AllowAppleStreamingOnMainNetwork,
                AllowAllStreamingOnMainNetwork = options.AllowAllStreamingOnMainNetwork,
                AllowNameBrandTVsOnMainNetwork = options.AllowNameBrandTVsOnMainNetwork,
                AllowAllTVsOnMainNetwork = options.AllowAllTVsOnMainNetwork,
                AllowPrintersOnMainNetwork = options.AllowPrintersOnMainNetwork
            };

            // Configure unused port detection thresholds
            UnusedPortRule.SetThresholds(options.UnusedPortInactivityDays, options.NamedPortInactivityDays);

            // Run the audit engine with all available data for comprehensive analysis
            var auditResult = await _auditEngine.RunAuditAsync(new Audit.Models.AuditRequest
            {
                DeviceDataJson = deviceDataJson,
                Clients = clients,
                ClientHistory = clientHistory,
                FingerprintDb = fingerprintDb,
                SettingsData = settingsData,
                FirewallPoliciesData = firewallPoliciesData,
                FirewallGroups = firewallGroups,
                NatRulesData = natRulesData,
                AllowanceSettings = allowanceSettings,
                ProtectCameras = protectCameras,
                PortProfiles = portProfiles,
                ClientName = "Network Audit",
                DnatExcludedVlanIds = options.DnatExcludedVlanIds,
                PiholeManagementPort = options.PiholeManagementPort,
                UpnpEnabled = upnpEnabled,
                PortForwardRules = portForwardRules
            });

            // Convert audit result to web models
            var webResult = ConvertAuditResult(auditResult, options);

            // Cache the result
            LastAuditResultCached = webResult;
            LastAuditTimeCached = DateTime.UtcNow;

            // Persist to database
            await PersistAuditResultAsync(webResult);

            _logger.LogInformation("Audit complete: Score={Score}, Critical={Critical}, Recommended={Recommended}",
                webResult.Score, webResult.CriticalCount, webResult.WarningCount);

            return webResult;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error running security audit");

            return new AuditResult
            {
                Score = 0,
                ScoreLabel = "ERROR",
                ScoreClass = "poor",
                Issues = new List<AuditIssue>
                {
                    new AuditIssue
                    {
                        Severity = AuditModels.AuditSeverity.Critical,
                        Category = "System",
                        Title = "Audit Failed",
                        Description = $"An error occurred while running the security audit: {ex.Message}",
                        Recommendation = "Check the logs for more details and ensure the UniFi controller is accessible."
                    }
                },
                CompletedAt = DateTime.UtcNow
            };
        }
    }

    private AuditResult ConvertAuditResult(AuditModels.AuditResult engineResult, AuditOptions options)
    {
        var issues = new List<AuditIssue>();

        foreach (var issue in engineResult.Issues)
        {
            // Filter based on options
            var category = GetCategory(issue.Type);
            if (!ShouldInclude(category, options))
                continue;

            // Extract configurable setting from metadata if present
            string? configurableSetting = null;
            if (issue.Metadata?.TryGetValue("configurable_setting", out var settingObj) == true)
            {
                configurableSetting = settingObj?.ToString();
            }

            issues.Add(new AuditIssue
            {
                Severity = issue.Severity,
                Category = category,
                Title = GetIssueTitle(issue.Type, issue.Message, issue.Severity),
                Description = issue.Message,
                Recommendation = issue.RecommendedAction ?? GetDefaultRecommendation(issue.Type),
                // Context fields
                DeviceName = issue.DeviceName,
                DeviceMac = issue.DeviceMac,
                Port = issue.Port,
                PortName = issue.PortName,
                CurrentNetwork = issue.CurrentNetwork,
                CurrentVlan = issue.CurrentVlan,
                RecommendedNetwork = issue.RecommendedNetwork,
                RecommendedVlan = issue.RecommendedVlan,
                // Wireless-specific fields
                IsWireless = issue.IsWireless,
                ClientName = issue.ClientName,
                ClientMac = issue.ClientMac,
                AccessPoint = issue.AccessPoint,
                WifiBand = issue.WifiBand,
                // Settings link
                ConfigurableSetting = configurableSetting
            });
        }

        // Group by severity in single pass to avoid multiple iterations
        var severityCounts = issues.GroupBy(i => i.Severity)
            .ToDictionary(g => g.Key, g => g.Count());
        var criticalCount = severityCounts.GetValueOrDefault(AuditModels.AuditSeverity.Critical, 0);
        var warningCount = severityCounts.GetValueOrDefault(AuditModels.AuditSeverity.Recommended, 0);
        var infoCount = severityCounts.GetValueOrDefault(AuditModels.AuditSeverity.Informational, 0);

        // Recalculate score based on FILTERED issues only (excluded features don't affect score)
        var score = CalculateFilteredScore(engineResult, options);
        var scoreLabel = GetScoreLabelForScore(score);

        var scoreClass = score switch
        {
            >= 90 => "excellent",
            >= 75 => "good",
            >= 60 => "fair",
            _ => "poor"
        };

        // Convert networks
        var networks = engineResult.Networks
            .OrderBy(n => n.VlanId)
            .Select(n => new NetworkReference
            {
                Id = n.Id,
                Name = n.Name,
                VlanId = n.VlanId,
                Subnet = n.Subnet,
                Purpose = n.Purpose.ToDisplayString()
            })
            .ToList();

        // Convert switches with ports
        var switches = engineResult.Switches
            .OrderBy(s => s.IsGateway ? 0 : 1)
            .ThenBy(s => s.Name)
            .Select(s => new SwitchReference
            {
                Name = s.Name,
                Mac = s.MacAddress,
                Model = s.Model,
                ModelName = s.ModelName ?? s.Model,
                DeviceType = s.Type,
                IsGateway = s.IsGateway,
                IsAccessPoint = s.IsAccessPoint,
                MaxCustomMacAcls = s.Capabilities.MaxCustomMacAcls,
                Ports = s.Ports
                    .OrderBy(p => p.PortIndex)
                    .Select(p => ConvertPort(p, engineResult.Networks))
                    .ToList()
            })
            .ToList();

        // Convert wireless clients
        var wirelessClients = engineResult.WirelessClients
            .Select(wc => new WirelessClientReference
            {
                DisplayName = wc.DisplayName,
                Mac = wc.Mac ?? "",
                AccessPointName = wc.AccessPointName,
                AccessPointMac = wc.AccessPointMac,
                AccessPointModel = wc.AccessPointModel,
                AccessPointModelName = wc.AccessPointModelName,
                NetworkName = wc.Network?.Name,
                VlanId = wc.Network?.VlanId,
                DeviceCategory = wc.Detection.CategoryName,
                VendorName = wc.Detection.VendorName,
                DetectionConfidence = wc.Detection.ConfidenceScore,
                IsIoT = wc.Detection.Category.IsIoT(),
                IsCamera = wc.Detection.Category.IsSurveillance()
            })
            .ToList();

        // Convert offline clients from history
        var offlineClients = engineResult.OfflineClients
            .Select(oc => new OfflineClientReference
            {
                DisplayName = oc.DisplayName,
                Mac = oc.Mac,
                LastUplinkName = oc.LastUplinkName,
                LastUplinkModelName = oc.LastUplinkModelName,
                LastNetwork = oc.LastNetwork,
                DeviceCategory = oc.Detection.CategoryName,
                LastSeenDisplay = oc.LastSeenDisplay,
                IsRecentlyActive = oc.IsRecentlyActive,
                IsIoT = oc.Detection.Category.IsIoT(),
                IsCamera = oc.Detection.Category.IsSurveillance(),
                Detection = oc.Detection
            })
            .ToList();

        // Convert DNS security info
        DnsSecurityReference? dnsSecurity = null;
        if (engineResult.DnsSecurity != null)
        {
            var dns = engineResult.DnsSecurity;
            dnsSecurity = new DnsSecurityReference
            {
                DohEnabled = dns.DohEnabled,
                DohState = dns.DohState,
                DohProviders = dns.DohProviders.ToList(),
                DohConfigNames = dns.DohConfigNames.ToList(),
                DnsLeakProtection = dns.DnsLeakProtection,
                DotBlocked = dns.DotBlocked,
                DoqBlocked = dns.DoqBlocked,
                DohBypassBlocked = dns.DohBypassBlocked,
                Doh3Blocked = dns.Doh3Blocked,
                FullyProtected = dns.FullyProtected,
                WanDnsServers = dns.WanDnsServers.ToList(),
                WanDnsPtrResults = dns.WanDnsPtrResults.ToList(),
                WanDnsMatchesDoH = dns.WanDnsMatchesDoH,
                WanDnsOrderCorrect = dns.WanDnsOrderCorrect,
                WanDnsProvider = dns.WanDnsProvider,
                ExpectedDnsProvider = dns.ExpectedDnsProvider,
                DeviceDnsPointsToGateway = dns.DeviceDnsPointsToGateway,
                TotalDevicesChecked = dns.TotalDevicesChecked,
                DevicesWithCorrectDns = dns.DevicesWithCorrectDns,
                DhcpDeviceCount = dns.DhcpDeviceCount,
                InterfacesWithoutDns = dns.InterfacesWithoutDns.ToList(),
                InterfacesWithMismatch = dns.InterfacesWithMismatch.ToList(),
                MismatchedDnsServers = dns.MismatchedDnsServers.ToList(),
                MatchedDnsServers = dns.MatchedDnsServers.ToList(),
                // Third-party DNS
                HasThirdPartyDns = dns.HasThirdPartyDns,
                IsPiholeDetected = dns.IsPiholeDetected,
                ThirdPartyDnsProviderName = dns.ThirdPartyDnsProviderName,
                ThirdPartyNetworks = dns.ThirdPartyNetworks
                    .Select(n => new ThirdPartyDnsNetworkReference
                    {
                        NetworkName = n.NetworkName,
                        VlanId = n.VlanId,
                        DnsServerIp = n.DnsServerIp,
                        DnsProviderName = n.DnsProviderName
                    })
                    .ToList(),
                // DNAT DNS Coverage
                HasDnatDnsRules = dns.HasDnatDnsRules,
                DnatProvidesFullCoverage = dns.DnatProvidesFullCoverage,
                DnatRedirectTarget = dns.DnatRedirectTarget,
                DnatCoveredNetworks = dns.DnatCoveredNetworks.ToList(),
                DnatUncoveredNetworks = dns.DnatUncoveredNetworks.ToList()
            };
        }

        return new AuditResult
        {
            Score = score,
            ScoreLabel = scoreLabel,
            ScoreClass = scoreClass,
            CriticalCount = criticalCount,
            WarningCount = warningCount,
            InfoCount = infoCount,
            Issues = issues,
            CompletedAt = DateTime.UtcNow,
            Statistics = new AuditStatistics
            {
                TotalPorts = engineResult.Statistics.TotalPorts,
                ActivePorts = engineResult.Statistics.ActivePorts,
                DisabledPorts = engineResult.Statistics.DisabledPorts,
                MacRestrictedPorts = engineResult.Statistics.MacRestrictedPorts,
                NetworkCount = engineResult.Networks.Count,
                SwitchCount = engineResult.Switches.Count
            },
            HardeningMeasures = engineResult.HardeningMeasures.ToList(),
            Networks = networks,
            Switches = switches,
            WirelessClients = wirelessClients,
            OfflineClients = offlineClients,
            DnsSecurity = dnsSecurity
        };
    }

    private PortReference ConvertPort(AuditModels.PortInfo port, List<AuditModels.NetworkInfo> networks)
    {
        var nativeNetwork = networks.FirstOrDefault(n => n.Id == port.NativeNetworkId);
        var excludedNetworks = port.ExcludedNetworkIds?
            .Select(id => networks.FirstOrDefault(n => n.Id == id)?.Name)
            .Where(name => name != null)
            .Select(name => name!)
            .ToList() ?? new List<string>();

        return new PortReference
        {
            PortIndex = port.PortIndex,
            Name = port.Name ?? $"Port {port.PortIndex}",
            IsUp = port.IsUp,
            Speed = port.Speed,
            Forward = port.ForwardMode ?? "all",
            IsUplink = port.IsUplink,
            IsWan = port.IsWan,
            NativeNetwork = nativeNetwork?.Name,
            NativeVlan = nativeNetwork?.VlanId,
            ExcludedNetworks = excludedNetworks,
            PortSecurityEnabled = port.PortSecurityEnabled,
            PortSecurityMacs = port.AllowedMacAddresses ?? new List<string>(),
            Isolation = port.IsolationEnabled,
            PoeEnabled = port.PoeEnabled,
            PoePower = port.PoePower,
            PoeMode = port.PoeMode,
            ConnectedDeviceType = port.ConnectedDeviceType
        };
    }

    private static string GetCategory(string issueType) => issueType switch
    {
        // Firewall rule issues
        Audit.IssueTypes.FwAnyAny or
        Audit.IssueTypes.AllowSubvertsDeny or Audit.IssueTypes.AllowExceptionPattern or Audit.IssueTypes.DenyShadowsAllow or
        Audit.IssueTypes.PermissiveRule or Audit.IssueTypes.BroadRule or Audit.IssueTypes.OrphanedRule or
        Audit.IssueTypes.MissingIsolation or Audit.IssueTypes.IsolationBypassed => "Firewall Rules",
        var t when t.StartsWith("FW-") => "Firewall Rules",

        // VLAN security issues (includes device placement - putting devices on correct VLAN)
        "VLAN_VIOLATION" or "INTER_VLAN" or Audit.IssueTypes.RoutingEnabled => "VLAN Security",
        Audit.IssueTypes.MgmtDhcpEnabled => "VLAN Security",
        Audit.IssueTypes.SecurityNetworkNotIsolated or Audit.IssueTypes.MgmtNetworkNotIsolated or Audit.IssueTypes.IotNetworkNotIsolated => "VLAN Security",
        Audit.IssueTypes.SecurityNetworkHasInternet or Audit.IssueTypes.MgmtNetworkHasInternet => "VLAN Security",
        Audit.IssueTypes.MgmtMissingUnifiAccess or Audit.IssueTypes.MgmtMissingAfcAccess or Audit.IssueTypes.MgmtMissingNtpAccess or Audit.IssueTypes.MgmtMissing5gAccess => "Firewall Rules",
        // Device placement (wrong VLAN) - controlled by VLAN Security checkbox
        Audit.IssueTypes.IotVlan or Audit.IssueTypes.WifiIotVlan or "OFFLINE-IOT-VLAN" => "VLAN Security",
        Audit.IssueTypes.CameraVlan or Audit.IssueTypes.WifiCameraVlan or "OFFLINE-CAMERA-VLAN" => "VLAN Security",
        Audit.IssueTypes.InfraNotOnMgmt => "VLAN Security",

        // Port security issues
        Audit.IssueTypes.MacRestriction or Audit.IssueTypes.UnusedPort or Audit.IssueTypes.PortIsolation or "PORT_SECURITY" => "Port Security",

        // DNS security issues
        Audit.IssueTypes.DnsLeakage or Audit.IssueTypes.DnsNoDoh or Audit.IssueTypes.DnsDohAuto or Audit.IssueTypes.DnsNo53Block or
        Audit.IssueTypes.DnsNoDotBlock or Audit.IssueTypes.DnsNoDohBlock or Audit.IssueTypes.DnsIsp or
        Audit.IssueTypes.DnsWanMismatch or Audit.IssueTypes.DnsWanOrder or Audit.IssueTypes.DnsWanNoStatic or Audit.IssueTypes.DnsDeviceMisconfigured => "DNS Security",

        // UPnP security issues
        Audit.IssueTypes.UpnpEnabled or Audit.IssueTypes.UpnpNonHomeNetwork or
        Audit.IssueTypes.UpnpPrivilegedPort or Audit.IssueTypes.UpnpPortsExposed or
        Audit.IssueTypes.StaticPortForward => "UPnP Security",

        _ => "General"
    };

    private static bool ShouldInclude(string category, AuditOptions options) => category switch
    {
        "Firewall Rules" => options.IncludeFirewallRules,
        "VLAN Security" => options.IncludeVlanSecurity,
        "Port Security" => options.IncludePortSecurity,
        "DNS Security" => options.IncludeDnsSecurity,
        _ => true
    };

    private static string GetIssueTitle(string type, string message, Audit.Models.AuditSeverity severity)
    {
        // Extract a short title from the issue type
        // For informational IoT/Camera issues, use "Possibly" wording
        var isInformational = severity == Audit.Models.AuditSeverity.Informational;

        return type switch
        {
            // Firewall rules
            Audit.IssueTypes.FwAnyAny => "Firewall: Any-Any Rule",
            Audit.IssueTypes.PermissiveRule => "Firewall: Overly Permissive Rule",
            Audit.IssueTypes.BroadRule => "Firewall: Broad Rule",
            Audit.IssueTypes.OrphanedRule => "Firewall: Orphaned Rule",
            Audit.IssueTypes.AllowExceptionPattern => "Firewall: Allow Exception Pattern",
            Audit.IssueTypes.AllowSubvertsDeny => "Firewall: Rule Order Issue",
            Audit.IssueTypes.DenyShadowsAllow => "Firewall: Ineffective Allow Rule",
            Audit.IssueTypes.MissingIsolation => "Firewall: Missing VLAN Isolation",
            Audit.IssueTypes.IsolationBypassed => "Firewall: VLAN Isolation Bypassed",
            "VLAN_VIOLATION" => "VLAN Policy Violation",
            "INTER_VLAN" => "Inter-VLAN Access Issue",

            // Management network firewall access
            Audit.IssueTypes.MgmtMissingUnifiAccess => "Firewall: Missing UniFi Cloud Access",
            Audit.IssueTypes.MgmtMissingAfcAccess => "Firewall: Missing AFC Access",
            Audit.IssueTypes.MgmtMissingNtpAccess => "Firewall: Missing NTP Access",
            Audit.IssueTypes.MgmtMissing5gAccess => "Firewall: Missing 5G/LTE Access",

            // VLAN security
            Audit.IssueTypes.RoutingEnabled => "Routing on Isolated VLAN",
            Audit.IssueTypes.MgmtDhcpEnabled => "Management VLAN Has DHCP Enabled",
            Audit.IssueTypes.SecurityNetworkNotIsolated => "Security Network Not Isolated",
            Audit.IssueTypes.MgmtNetworkNotIsolated => "Management Network Not Isolated",
            Audit.IssueTypes.IotNetworkNotIsolated => "IoT Network Not Isolated",
            Audit.IssueTypes.SecurityNetworkHasInternet => "Security Network Has Internet",
            Audit.IssueTypes.MgmtNetworkHasInternet => "Management Network Has Internet",
            Audit.IssueTypes.IotVlan or Audit.IssueTypes.WifiIotVlan or "OFFLINE-IOT-VLAN" or "OFFLINE-PRINTER-VLAN" =>
                message.StartsWith("Printer") || message.StartsWith("Scanner")
                    ? (message.Contains("allowed per Settings")
                        ? "Printer Allowed on VLAN"
                        : (isInformational ? "Printer Possibly on Wrong VLAN" : "Printer on Wrong VLAN"))
                    : message.StartsWith("Cloud Camera")
                        ? (isInformational ? "Camera Possibly on Wrong VLAN" : "Camera on Wrong VLAN")
                        : message.Contains("allowed per Settings")
                            ? "IoT Device Allowed on VLAN"
                            : (isInformational ? "IoT Device Possibly on Wrong VLAN" : "IoT Device on Wrong VLAN"),
            Audit.IssueTypes.CameraVlan or Audit.IssueTypes.WifiCameraVlan or "OFFLINE-CAMERA-VLAN" or "OFFLINE-CLOUD-CAMERA-VLAN" =>
                isInformational ? "Camera Possibly on Wrong VLAN" : "Camera on Wrong VLAN",
            Audit.IssueTypes.InfraNotOnMgmt => "Infrastructure Device on Wrong VLAN",

            // Port security
            Audit.IssueTypes.MacRestriction => "Missing MAC Restriction",
            Audit.IssueTypes.UnusedPort => "Unused Port Enabled",
            Audit.IssueTypes.PortIsolation => "Missing Port Isolation",
            "PORT_SECURITY" => "Port Security Issue",

            // VLAN subnet mismatch
            Audit.IssueTypes.VlanSubnetMismatch => "VLAN Subnet Mismatch",
            Audit.IssueTypes.WiredSubnetMismatch => "Wired Subnet Mismatch",

            // DNS security
            Audit.IssueTypes.DnsLeakage => "DNS: Leak Detected",
            Audit.IssueTypes.DnsNoDoh => "DNS: DoH Not Configured",
            Audit.IssueTypes.DnsDohAuto => "DNS: DoH Set to Auto Mode",
            Audit.IssueTypes.DnsNo53Block => "DNS: No Leak Prevention",
            Audit.IssueTypes.DnsNoDotBlock => "DNS: DoT Not Blocked",
            Audit.IssueTypes.DnsNoDohBlock => "DNS: DoH Bypass Not Blocked",
            Audit.IssueTypes.DnsNoDoqBlock => "DNS: DoQ Not Blocked",
            Audit.IssueTypes.DnsIsp => "DNS: Using ISP Servers",
            Audit.IssueTypes.DnsWanMismatch => "DNS: WAN Mismatch",
            Audit.IssueTypes.DnsWanOrder => "DNS: WAN Wrong Order",
            Audit.IssueTypes.DnsWanNoStatic => "DNS: WAN Not Configured",
            Audit.IssueTypes.DnsDeviceMisconfigured => "DNS: Device Misconfigured",
            Audit.IssueTypes.DnsThirdPartyDetected => "DNS: Third-Party Detected",
            Audit.IssueTypes.DnsInconsistentConfig => "DNS: Inconsistent Configuration",
            Audit.IssueTypes.DnsUnknownConfig => "DNS: Unknown Configuration",
            Audit.IssueTypes.DnsDnatPartialCoverage => "DNS: Partial DNAT Coverage",
            Audit.IssueTypes.DnsDnatSingleIp => "DNS: Single IP DNAT",
            Audit.IssueTypes.DnsDnatWrongDestination => "DNS: Invalid DNAT Target",

            // UPnP security
            Audit.IssueTypes.UpnpEnabled => "UPnP: Enabled",
            Audit.IssueTypes.UpnpNonHomeNetwork => "UPnP: Non-Home Network",
            Audit.IssueTypes.UpnpPrivilegedPort => "UPnP: Privileged Port Exposed",
            Audit.IssueTypes.UpnpPortsExposed => "UPnP: Ports Exposed",
            Audit.IssueTypes.StaticPortForward => "Port Forwards: Static Rules",
            Audit.IssueTypes.StaticPrivilegedPort => "Port Forwards: Privileged Ports",

            _ => message.Split('.').FirstOrDefault() ?? type
        };
    }

    /// <summary>
    /// Calculate security score based only on issues from enabled features.
    /// This ensures excluded features don't affect the score.
    /// Severity is already set correctly by the audit engine based on device allowance settings.
    /// </summary>
    private int CalculateFilteredScore(AuditModels.AuditResult engineResult, AuditOptions options)
    {
        // Filter issues based on enabled options
        var filteredIssues = engineResult.Issues
            .Where(issue => ShouldInclude(GetCategory(issue.Type), options))
            .ToList();

        // Calculate deductions from filtered issues only
        var criticalDeduction = Math.Min(
            filteredIssues.Where(i => i.Severity == AuditModels.AuditSeverity.Critical).Sum(i => i.ScoreImpact),
            Audit.Scoring.ScoreConstants.MaxCriticalDeduction);

        var recommendedDeduction = Math.Min(
            filteredIssues.Where(i => i.Severity == AuditModels.AuditSeverity.Recommended).Sum(i => i.ScoreImpact),
            Audit.Scoring.ScoreConstants.MaxRecommendedDeduction);

        var informationalDeduction = Math.Min(
            filteredIssues.Where(i => i.Severity == AuditModels.AuditSeverity.Informational).Sum(i => i.ScoreImpact),
            Audit.Scoring.ScoreConstants.MaxInformationalDeduction);

        // Calculate hardening bonus (same as original - not filtered)
        var hardeningBonus = 0;
        if (engineResult.Statistics.HardeningPercentage >= Audit.Scoring.ScoreConstants.ExcellentHardeningPercentage)
            hardeningBonus = Audit.Scoring.ScoreConstants.MaxHardeningPercentageBonus;
        else if (engineResult.Statistics.HardeningPercentage >= Audit.Scoring.ScoreConstants.GoodHardeningPercentage)
            hardeningBonus = 3;
        else if (engineResult.Statistics.HardeningPercentage >= Audit.Scoring.ScoreConstants.FairHardeningPercentage)
            hardeningBonus = 2;

        if (engineResult.HardeningMeasures.Count >= Audit.Scoring.ScoreConstants.ManyHardeningMeasures)
            hardeningBonus += Audit.Scoring.ScoreConstants.MaxHardeningMeasureBonus;
        else if (engineResult.HardeningMeasures.Count >= Audit.Scoring.ScoreConstants.SomeHardeningMeasures)
            hardeningBonus += 2;
        else if (engineResult.HardeningMeasures.Count >= 1)
            hardeningBonus += 1;

        var score = Audit.Scoring.ScoreConstants.BaseScore - criticalDeduction - recommendedDeduction - informationalDeduction + hardeningBonus;

        _logger.LogInformation(
            "Filtered Security Score: {Score}/100 (Critical: -{Critical}, Recommended: -{Recommended}, Informational: -{Informational}, Bonus: +{Bonus})",
            score, criticalDeduction, recommendedDeduction, informationalDeduction, hardeningBonus);

        return Math.Max(0, Math.Min(100, score));
    }

    private static string GetScoreLabelForScore(int score) => Audit.Analyzers.AuditScorer.GetScoreLabel(score);

    private static List<int>? ParseVlanIds(string? commaSeparated)
    {
        if (string.IsNullOrWhiteSpace(commaSeparated))
            return null;

        var ids = new List<int>();
        foreach (var part in commaSeparated.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(part, out var vlanId) && vlanId > 0 && vlanId <= 4094)
                ids.Add(vlanId);
        }
        return ids.Count > 0 ? ids : null;
    }

    private static string GetDefaultRecommendation(string type) => type switch
    {
        Audit.IssueTypes.FwAnyAny => "Replace with specific allow rules for required traffic",
        Audit.IssueTypes.PermissiveRule => "Tighten the rule to only allow necessary traffic",
        Audit.IssueTypes.OrphanedRule => "Remove rules that reference non-existent objects",
        Audit.IssueTypes.MacRestriction => "Consider enabling MAC-based port security on access ports where device churn is low",
        Audit.IssueTypes.UnusedPort => "Disable unused ports to reduce attack surface",
        Audit.IssueTypes.PortIsolation => "Enable port isolation for security devices",
        Audit.IssueTypes.VlanSubnetMismatch => "Reconnect device to obtain new DHCP lease, or update fixed IP assignment to match VLAN subnet",
        Audit.IssueTypes.WiredSubnetMismatch => "Reconnect device to obtain new DHCP lease, or update fixed IP assignment to match port's VLAN subnet",
        Audit.IssueTypes.IotVlan or Audit.IssueTypes.WifiIotVlan => "Move IoT devices to a dedicated IoT VLAN",
        Audit.IssueTypes.CameraVlan or Audit.IssueTypes.WifiCameraVlan => "Move cameras to a dedicated Security VLAN",
        Audit.IssueTypes.InfraNotOnMgmt => "Move network infrastructure to a dedicated Management VLAN",
        Audit.IssueTypes.DnsLeakage => "Configure firewall to block direct DNS queries from isolated networks",
        Audit.IssueTypes.DnsNoDoh => "Configure DoH in Network Settings with a trusted provider like NextDNS or Cloudflare",
        Audit.IssueTypes.DnsDohAuto => "Set DoH to 'custom' mode with explicit servers for guaranteed encryption",
        Audit.IssueTypes.DnsNo53Block => "Create firewall rule to block outbound UDP/TCP port 53 to Internet for all VLANs",
        Audit.IssueTypes.DnsNoDotBlock => "Create firewall rule to block outbound TCP port 853 to Internet",
        Audit.IssueTypes.DnsNoDohBlock => "Create firewall rule to block HTTPS to known DoH provider domains",
        Audit.IssueTypes.DnsIsp => "Configure custom DNS servers or enable DoH with a privacy-focused provider",
        Audit.IssueTypes.DnsWanMismatch => "Set WAN DNS servers to match your DoH provider",
        Audit.IssueTypes.DnsWanNoStatic => "Configure static DNS on the WAN interface to use your DoH provider's servers",
        Audit.IssueTypes.DnsDeviceMisconfigured => "Configure device DNS to point to the gateway",
        Audit.IssueTypes.DnsDnatPartialCoverage => "Add DNAT rules for remaining networks or block DNS port 53 at firewall",
        Audit.IssueTypes.DnsDnatSingleIp => "Configure DNAT rules to use network references or CIDR ranges for complete coverage",
        Audit.IssueTypes.UpnpEnabled => "UPnP is acceptable on Home networks for gaming and media",
        Audit.IssueTypes.UpnpNonHomeNetwork => "Disable UPnP or ensure it's only enabled for Home/Gaming networks",
        Audit.IssueTypes.UpnpPrivilegedPort => "Review UPnP mappings - privileged ports should not be exposed via UPnP",
        Audit.IssueTypes.UpnpPortsExposed => "Review UPnP mappings periodically in the UPnP Inspector",
        Audit.IssueTypes.StaticPortForward => "Review static port forwards periodically to ensure they are still needed",
        Audit.IssueTypes.StaticPrivilegedPort => "Ensure these privileged ports are intentionally exposed and properly secured",
        _ => "Review the configuration and apply security best practices"
    };
}

public class AuditOptions
{
    public bool IncludeFirewallRules { get; set; } = true;
    public bool IncludeVlanSecurity { get; set; } = true;
    public bool IncludePortSecurity { get; set; } = true;
    public bool IncludeDnsSecurity { get; set; } = true;
    public bool AllowAppleStreamingOnMainNetwork { get; set; } = false;
    public bool AllowAllStreamingOnMainNetwork { get; set; } = false;
    public bool AllowNameBrandTVsOnMainNetwork { get; set; } = false;
    public bool AllowAllTVsOnMainNetwork { get; set; } = false;
    public bool AllowPrintersOnMainNetwork { get; set; } = true;
    public List<int>? DnatExcludedVlanIds { get; set; }
    public int? PiholeManagementPort { get; set; }

    // Unused port detection thresholds
    public int UnusedPortInactivityDays { get; set; } = 15;
    public int NamedPortInactivityDays { get; set; } = 45;
}

public class AuditResult
{
    public int Score { get; set; }
    public string ScoreLabel { get; set; } = "";
    public string ScoreClass { get; set; } = "";
    public int CriticalCount { get; set; }
    public int WarningCount { get; set; }
    public int InfoCount { get; set; }
    public List<AuditIssue> Issues { get; set; } = new();
    public DateTime CompletedAt { get; set; }
    public AuditStatistics? Statistics { get; set; }
    public List<string> HardeningMeasures { get; set; } = new();
    public List<NetworkReference> Networks { get; set; } = new();
    public List<SwitchReference> Switches { get; set; } = new();
    public List<WirelessClientReference> WirelessClients { get; set; } = new();
    public List<OfflineClientReference> OfflineClients { get; set; } = new();
    public DnsSecurityReference? DnsSecurity { get; set; }
}

public class DnsSecurityReference
{
    public bool DohEnabled { get; set; }
    public string DohState { get; set; } = "disabled";
    public List<string> DohProviders { get; set; } = new();
    public List<string> DohConfigNames { get; set; } = new();
    public bool DnsLeakProtection { get; set; }
    public bool DotBlocked { get; set; }
    public bool DoqBlocked { get; set; }
    public bool DohBypassBlocked { get; set; }
    public bool Doh3Blocked { get; set; }
    public bool FullyProtected { get; set; }
    public List<string> WanDnsServers { get; set; } = new();
    public List<string?> WanDnsPtrResults { get; set; } = new();
    public bool WanDnsMatchesDoH { get; set; }
    public bool WanDnsOrderCorrect { get; set; } = true;
    public string? WanDnsProvider { get; set; }
    public string? ExpectedDnsProvider { get; set; }
    public List<string> InterfacesWithoutDns { get; set; } = new();
    public List<string> InterfacesWithMismatch { get; set; } = new();
    public List<string> MismatchedDnsServers { get; set; } = new();
    public List<string> MatchedDnsServers { get; set; } = new();
    public bool DeviceDnsPointsToGateway { get; set; } = true;
    public int TotalDevicesChecked { get; set; }
    public int DevicesWithCorrectDns { get; set; }
    public int DhcpDeviceCount { get; set; }
    public bool HasThirdPartyDns { get; set; }
    public bool IsPiholeDetected { get; set; }
    public string? ThirdPartyDnsProviderName { get; set; }
    public List<ThirdPartyDnsNetworkReference> ThirdPartyNetworks { get; set; } = new();

    // DNAT DNS Coverage
    public bool HasDnatDnsRules { get; set; }
    public bool DnatProvidesFullCoverage { get; set; }
    public string? DnatRedirectTarget { get; set; }
    public List<string> DnatCoveredNetworks { get; set; } = new();
    public List<string> DnatUncoveredNetworks { get; set; } = new();
}

public class ThirdPartyDnsNetworkReference
{
    public required string NetworkName { get; init; }
    public int VlanId { get; init; }
    public required string DnsServerIp { get; init; }
    public string? DnsProviderName { get; init; }
}

public class AuditStatistics
{
    public int TotalPorts { get; set; }
    public int ActivePorts { get; set; }
    public int DisabledPorts { get; set; }
    public int MacRestrictedPorts { get; set; }
    public int NetworkCount { get; set; }
    public int SwitchCount { get; set; }
}

public class AuditIssue
{
    public AuditModels.AuditSeverity Severity { get; set; }
    public string Category { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Recommendation { get; set; } = "";
    public string? DeviceName { get; set; }
    public string? DeviceMac { get; set; }
    public string? Port { get; set; }
    public string? PortName { get; set; }
    public string? CurrentNetwork { get; set; }
    public int? CurrentVlan { get; set; }
    public string? RecommendedNetwork { get; set; }
    public int? RecommendedVlan { get; set; }
    public bool IsWireless { get; set; }
    public string? ClientName { get; set; }
    public string? ClientMac { get; set; }
    public string? AccessPoint { get; set; }
    public string? WifiBand { get; set; }
    /// <summary>
    /// Settings key for configurable device allowances (e.g., "printers", "streaming-devices")
    /// If set, UI shows a link to configure this setting
    /// </summary>
    public string? ConfigurableSetting { get; set; }
}

public class AuditSummary
{
    public int Score { get; set; }
    public int CriticalCount { get; set; }
    public int WarningCount { get; set; }
    public DateTime? LastAuditTime { get; set; }
    public List<AuditIssue> RecentIssues { get; set; } = new();
}

public class NetworkReference
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int VlanId { get; set; }
    public string? Subnet { get; set; }
    public string Purpose { get; set; } = "corporate";
}

public class SwitchReference
{
    public string Name { get; set; } = "";
    public string? Mac { get; set; }
    public string? Model { get; set; }
    public string? ModelName { get; set; }
    public string? DeviceType { get; set; }
    public bool IsGateway { get; set; }
    public bool IsAccessPoint { get; set; }
    public int MaxCustomMacAcls { get; set; }
    public List<PortReference> Ports { get; set; } = new();
}

public class PortReference
{
    public int PortIndex { get; set; }
    public string Name { get; set; } = "";
    public bool IsUp { get; set; }
    public int Speed { get; set; }
    public string Forward { get; set; } = "all";
    public bool IsUplink { get; set; }
    public bool IsWan { get; set; }
    public string? NativeNetwork { get; set; }
    public int? NativeVlan { get; set; }
    public List<string> ExcludedNetworks { get; set; } = new();
    public bool PortSecurityEnabled { get; set; }
    public List<string> PortSecurityMacs { get; set; } = new();
    public bool Isolation { get; set; }
    public bool PoeEnabled { get; set; }
    public double PoePower { get; set; }
    public string? PoeMode { get; set; }
    /// <summary>
    /// Type of UniFi device connected to this port (e.g., "uap", "usw"). Null for regular clients.
    /// </summary>
    public string? ConnectedDeviceType { get; set; }
}

public class WirelessClientReference
{
    public string DisplayName { get; set; } = "";
    public string Mac { get; set; } = "";
    public string? AccessPointName { get; set; }
    public string? AccessPointMac { get; set; }
    public string? AccessPointModel { get; set; }
    public string? AccessPointModelName { get; set; }
    public string? NetworkName { get; set; }
    public int? VlanId { get; set; }
    public string DeviceCategory { get; set; } = "";
    public string? VendorName { get; set; }
    public int DetectionConfidence { get; set; }
    public bool IsIoT { get; set; }
    public bool IsCamera { get; set; }
}

public class OfflineClientReference
{
    public string DisplayName { get; set; } = "";
    public string? Mac { get; set; }
    public string? LastUplinkName { get; set; }
    public string? LastUplinkModelName { get; set; }
    public NetworkOptimizer.Audit.Models.NetworkInfo? LastNetwork { get; set; }
    public string DeviceCategory { get; set; } = "";
    public string LastSeenDisplay { get; set; } = "";
    public bool IsRecentlyActive { get; set; }
    public bool IsIoT { get; set; }
    public bool IsCamera { get; set; }
    public NetworkOptimizer.Audit.Models.DeviceDetectionResult Detection { get; set; } = null!;
}
