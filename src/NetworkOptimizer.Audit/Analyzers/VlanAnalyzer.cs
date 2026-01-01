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
    // Note: "work" removed - it matches "network" which causes false positives
    private static readonly string[] CorporatePatterns = { "corporate", "office", "business", "enterprise" };

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
        var networkIsolationEnabled = network.GetBoolOrDefault("network_isolation_enabled");
        var internetAccessEnabled = network.GetBoolOrDefault("internet_access_enabled");
        var purpose = ClassifyNetwork(name, purposeStr, vlanId, dhcpEnabled);

        _logger.LogDebug("Network '{Name}' classified as: {Purpose}, DHCP: {DhcpEnabled}, Isolated: {Isolated}, Internet: {Internet}",
            name, purpose, dhcpEnabled, networkIsolationEnabled, internetAccessEnabled);

        var rawSubnet = network.GetStringOrNull("ip_subnet");

        // Gateway IP can come from explicit field or be extracted from ip_subnet
        // UniFi stores ip_subnet as "192.168.1.1/24" where the IP is the gateway
        var gateway = network.GetStringFromAny("gateway_ip", "dhcpd_gateway");
        if (string.IsNullOrEmpty(gateway) && !string.IsNullOrEmpty(rawSubnet))
        {
            // Extract gateway IP from ip_subnet (the IP before the /)
            var slashIndex = rawSubnet.IndexOf('/');
            if (slashIndex > 0)
            {
                gateway = rawSubnet[..slashIndex];
                _logger.LogDebug("Extracted gateway {Gateway} from ip_subnet for network '{Name}'", gateway, name);
            }
        }

        return new NetworkInfo
        {
            Id = networkId,
            Name = name,
            VlanId = vlanId,
            Purpose = purpose,
            Subnet = NormalizeSubnet(rawSubnet),
            Gateway = gateway,
            DnsServers = network.GetStringArrayOrNull("dhcpd_dns"),
            DhcpEnabled = dhcpEnabled,
            NetworkIsolationEnabled = networkIsolationEnabled,
            InternetAccessEnabled = internetAccessEnabled
        };
    }

    /// <summary>
    /// Normalize subnet to use the network address instead of a host address.
    /// Converts "192.168.1.1/24" to "192.168.1.0/24"
    /// </summary>
    private static string? NormalizeSubnet(string? subnet)
    {
        if (string.IsNullOrEmpty(subnet))
            return null;

        var parts = subnet.Split('/');
        if (parts.Length != 2 || !int.TryParse(parts[1], out var cidr))
            return subnet;

        var ipParts = parts[0].Split('.');
        if (ipParts.Length != 4)
            return subnet;

        // Parse IP octets
        if (!ipParts.All(p => byte.TryParse(p, out _)))
            return subnet;

        var octets = ipParts.Select(byte.Parse).ToArray();

        // Calculate network address based on CIDR
        // For /24 we zero the last octet, for /16 we zero last 2, etc.
        var hostBits = 32 - cidr;
        var mask = hostBits >= 32 ? 0u : ~((1u << hostBits) - 1);

        var ip = ((uint)octets[0] << 24) | ((uint)octets[1] << 16) | ((uint)octets[2] << 8) | octets[3];
        var network = ip & mask;

        var networkOctets = new[]
        {
            (byte)((network >> 24) & 0xFF),
            (byte)((network >> 16) & 0xFF),
            (byte)((network >> 8) & 0xFF),
            (byte)(network & 0xFF)
        };

        return $"{networkOctets[0]}.{networkOctets[1]}.{networkOctets[2]}.{networkOctets[3]}/{cidr}";
    }

    /// <summary>
    /// Classify a network based on its name and purpose
    /// </summary>
    public NetworkPurpose ClassifyNetwork(string networkName, string? purpose = null, int? vlanId = null, bool? dhcpEnabled = null)
    {
        // Check explicit UniFi "guest" purpose first (UniFi marks guest networks specially)
        if (!string.IsNullOrEmpty(purpose) && purpose.Equals("guest", StringComparison.OrdinalIgnoreCase))
        {
            return NetworkPurpose.Guest;
        }

        // Check name patterns - these take priority over UniFi's generic "corporate" purpose
        // Order matters: more specific patterns first

        // Security first to avoid false positives with "Security Devices" matching IoT
        if (SecurityPatterns.Any(p => networkName.Contains(p, StringComparison.OrdinalIgnoreCase)))
            return NetworkPurpose.Security;

        if (IoTPatterns.Any(p => networkName.Contains(p, StringComparison.OrdinalIgnoreCase)))
            return NetworkPurpose.IoT;

        if (ManagementPatterns.Any(p => networkName.Contains(p, StringComparison.OrdinalIgnoreCase)))
            return NetworkPurpose.Management;

        if (GuestPatterns.Any(p => networkName.Contains(p, StringComparison.OrdinalIgnoreCase)))
            return NetworkPurpose.Guest;

        // Check for corporate patterns
        if (CorporatePatterns.Any(p => networkName.Contains(p, StringComparison.OrdinalIgnoreCase)))
            return NetworkPurpose.Corporate;

        // Check for home/residential patterns
        if (HomePatterns.Any(p => networkName.Contains(p, StringComparison.OrdinalIgnoreCase)))
            return NetworkPurpose.Home;

        // For VLAN 1 (native) with DHCP enabled and no specific keywords, assume Home
        // This is the most common case for residential networks
        if (vlanId == 1 && dhcpEnabled == true)
            return NetworkPurpose.Home;

        // Fallback: if name is "default" or "lan", treat as Home
        if (networkName.Equals("default", StringComparison.OrdinalIgnoreCase) ||
            networkName.Equals("lan", StringComparison.OrdinalIgnoreCase))
            return NetworkPurpose.Home;

        // Log unclassified networks for debugging and pattern improvement
        _logger.LogDebug("Network '{NetworkName}' (VLAN {VlanId}) could not be classified - consider adding a matching pattern",
            networkName, vlanId);
        return NetworkPurpose.Unknown;
    }

    /// <summary>
    /// Check if a network name suggests IoT usage
    /// </summary>
    public bool IsIoTNetwork(string? networkName)
    {
        if (string.IsNullOrEmpty(networkName))
            return false;

        return IoTPatterns.Any(p => networkName.Contains(p, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Check if a network name suggests security/camera usage
    /// </summary>
    public bool IsSecurityNetwork(string? networkName)
    {
        if (string.IsNullOrEmpty(networkName))
            return false;

        return SecurityPatterns.Any(p => networkName.Contains(p, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Check if a network name suggests management usage
    /// </summary>
    public bool IsManagementNetwork(string? networkName)
    {
        if (string.IsNullOrEmpty(networkName))
            return false;

        return ManagementPatterns.Any(p => networkName.Contains(p, StringComparison.OrdinalIgnoreCase));
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
                        Type = IssueTypes.DnsLeakage,
                        Severity = AuditSeverity.Informational,
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
                    Type = IssueTypes.RoutingEnabled,
                    Severity = AuditSeverity.Informational,
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
                    Type = IssueTypes.MgmtDhcpEnabled,
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

    /// <summary>
    /// Analyze network isolation configuration.
    /// Security, Management, and IoT networks should have network isolation enabled.
    /// </summary>
    public List<AuditIssue> AnalyzeNetworkIsolation(List<NetworkInfo> networks, string gatewayName = "Gateway")
    {
        var issues = new List<AuditIssue>();

        foreach (var network in networks)
        {
            // Skip native VLAN - it's the default network and usually doesn't need isolation
            if (network.IsNative)
                continue;

            // Check Security/Camera networks
            if (network.Purpose == NetworkPurpose.Security && !network.NetworkIsolationEnabled)
            {
                issues.Add(new AuditIssue
                {
                    Type = IssueTypes.SecurityNetworkNotIsolated,
                    Severity = AuditSeverity.Critical,
                    Message = $"Security/Camera VLAN '{network.Name}' is not isolated",
                    DeviceName = gatewayName,
                    CurrentNetwork = network.Name,
                    CurrentVlan = network.VlanId,
                    Metadata = new Dictionary<string, object>
                    {
                        { "network", network.Name },
                        { "vlan", network.VlanId },
                        { "network_isolation_enabled", network.NetworkIsolationEnabled }
                    },
                    RuleId = "NET-ISO-001",
                    ScoreImpact = 15,
                    RecommendedAction = "Enable network isolation to prevent cameras from accessing other network segments"
                });
            }

            // Check Management networks
            if (network.Purpose == NetworkPurpose.Management && !network.NetworkIsolationEnabled)
            {
                issues.Add(new AuditIssue
                {
                    Type = IssueTypes.MgmtNetworkNotIsolated,
                    Severity = AuditSeverity.Critical,
                    Message = $"Management VLAN '{network.Name}' is not isolated",
                    DeviceName = gatewayName,
                    CurrentNetwork = network.Name,
                    CurrentVlan = network.VlanId,
                    Metadata = new Dictionary<string, object>
                    {
                        { "network", network.Name },
                        { "vlan", network.VlanId },
                        { "network_isolation_enabled", network.NetworkIsolationEnabled }
                    },
                    RuleId = "NET-ISO-002",
                    ScoreImpact = 15,
                    RecommendedAction = "Enable network isolation to protect management infrastructure"
                });
            }

            // Check IoT networks
            if (network.Purpose == NetworkPurpose.IoT && !network.NetworkIsolationEnabled)
            {
                issues.Add(new AuditIssue
                {
                    Type = IssueTypes.IotNetworkNotIsolated,
                    Severity = AuditSeverity.Recommended,
                    Message = $"IoT VLAN '{network.Name}' is not isolated",
                    DeviceName = gatewayName,
                    CurrentNetwork = network.Name,
                    CurrentVlan = network.VlanId,
                    Metadata = new Dictionary<string, object>
                    {
                        { "network", network.Name },
                        { "vlan", network.VlanId },
                        { "network_isolation_enabled", network.NetworkIsolationEnabled }
                    },
                    RuleId = "NET-ISO-003",
                    ScoreImpact = 10,
                    RecommendedAction = "Enable network isolation to contain potentially insecure IoT devices"
                });
            }
        }

        return issues;
    }

    /// <summary>
    /// Analyze internet access configuration.
    /// Security/Camera and Management networks should not have internet access enabled.
    /// </summary>
    public List<AuditIssue> AnalyzeInternetAccess(List<NetworkInfo> networks, string gatewayName = "Gateway")
    {
        var issues = new List<AuditIssue>();

        foreach (var network in networks)
        {
            // Skip native VLAN
            if (network.IsNative)
                continue;

            // Check Security/Camera networks - should NOT have internet access
            if (network.Purpose == NetworkPurpose.Security && network.InternetAccessEnabled)
            {
                issues.Add(new AuditIssue
                {
                    Type = IssueTypes.SecurityNetworkHasInternet,
                    Severity = AuditSeverity.Critical,
                    Message = $"Security/Camera VLAN '{network.Name}' has internet access enabled",
                    DeviceName = gatewayName,
                    CurrentNetwork = network.Name,
                    CurrentVlan = network.VlanId,
                    Metadata = new Dictionary<string, object>
                    {
                        { "network", network.Name },
                        { "vlan", network.VlanId },
                        { "internet_access_enabled", network.InternetAccessEnabled }
                    },
                    RuleId = "NET-INT-001",
                    ScoreImpact = 15,
                    RecommendedAction = "Disable internet access to prevent cameras from phoning home to unknown servers"
                });
            }

            // Check Management networks - should NOT have internet access (with exceptions for UniFi cloud)
            if (network.Purpose == NetworkPurpose.Management && network.InternetAccessEnabled)
            {
                issues.Add(new AuditIssue
                {
                    Type = IssueTypes.MgmtNetworkHasInternet,
                    Severity = AuditSeverity.Recommended,
                    Message = $"Management VLAN '{network.Name}' has internet access enabled",
                    DeviceName = gatewayName,
                    CurrentNetwork = network.Name,
                    CurrentVlan = network.VlanId,
                    Metadata = new Dictionary<string, object>
                    {
                        { "network", network.Name },
                        { "vlan", network.VlanId },
                        { "internet_access_enabled", network.InternetAccessEnabled }
                    },
                    RuleId = "NET-INT-002",
                    ScoreImpact = 5,
                    RecommendedAction = "Consider disabling internet access and using firewall rules to allow specific traffic (UniFi cloud, AFC, etc.)"
                });
            }
        }

        return issues;
    }
}
