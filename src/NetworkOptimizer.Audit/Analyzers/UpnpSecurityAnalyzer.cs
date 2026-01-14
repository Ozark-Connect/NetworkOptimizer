using Microsoft.Extensions.Logging;
using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.Audit.Analyzers;

/// <summary>
/// Analyzes UPnP configuration and port forwarding rules for security issues.
/// UPnP allows devices to automatically open ports on the firewall, which can
/// be a security risk if enabled on non-Home networks.
/// </summary>
public class UpnpSecurityAnalyzer
{
    private readonly ILogger<UpnpSecurityAnalyzer> _logger;

    /// <summary>
    /// Port number threshold for privileged ports (0-1023 are system/privileged ports)
    /// </summary>
    private const int PrivilegedPortThreshold = 1024;

    public UpnpSecurityAnalyzer(ILogger<UpnpSecurityAnalyzer> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Analyze UPnP configuration and port forwarding rules.
    /// </summary>
    /// <param name="upnpEnabled">Whether UPnP is enabled on the gateway</param>
    /// <param name="portForwardRules">Port forwarding rules including UPnP mappings</param>
    /// <param name="networks">Network configurations for purpose checking</param>
    /// <param name="gatewayName">Gateway device name for issue reporting</param>
    /// <returns>List of audit issues found</returns>
    public UpnpAnalysisResult Analyze(
        bool? upnpEnabled,
        List<UniFiPortForwardRule>? portForwardRules,
        List<NetworkInfo> networks,
        string gatewayName = "Gateway")
    {
        var issues = new List<AuditIssue>();
        var hardeningNotes = new List<string>();

        // If we don't have UPnP data, skip analysis
        if (upnpEnabled == null)
        {
            _logger.LogDebug("UPnP status not available - skipping UPnP security analysis");
            return new UpnpAnalysisResult { Issues = issues, HardeningNotes = hardeningNotes };
        }

        var isEnabled = upnpEnabled.Value;
        var upnpRules = portForwardRules?.Where(r => r.IsUpnp == 1).ToList() ?? [];
        var upnpRuleCount = upnpRules.Count;

        _logger.LogInformation("Analyzing UPnP security: Enabled={Enabled}, UPnP rules={RuleCount}",
            isEnabled, upnpRuleCount);

        // Find Home network(s) - UPnP is acceptable on these
        var homeNetworks = networks.Where(n => n.Purpose == NetworkPurpose.Home).ToList();

        if (!isEnabled)
        {
            // UPnP disabled is a hardening measure
            hardeningNotes.Add("UPnP is disabled on the gateway");
            _logger.LogDebug("UPnP is disabled - no issues to report");
            return new UpnpAnalysisResult { Issues = issues, HardeningNotes = hardeningNotes };
        }

        // UPnP is enabled - analyze the configuration
        if (homeNetworks.Count == 0)
        {
            // No Home network found, UPnP on any network is a warning
            issues.Add(new AuditIssue
            {
                Type = IssueTypes.UpnpNonHomeNetwork,
                Severity = AuditSeverity.Recommended,
                Message = "UPnP is enabled but no Home network was detected",
                DeviceName = gatewayName,
                Metadata = new Dictionary<string, object>
                {
                    ["upnp_enabled"] = true,
                    ["upnp_rule_count"] = upnpRuleCount
                },
                RuleId = "UPNP-002",
                ScoreImpact = 5,
                RecommendedAction = "Disable UPnP or ensure it's only enabled for Home/Gaming networks"
            });
        }
        else
        {
            // Home network exists - UPnP on Home is acceptable, report as informational
            var homeNetworkNames = string.Join(", ", homeNetworks.Select(n => n.Name));
            issues.Add(new AuditIssue
            {
                Type = IssueTypes.UpnpEnabled,
                Severity = AuditSeverity.Informational,
                Message = $"UPnP is enabled for Home network ({homeNetworkNames})",
                DeviceName = gatewayName,
                CurrentNetwork = homeNetworkNames,
                Metadata = new Dictionary<string, object>
                {
                    ["upnp_enabled"] = true,
                    ["home_networks"] = homeNetworkNames,
                    ["upnp_rule_count"] = upnpRuleCount
                },
                RuleId = "UPNP-001",
                ScoreImpact = 0,
                RecommendedAction = "No action needed - UPnP is acceptable on Home networks for gaming and media"
            });
        }

        // Analyze UPnP rules for security concerns
        if (upnpRules.Count > 0)
        {
            AnalyzeUpnpRules(upnpRules, issues, gatewayName);
        }

        // Analyze static port forwards (informational - these are intentional)
        var staticRules = portForwardRules?.Where(r => r.IsUpnp != 1 && r.Enabled == true).ToList() ?? [];
        if (staticRules.Count > 0)
        {
            AnalyzeStaticPortForwards(staticRules, issues, gatewayName);
        }

        return new UpnpAnalysisResult { Issues = issues, HardeningNotes = hardeningNotes };
    }

    /// <summary>
    /// Report static port forwards as informational items.
    /// These are intentional configurations but worth documenting.
    /// </summary>
    private void AnalyzeStaticPortForwards(List<UniFiPortForwardRule> staticRules, List<AuditIssue> issues, string gatewayName)
    {
        var exposedPorts = staticRules
            .Where(r => !string.IsNullOrEmpty(r.DstPort))
            .Select(r => r.DstPort!)
            .ToList();

        if (exposedPorts.Count > 0)
        {
            issues.Add(new AuditIssue
            {
                Type = IssueTypes.StaticPortForward,
                Severity = AuditSeverity.Informational,
                Message = $"{staticRules.Count} static port forward(s) configured",
                DeviceName = gatewayName,
                Metadata = new Dictionary<string, object>
                {
                    ["static_forwards"] = staticRules.Select(r => new
                    {
                        name = r.Name ?? "Unnamed",
                        port = r.DstPort,
                        protocol = r.Proto,
                        target = r.Fwd
                    }).Take(10).ToList(),
                    ["count"] = staticRules.Count
                },
                RuleId = "UPNP-005",
                ScoreImpact = 0,
                RecommendedAction = "Review static port forwards periodically in the UPnP Inspector to ensure they are still needed"
            });
        }
    }

    /// <summary>
    /// Analyze individual UPnP rules for security concerns.
    /// </summary>
    private void AnalyzeUpnpRules(List<UniFiPortForwardRule> upnpRules, List<AuditIssue> issues, string gatewayName)
    {
        var privilegedPortRules = new List<(UniFiPortForwardRule Rule, int Port)>();
        var allExposedPorts = new List<string>();

        foreach (var rule in upnpRules)
        {
            var dstPort = rule.DstPort;
            if (string.IsNullOrEmpty(dstPort))
                continue;

            allExposedPorts.Add(dstPort);

            // Check for privileged ports (< 1024)
            var ports = ParsePorts(dstPort);
            foreach (var port in ports)
            {
                if (port < PrivilegedPortThreshold)
                {
                    privilegedPortRules.Add((rule, port));
                }
            }
        }

        // Report privileged port exposure as warning
        if (privilegedPortRules.Count > 0)
        {
            var portDetails = privilegedPortRules
                .Select(p => $"{p.Port} ({p.Rule.ApplicationName ?? p.Rule.Name ?? "Unknown"})")
                .Distinct()
                .ToList();

            issues.Add(new AuditIssue
            {
                Type = IssueTypes.UpnpPrivilegedPort,
                Severity = AuditSeverity.Recommended,
                Message = $"UPnP is exposing {privilegedPortRules.Count} privileged port(s) below 1024: {string.Join(", ", portDetails.Take(5))}{(portDetails.Count > 5 ? "..." : "")}",
                DeviceName = gatewayName,
                Metadata = new Dictionary<string, object>
                {
                    ["privileged_ports"] = portDetails,
                    ["count"] = privilegedPortRules.Count
                },
                RuleId = "UPNP-003",
                ScoreImpact = 8,
                RecommendedAction = "Review UPnP mappings - privileged ports are typically used by system services and should not be exposed via UPnP"
            });
        }
        // If no privileged ports, just report that ports are exposed as informational
        else if (allExposedPorts.Count > 0)
        {
            issues.Add(new AuditIssue
            {
                Type = IssueTypes.UpnpPortsExposed,
                Severity = AuditSeverity.Informational,
                Message = $"UPnP has {allExposedPorts.Count} active port mapping(s)",
                DeviceName = gatewayName,
                Metadata = new Dictionary<string, object>
                {
                    ["exposed_ports"] = allExposedPorts.Take(10).ToList(),
                    ["count"] = allExposedPorts.Count
                },
                RuleId = "UPNP-004",
                ScoreImpact = 0,
                RecommendedAction = "Review UPnP mappings periodically in the UPnP Inspector to ensure only expected applications are opening ports"
            });
        }
    }

    /// <summary>
    /// Parse port specification into individual port numbers.
    /// Handles formats: "80", "80-100", "80,443,8080"
    /// </summary>
    private static List<int> ParsePorts(string portSpec)
    {
        var ports = new List<int>();

        if (string.IsNullOrEmpty(portSpec))
            return ports;

        // Handle comma-separated ports
        var parts = portSpec.Split(',', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            var trimmed = part.Trim();

            // Handle port range (e.g., "80-100")
            if (trimmed.Contains('-'))
            {
                var rangeParts = trimmed.Split('-');
                if (rangeParts.Length == 2 &&
                    int.TryParse(rangeParts[0].Trim(), out var start) &&
                    int.TryParse(rangeParts[1].Trim(), out var end))
                {
                    for (int i = start; i <= end && i < start + 100; i++) // Limit range expansion
                    {
                        ports.Add(i);
                    }
                }
            }
            else if (int.TryParse(trimmed, out var port))
            {
                ports.Add(port);
            }
        }

        return ports;
    }
}

/// <summary>
/// Result of UPnP security analysis
/// </summary>
public class UpnpAnalysisResult
{
    /// <summary>
    /// Security issues found
    /// </summary>
    public List<AuditIssue> Issues { get; init; } = [];

    /// <summary>
    /// Hardening notes (positive security measures)
    /// </summary>
    public List<string> HardeningNotes { get; init; } = [];
}
