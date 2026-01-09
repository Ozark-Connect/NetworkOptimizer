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

// Disambiguate types that exist in both Audit.Models and Core.Models
using AuditResult = NetworkOptimizer.Audit.Models.AuditResult;
using AuditStatistics = NetworkOptimizer.Audit.Models.AuditStatistics;

using static NetworkOptimizer.Core.Helpers.DisplayFormatters;

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
    private readonly AuditScorer _scorer;

    /// <summary>
    /// Internal context passed between audit phases
    /// </summary>
    private sealed class AuditContext
    {
        public required JsonElement DeviceData { get; init; }
        public required List<UniFiClientResponse>? Clients { get; init; }
        public required List<UniFiClientHistoryResponse>? ClientHistory { get; init; }
        public required JsonElement? SettingsData { get; init; }
        public required JsonElement? FirewallPoliciesData { get; init; }
        public required string? ClientName { get; init; }
        public required PortSecurityAnalyzer SecurityEngine { get; init; }
        public required DeviceAllowanceSettings AllowanceSettings { get; init; }

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
            ieeeOuiDb: ieeeOuiDb);

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
        _scorer = new AuditScorer(loggerFactory.CreateLogger<AuditScorer>());
    }

    /// <summary>
    /// Run a comprehensive audit on UniFi device data
    /// </summary>
    /// <param name="deviceDataJson">JSON string containing UniFi device data from /stat/device API</param>
    /// <param name="clientName">Optional client/site name for the report</param>
    /// <returns>Complete audit results</returns>
    public Task<AuditResult> RunAuditAsync(string deviceDataJson, string? clientName = null)
        => RunAuditAsync(deviceDataJson, clients: null, fingerprintDb: null, settingsData: null, firewallPoliciesData: null, allowanceSettings: null, clientName);

    /// <summary>
    /// Run a comprehensive audit on UniFi device data with client data for enhanced detection
    /// </summary>
    /// <param name="deviceDataJson">JSON string containing UniFi device data from /stat/device API</param>
    /// <param name="clients">Connected clients for device type detection (optional)</param>
    /// <param name="clientName">Optional client/site name for the report</param>
    /// <returns>Complete audit results</returns>
    public Task<AuditResult> RunAuditAsync(string deviceDataJson, List<UniFiClientResponse>? clients, string? clientName = null)
        => RunAuditAsync(deviceDataJson, clients, fingerprintDb: null, settingsData: null, firewallPoliciesData: null, allowanceSettings: null, clientName);

    /// <summary>
    /// Run a comprehensive audit on UniFi device data with client data and fingerprint database for enhanced detection
    /// </summary>
    /// <param name="deviceDataJson">JSON string containing UniFi device data from /stat/device API</param>
    /// <param name="clients">Connected clients for device type detection (optional)</param>
    /// <param name="fingerprintDb">UniFi fingerprint database for device name lookups (optional)</param>
    /// <param name="clientName">Optional client/site name for the report</param>
    /// <returns>Complete audit results</returns>
    public Task<AuditResult> RunAuditAsync(string deviceDataJson, List<UniFiClientResponse>? clients, UniFiFingerprintDatabase? fingerprintDb, string? clientName = null)
        => RunAuditAsync(deviceDataJson, clients, fingerprintDb, settingsData: null, firewallPoliciesData: null, allowanceSettings: null, clientName);

    /// <summary>
    /// Run a comprehensive audit on UniFi device data with all available data sources
    /// </summary>
    /// <param name="deviceDataJson">JSON string containing UniFi device data from /stat/device API</param>
    /// <param name="clients">Connected clients for device type detection (optional)</param>
    /// <param name="fingerprintDb">UniFi fingerprint database for device name lookups (optional)</param>
    /// <param name="settingsData">Site settings data including DoH configuration (optional)</param>
    /// <param name="firewallPoliciesData">Firewall policies data for DNS leak prevention analysis (optional)</param>
    /// <param name="clientName">Optional client/site name for the report</param>
    /// <returns>Complete audit results</returns>
    public Task<AuditResult> RunAuditAsync(
        string deviceDataJson,
        List<UniFiClientResponse>? clients,
        UniFiFingerprintDatabase? fingerprintDb,
        JsonElement? settingsData,
        JsonElement? firewallPoliciesData,
        string? clientName = null)
        => RunAuditAsync(deviceDataJson, clients, fingerprintDb, settingsData, firewallPoliciesData, allowanceSettings: null, clientName);

    /// <summary>
    /// Run a comprehensive audit on UniFi device data with all available data sources and device allowance settings
    /// </summary>
    public Task<AuditResult> RunAuditAsync(
        string deviceDataJson,
        List<UniFiClientResponse>? clients,
        UniFiFingerprintDatabase? fingerprintDb,
        JsonElement? settingsData,
        JsonElement? firewallPoliciesData,
        DeviceAllowanceSettings? allowanceSettings,
        string? clientName = null)
        => RunAuditAsync(deviceDataJson, clients, clientHistory: null, fingerprintDb, settingsData, firewallPoliciesData, allowanceSettings, protectCameras: null, clientName);

    /// <summary>
    /// Run a comprehensive audit on UniFi device data with client history for offline device detection
    /// </summary>
    /// <param name="deviceDataJson">JSON string containing UniFi device data from /stat/device API</param>
    /// <param name="clients">Connected clients for device type detection (optional)</param>
    /// <param name="clientHistory">Historical clients for offline device detection (optional)</param>
    /// <param name="fingerprintDb">UniFi fingerprint database for device name lookups (optional)</param>
    /// <param name="settingsData">Site settings data including DoH configuration (optional)</param>
    /// <param name="firewallPoliciesData">Firewall policies data for DNS leak prevention analysis (optional)</param>
    /// <param name="allowanceSettings">Settings for allowing devices on main network (optional)</param>
    /// <param name="protectCameras">UniFi Protect cameras for 100% confidence detection (optional)</param>
    /// <param name="clientName">Optional client/site name for the report</param>
    /// <returns>Complete audit results</returns>
    public Task<AuditResult> RunAuditAsync(
        string deviceDataJson,
        List<UniFiClientResponse>? clients,
        List<UniFiClientHistoryResponse>? clientHistory,
        UniFiFingerprintDatabase? fingerprintDb,
        JsonElement? settingsData,
        JsonElement? firewallPoliciesData,
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
            FirewallPoliciesData = firewallPoliciesData,
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

        // Execute audit phases
        ExecutePhase1_ExtractNetworks(ctx);
        ExecutePhase2_ExtractSwitches(ctx);
        ExecutePhase3_AnalyzePortSecurity(ctx);
        ExecutePhase3b_AnalyzeWirelessClients(ctx);
        ExecutePhase3c_AnalyzeOfflineClients(ctx);
        ExecutePhase4_AnalyzeNetworkConfiguration(ctx);
        ExecutePhase5_AnalyzeFirewallRules(ctx);
        await ExecutePhase5b_AnalyzeDnsSecurityAsync(ctx);
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
            _ieeeOuiDb);

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

        return new AuditContext
        {
            DeviceData = deviceData,
            Clients = request.Clients,
            ClientHistory = request.ClientHistory,
            SettingsData = request.SettingsData,
            FirewallPoliciesData = request.FirewallPoliciesData,
            ClientName = request.ClientName,
            SecurityEngine = securityEngine,
            AllowanceSettings = effectiveSettings
        };
    }

    private void ExecutePhase1_ExtractNetworks(AuditContext ctx)
    {
        _logger.LogInformation("Phase 1: Extracting network topology");
        ctx.Networks = _vlanAnalyzer.ExtractNetworks(ctx.DeviceData);
        _logger.LogInformation("Found {NetworkCount} networks", ctx.Networks.Count);
    }

    private void ExecutePhase2_ExtractSwitches(AuditContext ctx)
    {
        _logger.LogInformation("Phase 2: Extracting switch configurations");
        ctx.Switches = ctx.SecurityEngine.ExtractSwitches(ctx.DeviceData, ctx.Networks, ctx.Clients, ctx.ClientHistory);
        _logger.LogInformation("Found {SwitchCount} switches with {PortCount} total ports",
            ctx.Switches.Count, ctx.Switches.Sum(s => s.Ports.Count));
    }

    private void ExecutePhase3_AnalyzePortSecurity(AuditContext ctx)
    {
        _logger.LogInformation("Phase 3: Analyzing port security");
        var portIssues = ctx.SecurityEngine.AnalyzePorts(ctx.Switches, ctx.Networks);
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

    private static bool ShouldSkipOfflineClient(UniFiClientHistoryResponse client, HashSet<string> onlineMacs)
    {
        // Skip if currently online
        if (!string.IsNullOrEmpty(client.Mac) && onlineMacs.Contains(client.Mac))
            return true;
        // Skip wired devices (handled by port security analysis via LastConnectionMac)
        return client.IsWired;
    }

    private static DeviceDetectionResult DetectOfflineClientType(
        UniFiClientHistoryResponse client,
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
        UniFiClientHistoryResponse historyClient,
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
        UniFiClientHistoryResponse historyClient,
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
        UniFiClientHistoryResponse historyClient,
        NetworkInfo lastNetwork,
        DeviceDetectionResult detection,
        long twoWeeksAgo)
    {
        // Skip cloud cameras - they're handled by CheckOfflineCameraPlacement
        if (detection.Category.IsCloudCamera())
            return;

        var placement = Rules.VlanPlacementChecker.CheckIoTPlacement(
            detection.Category, lastNetwork, ctx.Networks, 10, ctx.AllowanceSettings, detection.VendorName);

        if (placement.IsCorrectlyPlaced)
            return;

        var isRecent = historyClient.LastSeen >= twoWeeksAgo;
        var displayName = historyClient.DisplayName ?? historyClient.Name ?? historyClient.Hostname ?? historyClient.Mac;

        ctx.AllIssues.Add(CreateOfflineVlanIssue(
            "OFFLINE-IOT-VLAN",
            $"{detection.CategoryName} on {lastNetwork.Name} VLAN - should be isolated",
            displayName, lastNetwork, placement, detection, historyClient.LastSeen, isRecent,
            placement.RecommendedNetwork != null ? $"Move to {placement.RecommendedNetworkLabel}" : "Create IoT VLAN",
            isRecent ? placement.Severity : Models.AuditSeverity.Informational,
            isRecent ? placement.ScoreImpact : 0,
            placement.IsLowRisk));
    }

    private void CheckOfflineCameraPlacement(
        AuditContext ctx,
        UniFiClientHistoryResponse historyClient,
        NetworkInfo lastNetwork,
        DeviceDetectionResult detection,
        long twoWeeksAgo)
    {
        // Cloud cameras (Ring, Nest, Wyze, Blink, Arlo) should go on IoT VLAN, not Security VLAN
        var isCloudCamera = detection.Category.IsCloudCamera();
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
            placement.RecommendedNetwork != null ? $"Move to {placement.RecommendedNetworkLabel}" : fallbackAction,
            isRecent ? (isCloudCamera ? placement.Severity : Models.AuditSeverity.Critical) : Models.AuditSeverity.Informational,
            isRecent ? placement.ScoreImpact : 0,
            isCloudCamera ? placement.IsLowRisk : false));
    }

    private void CheckOfflinePrinterPlacement(
        AuditContext ctx,
        UniFiClientHistoryResponse historyClient,
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

        ctx.AllIssues.Add(CreateOfflineVlanIssue(
            "OFFLINE-PRINTER-VLAN",
            $"{detection.CategoryName} on {lastNetwork.Name} VLAN - should be isolated",
            displayName, lastNetwork, placement, detection, historyClient.LastSeen, isRecent,
            placement.RecommendedNetwork != null ? $"Move to {placement.RecommendedNetworkLabel}" : "Create Printer or IoT VLAN",
            isRecent ? placement.Severity : Models.AuditSeverity.Informational,
            isRecent ? placement.ScoreImpact : 0,
            placement.IsLowRisk));
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
        bool? isLowRisk = null)
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
        var networkIsolationIssues = _vlanAnalyzer.AnalyzeNetworkIsolation(ctx.Networks, gatewayName);
        var internetAccessIssues = _vlanAnalyzer.AnalyzeInternetAccess(ctx.Networks, gatewayName);
        var infraVlanIssues = _vlanAnalyzer.AnalyzeInfrastructureVlanPlacement(ctx.DeviceData, ctx.Networks, gatewayName);

        ctx.AllIssues.AddRange(dnsIssues);
        ctx.AllIssues.AddRange(gatewayIssues);
        ctx.AllIssues.AddRange(mgmtDhcpIssues);
        ctx.AllIssues.AddRange(networkIsolationIssues);
        ctx.AllIssues.AddRange(internetAccessIssues);
        ctx.AllIssues.AddRange(infraVlanIssues);

        _logger.LogInformation("Found {DnsIssues} DNS issues, {GatewayIssues} gateway issues, {MgmtIssues} management VLAN issues, {IsolationIssues} network isolation issues, {InternetIssues} internet access issues, {InfraIssues} infrastructure VLAN issues",
            dnsIssues.Count, gatewayIssues.Count, mgmtDhcpIssues.Count, networkIsolationIssues.Count, internetAccessIssues.Count, infraVlanIssues.Count);
    }

    private void ExecutePhase5_AnalyzeFirewallRules(AuditContext ctx)
    {
        _logger.LogInformation("Phase 5: Analyzing firewall rules");

        var firewallRules = _firewallAnalyzer.ExtractFirewallRules(ctx.DeviceData);
        var policyRules = _firewallAnalyzer.ExtractFirewallPolicies(ctx.FirewallPoliciesData);
        firewallRules.AddRange(policyRules);

        var firewallIssues = firewallRules.Any()
            ? _firewallAnalyzer.AnalyzeFirewallRules(firewallRules, ctx.Networks)
            : new List<AuditIssue>();

        // Check if there's a 5G/LTE device on the network
        var has5GDevice = ctx.Switches.Any(s =>
            s.Model?.StartsWith("U5G", StringComparison.OrdinalIgnoreCase) == true ||
            s.Model?.StartsWith("U-LTE", StringComparison.OrdinalIgnoreCase) == true);

        var mgmtFirewallIssues = _firewallAnalyzer.AnalyzeManagementNetworkFirewallAccess(firewallRules, ctx.Networks, has5GDevice);

        ctx.AllIssues.AddRange(firewallIssues);
        ctx.AllIssues.AddRange(mgmtFirewallIssues);

        _logger.LogInformation("Found {IssueCount} firewall issues, {MgmtFwIssues} management network firewall issues (5G device: {Has5G})",
            firewallIssues.Count, mgmtFirewallIssues.Count, has5GDevice);

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

        if (ctx.SettingsData.HasValue || ctx.FirewallPoliciesData.HasValue)
        {
            ctx.DnsSecurityResult = await _dnsAnalyzer.AnalyzeAsync(
                ctx.SettingsData, ctx.FirewallPoliciesData, ctx.Switches, ctx.Networks, ctx.DeviceData);
            ctx.AllIssues.AddRange(ctx.DnsSecurityResult.Issues);
            ctx.HardeningMeasures.AddRange(ctx.DnsSecurityResult.HardeningNotes);
            _logger.LogInformation("Found {IssueCount} DNS security issues", ctx.DnsSecurityResult.Issues.Count);
        }
        else
        {
            _logger.LogDebug("Skipping DNS security analysis - no settings or firewall policy data provided");
        }
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
            DnsLeakProtection = dnsSecurityResult.HasDns53BlockRule,
            DotBlocked = dnsSecurityResult.HasDotBlockRule,
            DohBypassBlocked = dnsSecurityResult.HasDohBlockRule,
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
            ThirdPartyNetworks = thirdPartyNetworks
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
