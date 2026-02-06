using System.Text.Json;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Audit.Analyzers;
using NetworkOptimizer.Audit.Dns;
using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.Audit.Services;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Core.Helpers;
using NetworkOptimizer.Core.Models;
using NetworkOptimizer.UniFi.Models;
using static NetworkOptimizer.Core.Helpers.DisplayFormatters;
// Disambiguate types that exist in both Audit.Models and Core.Models
using AuditResult = NetworkOptimizer.Audit.Models.AuditResult;
using AuditStatistics = NetworkOptimizer.Audit.Models.AuditStatistics;
using FirewallRule = NetworkOptimizer.Audit.Models.FirewallRule;

namespace NetworkOptimizer.Audit;

/// <summary>
/// Main orchestrator for comprehensive UniFi network configuration audits
/// Coordinates all analyzers and generates complete audit results
/// </summary>
public class ConfigAuditEngine
{
    private readonly ILogger<ConfigAuditEngine> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IeeeOuiDatabase? _ieeeOuiDb;
    private readonly VlanAnalyzer _vlanAnalyzer;
    private readonly PortSecurityAnalyzer _securityEngine;
    private readonly FirewallRuleAnalyzer _firewallAnalyzer;
    private readonly DnsSecurityAnalyzer _dnsAnalyzer;
    private readonly UpnpSecurityAnalyzer _upnpAnalyzer;
    private readonly AuditScorer _scorer;

    /// <summary>
    /// Internal context passed between audit phases
    /// </summary>
    private sealed class AuditContext
    {
        public required JsonElement DeviceData { get; init; }
        public required List<UniFiClientResponse>? Clients { get; init; }
        public required List<UniFiClientDetailResponse>? ClientHistory { get; init; }
        public required JsonElement? SettingsData { get; init; }
        public required List<FirewallRule>? FirewallRules { get; init; }
        public required List<UniFiFirewallGroup>? FirewallGroups { get; init; }
        public required JsonElement? NatRulesData { get; init; }
        public required string? ClientName { get; init; }
        public required PortSecurityAnalyzer SecurityEngine { get; init; }
        public required DeviceAllowanceSettings AllowanceSettings { get; init; }
        public required List<UniFiPortProfile>? PortProfiles { get; init; }
        public List<int>? DnatExcludedVlanIds { get; init; }
        public int? PiholeManagementPort { get; init; }  // Used for all third-party DNS (Pi-hole, AdGuard Home, etc.)
        public bool? UpnpEnabled { get; init; }
        public List<UniFiPortForwardRule>? PortForwardRules { get; init; }
        public List<UniFiNetworkConfig>? NetworkConfigs { get; init; }

        // Populated by phases
        public List<NetworkInfo> Networks { get; set; } = [];
        public List<SwitchInfo> Switches { get; set; } = [];
        public List<WirelessClientInfo> WirelessClients { get; set; } = [];
        public List<OfflineClientInfo> OfflineClients { get; set; } = [];
        public Dictionary<string, string?> ApNameToModel { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public List<AuditIssue> AllIssues { get; } = [];
        public List<string> HardeningMeasures { get; set; } = [];
        public DnsSecurityResult? DnsSecurityResult { get; set; }
        public AuditStatistics? Statistics { get; set; }

        /// <summary>
        /// The firewall zone ID for external/WAN traffic.
        /// Determined early from NetworkConfigs by finding a WAN network's firewall_zone_id.
        /// Used by firewall rule analysis to identify rules targeting internet traffic.
        /// </summary>
        public string? ExternalZoneId { get; set; }

        /// <summary>
        /// Firewall zones from /firewall/zone API.
        /// Used to validate zone assumptions and identify DMZ/Hotspot networks.
        /// </summary>
        public List<UniFiFirewallZone>? FirewallZones { get; init; }

        /// <summary>
        /// Lookup service for firewall zones.
        /// Provides zone ID to zone key mapping and validation.
        /// </summary>
        public FirewallZoneLookup? ZoneLookup { get; set; }
    }

    /// <summary>
    /// Create ConfigAuditEngine with dependency injection.
    /// Internal analyzers are composed here rather than injected individually -
    /// they're implementation details, not swappable services.
    /// </summary>
    public ConfigAuditEngine(
        ILogger<ConfigAuditEngine> logger,
        ILoggerFactory loggerFactory,
        IeeeOuiDatabase? ieeeOuiDb = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _ieeeOuiDb = ieeeOuiDb;

        _vlanAnalyzer = new VlanAnalyzer(loggerFactory.CreateLogger<VlanAnalyzer>());

        // Create detection service with logging for enhanced device type detection
        var detectionService = new DeviceTypeDetectionService(
            loggerFactory.CreateLogger<DeviceTypeDetectionService>(),
            fingerprintDb: null,
            ieeeOuiDb: ieeeOuiDb,
            loggerFactory: loggerFactory);

        _securityEngine = new PortSecurityAnalyzer(
            loggerFactory.CreateLogger<PortSecurityAnalyzer>(),
            detectionService);
        var firewallParser = new FirewallRuleParser(loggerFactory.CreateLogger<FirewallRuleParser>());
        _firewallAnalyzer = new FirewallRuleAnalyzer(loggerFactory.CreateLogger<FirewallRuleAnalyzer>(), firewallParser);

        // HttpClient here is fine - audits run infrequently (manual/daily), not per-request
        var thirdPartyDetector = new ThirdPartyDnsDetector(
            loggerFactory.CreateLogger<ThirdPartyDnsDetector>(),
            new HttpClient { Timeout = TimeSpan.FromSeconds(3) });
        _dnsAnalyzer = new DnsSecurityAnalyzer(loggerFactory.CreateLogger<DnsSecurityAnalyzer>(), thirdPartyDetector);
        _upnpAnalyzer = new UpnpSecurityAnalyzer(loggerFactory.CreateLogger<UpnpSecurityAnalyzer>());
        _scorer = new AuditScorer(loggerFactory.CreateLogger<AuditScorer>());
    }

    /// <summary>
    /// Run a comprehensive audit on UniFi device data
    /// </summary>
    /// <param name="deviceDataJson">JSON string containing UniFi device data from /stat/device API</param>
    /// <param name="clientName">Optional client/site name for the report</param>
    /// <returns>Complete audit results</returns>
    public Task<AuditResult> RunAuditAsync(string deviceDataJson, string? clientName = null)
        => RunAuditAsync(deviceDataJson, clients: null, fingerprintDb: null, settingsData: null, firewallRules: null, allowanceSettings: null, clientName);

    /// <summary>
    /// Run a comprehensive audit on UniFi device data with client data for enhanced detection
    /// </summary>
    /// <param name="deviceDataJson">JSON string containing UniFi device data from /stat/device API</param>
    /// <param name="clients">Connected clients for device type detection (optional)</param>
    /// <param name="clientName">Optional client/site name for the report</param>
    /// <returns>Complete audit results</returns>
    public Task<AuditResult> RunAuditAsync(string deviceDataJson, List<UniFiClientResponse>? clients, string? clientName = null)
        => RunAuditAsync(deviceDataJson, clients, fingerprintDb: null, settingsData: null, firewallRules: null, allowanceSettings: null, clientName);

