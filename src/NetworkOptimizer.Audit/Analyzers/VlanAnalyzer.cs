using System.Text.Json;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.Core.Helpers;

namespace NetworkOptimizer.Audit.Analyzers;

/// <summary>
/// Analyzes network/VLAN configuration and builds network topology map
/// </summary>
public class VlanAnalyzer
{
    private readonly ILogger<VlanAnalyzer> _logger;

    // Network classification patterns (case-insensitive)
    // Note: "device" removed from IoT - too generic, causes false positives with "Security Devices"
    private static readonly string[] IoTPatterns = { "iot", "smart", "automation", "zero trust" };
    private static readonly string[] SecurityPatterns = { "camera", "security", "nvr", "surveillance", "protect" };
    private static readonly string[] ManagementPatterns = { "management", "mgmt", "admin", "infrastructure" };
    private static readonly string[] GuestPatterns = { "guest", "visitor" };
    private static readonly string[] HomePatterns = { "home", "main", "primary", "personal", "family", "trusted", "private" };
    private static readonly string[] CorporatePatterns = { "corporate", "office", "work", "business", "enterprise" };

    public VlanAnalyzer(ILogger<VlanAnalyzer> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Extract network map from UniFi device JSON data
    /// </summary>
    public List<NetworkInfo> ExtractNetworks(JsonElement deviceData)
    {
        var networks = new List<NetworkInfo>();

        foreach (var device in deviceData.UnwrapDataArray())
        {
            var deviceType = device.GetStringOrNull("type");
            if (deviceType == null)
                continue;
            var isGateway = UniFiDeviceTypes.IsGateway(deviceType);

            var networkTableItems = device.GetArrayOrEmpty("network_table").ToList();
            if (networkTableItems.Count == 0)
            {
                if (!isGateway)
                    continue;
            }

            _logger.LogInformation("Found network_table on {DeviceType} device", deviceType);

            foreach (var network in networkTableItems)
            {
                var networkInfo = ParseNetwork(network);
                if (networkInfo != null)
                {
                    networks.Add(networkInfo);
                    _logger.LogDebug("Discovered network: {Name} (VLAN {VlanId}, DHCP: {DhcpEnabled})",
                        networkInfo.Name, networkInfo.VlanId, networkInfo.DhcpEnabled);
                }
            }

            // Found network table, no need to check other devices
            if (networks.Any())
                break;
        }

        return networks;
    }

    /// <summary>
    /// Parse a single network from JSON
    /// </summary>
    private NetworkInfo? ParseNetwork(JsonElement network)
    {
        var networkId = network.GetStringFromAny("_id", "network_id");
        if (string.IsNullOrEmpty(networkId))
            return null;

        var name = network.GetStringOrDefault("name", "Unknown");
        var vlanId = network.GetIntOrDefault("vlan", network.GetIntOrDefault("vlan_id", 1));
        var purposeStr = network.GetStringOrNull("purpose");
        var dhcpEnabled = network.GetBoolOrDefault("dhcpd_enabled");
        var purpose = ClassifyNetwork(name, purposeStr, vlanId, dhcpEnabled);

        _logger.LogDebug("Network '{Name}' classified as: {Purpose}, DHCP: {DhcpEnabled}", name, purpose, dhcpEnabled);

        return new NetworkInfo
        {
            Id = networkId,
            Name = name,
            VlanId = vlanId,
            Purpose = purpose,
            Subnet = network.GetStringOrNull("ip_subnet"),
            Gateway = network.GetStringFromAny("gateway_ip", "dhcpd_gateway"),
            DnsServers = network.GetStringArrayOrNull("dhcpd_dns"),
            DhcpEnabled = dhcpEnabled
        };
    }

    /// <summary>
    /// Classify a network based on its name and purpose
    /// </summary>
    public NetworkPurpose ClassifyNetwork(string networkName, string? purpose = null, int? vlanId = null, bool? dhcpEnabled = null)
    {
        var nameLower = networkName.ToLowerInvariant();

        // Check explicit UniFi "guest" purpose first (UniFi marks guest networks specially)
        if (!string.IsNullOrEmpty(purpose) && purpose.Equals("guest", StringComparison.OrdinalIgnoreCase))
        {
            return NetworkPurpose.Guest;
        }

        // Check name patterns - these take priority over UniFi's generic "corporate" purpose
        // Order matters: more specific patterns first

        // Security first to avoid false positives with "Security Devices" matching IoT
        if (SecurityPatterns.Any(p => nameLower.Contains(p)))
            return NetworkPurpose.Security;

        if (IoTPatterns.Any(p => nameLower.Contains(p)))
            return NetworkPurpose.IoT;

        if (ManagementPatterns.Any(p => nameLower.Contains(p)))
            return NetworkPurpose.Management;

        if (GuestPatterns.Any(p => nameLower.Contains(p)))
            return NetworkPurpose.Guest;

        // Check for corporate patterns
        if (CorporatePatterns.Any(p => nameLower.Contains(p)))
            return NetworkPurpose.Corporate;

        // Check for home/residential patterns
        if (HomePatterns.Any(p => nameLower.Contains(p)))
            return NetworkPurpose.Home;

        // For VLAN 1 (native) with DHCP enabled and no specific keywords, assume Home
        // This is the most common case for residential networks
        if (vlanId == 1 && dhcpEnabled == true)
            return NetworkPurpose.Home;

        // Fallback: if name is "default" or "lan", treat as Home
        if (nameLower == "default" || nameLower == "lan")
            return NetworkPurpose.Home;

        return NetworkPurpose.Unknown;
    }

    /// <summary>
    /// Check if a network name suggests IoT usage
    /// </summary>
    public bool IsIoTNetwork(string? networkName)
    {
        if (string.IsNullOrEmpty(networkName))
            return false;

        var nameLower = networkName.ToLowerInvariant();
        return IoTPatterns.Any(p => nameLower.Contains(p));
    }

    /// <summary>
    /// Check if a network name suggests security/camera usage
    /// </summary>
    public bool IsSecurityNetwork(string? networkName)
    {
        if (string.IsNullOrEmpty(networkName))
            return false;

        var nameLower = networkName.ToLowerInvariant();
        return SecurityPatterns.Any(p => nameLower.Contains(p));
    }

    /// <summary>
    /// Check if a network name suggests management usage
    /// </summary>
    public bool IsManagementNetwork(string? networkName)
    {
        if (string.IsNullOrEmpty(networkName))
            return false;

        var nameLower = networkName.ToLowerInvariant();
        return ManagementPatterns.Any(p => nameLower.Contains(p));
    }

    /// <summary>
    /// Find the first IoT network in the list
    /// </summary>
    public NetworkInfo? FindIoTNetwork(List<NetworkInfo> networks)
    {
        return networks.FirstOrDefault(n => n.Purpose == NetworkPurpose.IoT);
    }

    /// <summary>
    /// Find the first security network in the list
    /// </summary>
    public NetworkInfo? FindSecurityNetwork(List<NetworkInfo> networks)
    {
        return networks.FirstOrDefault(n => n.Purpose == NetworkPurpose.Security);
    }

    /// <summary>
    /// Build a network display string like "Main (1)" or "Security (42)"
    /// </summary>
    public string GetNetworkDisplay(NetworkInfo network)
    {
        var vlanStr = network.IsNative ? $"{network.VlanId} (native)" : network.VlanId.ToString();
        return $"{network.Name} ({vlanStr})";
    }

    /// <summary>
    /// Analyze DNS configuration for potential leakage
    /// </summary>
    public List<AuditIssue> AnalyzeDnsConfiguration(List<NetworkInfo> networks)
    {
        var issues = new List<AuditIssue>();

        // Find networks that should be isolated but share DNS with corporate
        var corporateNetworks = networks.Where(n => n.Purpose == NetworkPurpose.Corporate).ToList();
        var isolatedNetworks = networks.Where(n =>
            n.Purpose is NetworkPurpose.IoT or NetworkPurpose.Guest or NetworkPurpose.Security).ToList();

        foreach (var isolated in isolatedNetworks)
        {
            if (isolated.DnsServers == null || !isolated.DnsServers.Any())
                continue;

            foreach (var corporate in corporateNetworks)
            {
                if (corporate.DnsServers == null || !corporate.DnsServers.Any())
                    continue;

                // Check if they share DNS servers
                var sharedDns = isolated.DnsServers.Intersect(corporate.DnsServers).ToList();
                if (sharedDns.Any())
                {
                    issues.Add(new AuditIssue
                    {
                        Type = "DNS_LEAKAGE",
                        Severity = AuditSeverity.Investigate,
                        Message = $"Network '{isolated.Name}' shares DNS servers with corporate network",
                        Metadata = new Dictionary<string, object>
                        {
                            { "isolated_network", isolated.Name },
                            { "corporate_network", corporate.Name },
                            { "shared_dns", sharedDns }
                        },
                        RuleId = "DNS-001",
                        ScoreImpact = 3
                    });
                }
            }
        }

        return issues;
    }

    /// <summary>
    /// Analyze gateway configuration for potential routing leakage
    /// </summary>
    public List<AuditIssue> AnalyzeGatewayConfiguration(List<NetworkInfo> networks)
    {
        var issues = new List<AuditIssue>();

        // Check if IoT/Guest networks have routing enabled
        var isolatedNetworks = networks.Where(n =>
            n.Purpose is NetworkPurpose.IoT or NetworkPurpose.Guest).ToList();

        foreach (var network in isolatedNetworks)
        {
            if (network.AllowsRouting)
            {
                issues.Add(new AuditIssue
                {
                    Type = "ROUTING_ENABLED",
                    Severity = AuditSeverity.Investigate,
                    Message = $"Isolated network '{network.Name}' has routing enabled - may allow cross-VLAN access",
                    Metadata = new Dictionary<string, object>
                    {
                        { "network", network.Name },
                        { "vlan", network.VlanId }
                    },
                    RuleId = "ROUTE-001",
                    ScoreImpact = 5
                });
            }
        }

        return issues;
    }

    /// <summary>
    /// Analyze management VLAN DHCP configuration
    /// Management VLANs (not VLAN 1) should have DHCP disabled
    /// </summary>
    public List<AuditIssue> AnalyzeManagementVlanDhcp(List<NetworkInfo> networks, string gatewayName = "Gateway")
    {
        var issues = new List<AuditIssue>();

        // Find management networks that are not the native VLAN
        var managementNetworks = networks.Where(n =>
            n.Purpose == NetworkPurpose.Management && !n.IsNative).ToList();

        foreach (var network in managementNetworks)
        {
            if (network.DhcpEnabled)
            {
                issues.Add(new AuditIssue
                {
                    Type = "MGMT_DHCP_ENABLED",
                    Severity = AuditSeverity.Recommended,
                    Message = $"Management VLAN '{network.Name}' has DHCP enabled",
                    DeviceName = gatewayName,
                    CurrentNetwork = network.Name,
                    CurrentVlan = network.VlanId,
                    Metadata = new Dictionary<string, object>
                    {
                        { "network", network.Name },
                        { "vlan", network.VlanId }
                    },
                    RuleId = "MGMT-DHCP-001",
                    ScoreImpact = 3,
                    RecommendedAction = "Disable DHCP and configure static IPs for management devices"
                });
            }
        }

        return issues;
    }
}
