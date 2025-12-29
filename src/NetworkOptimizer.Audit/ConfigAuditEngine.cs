using System.Text.Json;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Audit.Analyzers;
using NetworkOptimizer.Audit.Dns;
using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.Audit.Services;
using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.Audit;

/// <summary>
/// Main orchestrator for comprehensive UniFi network configuration audits
/// Coordinates all analyzers and generates complete audit results
/// </summary>
public class ConfigAuditEngine
{
    private readonly ILogger<ConfigAuditEngine> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly VlanAnalyzer _vlanAnalyzer;
    private readonly SecurityAuditEngine _securityEngine;
    private readonly FirewallRuleAnalyzer _firewallAnalyzer;
    private readonly DnsSecurityAnalyzer _dnsAnalyzer;
    private readonly AuditScorer _scorer;

    /// <summary>
    /// Create ConfigAuditEngine with dependency injection
    /// </summary>
    public ConfigAuditEngine(
        ILogger<ConfigAuditEngine> logger,
        ILoggerFactory loggerFactory)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));

        _vlanAnalyzer = new VlanAnalyzer(loggerFactory.CreateLogger<VlanAnalyzer>());

        // Create detection service with logging for enhanced device type detection
        var detectionService = new DeviceTypeDetectionService(
            loggerFactory.CreateLogger<DeviceTypeDetectionService>());

        _securityEngine = new SecurityAuditEngine(
            loggerFactory.CreateLogger<SecurityAuditEngine>(),
            detectionService);
        _firewallAnalyzer = new FirewallRuleAnalyzer(loggerFactory.CreateLogger<FirewallRuleAnalyzer>());
        _dnsAnalyzer = new DnsSecurityAnalyzer(loggerFactory.CreateLogger<DnsSecurityAnalyzer>());
        _scorer = new AuditScorer(loggerFactory.CreateLogger<AuditScorer>());
    }

    /// <summary>
    /// Run a comprehensive audit on UniFi device data
    /// </summary>
    /// <param name="deviceDataJson">JSON string containing UniFi device data from /stat/device API</param>
    /// <param name="clientName">Optional client/site name for the report</param>
    /// <returns>Complete audit results</returns>
    public AuditResult RunAudit(string deviceDataJson, string? clientName = null)
        => RunAudit(deviceDataJson, clients: null, fingerprintDb: null, settingsData: null, firewallPoliciesData: null, clientName);

    /// <summary>
    /// Run a comprehensive audit on UniFi device data with client data for enhanced detection
    /// </summary>
    /// <param name="deviceDataJson">JSON string containing UniFi device data from /stat/device API</param>
    /// <param name="clients">Connected clients for device type detection (optional)</param>
    /// <param name="clientName">Optional client/site name for the report</param>
    /// <returns>Complete audit results</returns>
    public AuditResult RunAudit(string deviceDataJson, List<UniFiClientResponse>? clients, string? clientName = null)
        => RunAudit(deviceDataJson, clients, fingerprintDb: null, settingsData: null, firewallPoliciesData: null, clientName);

    /// <summary>
    /// Run a comprehensive audit on UniFi device data with client data and fingerprint database for enhanced detection
    /// </summary>
    /// <param name="deviceDataJson">JSON string containing UniFi device data from /stat/device API</param>
    /// <param name="clients">Connected clients for device type detection (optional)</param>
    /// <param name="fingerprintDb">UniFi fingerprint database for device name lookups (optional)</param>
    /// <param name="clientName">Optional client/site name for the report</param>
    /// <returns>Complete audit results</returns>
    public AuditResult RunAudit(string deviceDataJson, List<UniFiClientResponse>? clients, UniFiFingerprintDatabase? fingerprintDb, string? clientName = null)
        => RunAudit(deviceDataJson, clients, fingerprintDb, settingsData: null, firewallPoliciesData: null, clientName);

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
    public AuditResult RunAudit(
        string deviceDataJson,
        List<UniFiClientResponse>? clients,
        UniFiFingerprintDatabase? fingerprintDb,
        JsonElement? settingsData,
        JsonElement? firewallPoliciesData,
        string? clientName = null)
    {
        _logger.LogInformation("Starting network configuration audit for {Client}", clientName ?? "Unknown");
        if (clients != null)
        {
            _logger.LogInformation("Client data available for enhanced detection: {ClientCount} clients", clients.Count);
        }
        if (fingerprintDb != null)
        {
            _logger.LogInformation("Fingerprint database available: {DeviceCount} devices", fingerprintDb.DevIds.Count);
        }

        // Use a security engine with fingerprint database if available
        var securityEngine = _securityEngine;
        if (fingerprintDb != null)
        {
            var detectionService = new DeviceTypeDetectionService(
                _loggerFactory.CreateLogger<DeviceTypeDetectionService>(),
                fingerprintDb);
            securityEngine = new SecurityAuditEngine(
                _loggerFactory.CreateLogger<SecurityAuditEngine>(),
                detectionService);
        }

        // Parse JSON
        var deviceData = JsonDocument.Parse(deviceDataJson).RootElement;

        // Extract network topology
        _logger.LogInformation("Phase 1: Extracting network topology");
        var networks = _vlanAnalyzer.ExtractNetworks(deviceData);
        _logger.LogInformation("Found {NetworkCount} networks", networks.Count);

        // Extract switches and ports (with client correlation for detection)
        _logger.LogInformation("Phase 2: Extracting switch configurations");
        var switches = securityEngine.ExtractSwitches(deviceData, networks, clients);
        _logger.LogInformation("Found {SwitchCount} switches with {PortCount} total ports",
            switches.Count, switches.Sum(s => s.Ports.Count));

        // Run security analysis on ports
        _logger.LogInformation("Phase 3: Analyzing port security");
        var portIssues = securityEngine.AnalyzePorts(switches, networks);
        _logger.LogInformation("Found {IssueCount} port security issues", portIssues.Count);

        // Extract and analyze wireless clients
        _logger.LogInformation("Phase 3b: Analyzing wireless clients");
        var apLookup = securityEngine.ExtractAccessPointLookup(deviceData);
        var wirelessClients = securityEngine.ExtractWirelessClients(clients, networks, apLookup);
        var wirelessIssues = securityEngine.AnalyzeWirelessClients(wirelessClients, networks);
        _logger.LogInformation("Found {IssueCount} wireless client issues from {ClientCount} detected devices",
            wirelessIssues.Count, wirelessClients.Count);

        // Analyze network configuration
        _logger.LogInformation("Phase 4: Analyzing network configuration");
        var dnsIssues = _vlanAnalyzer.AnalyzeDnsConfiguration(networks);
        var gatewayIssues = _vlanAnalyzer.AnalyzeGatewayConfiguration(networks);
        var gatewayName = switches.FirstOrDefault(s => s.IsGateway)?.Name ?? "Gateway";
        var mgmtDhcpIssues = _vlanAnalyzer.AnalyzeManagementVlanDhcp(networks, gatewayName);
        _logger.LogInformation("Found {DnsIssues} DNS issues, {GatewayIssues} gateway issues, {MgmtIssues} management VLAN issues",
            dnsIssues.Count, gatewayIssues.Count, mgmtDhcpIssues.Count);

        // Extract and analyze firewall rules
        _logger.LogInformation("Phase 5: Analyzing firewall rules");
        var firewallRules = _firewallAnalyzer.ExtractFirewallRules(deviceData);
        var firewallIssues = firewallRules.Any()
            ? _firewallAnalyzer.AnalyzeFirewallRules(firewallRules, networks)
            : new List<AuditIssue>();
        _logger.LogInformation("Found {IssueCount} firewall issues", firewallIssues.Count);

        // Analyze DNS security (DoH configuration and firewall rules for DNS leak prevention)
        _logger.LogInformation("Phase 5b: Analyzing DNS security");
        DnsSecurityResult? dnsSecurityResult = null;
        var dnsSecurityIssues = new List<AuditIssue>();
        var dnsHardeningNotes = new List<string>();

        if (settingsData.HasValue || firewallPoliciesData.HasValue)
        {
            dnsSecurityResult = _dnsAnalyzer.Analyze(settingsData, firewallPoliciesData);
            dnsSecurityIssues = dnsSecurityResult.Issues;
            dnsHardeningNotes = dnsSecurityResult.HardeningNotes;
            _logger.LogInformation("Found {IssueCount} DNS security issues", dnsSecurityIssues.Count);
        }
        else
        {
            _logger.LogDebug("Skipping DNS security analysis - no settings or firewall policy data provided");
        }

        // Combine all issues
        var allIssues = new List<AuditIssue>();
        allIssues.AddRange(portIssues);
        allIssues.AddRange(wirelessIssues);
        allIssues.AddRange(dnsIssues);
        allIssues.AddRange(gatewayIssues);
        allIssues.AddRange(mgmtDhcpIssues);
        allIssues.AddRange(firewallIssues);
        allIssues.AddRange(dnsSecurityIssues);

        // Analyze hardening measures
        _logger.LogInformation("Phase 6: Analyzing hardening measures");
        var hardeningMeasures = securityEngine.AnalyzeHardening(switches, networks);
        hardeningMeasures.AddRange(dnsHardeningNotes);
        _logger.LogInformation("Found {MeasureCount} hardening measures in place", hardeningMeasures.Count);

        // Calculate statistics
        var statistics = securityEngine.CalculateStatistics(switches);

        // Build DNS security info from analyzer result
        DnsSecurityInfo? dnsSecurityInfo = null;
        if (dnsSecurityResult != null)
        {
            var providerNames = dnsSecurityResult.ConfiguredServers
                .Where(s => s.Enabled)
                .Select(s => s.StampInfo?.ProviderInfo?.Name ?? s.Provider?.Name ?? s.ServerName)
                .Distinct()
                .ToList();

            dnsSecurityInfo = new DnsSecurityInfo
            {
                DohEnabled = dnsSecurityResult.DohConfigured,
                DohState = dnsSecurityResult.DohState,
                DohProviders = providerNames,
                DnsLeakProtection = dnsSecurityResult.HasDns53BlockRule,
                DotBlocked = dnsSecurityResult.HasDotBlockRule,
                DohBypassBlocked = dnsSecurityResult.HasDohBlockRule
            };
        }

        // Build audit result
        var auditResult = new AuditResult
        {
            Timestamp = DateTime.UtcNow,
            ClientName = clientName,
            Networks = networks,
            Switches = switches,
            WirelessClients = wirelessClients,
            Issues = allIssues,
            HardeningMeasures = hardeningMeasures,
            Statistics = statistics,
            DnsSecurity = dnsSecurityInfo
        };

        // Calculate security score
        _logger.LogInformation("Phase 7: Calculating security score");
        var score = _scorer.CalculateScore(auditResult);
        var posture = _scorer.DeterminePosture(score, auditResult.CriticalIssues.Count);

        auditResult.SecurityScore = score;
        auditResult.Posture = posture;

        _logger.LogInformation("Audit complete: {Posture} (Score: {Score}/100, {Critical} critical, {Recommended} recommended)",
            posture, score, auditResult.CriticalIssues.Count, auditResult.RecommendedIssues.Count);

        return auditResult;
    }

    /// <summary>
    /// Run audit from a JSON file
    /// </summary>
    public AuditResult RunAuditFromFile(string jsonFilePath, string? clientName = null)
    {
        _logger.LogInformation("Loading device data from {FilePath}", jsonFilePath);

        if (!File.Exists(jsonFilePath))
        {
            throw new FileNotFoundException($"Device data file not found: {jsonFilePath}");
        }

        var json = File.ReadAllText(jsonFilePath);
        return RunAudit(json, clientName);
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
            report.AppendLine($"{deviceType} {sw.Name} ({sw.ModelName})");
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
}