    /// <summary>
    /// Run a comprehensive audit on UniFi device data with client data and fingerprint database for enhanced detection
    /// </summary>
    /// <param name="deviceDataJson">JSON string containing UniFi device data from /stat/device API</param>
    /// <param name="clients">Connected clients for device type detection (optional)</param>
    /// <param name="fingerprintDb">UniFi fingerprint database for device name lookups (optional)</param>
    /// <param name="clientName">Optional client/site name for the report</param>
    /// <returns>Complete audit results</returns>
    public Task<AuditResult> RunAuditAsync(string deviceDataJson, List<UniFiClientResponse>? clients, UniFiFingerprintDatabase? fingerprintDb, string? clientName = null)
        => RunAuditAsync(deviceDataJson, clients, fingerprintDb, settingsData: null, firewallRules: null, allowanceSettings: null, clientName);

    /// <summary>
    /// Run a comprehensive audit on UniFi device data with all available data sources
    /// </summary>
    /// <param name="deviceDataJson">JSON string containing UniFi device data from /stat/device API</param>
    /// <param name="clients">Connected clients for device type detection (optional)</param>
    /// <param name="fingerprintDb">UniFi fingerprint database for device name lookups (optional)</param>
    /// <param name="settingsData">Site settings data including DoH configuration (optional)</param>
    /// <param name="firewallRules">Parsed firewall rules for DNS leak prevention analysis (optional)</param>
    /// <param name="clientName">Optional client/site name for the report</param>
    /// <returns>Complete audit results</returns>
    public Task<AuditResult> RunAuditAsync(
        string deviceDataJson,
        List<UniFiClientResponse>? clients,
        UniFiFingerprintDatabase? fingerprintDb,
        JsonElement? settingsData,
        List<FirewallRule>? firewallRules,
        string? clientName = null)
        => RunAuditAsync(deviceDataJson, clients, fingerprintDb, settingsData, firewallRules, allowanceSettings: null, clientName);

    /// <summary>
    /// Run a comprehensive audit on UniFi device data with all available data sources and device allowance settings
    /// </summary>
    public Task<AuditResult> RunAuditAsync(
        string deviceDataJson,
        List<UniFiClientResponse>? clients,
        UniFiFingerprintDatabase? fingerprintDb,
        JsonElement? settingsData,
        List<FirewallRule>? firewallRules,
        DeviceAllowanceSettings? allowanceSettings,
        string? clientName = null)
        => RunAuditAsync(deviceDataJson, clients, clientHistory: null, fingerprintDb, settingsData, firewallRules, allowanceSettings, protectCameras: null, clientName);

    /// <summary>
    /// Run a comprehensive audit on UniFi device data with client history for offline device detection
    /// </summary>
    /// <param name="deviceDataJson">JSON string containing UniFi device data from /stat/device API</param>
    /// <param name="clients">Connected clients for device type detection (optional)</param>
    /// <param name="clientHistory">Historical clients for offline device detection (optional)</param>
    /// <param name="fingerprintDb">UniFi fingerprint database for device name lookups (optional)</param>
    /// <param name="settingsData">Site settings data including DoH configuration (optional)</param>
    /// <param name="firewallRules">Parsed firewall rules for DNS leak prevention analysis (optional)</param>
    /// <param name="allowanceSettings">Settings for allowing devices on main network (optional)</param>
    /// <param name="protectCameras">UniFi Protect cameras for 100% confidence detection (optional)</param>
    /// <param name="clientName">Optional client/site name for the report</param>
    /// <returns>Complete audit results</returns>
    public Task<AuditResult> RunAuditAsync(
        string deviceDataJson,
        List<UniFiClientResponse>? clients,
        List<UniFiClientDetailResponse>? clientHistory,
        UniFiFingerprintDatabase? fingerprintDb,
        JsonElement? settingsData,
        List<FirewallRule>? firewallRules,
        DeviceAllowanceSettings? allowanceSettings,
        ProtectCameraCollection? protectCameras,
        string? clientName = null)
    {
        return RunAuditAsync(new AuditRequest
        {
            DeviceDataJson = deviceDataJson,
            Clients = clients,
            ClientHistory = clientHistory,
            FingerprintDb = fingerprintDb,
            SettingsData = settingsData,
            FirewallRules = firewallRules,
            AllowanceSettings = allowanceSettings,
            ProtectCameras = protectCameras,
            ClientName = clientName
        });
    }

    /// <summary>
    /// Run a comprehensive security audit using the provided request parameters.
    /// </summary>
    /// <param name="request">Audit request containing all parameters</param>
    /// <returns>Complete audit results</returns>
    public async Task<AuditResult> RunAuditAsync(AuditRequest request)
    {
        _logger.LogInformation("Starting network configuration audit for {Client}", request.ClientName ?? "Unknown");

        // Initialize context with parsed data and security engine
        var ctx = InitializeAuditContext(request);

        // Check if external zone could be determined from network configs
        // This should only trigger if no WAN networks exist at all (very unusual)
        if (ctx.ExternalZoneId == null && ctx.NetworkConfigs != null && ctx.NetworkConfigs.Count > 0)
        {
            var wanNetworkCount = ctx.NetworkConfigs.Count(n =>
                string.Equals(n.Purpose, "wan", StringComparison.OrdinalIgnoreCase));

            // Only warn if we have network configs but couldn't find any WAN networks
            // (With our fix, WAN networks should always yield a zone ID - either real or synthetic)
            _logger.LogWarning("Could not determine External Zone ID from {Count} network configs ({WanCount} WAN networks). " +
                "This may indicate an unusual network configuration without any WAN interfaces.",
                ctx.NetworkConfigs.Count, wanNetworkCount);

            ctx.AllIssues.Add(new AuditIssue
            {
                Type = IssueTypes.ExternalZoneNotDetected,
                Severity = Models.AuditSeverity.Critical,
                Message = "Unable to determine External/WAN firewall zone ID. No WAN networks detected in network configuration.",
                Metadata = new Dictionary<string, object>
                {
                    { "network_config_count", ctx.NetworkConfigs.Count },
                    { "wan_network_count", wanNetworkCount }
                },
                RuleId = "FW-ZONE-001",
                ScoreImpact = 5,
                RecommendedAction = "This network appears to have no WAN interfaces configured. " +
                    "If this is unexpected, please report this issue at https://github.com/Ozark-Connect/NetworkOptimizer/issues with your UniFi controller version."
            });
        }

        // Execute audit phases
        ExecutePhase1_ExtractNetworks(ctx);
        ExecutePhase2_ExtractSwitches(ctx);
        ExecutePhase3_AnalyzePortSecurity(ctx);
        ExecutePhase3b_AnalyzeWirelessClients(ctx);
        ExecutePhase3c_AnalyzeOfflineClients(ctx);
        ExecutePhase4_AnalyzeNetworkConfiguration(ctx);
        ExecutePhase5_AnalyzeFirewallRules(ctx);
        await ExecutePhase5b_AnalyzeDnsSecurityAsync(ctx);
        ExecutePhase5c_AnalyzeUpnpSecurity(ctx);
        ExecutePhase6_AnalyzeHardeningMeasures(ctx);

        // Build and score the final result
        var auditResult = BuildAuditResult(ctx);
        ExecutePhase7_CalculateSecurityScore(auditResult);

        _logger.LogInformation("Audit complete: {Posture} (Score: {Score}/100, {Critical} critical, {Recommended} recommended)",
            auditResult.Posture, auditResult.SecurityScore, auditResult.CriticalIssues.Count, auditResult.RecommendedIssues.Count);

        return auditResult;
    }

    #region Audit Phase Methods

