using System.Text.Json;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Audit;
using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage.Interfaces;
using NetworkOptimizer.Storage.Models;
using AuditModels = NetworkOptimizer.Audit.Models;
using StorageAuditResult = NetworkOptimizer.Storage.Models.AuditResult;

namespace NetworkOptimizer.Web.Services;

public class AuditService
{
    private readonly ILogger<AuditService> _logger;
    private readonly UniFiConnectionService _connectionService;
    private readonly ConfigAuditEngine _auditEngine;
    private readonly IServiceProvider _serviceProvider;
    private readonly FingerprintDatabaseService _fingerprintService;

    // Cache the last audit result
    private AuditResult? _lastAuditResult;
    private DateTime? _lastAuditTime;

    // Track dismissed issues (by unique key: Title + DeviceName + Port)
    private readonly HashSet<string> _dismissedIssues = new();
    private bool _dismissedIssuesLoaded = false;

    public AuditService(
        ILogger<AuditService> logger,
        UniFiConnectionService connectionService,
        ConfigAuditEngine auditEngine,
        IServiceProvider serviceProvider,
        FingerprintDatabaseService fingerprintService)
    {
        _logger = logger;
        _connectionService = connectionService;
        _auditEngine = auditEngine;
        _serviceProvider = serviceProvider;
        _fingerprintService = fingerprintService;
    }

    /// <summary>
    /// Ensure dismissed issues are loaded from database
    /// </summary>
    private async Task EnsureDismissedIssuesLoadedAsync()
    {
        if (_dismissedIssuesLoaded) return;

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IAuditRepository>();

            var dismissed = await repository.GetDismissedIssuesAsync();
            foreach (var issue in dismissed)
            {
                _dismissedIssues.Add(issue.IssueKey);
            }
            _dismissedIssuesLoaded = true;
            _logger.LogInformation("Loaded {Count} dismissed issues from database", dismissed.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load dismissed issues from database");
            _dismissedIssuesLoaded = true; // Don't retry on every call
        }
    }

    public AuditResult? LastAuditResult => _lastAuditResult;
    public DateTime? LastAuditTime => _lastAuditTime;

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
        if (_dismissedIssues.Add(key))
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var repository = scope.ServiceProvider.GetRequiredService<IAuditRepository>();

