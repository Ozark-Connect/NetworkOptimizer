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

    /// <summary>
    /// Maximum number of ports to expand from a port range to prevent excessive memory usage
    /// </summary>
    private const int MaxPortRangeExpansion = 100;

    /// <summary>
    /// Well-known ports and their service names for reporting
    /// </summary>
    private static readonly Dictionary<int, string> WellKnownPorts = new()
    {
        [20] = "FTP Data",
        [21] = "FTP",
        [22] = "SSH",
        [23] = "Telnet",
        [25] = "SMTP",
        [53] = "DNS",
        [67] = "DHCP Server",
        [68] = "DHCP Client",
        [69] = "TFTP",
        [80] = "HTTP",
        [110] = "POP3",
        [119] = "NNTP",
        [123] = "NTP",
        [135] = "MS RPC",
        [137] = "NetBIOS Name",
        [138] = "NetBIOS Datagram",
        [139] = "NetBIOS Session",
        [143] = "IMAP",
        [161] = "SNMP",
        [162] = "SNMP Trap",
        [389] = "LDAP",
        [443] = "HTTPS",
        [445] = "SMB",
        [465] = "SMTPS",
        [514] = "Syslog",
        [515] = "LPD Print",
        [587] = "SMTP Submission",
        [636] = "LDAPS",
        [993] = "IMAPS",
        [995] = "POP3S"
    };

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

        // Find networks with UPnP explicitly enabled (per-network binding)
        // Include disabled networks since they can still have UPnP bindings configured
        var networksWithUpnp = networks.Where(n => n.UpnpLanEnabled).ToList();
        var homeNetworksWithUpnp = networksWithUpnp.Where(n => n.Purpose == NetworkPurpose.Home).ToList();
        var nonHomeNetworksWithUpnp = networksWithUpnp.Where(n => n.Purpose != NetworkPurpose.Home).ToList();

        _logger.LogInformation("Analyzing UPnP security: GlobalEnabled={Enabled}, UPnP rules={RuleCount}, Networks with UPnP={NetworkCount} (Home={HomeCount}, Non-Home={NonHomeCount})",
            isEnabled, upnpRuleCount, networksWithUpnp.Count, homeNetworksWithUpnp.Count, nonHomeNetworksWithUpnp.Count);

        if (!isEnabled)
        {
            // UPnP disabled globally is a hardening measure
            hardeningNotes.Add("UPnP is disabled on the gateway");
            _logger.LogDebug("UPnP is disabled - checking static port forwards only");
        }
        else
        {
            // Check for UPnP enabled on non-Home networks (Critical - highest priority)
            if (nonHomeNetworksWithUpnp.Count > 0)
            {
                var nonHomeNetworkNames = string.Join(", ", nonHomeNetworksWithUpnp.Select(n => $"{n.Name} ({n.Purpose.ToDisplayString()})"));
                issues.Add(new AuditIssue
                {
                    Type = IssueTypes.UpnpNonHomeNetwork,
                    Severity = AuditSeverity.Critical,
                    Message = $"UPnP is enabled on non-Home network(s): {nonHomeNetworkNames}",
                    DeviceName = gatewayName,
                    Metadata = new Dictionary<string, object>
                    {
                        ["upnp_enabled"] = true,
                        ["non_home_networks"] = nonHomeNetworksWithUpnp.Select(n => n.Name).ToList(),
                        ["upnp_rule_count"] = upnpRuleCount
                    },
                    RuleId = "UPNP-002",
                    ScoreImpact = 15,
                    RecommendedAction = "Disable UPnP on non-Home networks. UPnP allows devices to automatically open firewall ports, which is dangerous on IoT, Guest, or other restricted networks."
                });
            }

            // Check for UPnP enabled on Home networks
            if (homeNetworksWithUpnp.Count > 0)
            {
                var homeNetworkNames = string.Join(", ", homeNetworksWithUpnp.Select(n => n.Name));

                // Single Home network with UPnP is acceptable (Informational)
                // Multiple Home networks with UPnP should be consolidated (Recommended)
                var isSingleHomeNetwork = homeNetworksWithUpnp.Count == 1;
                var severity = isSingleHomeNetwork ? AuditSeverity.Informational : AuditSeverity.Recommended;
                var scoreImpact = isSingleHomeNetwork ? 0 : 5;
                var message = isSingleHomeNetwork
                    ? $"UPnP is enabled on Home network: {homeNetworkNames}"
                    : $"UPnP is enabled on {homeNetworksWithUpnp.Count} Home networks: {homeNetworkNames}";
                var recommendation = isSingleHomeNetwork
                    ? "UPnP on a dedicated Home/Gaming network is acceptable for gaming and screen streaming"
                    : "Consider enabling UPnP on only one dedicated Home/Gaming VLAN rather than multiple networks";

                issues.Add(new AuditIssue
                {
                    Type = IssueTypes.UpnpEnabled,
                    Severity = severity,
                    Message = message,
                    DeviceName = gatewayName,
                    CurrentNetwork = homeNetworkNames,
                    Metadata = new Dictionary<string, object>
                    {
                        ["upnp_enabled"] = true,
                        ["home_networks"] = homeNetworkNames,
                        ["home_network_count"] = homeNetworksWithUpnp.Count,
                        ["upnp_rule_count"] = upnpRuleCount
                    },
                    RuleId = "UPNP-001",
                    ScoreImpact = scoreImpact,
                    RecommendedAction = recommendation
                });
            }

            // If UPnP is globally enabled but no networks have it bound, note this edge case
            if (networksWithUpnp.Count == 0)
            {
                _logger.LogDebug("UPnP is globally enabled but not bound to any networks");
                hardeningNotes.Add("UPnP is enabled but not bound to any networks");
            }
        }

        // Analyze UPnP rules for security concerns (only when UPnP is enabled)
        if (isEnabled && upnpRules.Count > 0)
        {
            AnalyzeUpnpRules(upnpRules, issues, gatewayName);
        }

        // Analyze static port forwards regardless of UPnP status
        // hasHomeNetwork check is used for static port forwards to determine warning severity
        var hasHomeNetwork = networks.Any(n => n.Purpose == NetworkPurpose.Home);
        var staticRules = portForwardRules?.Where(r => r.IsUpnp != 1 && r.Enabled == true).ToList() ?? [];
        if (staticRules.Count > 0)
        {
            AnalyzeStaticPortForwards(staticRules, issues, gatewayName, hasHomeNetwork);
        }

        return new UpnpAnalysisResult { Issues = issues, HardeningNotes = hardeningNotes };
    }

    /// <summary>
    /// Report static port forwards, highlighting privileged ports.
    /// These are intentional configurations but worth documenting.
    /// Privileged ports without source IP restrictions on Home networks are upgraded to warnings.
    /// </summary>
    private void AnalyzeStaticPortForwards(List<UniFiPortForwardRule> staticRules, List<AuditIssue> issues, string gatewayName, bool hasHomeNetwork)
    {
        var privilegedPortRules = new List<(UniFiPortForwardRule Rule, int Port)>();
        var nonPrivilegedRules = new List<UniFiPortForwardRule>();
        var privilegedPortsCovered = new HashSet<int>();

        foreach (var rule in staticRules)
        {
            var dstPort = rule.DstPort;
            if (string.IsNullOrEmpty(dstPort))
                continue;

            var ports = ParsePorts(dstPort);
            var hasPrivileged = false;

            foreach (var port in ports)
            {
                if (port < PrivilegedPortThreshold)
                {
                    privilegedPortRules.Add((rule, port));
                    privilegedPortsCovered.Add(port);
                    hasPrivileged = true;
                }
            }

            // Track rules that only have non-privileged ports
            if (!hasPrivileged)
            {
                nonPrivilegedRules.Add(rule);
            }
        }

        // Report privileged port exposure with service names
        if (privilegedPortRules.Count > 0)
        {
            var portDetails = privilegedPortRules
                .Select(p => WellKnownPorts.TryGetValue(p.Port, out var service)
                    ? $"{p.Port}/{service} ({p.Rule.Name ?? "Unnamed"})"
                    : $"{p.Port} ({p.Rule.Name ?? "Unnamed"})")
                .Distinct()
                .ToList();

            // Check if any privileged port rules lack source IP restrictions
            // A rule is restricted only if src_limiting_enabled is true AND either:
            // - src_limiting_type is "firewall_group" with a valid src_firewall_group_id, OR
            // - src_limiting_type is "ip" with a valid src value
            var unrestrictedRules = privilegedPortRules
                .Where(p => !IsSourceRestricted(p.Rule))
                .Select(p => p.Rule)
                .Distinct()
                .ToList();

            // Upgrade to warning if on Home network with unrestricted privileged ports
            var isUnrestricted = unrestrictedRules.Count > 0 && hasHomeNetwork;
            var severity = isUnrestricted ? AuditSeverity.Recommended : AuditSeverity.Informational;
            var scoreImpact = isUnrestricted ? 8 : 0;
            var recommendation = isUnrestricted
                ? "Edit the Port Forwarding Policy and set 'From' to 'Limited' to restrict which source IPs can access these ports"
                : "Ensure these privileged ports are intentionally exposed and properly secured";

            issues.Add(new AuditIssue
            {
                Type = IssueTypes.StaticPrivilegedPort,
                Severity = severity,
                Message = $"Static port forward(s) exposing {privilegedPortsCovered.Count} privileged port(s): {string.Join(", ", portDetails.Take(5))}{(portDetails.Count > 5 ? "..." : "")}",
                DeviceName = gatewayName,
                Metadata = new Dictionary<string, object>
                {
                    ["privileged_ports"] = portDetails,
                    ["count"] = privilegedPortsCovered.Count,
                    ["unrestricted"] = isUnrestricted,
                    ["unrestricted_count"] = unrestrictedRules.Count
                },
                RuleId = "UPNP-006",
                ScoreImpact = scoreImpact,
                RecommendedAction = recommendation
            });
        }

        // Only report generic static forwards if there are non-privileged ports not already covered
        if (nonPrivilegedRules.Count > 0)
        {
            issues.Add(new AuditIssue
            {
                Type = IssueTypes.StaticPortForward,
                Severity = AuditSeverity.Informational,
                Message = $"{nonPrivilegedRules.Count} static port forward(s) on non-privileged ports",
                DeviceName = gatewayName,
                Metadata = new Dictionary<string, object>
                {
                    ["static_forwards"] = nonPrivilegedRules.Select(r => new
                    {
                        name = r.Name ?? "Unnamed",
                        port = r.DstPort,
                        protocol = r.Proto,
                        target = r.Fwd
                    }).Take(10).ToList(),
                    ["count"] = nonPrivilegedRules.Count
                },
                RuleId = "UPNP-005",
                ScoreImpact = 0,
                RecommendedAction = "Review static port forwards periodically in the UPnP Inspector to ensure they are still needed."
            });
        }
    }

    /// <summary>
    /// Analyze individual UPnP rules for security concerns.
    /// </summary>
    private void AnalyzeUpnpRules(List<UniFiPortForwardRule> upnpRules, List<AuditIssue> issues, string gatewayName)
    {
        var privilegedPortRules = new List<(UniFiPortForwardRule Rule, int Port)>();
        var nonPrivilegedRules = new List<UniFiPortForwardRule>();

        foreach (var rule in upnpRules)
        {
            var dstPort = rule.DstPort;
            if (string.IsNullOrEmpty(dstPort))
                continue;

            // Check for privileged ports (< 1024)
            var ports = ParsePorts(dstPort);
            var hasPrivileged = false;

            foreach (var port in ports)
            {
                if (port < PrivilegedPortThreshold)
                {
                    privilegedPortRules.Add((rule, port));
                    hasPrivileged = true;
                }
            }

            // Track rules that only have non-privileged ports
            if (!hasPrivileged)
            {
                nonPrivilegedRules.Add(rule);
            }
        }

        // Report privileged port exposure as warning with service names
        if (privilegedPortRules.Count > 0)
        {
            var portDetails = privilegedPortRules
                .Select(p => WellKnownPorts.TryGetValue(p.Port, out var service)
                    ? $"{p.Port}/{service} ({p.Rule.ApplicationName ?? p.Rule.Name ?? "Unknown"})"
                    : $"{p.Port} ({p.Rule.ApplicationName ?? p.Rule.Name ?? "Unknown"})")
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
                RecommendedAction = "Review UPnP mappings - privileged ports are typically used by system services and should not be exposed via UPnP."
            });
        }

        // Only report generic exposed ports if there are non-privileged ports not covered by the warning
        if (nonPrivilegedRules.Count > 0)
        {
            issues.Add(new AuditIssue
            {
                Type = IssueTypes.UpnpPortsExposed,
                Severity = AuditSeverity.Informational,
                Message = $"UPnP has {nonPrivilegedRules.Count} active port mapping(s) on non-privileged ports",
                DeviceName = gatewayName,
                Metadata = new Dictionary<string, object>
                {
                    ["exposed_ports"] = nonPrivilegedRules.Select(r => r.DstPort).Take(10).ToList(),
                    ["count"] = nonPrivilegedRules.Count
                },
                RuleId = "UPNP-004",
                ScoreImpact = 0,
                RecommendedAction = "Review UPnP mappings periodically in the UPnP Inspector to ensure only expected applications are opening ports."
            });
        }
    }

    /// <summary>
    /// Parse port specification into individual port numbers.
    /// Handles formats: "80", "80-100", "80,443,8080"
    /// </summary>
    private List<int> ParsePorts(string portSpec)
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
                    int rangeSize = end - start + 1;
                    if (rangeSize > MaxPortRangeExpansion)
                    {
                        _logger.LogWarning(
                            "Port range {Start}-{End} ({Size} ports) truncated to first {Max} ports for analysis",
                            start, end, rangeSize, MaxPortRangeExpansion);
                    }

                    for (int i = start; i <= end && i < start + MaxPortRangeExpansion; i++)
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

    /// <summary>
    /// Check if a port forward rule has source IP/firewall group restrictions enabled and configured.
    /// A rule is restricted only if:
    /// - src_limiting_enabled is true, AND
    /// - Either: src_limiting_type is "firewall_group" with a valid src_firewall_group_id,
    ///   OR src_limiting_type is "ip" with a valid src value (IP, CIDR, or range)
    /// </summary>
    private static bool IsSourceRestricted(UniFiPortForwardRule rule)
    {
        // Source limiting must be explicitly enabled
        if (rule.SrcLimitingEnabled != true)
            return false;

        // Check based on limiting type
        return rule.SrcLimitingType switch
        {
            "firewall_group" => !string.IsNullOrEmpty(rule.SrcFirewallGroupId),
            "ip" => !string.IsNullOrEmpty(rule.Src),
            _ => false
        };
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