    private AuditContext InitializeAuditContext(AuditRequest request)
    {
        if (request.Clients != null)
            _logger.LogInformation("Client data available for enhanced detection: {ClientCount} clients", request.Clients.Count);
        if (request.ClientHistory != null)
            _logger.LogInformation("Client history available for offline detection: {HistoryCount} historical clients", request.ClientHistory.Count);
        if (request.FingerprintDb != null)
            _logger.LogInformation("Fingerprint database available: {DeviceCount} devices", request.FingerprintDb.DevIds.Count);
        if (request.ProtectCameras != null)
            _logger.LogInformation("UniFi Protect cameras available for priority detection: {CameraCount} cameras", request.ProtectCameras.Count);

        // Create detection service with all available data sources
        var detectionService = new DeviceTypeDetectionService(
            _loggerFactory.CreateLogger<DeviceTypeDetectionService>(),
            request.FingerprintDb,
            _ieeeOuiDb,
            _loggerFactory);

        // Set UniFi Protect cameras (highest priority detection)
        if (request.ProtectCameras != null && request.ProtectCameras.Count > 0)
        {
            detectionService.SetProtectCameras(request.ProtectCameras);
        }

        // Set client history for enhanced offline device detection
        if (request.ClientHistory != null)
        {
            detectionService.SetClientHistory(request.ClientHistory);
        }

        var securityEngine = new PortSecurityAnalyzer(
            _loggerFactory.CreateLogger<PortSecurityAnalyzer>(),
            detectionService);

        // Parse JSON with error handling
        // Clone the RootElement to detach it from the JsonDocument, allowing proper disposal
        JsonElement deviceData;
        try
        {
            using var doc = JsonDocument.Parse(request.DeviceDataJson);
            deviceData = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse device data JSON");
            throw new InvalidOperationException("Invalid device data JSON format. Ensure the data is valid JSON from the UniFi API.", ex);
        }

        // Apply allowance settings to rules
        var effectiveSettings = request.AllowanceSettings ?? DeviceAllowanceSettings.Default;
        securityEngine.SetAllowanceSettings(effectiveSettings);

        // Set Protect cameras for network ID override (uses connection_network_id from Protect API)
        if (request.ProtectCameras != null && request.ProtectCameras.Count > 0)
        {
            securityEngine.SetProtectCameras(request.ProtectCameras);
        }

        // Create zone lookup for zone validation and DMZ/Hotspot identification
        var zoneLookup = new FirewallZoneLookup(request.FirewallZones, _loggerFactory.CreateLogger<FirewallZoneLookup>());
        if (request.FirewallZones != null)
            _logger.LogInformation("Firewall zone data available: {ZoneCount} zones", request.FirewallZones.Count);

        // Determine external zone ID from WAN network or legacy rules
        var externalZoneId = DetermineExternalZoneId(request.NetworkConfigs, request.FirewallRules);

        // Validate zone assumptions if we have both zone lookup and external zone ID
        if (zoneLookup.HasZoneData && externalZoneId != null)
        {
            // Find the WAN network to validate its zone assignment
            var wanNetwork = request.NetworkConfigs?.FirstOrDefault(n =>
                string.Equals(n.Purpose, "wan", StringComparison.OrdinalIgnoreCase));

            if (wanNetwork != null)
            {
                zoneLookup.ValidateWanZoneAssumption(wanNetwork.Name, wanNetwork.FirewallZoneId);
            }

            // Cross-check our determined external zone ID against the zone lookup
            zoneLookup.ValidateExternalZoneId(externalZoneId);
        }

        return new AuditContext
        {
            DeviceData = deviceData,
            Clients = request.Clients,
            ClientHistory = request.ClientHistory,
            SettingsData = request.SettingsData,
            FirewallRules = request.FirewallRules,
            FirewallGroups = request.FirewallGroups,
            NatRulesData = request.NatRulesData,
            ClientName = request.ClientName,
            SecurityEngine = securityEngine,
            AllowanceSettings = effectiveSettings,
            PortProfiles = request.PortProfiles,
            DnatExcludedVlanIds = request.DnatExcludedVlanIds,
            PiholeManagementPort = request.PiholeManagementPort,
            UpnpEnabled = request.UpnpEnabled,
            PortForwardRules = request.PortForwardRules,
            NetworkConfigs = request.NetworkConfigs,
            FirewallZones = request.FirewallZones,
            ZoneLookup = zoneLookup,
            ExternalZoneId = externalZoneId
        };
    }

    /// <summary>
    /// Determine the External/WAN firewall zone ID from network configurations or firewall rules.
    /// First tries to find zone ID from WAN network configs (v2 zone-based).
    /// Falls back to synthetic legacy zone ID for legacy systems without zone IDs.
    /// </summary>
    private string? DetermineExternalZoneId(List<UniFiNetworkConfig>? networkConfigs, List<FirewallRule>? firewallRules)
    {
        // Try to find zone ID from network configs first (v2 zone-based systems)
        if (networkConfigs != null && networkConfigs.Count > 0)
        {
            var wanNetwork = networkConfigs.FirstOrDefault(n =>
                string.Equals(n.Purpose, "wan", StringComparison.OrdinalIgnoreCase));

            if (wanNetwork?.FirewallZoneId != null)
            {
                _logger.LogDebug("Determined External Zone ID from WAN network '{Name}': {ZoneId}",
                    wanNetwork.Name, wanNetwork.FirewallZoneId);
                return wanNetwork.FirewallZoneId;
            }

            // WAN network exists but no firewall_zone_id - this is a legacy system
            // Use synthetic legacy zone ID for firewall rule analysis
            if (wanNetwork != null)
            {
                _logger.LogDebug("WAN network '{Name}' has no firewall_zone_id (legacy system), using synthetic legacy zone ID",
                    wanNetwork.Name);
                return FirewallRuleParser.LegacyExternalZoneId;
            }

            _logger.LogDebug("No WAN network found in {Count} network configs", networkConfigs.Count);
        }

        // Fall back to synthetic legacy zone ID if any rules already use it
        // This handles cases where rules were parsed before network configs
        if (firewallRules != null && firewallRules.Any(r =>
                r.DestinationZoneId == FirewallRuleParser.LegacyExternalZoneId ||
                r.SourceZoneId == FirewallRuleParser.LegacyExternalZoneId))
        {
            _logger.LogDebug("Using synthetic legacy External Zone ID from firewall rules");
            return FirewallRuleParser.LegacyExternalZoneId;
        }

        return null;
    }

    private void ExecutePhase1_ExtractNetworks(AuditContext ctx)
    {
        _logger.LogInformation("Phase 1: Extracting network topology");
        ctx.Networks = _vlanAnalyzer.ExtractNetworks(ctx.DeviceData, ctx.ZoneLookup);
        _logger.LogInformation("Found {NetworkCount} networks", ctx.Networks.Count);
    }

    private void ExecutePhase2_ExtractSwitches(AuditContext ctx)
    {
        _logger.LogInformation("Phase 2: Extracting switch configurations");
        if (ctx.PortProfiles != null)
            _logger.LogDebug("Port profiles available for resolution: {Count} profiles", ctx.PortProfiles.Count);
        ctx.Switches = ctx.SecurityEngine.ExtractSwitches(ctx.DeviceData, ctx.Networks, ctx.Clients, ctx.ClientHistory, ctx.PortProfiles);
        _logger.LogInformation("Found {SwitchCount} switches with {PortCount} total ports",
            ctx.Switches.Count, ctx.Switches.Sum(s => s.Ports.Count));
    }