                await repository.SaveDismissedIssueAsync(new DismissedIssue
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
        _dismissedIssues.Contains(GetIssueKey(issue));

    /// <summary>
    /// Get active (non-dismissed) issues (synchronous - may not include dismissed filter if not yet loaded)
    /// Prefer using GetActiveIssuesAsync() for reliable results.
    /// </summary>
    public List<AuditIssue> GetActiveIssues()
    {
        // Return cached data only - don't block on loading dismissed issues
        // If dismissed issues haven't loaded yet, returns all issues
        return _lastAuditResult?.Issues.Where(i => !IsIssueDismissed(i)).ToList() ?? new();
    }

    /// <summary>
    /// Get active (non-dismissed) issues (async version)
    /// </summary>
    public async Task<List<AuditIssue>> GetActiveIssuesAsync()
    {
        await EnsureDismissedIssuesLoadedAsync();
        return _lastAuditResult?.Issues.Where(i => !IsIssueDismissed(i)).ToList() ?? new();
    }

    /// <summary>
    /// Get dismissed issues from the current audit result
    /// </summary>
    public async Task<List<AuditIssue>> GetDismissedIssuesAsync()
    {
        await EnsureDismissedIssuesLoadedAsync();
        return _lastAuditResult?.Issues.Where(i => IsIssueDismissed(i)).ToList() ?? new();
    }

    /// <summary>
    /// Restore a dismissed issue (removes from dismissed list)
    /// </summary>
    public async Task RestoreIssueAsync(AuditIssue issue)
    {
        var key = GetIssueKey(issue);
        if (_dismissedIssues.Remove(key))
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var repository = scope.ServiceProvider.GetRequiredService<IAuditRepository>();

                await repository.DeleteDismissedIssueAsync(key);
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
        GetActiveIssues().Count(i => i.Severity == "Critical");

    /// <summary>
    /// Get count of active warning issues
    /// </summary>
    public int ActiveWarningCount =>
        GetActiveIssues().Count(i => i.Severity == "Warning");

    /// <summary>
    /// Clear all dismissed issues (removes from database too)
    /// </summary>
    public async Task ClearDismissedIssuesAsync()
    {
        _dismissedIssues.Clear();
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IAuditRepository>();

            await repository.ClearAllDismissedIssuesAsync();
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
            using var scope = _serviceProvider.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IAuditRepository>();

            var latestAudit = await repository.GetLatestAuditResultAsync();

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
                    _logger.LogInformation("Restored report data: {Networks} networks, {Switches} switches, {Wireless} wireless clients",
                        result.Networks.Count, result.Switches.Count, result.WirelessClients.Count);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Failed to parse audit report data JSON");
                }
            }

            // Cache it
            _lastAuditResult = result;
            _lastAuditTime = latestAudit.AuditDate;

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
        if (_lastAuditResult != null && _lastAuditTime != null)
        {
            var activeIssues = GetActiveIssues();
            return new AuditSummary
            {
                Score = _lastAuditResult.Score,
                CriticalCount = activeIssues.Count(i => i.Severity == "Critical"),
                WarningCount = activeIssues.Count(i => i.Severity == "Warning"),
                LastAuditTime = _lastAuditTime.Value,
                RecentIssues = activeIssues.Take(5).ToList()
            };
        }

        // Try to load from database
        var dbResult = await LoadLastAuditFromDatabaseAsync();
        if (dbResult != null && _lastAuditTime != null)
        {
            return new AuditSummary
            {
                Score = dbResult.Score,
                CriticalCount = dbResult.CriticalCount,
                WarningCount = dbResult.WarningCount,
                LastAuditTime = _lastAuditTime.Value,
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
            using var scope = _serviceProvider.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IAuditRepository>();

            // Serialize the full report data for PDF generation after page reload
            var reportData = new
            {
                Statistics = result.Statistics,
                HardeningMeasures = result.HardeningMeasures,
                Networks = result.Networks,
                Switches = result.Switches,
                WirelessClients = result.WirelessClients
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

            await repository.SaveAuditResultAsync(storageResult);

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
                        Severity = "Critical",
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
            // Get raw device data from UniFi API
            var deviceDataJson = await _connectionService.Client.GetDevicesRawJsonAsync();

            if (string.IsNullOrEmpty(deviceDataJson))
            {
                throw new Exception("No device data returned from UniFi API");
            }

            // Fetch connected clients for enhanced device detection (fingerprint, MAC OUI)
            var clients = await _connectionService.Client.GetClientsAsync();
            _logger.LogInformation("Fetched {ClientCount} connected clients for device detection", clients?.Count ?? 0);

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

            _logger.LogInformation("Running audit engine on device data ({Length} bytes)", deviceDataJson.Length);

            // Run the audit engine with all available data for comprehensive analysis
            var auditResult = _auditEngine.RunAudit(deviceDataJson, clients, fingerprintDb, settingsData, firewallPoliciesData, "Network Audit");

            // Convert audit result to web models
            var webResult = ConvertAuditResult(auditResult, options);

            // Cache the result
            _lastAuditResult = webResult;
            _lastAuditTime = DateTime.UtcNow;

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
                        Severity = "Critical",
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

            issues.Add(new AuditIssue
            {
                Severity = ConvertSeverity(issue.Severity),
                Category = category,
                Title = GetIssueTitle(issue.Type, issue.Message),
                Description = issue.Message,
                Recommendation = issue.RecommendedAction ?? GetDefaultRecommendation(issue.Type),
                // Context fields
                DeviceName = issue.DeviceName,
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
                AccessPoint = issue.AccessPoint
            });
        }

        var criticalCount = issues.Count(i => i.Severity == "Critical");
        var warningCount = issues.Count(i => i.Severity == "Warning");
        var infoCount = issues.Count(i => i.Severity == "Info");

        var score = engineResult.SecurityScore;
        var scoreLabel = engineResult.Posture.ToString().ToUpperInvariant();

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
                Model = s.Model,
                ModelName = s.ModelName ?? s.Model,
                DeviceType = s.Type,
                IsGateway = s.IsGateway,
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
                NetworkName = wc.Network?.Name,
                VlanId = wc.Network?.VlanId,
                DeviceCategory = wc.Detection.CategoryName,
                VendorName = wc.Detection.VendorName,
                DetectionConfidence = wc.Detection.ConfidenceScore,
                IsIoT = wc.Detection.Category.IsIoT(),
                IsCamera = wc.Detection.Category.IsSurveillance()
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
                DnsLeakProtection = dns.DnsLeakProtection,
                DotBlocked = dns.DotBlocked,
                DohBypassBlocked = dns.DohBypassBlocked,
                FullyProtected = dns.FullyProtected
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
            PoeMode = port.PoeMode
        };
    }

    private static string GetCategory(string issueType) => issueType switch
    {
        "FW_SHADOWED" or "FW_PERMISSIVE" or "FW_ORPHANED" or "FW_ANY_ANY" => "Firewall Rules",
        "VLAN_VIOLATION" or "INTER_VLAN" or "ROUTING_ENABLED" or "MGMT_DHCP_ENABLED" or "MGMT-DHCP-001" => "VLAN Security",
        "MAC_RESTRICTION" or "MAC-RESTRICT-001" or "UNUSED_PORT" or "UNUSED-PORT-001" or "PORT_ISOLATION" or "PORT-ISOLATE-001" or "PORT_SECURITY" => "Port Security",
        "DNS_LEAKAGE" or "DNS_NO_DOH" or "DNS_DOH_AUTO" or "DNS_NO_53_BLOCK" or "DNS_NO_DOT_BLOCK" or "DNS_NO_DOH_BLOCK" or "DNS_ISP" => "DNS Security",
        "IOT_WRONG_VLAN" or "IOT-VLAN-001" or "CAMERA_WRONG_VLAN" or "CAM-VLAN-001" => "Device Placement",
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

    private static string ConvertSeverity(AuditModels.AuditSeverity severity) => severity switch
    {
        AuditModels.AuditSeverity.Critical => "Critical",
        AuditModels.AuditSeverity.Recommended => "Warning",
        AuditModels.AuditSeverity.Investigate => "Info",
        _ => "Info"
    };

    private static string GetIssueTitle(string type, string message)
    {
        // Extract a short title from the issue type
        return type switch
        {
            "FW_SHADOWED" => "Shadowed Firewall Rule",
            "FW_PERMISSIVE" => "Overly Permissive Rule",
            "FW_ORPHANED" => "Orphaned Firewall Rule",
            "FW_ANY_ANY" => "Any-Any Firewall Rule",
            "VLAN_VIOLATION" => "VLAN Policy Violation",
            "INTER_VLAN" => "Inter-VLAN Access Issue",
            "ROUTING_ENABLED" => "Routing on Isolated VLAN",
            "MAC_RESTRICTION" or "MAC-RESTRICT-001" => "Missing MAC Restriction",
            "UNUSED_PORT" or "UNUSED-PORT-001" => "Unused Port Enabled",
            "PORT_ISOLATION" or "PORT-ISOLATE-001" => "Missing Port Isolation",
            "PORT_SECURITY" => "Port Security Issue",
            "DNS_LEAKAGE" => "DNS Leak Detected",
            "DNS_NO_DOH" => "DoH Not Configured",
            "DNS_DOH_AUTO" => "DoH Set to Auto Mode",
            "DNS_NO_53_BLOCK" => "No DNS Leak Prevention",
            "DNS_NO_DOT_BLOCK" => "DNS-over-TLS Not Blocked",
            "DNS_NO_DOH_BLOCK" => "DoH Bypass Not Blocked",
            "DNS_ISP" => "Using ISP DNS Servers",
            "IOT_WRONG_VLAN" or "IOT-VLAN-001" => "IoT Device on Wrong VLAN",
            "CAMERA_WRONG_VLAN" or "CAM-VLAN-001" => "Camera on Wrong VLAN",
            "MGMT_DHCP_ENABLED" or "MGMT-DHCP-001" => "Management VLAN Has DHCP Enabled",
            _ => message.Split('.').FirstOrDefault() ?? type
        };
    }

    private static string GetDefaultRecommendation(string type) => type switch
    {
        "FW_SHADOWED" => "Reorder firewall rules so specific rules appear before broader ones.",
        "FW_PERMISSIVE" => "Tighten the rule to only allow necessary traffic.",
        "FW_ORPHANED" => "Remove rules that reference non-existent objects.",
        "FW_ANY_ANY" => "Replace with specific allow rules for required traffic.",
        "MAC_RESTRICTION" or "MAC-RESTRICT-001" => "Enable MAC-based port security on critical infrastructure ports.",
        "UNUSED_PORT" or "UNUSED-PORT-001" => "Disable unused ports to reduce attack surface.",
        "PORT_ISOLATION" or "PORT-ISOLATE-001" => "Enable port isolation for security devices.",
        "IOT_WRONG_VLAN" or "IOT-VLAN-001" => "Move IoT devices to a dedicated IoT VLAN.",
        "CAMERA_WRONG_VLAN" or "CAM-VLAN-001" => "Move cameras to a dedicated Security VLAN.",
        "DNS_LEAKAGE" => "Configure firewall to block direct DNS queries from isolated networks.",
        "DNS_NO_DOH" => "Configure DoH in Network Settings with a trusted provider like NextDNS or Cloudflare.",
        "DNS_DOH_AUTO" => "Set DoH to 'custom' mode with explicit servers for guaranteed encryption.",
        "DNS_NO_53_BLOCK" => "Create firewall rule to block outbound UDP/TCP port 53 to Internet for all VLANs.",
        "DNS_NO_DOT_BLOCK" => "Create firewall rule to block outbound TCP port 853 to Internet.",
        "DNS_NO_DOH_BLOCK" => "Create firewall rule to block HTTPS to known DoH provider domains.",
        "DNS_ISP" => "Configure custom DNS servers or enable DoH with a privacy-focused provider.",
        _ => "Review the configuration and apply security best practices."
    };
}

public class AuditOptions
{
    public bool IncludeFirewallRules { get; set; } = true;
    public bool IncludeVlanSecurity { get; set; } = true;
    public bool IncludePortSecurity { get; set; } = true;
    public bool IncludeDnsSecurity { get; set; } = true;
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
    public DnsSecurityReference? DnsSecurity { get; set; }
}

public class DnsSecurityReference
{
    public bool DohEnabled { get; set; }
    public string DohState { get; set; } = "disabled";
    public List<string> DohProviders { get; set; } = new();
    public bool DnsLeakProtection { get; set; }
    public bool DotBlocked { get; set; }
    public bool DohBypassBlocked { get; set; }
    public bool FullyProtected { get; set; }
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
    public string Severity { get; set; } = "";
    public string Category { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Recommendation { get; set; } = "";

    // Context fields
    public string? DeviceName { get; set; }
    public string? Port { get; set; }
    public string? PortName { get; set; }
    public string? CurrentNetwork { get; set; }
    public int? CurrentVlan { get; set; }
    public string? RecommendedNetwork { get; set; }
    public int? RecommendedVlan { get; set; }

    // Wireless-specific fields
    public bool IsWireless { get; set; }
    public string? ClientName { get; set; }
    public string? ClientMac { get; set; }
    public string? AccessPoint { get; set; }
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
    public string? Model { get; set; }
    public string? ModelName { get; set; }
    public string? DeviceType { get; set; }
    public bool IsGateway { get; set; }
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
}

public class WirelessClientReference
{
    public string DisplayName { get; set; } = "";
    public string Mac { get; set; } = "";
    public string? AccessPointName { get; set; }
    public string? AccessPointMac { get; set; }
    public string? NetworkName { get; set; }
    public int? VlanId { get; set; }
    public string DeviceCategory { get; set; } = "";
    public string? VendorName { get; set; }
    public int DetectionConfidence { get; set; }
    public bool IsIoT { get; set; }
    public bool IsCamera { get; set; }
}