    private void ExecutePhase3_AnalyzePortSecurity(AuditContext ctx)
    {
        _logger.LogInformation("Phase 3: Analyzing port security");

        // Build allNetworks from NetworkConfigs (includes disabled networks)
        // This is needed for rules like AccessPortVlanRule that count tagged VLANs
        // Disabled networks are dormant config that could become active if re-enabled
        // Only include corporate and guest networks - these are the ones selectable as tagged VLANs
        // (excludes WAN and VPN-client networks which can't be tagged on switch ports)
        List<NetworkInfo>? allNetworks = null;
        if (ctx.NetworkConfigs != null && ctx.NetworkConfigs.Count > 0)
        {
            allNetworks = ctx.NetworkConfigs
                .Where(nc => !string.IsNullOrEmpty(nc.Id) &&
                    (string.Equals(nc.Purpose, "corporate", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(nc.Purpose, "guest", StringComparison.OrdinalIgnoreCase)))
                .Select(nc => new NetworkInfo
                {
                    Id = nc.Id,
                    Name = nc.Name ?? "Unknown",
                    VlanId = nc.Vlan ?? 1,
                    Enabled = nc.Enabled
                })
                .ToList();

            var enabledCount = allNetworks.Count(n => n.Enabled);
            var disabledCount = allNetworks.Count(n => !n.Enabled);
            _logger.LogDebug("Built allNetworks from NetworkConfigs: {Total} total ({Enabled} enabled, {Disabled} disabled)",
                allNetworks.Count, enabledCount, disabledCount);
        }

        var portIssues = ctx.SecurityEngine.AnalyzePorts(ctx.Switches, ctx.Networks, allNetworks ?? ctx.Networks);
        ctx.AllIssues.AddRange(portIssues);
        _logger.LogInformation("Found {IssueCount} port security issues", portIssues.Count);
    }

    private void ExecutePhase3b_AnalyzeWirelessClients(AuditContext ctx)
    {
        _logger.LogInformation("Phase 3b: Analyzing wireless clients");
        var apLookup = ctx.SecurityEngine.ExtractAccessPointInfoLookup(ctx.DeviceData);

        // Store AP name-to-model lookup for offline client analysis
        ctx.ApNameToModel = apLookup.Values
            .Where(ap => !string.IsNullOrEmpty(ap.Name))
            .GroupBy(ap => ap.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().ModelName, StringComparer.OrdinalIgnoreCase);

        ctx.WirelessClients = ctx.SecurityEngine.ExtractWirelessClients(ctx.Clients, ctx.Networks, apLookup);
        var wirelessIssues = ctx.SecurityEngine.AnalyzeWirelessClients(ctx.WirelessClients, ctx.Networks);
        ctx.AllIssues.AddRange(wirelessIssues);
        _logger.LogInformation("Found {IssueCount} wireless client issues from {ClientCount} detected devices",
            wirelessIssues.Count, ctx.WirelessClients.Count);
    }

    private void ExecutePhase3c_AnalyzeOfflineClients(AuditContext ctx)
    {
        if (ctx.ClientHistory == null || ctx.ClientHistory.Count == 0)
        {
            _logger.LogDebug("Phase 3c: Skipping offline client analysis (no client history)");
            return;
        }

        _logger.LogInformation("Phase 3c: Analyzing offline clients");

        var detectionService = ctx.SecurityEngine.DetectionService;
        if (detectionService == null)
        {
            _logger.LogWarning("No detection service available for offline client analysis");
            return;
        }

        var onlineClientMacs = BuildOnlineClientMacSet(ctx.Clients);
        var twoWeeksAgo = DateTimeOffset.UtcNow.AddDays(-14).ToUnixTimeSeconds();

        foreach (var historyClient in ctx.ClientHistory)
        {
            if (ShouldSkipOfflineClient(historyClient, onlineClientMacs))
                continue;

            var detection = DetectOfflineClientType(historyClient, detectionService);
            if (detection.Category == ClientDeviceCategory.Unknown)
                continue;

            var lastNetwork = ctx.Networks.FirstOrDefault(n => n.Id == historyClient.LastConnectionNetworkId);
            if (lastNetwork == null)
                continue;

            AddOfflineClientInfo(ctx, historyClient, lastNetwork, detection);
            CheckOfflineClientPlacement(ctx, historyClient, lastNetwork, detection, twoWeeksAgo);
        }

        _logger.LogInformation("Found {OfflineCount} offline clients with detection, {IssueCount} VLAN placement issues",
            ctx.OfflineClients.Count, ctx.AllIssues.Count(i => i.Type?.StartsWith("OFFLINE-") == true));
    }

    private static HashSet<string> BuildOnlineClientMacSet(List<UniFiClientResponse>? clients)
    {
        var macs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (clients != null)
        {
            foreach (var client in clients.Where(c => !string.IsNullOrEmpty(c.Mac)))
                macs.Add(client.Mac!);
        }
        return macs;
    }

    private static bool ShouldSkipOfflineClient(UniFiClientDetailResponse client, HashSet<string> onlineMacs)
    {
        // Skip if currently online
        if (!string.IsNullOrEmpty(client.Mac) && onlineMacs.Contains(client.Mac))
            return true;
        // Skip wired devices (handled by port security analysis via LastConnectionMac)
        return client.IsWired;
    }

    private static DeviceDetectionResult DetectOfflineClientType(
        UniFiClientDetailResponse client,
        DeviceTypeDetectionService detectionService)
    {
        var detection = detectionService.DetectFromMac(client.Mac ?? "");

        // Try name-based detection if MAC detection didn't work
        if (detection.Category == ClientDeviceCategory.Unknown)
        {
            var displayName = client.DisplayName ?? client.Name ?? client.Hostname;
            if (!string.IsNullOrEmpty(displayName))
                detection = detectionService.DetectFromPortName(displayName);
        }

        return detection;
    }

    private void AddOfflineClientInfo(
        AuditContext ctx,
        UniFiClientDetailResponse historyClient,
        NetworkInfo lastNetwork,
        DeviceDetectionResult detection)
    {
        string? lastUplinkModelName = null;
        if (!string.IsNullOrEmpty(historyClient.LastUplinkName))
            ctx.ApNameToModel.TryGetValue(historyClient.LastUplinkName, out lastUplinkModelName);

        ctx.OfflineClients.Add(new OfflineClientInfo
        {
            HistoryClient = historyClient,
            LastNetwork = lastNetwork,
            Detection = detection,
            LastUplinkModelName = lastUplinkModelName
        });
    }

    private void CheckOfflineClientPlacement(
        AuditContext ctx,
        UniFiClientDetailResponse historyClient,
        NetworkInfo lastNetwork,
        DeviceDetectionResult detection,
        long twoWeeksAgo)
    {
        if (detection.Category.IsIoT())
            CheckOfflineIoTPlacement(ctx, historyClient, lastNetwork, detection, twoWeeksAgo);

        if (detection.Category.IsSurveillance())
            CheckOfflineCameraPlacement(ctx, historyClient, lastNetwork, detection, twoWeeksAgo);

        // Printers and scanners have their own VLAN placement logic (not part of IsIoT)
        if (detection.Category == Core.Enums.ClientDeviceCategory.Printer ||
            detection.Category == Core.Enums.ClientDeviceCategory.Scanner)
            CheckOfflinePrinterPlacement(ctx, historyClient, lastNetwork, detection, twoWeeksAgo);
    }

    private void CheckOfflineIoTPlacement(
        AuditContext ctx,
        UniFiClientDetailResponse historyClient,
        NetworkInfo lastNetwork,
        DeviceDetectionResult detection,
        long twoWeeksAgo)
    {
        // Skip cloud surveillance devices - they're handled by CheckOfflineCameraPlacement
        if (detection.Category.IsCloudSurveillance())
            return;

        var placement = Rules.VlanPlacementChecker.CheckIoTPlacement(
            detection.Category, lastNetwork, ctx.Networks, 10, ctx.AllowanceSettings, detection.VendorName);

        if (placement.IsCorrectlyPlaced)
            return;

        var isRecent = historyClient.LastSeen >= twoWeeksAgo;
        var displayName = historyClient.DisplayName ?? historyClient.Name ?? historyClient.Hostname ?? historyClient.Mac;

        // Different messaging for allowed vs not-allowed devices
        string message;
        string recommendedAction;
        if (placement.IsAllowedBySettings)
        {
            message = $"{detection.CategoryName} allowed per Settings on {lastNetwork.Name} VLAN";
            recommendedAction = "Change in Settings if you want to isolate this device type";
        }
        else
        {
            message = $"{detection.CategoryName} on {lastNetwork.Name} VLAN - should be isolated";
            recommendedAction = Rules.VlanPlacementChecker.GetMoveRecommendation(placement, "Create IoT VLAN");
        }

        ctx.AllIssues.Add(CreateOfflineVlanIssue(
            "OFFLINE-IOT-VLAN",
            message,
            displayName, lastNetwork, placement, detection, historyClient.LastSeen, isRecent,
            recommendedAction,
            isRecent ? placement.Severity : Models.AuditSeverity.Informational,
            isRecent ? placement.ScoreImpact : 0,
            placement.IsLowRisk,
            placement.IsAllowedBySettings));
    }

    private void CheckOfflineCameraPlacement(
        AuditContext ctx,
        UniFiClientDetailResponse historyClient,
        NetworkInfo lastNetwork,
        DeviceDetectionResult detection,
        long twoWeeksAgo)
    {
        // Cloud surveillance (Ring, Nest, Wyze, Blink, Arlo, SimpliSafe) should go on IoT VLAN, not Security VLAN
        var isCloudCamera = detection.Category.IsCloudSurveillance();
        var placement = isCloudCamera
            ? Rules.VlanPlacementChecker.CheckIoTPlacement(
                detection.Category, lastNetwork, ctx.Networks, 8, ctx.AllowanceSettings, detection.VendorName)
            : Rules.VlanPlacementChecker.CheckCameraPlacement(lastNetwork, ctx.Networks, 8);

        if (placement.IsCorrectlyPlaced)
            return;

        var isRecent = historyClient.LastSeen >= twoWeeksAgo;
        var displayName = historyClient.DisplayName ?? historyClient.Name ?? historyClient.Hostname ?? historyClient.Mac;

        var ruleId = isCloudCamera ? "OFFLINE-CLOUD-CAMERA-VLAN" : "OFFLINE-CAMERA-VLAN";
        var message = isCloudCamera
            ? $"{detection.CategoryName} on {lastNetwork.Name} VLAN - should be isolated"
            : $"{detection.CategoryName} on {lastNetwork.Name} VLAN - should be on security VLAN";
        var fallbackAction = isCloudCamera ? "Create IoT VLAN" : "Create Security VLAN";

        ctx.AllIssues.Add(CreateOfflineVlanIssue(
            ruleId,
            message,
            displayName, lastNetwork, placement, detection, historyClient.LastSeen, isRecent,
            Rules.VlanPlacementChecker.GetMoveRecommendation(placement, fallbackAction),
            isRecent ? (isCloudCamera ? placement.Severity : Models.AuditSeverity.Critical) : Models.AuditSeverity.Informational,
            isRecent ? placement.ScoreImpact : 0,
            isCloudCamera ? placement.IsLowRisk : false));
    }

    private void CheckOfflinePrinterPlacement(
        AuditContext ctx,
        UniFiClientDetailResponse historyClient,
        NetworkInfo lastNetwork,
        DeviceDetectionResult detection,
        long twoWeeksAgo)
    {
        var placement = Rules.VlanPlacementChecker.CheckPrinterPlacement(
            lastNetwork, ctx.Networks, 10, ctx.AllowanceSettings);

        if (placement.IsCorrectlyPlaced)
            return;

        var isRecent = historyClient.LastSeen >= twoWeeksAgo;
        var displayName = historyClient.DisplayName ?? historyClient.Name ?? historyClient.Hostname ?? historyClient.Mac;

        // Different messaging for allowed vs not-allowed devices
        string message;
        string recommendedAction;
        if (placement.IsAllowedBySettings)
        {
            message = $"{detection.CategoryName} allowed per Settings on {lastNetwork.Name} VLAN";
            recommendedAction = "Change in Settings if you want to isolate this device type";
        }
        else
        {
            message = $"{detection.CategoryName} on {lastNetwork.Name} VLAN - should be isolated";
            recommendedAction = Rules.VlanPlacementChecker.GetMoveRecommendation(placement, "Create Printer or IoT VLAN");
        }

        ctx.AllIssues.Add(CreateOfflineVlanIssue(
            "OFFLINE-PRINTER-VLAN",
            message,
            displayName, lastNetwork, placement, detection, historyClient.LastSeen, isRecent,
            recommendedAction,
            isRecent ? placement.Severity : Models.AuditSeverity.Informational,
            isRecent ? placement.ScoreImpact : 0,
            placement.IsLowRisk,
            placement.IsAllowedBySettings));
    }

    private static AuditIssue CreateOfflineVlanIssue(
        string type,
        string message,
        string? displayName,
        NetworkInfo lastNetwork,
        Rules.VlanPlacementChecker.PlacementResult placement,
        DeviceDetectionResult detection,
        long lastSeen,
        bool isRecent,
        string recommendedAction,
        Models.AuditSeverity severity,
        int scoreImpact,
        bool? isLowRisk = null,
        bool isAllowedBySettings = false)
    {
        var metadata = new Dictionary<string, object>
        {
            ["category"] = detection.CategoryName,
            ["confidence"] = detection.ConfidenceScore,
            ["source"] = detection.Source.ToString(),
            ["lastSeen"] = lastSeen,
            ["isRecent"] = isRecent
        };

        if (isLowRisk.HasValue)
            metadata["isLowRisk"] = isLowRisk.Value;

        if (isAllowedBySettings)
            metadata["allowed_by_settings"] = true;

        return new AuditIssue
        {
            Type = type,
            Severity = severity,
            Message = message,
            DeviceName = $"{displayName} (offline)",
            CurrentNetwork = lastNetwork.Name,
            CurrentVlan = lastNetwork.VlanId,
            RecommendedNetwork = placement.RecommendedNetwork?.Name,
            RecommendedVlan = placement.RecommendedNetwork?.VlanId,
            RecommendedAction = recommendedAction,
            RuleId = type,
            ScoreImpact = scoreImpact,
            Metadata = metadata
        };
    }

    private void ExecutePhase4_AnalyzeNetworkConfiguration(AuditContext ctx)
    {
        _logger.LogInformation("Phase 4: Analyzing network configuration");
        var gatewayName = ctx.Switches.FirstOrDefault(s => s.IsGateway)?.Name ?? "Gateway";

        var dnsIssues = _vlanAnalyzer.AnalyzeDnsConfiguration(ctx.Networks);
        var gatewayIssues = _vlanAnalyzer.AnalyzeGatewayConfiguration(ctx.Networks);
        var mgmtDhcpIssues = _vlanAnalyzer.AnalyzeManagementVlanDhcp(ctx.Networks, gatewayName);
        // Note: Network isolation and internet access analysis moved to Phase 5 where firewall rules are available
        var infraVlanIssues = _vlanAnalyzer.AnalyzeInfrastructureVlanPlacement(ctx.DeviceData, ctx.Networks, gatewayName);

        ctx.AllIssues.AddRange(dnsIssues);
        ctx.AllIssues.AddRange(gatewayIssues);
        ctx.AllIssues.AddRange(mgmtDhcpIssues);
        ctx.AllIssues.AddRange(infraVlanIssues);

        _logger.LogInformation("Found {DnsIssues} DNS issues, {GatewayIssues} gateway issues, {MgmtIssues} management VLAN issues, {InfraIssues} infrastructure VLAN issues",
            dnsIssues.Count, gatewayIssues.Count, mgmtDhcpIssues.Count, infraVlanIssues.Count);
    }

    private void ExecutePhase5_AnalyzeFirewallRules(AuditContext ctx)
    {
        _logger.LogInformation("Phase 5: Analyzing firewall rules");

        // Use pre-parsed firewall rules from context, adding any rules extracted from device data
        _firewallAnalyzer.SetFirewallGroups(ctx.FirewallGroups);

        var firewallRules = _firewallAnalyzer.ExtractFirewallRules(ctx.DeviceData);
        if (ctx.FirewallRules != null)
        {
            firewallRules.AddRange(ctx.FirewallRules);
        }

        var firewallIssues = firewallRules.Any()
            ? _firewallAnalyzer.AnalyzeFirewallRules(firewallRules, ctx.Networks, ctx.NetworkConfigs, ctx.ExternalZoneId, ctx.ZoneLookup)
            : new List<AuditIssue>();

        // Check if there's a 5G/LTE device on the network
        // Check all raw devices since 5G modems may not have port tables (not in Switches)
        var has5GDevice = ctx.DeviceData.ValueKind is JsonValueKind.Array or JsonValueKind.Object &&
            ctx.DeviceData.UnwrapDataArray().Any(d =>
                UniFi.UniFiProductDatabase.IsCellularModem(
                    d.GetStringOrNull("model"),
                    d.GetStringOrNull("shortname"),
                    d.GetStringOrNull("type")));

        var mgmtFirewallIssues = _firewallAnalyzer.AnalyzeManagementNetworkFirewallAccess(firewallRules, ctx.Networks, has5GDevice, ctx.ExternalZoneId);

        // Analyze internet access with firewall rules to detect both methods of blocking:
        // 1. internet_access_enabled=false in network config
        // 2. Firewall rule blocking network -> external zone
        var gatewayName = ctx.Switches.FirstOrDefault(s => s.IsGateway)?.Name ?? "Gateway";
        var internetAccessIssues = _vlanAnalyzer.AnalyzeInternetAccess(ctx.Networks, gatewayName, firewallRules, ctx.ExternalZoneId, _firewallAnalyzer);

        // Analyze network isolation with firewall rules to detect both methods of isolation:
        // 1. network_isolation_enabled=true in network config
        // 2. Firewall rule blocking network -> other internal networks
        var networkIsolationIssues = _vlanAnalyzer.AnalyzeNetworkIsolation(ctx.Networks, gatewayName, firewallRules, ctx.ZoneLookup);

        ctx.AllIssues.AddRange(firewallIssues);
        ctx.AllIssues.AddRange(mgmtFirewallIssues);
        ctx.AllIssues.AddRange(internetAccessIssues);
        ctx.AllIssues.AddRange(networkIsolationIssues);

        _logger.LogInformation("Found {IssueCount} firewall issues, {MgmtFwIssues} management network firewall issues, {InternetIssues} internet access issues, {IsolationIssues} network isolation issues (5G device: {Has5G})",
            firewallIssues.Count, mgmtFirewallIssues.Count, internetAccessIssues.Count, networkIsolationIssues.Count, has5GDevice);

        // Store firewall info for hardening analysis
        ctx.HardeningMeasures = ctx.SecurityEngine.AnalyzeHardening(ctx.Switches, ctx.Networks);

        // Add firewall rule consistency hardening measure
        var firewallCriticalOrWarnings = firewallIssues.Count(i =>
            i.Severity == Models.AuditSeverity.Critical || i.Severity == Models.AuditSeverity.Recommended);
        if (firewallRules.Any() && firewallCriticalOrWarnings == 0)
        {
            ctx.HardeningMeasures.Add($"All {firewallRules.Count} firewall rules are consistent with no conflicts");
        }
    }

    private async Task ExecutePhase5b_AnalyzeDnsSecurityAsync(AuditContext ctx)
    {
        _logger.LogInformation("Phase 5b: Analyzing DNS security");

        if (ctx.SettingsData.HasValue || ctx.FirewallRules?.Count > 0 || ctx.NatRulesData.HasValue)
        {
            ctx.DnsSecurityResult = await _dnsAnalyzer.AnalyzeAsync(
                ctx.SettingsData, ctx.FirewallRules, ctx.Switches, ctx.Networks, ctx.DeviceData, ctx.PiholeManagementPort, ctx.NatRulesData, ctx.DnatExcludedVlanIds, ctx.ExternalZoneId, ctx.ZoneLookup);
            ctx.AllIssues.AddRange(ctx.DnsSecurityResult.Issues);
            ctx.HardeningMeasures.AddRange(ctx.DnsSecurityResult.HardeningNotes);
            _logger.LogInformation("Found {IssueCount} DNS security issues", ctx.DnsSecurityResult.Issues.Count);
        }
        else
        {
            _logger.LogDebug("Skipping DNS security analysis - no settings or firewall policy data provided");
        }
    }

    private void ExecutePhase5c_AnalyzeUpnpSecurity(AuditContext ctx)
    {
        _logger.LogInformation("Phase 5c: Analyzing UPnP security");

        var gatewayName = ctx.Switches.FirstOrDefault(s => s.IsGateway)?.Name ?? "Gateway";
        var result = _upnpAnalyzer.Analyze(ctx.UpnpEnabled, ctx.PortForwardRules, ctx.Networks, gatewayName);

        ctx.AllIssues.AddRange(result.Issues);
        ctx.HardeningMeasures.AddRange(result.HardeningNotes);

        _logger.LogInformation("Found {IssueCount} UPnP security issues, {HardeningCount} hardening notes",
            result.Issues.Count, result.HardeningNotes.Count);
    }

    private void ExecutePhase6_AnalyzeHardeningMeasures(AuditContext ctx)
    {
        _logger.LogInformation("Phase 6: Analyzing hardening measures");

        // Add IoT VLAN segmentation hardening measure (>90% threshold)
        var iotNetwork = ctx.Networks.FirstOrDefault(n => n.Purpose == NetworkPurpose.IoT);
        if (iotNetwork != null)
        {
            var wiredIotOnCorrectVlan = ctx.Switches.SelectMany(s => s.Ports)
                .Count(p => p.IsUp && !p.IsUplink && !p.IsWan &&
                    IsIotDeviceName(p.Name) && p.NativeNetworkId == iotNetwork.Id);
            var wiredIotTotal = ctx.Switches.SelectMany(s => s.Ports)
                .Count(p => p.IsUp && !p.IsUplink && !p.IsWan && IsIotDeviceName(p.Name));

            var wirelessIotOnCorrectVlan = ctx.WirelessClients
                .Count(c => c.Detection.Category.IsIoT() && c.Network?.Id == iotNetwork.Id);
            var wirelessIotTotal = ctx.WirelessClients
                .Count(c => c.Detection.Category.IsIoT());

            var totalIot = wiredIotTotal + wirelessIotTotal;
            var totalIotCorrect = wiredIotOnCorrectVlan + wirelessIotOnCorrectVlan;

            if (totalIot > 0)
            {
                var percentage = (double)totalIotCorrect / totalIot * 100;
                if (percentage >= 90)
                {
                    ctx.HardeningMeasures.Add($"{totalIotCorrect} of {totalIot} IoT devices properly segmented on IoT VLAN ({percentage:F0}%)");
                }
            }
        }

        ctx.Statistics = ctx.SecurityEngine.CalculateStatistics(ctx.Switches);
        _logger.LogInformation("Found {MeasureCount} hardening measures in place", ctx.HardeningMeasures.Count);
    }

    private AuditResult BuildAuditResult(AuditContext ctx)
    {
        var dnsSecurityInfo = BuildDnsSecurityInfo(ctx.DnsSecurityResult);

        return new AuditResult
        {
            Timestamp = DateTime.UtcNow,
            ClientName = ctx.ClientName,
            Networks = ctx.Networks,
            Switches = ctx.Switches,
            WirelessClients = ctx.WirelessClients,
            OfflineClients = ctx.OfflineClients,
            Issues = ctx.AllIssues,
            HardeningMeasures = ctx.HardeningMeasures,
            Statistics = ctx.Statistics ?? new AuditStatistics(),
            DnsSecurity = dnsSecurityInfo
        };
    }

    private static DnsSecurityInfo? BuildDnsSecurityInfo(DnsSecurityResult? dnsSecurityResult)
    {
        if (dnsSecurityResult == null)
            return null;

        var providerNames = dnsSecurityResult.ConfiguredServers
            .Where(s => s.Enabled)
            .Select(s => s.StampInfo?.ProviderInfo?.Name
                ?? s.Provider?.Name
                ?? DohProviderRegistry.IdentifyProviderFromName(s.ServerName)?.Name
                ?? (s.StampInfo?.Hostname != null ? DohProviderRegistry.IdentifyProvider(s.StampInfo.Hostname)?.Name : null)
                ?? (s.StampInfo?.Hostname?.Contains('.') == true ? s.StampInfo.Hostname : null)
                ?? (s.ServerName.Any(char.IsLetter) ? s.ServerName : "Custom DoH"))
            .Distinct()
            .ToList();

        var configNames = dnsSecurityResult.ConfiguredServers
            .Where(s => s.Enabled)
            .Select(s => s.ServerName)
            .Distinct()
            .ToList();

        var interfacesWithoutDns = dnsSecurityResult.WanInterfaces
            .Where(w => !w.HasStaticDns)
            .Select(w => NetworkFormatHelpers.FormatWanInterfaceName(w.InterfaceName, w.PortName))
            .ToList();

        var interfacesWithMismatch = dnsSecurityResult.WanInterfaces
            .Where(w => w.HasStaticDns && !w.MatchesDoH)
            .Select(w => NetworkFormatHelpers.FormatWanInterfaceName(w.InterfaceName, w.PortName))
            .ToList();

        var mismatchedDnsServers = dnsSecurityResult.WanInterfaces
            .Where(w => w.HasStaticDns && !w.MatchesDoH)
            .SelectMany(w => w.DnsServers)
            .Distinct()
            .ToList();

        var matchedDnsServers = dnsSecurityResult.WanInterfaces
            .Where(w => w.HasStaticDns && w.MatchesDoH)
            .SelectMany(w => w.DnsServers)
            .Distinct()
            .ToList();

        // Build third-party DNS network info
        var thirdPartyNetworks = dnsSecurityResult.ThirdPartyDnsServers
            .Select(t => new ThirdPartyDnsNetwork
            {
                NetworkName = t.NetworkName,
                VlanId = t.NetworkVlanId,
                DnsServerIp = t.DnsServerIp,
                DnsProviderName = t.DnsProviderName
            })
            .ToList();

        return new DnsSecurityInfo
        {
            DohEnabled = dnsSecurityResult.DohConfigured,
            DohState = dnsSecurityResult.DohState,
            DohProviders = providerNames,
            DohConfigNames = configNames,
            DnsLeakProtection = dnsSecurityResult.HasDns53BlockRule || (dnsSecurityResult.DnatProvidesFullCoverage && dnsSecurityResult.DnatRedirectTargetIsValid && dnsSecurityResult.DnatDestinationFilterIsValid),
            HasDns53BlockRule = dnsSecurityResult.HasDns53BlockRule,
            DotBlocked = dnsSecurityResult.HasDotBlockRule,
            DoqBlocked = dnsSecurityResult.HasDoqBlockRule,
            DohBypassBlocked = dnsSecurityResult.HasDohBlockRule,
            Doh3Blocked = dnsSecurityResult.HasDoh3BlockRule,
            WanDnsServers = dnsSecurityResult.WanDnsServers.ToList(),
            WanDnsPtrResults = dnsSecurityResult.WanDnsPtrResults.ToList(),
            WanDnsMatchesDoH = dnsSecurityResult.WanDnsMatchesDoH,
            WanDnsOrderCorrect = dnsSecurityResult.WanDnsOrderCorrect,
            WanDnsProvider = dnsSecurityResult.WanDnsProvider,
            ExpectedDnsProvider = dnsSecurityResult.ExpectedDnsProvider,
            DeviceDnsPointsToGateway = dnsSecurityResult.DeviceDnsPointsToGateway,
            TotalDevicesChecked = dnsSecurityResult.TotalDevicesChecked,
            DevicesWithCorrectDns = dnsSecurityResult.DevicesWithCorrectDns,
            DhcpDeviceCount = dnsSecurityResult.DhcpDeviceCount,
            InterfacesWithoutDns = interfacesWithoutDns,
            InterfacesWithMismatch = interfacesWithMismatch,
            MismatchedDnsServers = mismatchedDnsServers,
            MatchedDnsServers = matchedDnsServers,
            // Third-party DNS
            HasThirdPartyDns = dnsSecurityResult.HasThirdPartyDns,
            IsPiholeDetected = dnsSecurityResult.IsPiholeDetected,
            ThirdPartyDnsProviderName = dnsSecurityResult.ThirdPartyDnsProviderName,
            ThirdPartyNetworks = thirdPartyNetworks,
            // DNAT DNS Coverage
            HasDnatDnsRules = dnsSecurityResult.HasDnatDnsRules,
            DnatProvidesFullCoverage = dnsSecurityResult.DnatProvidesFullCoverage,
            DnatRedirectTarget = dnsSecurityResult.DnatRedirectTarget,
            DnatCoveredNetworks = dnsSecurityResult.DnatCoveredNetworks.ToList(),
            DnatUncoveredNetworks = dnsSecurityResult.DnatUncoveredNetworks.ToList()
        };
    }

    private void ExecutePhase7_CalculateSecurityScore(AuditResult auditResult)
    {
        _logger.LogInformation("Phase 7: Calculating security score");
        var score = _scorer.CalculateScore(auditResult);
        var posture = _scorer.DeterminePosture(score, auditResult.CriticalIssues.Count);

        auditResult.SecurityScore = score;
        auditResult.Posture = posture;
    }

    #endregion

    /// <summary>
    /// Run audit from a JSON file
    /// </summary>
    public async Task<AuditResult> RunAuditFromFileAsync(string jsonFilePath, string? clientName = null)
    {
        _logger.LogInformation("Loading device data from {FilePath}", jsonFilePath);

        if (!File.Exists(jsonFilePath))
        {
            throw new FileNotFoundException($"Device data file not found: {jsonFilePath}");
        }

        var json = await File.ReadAllTextAsync(jsonFilePath);
        return await RunAuditAsync(json, clientName);
    }

    /// <summary>
    /// Get recommendations for improving security posture
    /// </summary>
    public List<string> GetRecommendations(AuditResult auditResult)
    {
        return _scorer.GetRecommendations(auditResult);
    }

    /// <summary>
    /// Generate executive summary
    /// </summary>
    public string GenerateExecutiveSummary(AuditResult auditResult)
    {
        return _scorer.GenerateExecutiveSummary(auditResult);
    }

    /// <summary>
    /// Get detailed report as formatted text
    /// </summary>
    public string GenerateTextReport(AuditResult auditResult)
    {
        var report = new System.Text.StringBuilder();

        // Header
        report.AppendLine("================================================================================");
        report.AppendLine($"        UniFi Network Security Audit Report");
        if (!string.IsNullOrEmpty(auditResult.ClientName))
        {
            report.AppendLine($"        Client: {auditResult.ClientName}");
        }
        report.AppendLine($"        Generated: {auditResult.Timestamp:yyyy-MM-dd HH:mm:ss} UTC");
        report.AppendLine("================================================================================");
        report.AppendLine();

        // Executive Summary
        report.AppendLine("EXECUTIVE SUMMARY");
        report.AppendLine("--------------------------------------------------------------------------------");
        report.AppendLine(GenerateExecutiveSummary(auditResult));
        report.AppendLine();

        // Hardening Measures
        if (auditResult.HardeningMeasures.Any())
        {
            report.AppendLine("HARDENING MEASURES IN PLACE");
            report.AppendLine("--------------------------------------------------------------------------------");
            foreach (var measure in auditResult.HardeningMeasures)
            {
                report.AppendLine($"  ✓ {measure}");
            }
            report.AppendLine();
        }

        // Networks
        report.AppendLine("NETWORK TOPOLOGY");
        report.AppendLine("--------------------------------------------------------------------------------");
        report.AppendLine($"{"Network",-30} {"VLAN",-8} {"Purpose",-15} {"Subnet",-20}");
        report.AppendLine(new string('-', 80));
        foreach (var network in auditResult.Networks.OrderBy(n => n.VlanId))
        {
            var vlanStr = network.IsNative ? $"{network.VlanId} (native)" : network.VlanId.ToString();
            report.AppendLine($"{network.Name,-30} {vlanStr,-8} {network.Purpose,-15} {network.Subnet ?? "N/A",-20}");
        }
        report.AppendLine();

        // Statistics
        report.AppendLine("PORT SECURITY STATISTICS");
        report.AppendLine("--------------------------------------------------------------------------------");
        report.AppendLine($"  Total Ports:              {auditResult.Statistics.TotalPorts}");
        report.AppendLine($"  Active Ports:             {auditResult.Statistics.ActivePorts}");
        report.AppendLine($"  Disabled Ports:           {auditResult.Statistics.DisabledPorts}");
        report.AppendLine($"  MAC Restricted:           {auditResult.Statistics.MacRestrictedPorts}");
        report.AppendLine($"  Isolated Ports:           {auditResult.Statistics.IsolatedPorts}");
        report.AppendLine($"  Unprotected Active:       {auditResult.Statistics.UnprotectedActivePorts}");
        report.AppendLine($"  Hardening Percentage:     {auditResult.Statistics.HardeningPercentage:F1}%");
        report.AppendLine();

        // Critical Issues
        if (auditResult.CriticalIssues.Any())
        {
            report.AppendLine("CRITICAL ISSUES (Immediate Action Required)");
            report.AppendLine("================================================================================");
            foreach (var issue in auditResult.CriticalIssues)
            {
                report.AppendLine($"[!] {issue.DeviceName} - Port {issue.Port} ({issue.PortName})");
                report.AppendLine($"    Issue: {issue.Message}");
                if (!string.IsNullOrEmpty(issue.RecommendedAction))
                {
                    report.AppendLine($"    Action: {issue.RecommendedAction}");
                }
                report.AppendLine();
            }
        }

        // Recommended Issues
        if (auditResult.RecommendedIssues.Any())
        {
            report.AppendLine("RECOMMENDED IMPROVEMENTS");
            report.AppendLine("================================================================================");
            foreach (var issue in auditResult.RecommendedIssues)
            {
                var location = !string.IsNullOrEmpty(issue.DeviceName)
                    ? $"{issue.DeviceName} - Port {issue.Port}"
                    : "Network-wide";
                report.AppendLine($"[*] {location}");
                report.AppendLine($"    {issue.Message}");
                report.AppendLine();
            }
        }

        // Recommendations
        var recommendations = GetRecommendations(auditResult);
        if (recommendations.Any())
        {
            report.AppendLine("RECOMMENDATIONS");
            report.AppendLine("================================================================================");
            for (int i = 0; i < recommendations.Count; i++)
            {
                report.AppendLine($"{i + 1}. {recommendations[i]}");
            }
            report.AppendLine();
        }

        // Switch Details
        report.AppendLine("SWITCH DETAILS");
        report.AppendLine("================================================================================");
        foreach (var sw in auditResult.Switches)
        {
            var deviceType = sw.IsGateway ? "[Gateway]" : "[Switch]";
            var cleanName = StripDevicePrefix(sw.Name);
            report.AppendLine($"{deviceType} {cleanName} ({sw.ModelName})");
            report.AppendLine($"  IP: {sw.IpAddress ?? "N/A"}");
            report.AppendLine($"  Ports: {sw.Ports.Count}");
            report.AppendLine($"  Active: {sw.Ports.Count(p => p.IsUp)}");
            report.AppendLine($"  MAC ACL Support: {(sw.Capabilities.MaxCustomMacAcls > 0 ? $"Yes ({sw.Capabilities.MaxCustomMacAcls} max)" : "No")}");
            report.AppendLine();
        }

        report.AppendLine("================================================================================");
        report.AppendLine("End of Report");
        report.AppendLine("================================================================================");

        return report.ToString();
    }

    /// <summary>
    /// Export audit results to JSON
    /// </summary>
    public string ExportToJson(AuditResult auditResult)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        return JsonSerializer.Serialize(auditResult, options);
    }

    /// <summary>
    /// Save audit results to file
    /// </summary>
    public void SaveResults(AuditResult auditResult, string outputPath, string format = "json")
    {
        var content = format.ToLowerInvariant() switch
        {
            "json" => ExportToJson(auditResult),
            "text" or "txt" => GenerateTextReport(auditResult),
            _ => throw new ArgumentException($"Unsupported format: {format}")
        };

        File.WriteAllText(outputPath, content);
        _logger.LogInformation("Audit results saved to {OutputPath}", outputPath);
    }

    /// <summary>
    /// Check if port name indicates an IoT device
    /// </summary>
    private static bool IsIotDeviceName(string? portName) => DeviceNameHints.IsIoTDeviceName(portName);
}
