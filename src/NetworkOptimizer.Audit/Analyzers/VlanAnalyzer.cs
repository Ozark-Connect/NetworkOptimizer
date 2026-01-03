using System.Text.Json;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.Core.Helpers;
using static NetworkOptimizer.Core.Enums.DeviceTypeExtensions;

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
    // Patterns that require word boundary matching (to avoid false positives like "Hotspot" matching "not")
    private static readonly string[] SecurityWordBoundaryPatterns = { "not" }; // "NoT" = "Network of Things"
    private static readonly string[] ManagementPatterns = { "management", "mgmt", "admin", "infrastructure" };
    private static readonly string[] GuestPatterns = { "guest", "visitor", "hotspot" };
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
            var isGateway = FromUniFiApiType(deviceType).IsGateway();

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

        // Post-processing: if no Management network was found, designate VLAN 1 as Management
        // Enterprise networks typically use VLAN 1 as the native/management VLAN
        if (!networks.Any(n => n.Purpose == NetworkPurpose.Management))
        {
            var vlan1Network = networks.FirstOrDefault(n => n.VlanId == 1);
            if (vlan1Network != null && vlan1Network.Purpose == NetworkPurpose.Unknown)
            {
                _logger.LogInformation("No Management network found - designating VLAN 1 '{Name}' as Management", vlan1Network.Name);
                // NetworkInfo is immutable, so we need to replace it
                var index = networks.IndexOf(vlan1Network);
                networks[index] = new NetworkInfo
                {
                    Id = vlan1Network.Id,
                    Name = vlan1Network.Name,
                    VlanId = vlan1Network.VlanId,
                    Purpose = NetworkPurpose.Management,
                    Subnet = vlan1Network.Subnet,
                    Gateway = vlan1Network.Gateway,
                    DnsServers = vlan1Network.DnsServers,
                    DhcpEnabled = vlan1Network.DhcpEnabled,
                    NetworkIsolationEnabled = vlan1Network.NetworkIsolationEnabled,
                    InternetAccessEnabled = vlan1Network.InternetAccessEnabled
                };
            }
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
        var purpose = ClassifyNetwork(name, purposeStr, vlanId, dhcpEnabled, networkIsolationEnabled, internetAccessEnabled);

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
    /// Classify a network based on its name, purpose, and UniFi configuration flags.
    /// Uses name patterns as primary classification, then applies flag-based adjustments
    /// to catch misclassifications (e.g., "Home" network with no internet is suspicious).
    /// </summary>
    public NetworkPurpose ClassifyNetwork(string networkName, string? purpose = null, int? vlanId = null,
        bool? dhcpEnabled = null, bool? networkIsolationEnabled = null, bool? internetAccessEnabled = null)
    {
        // Check explicit UniFi "guest" purpose first (UniFi marks guest networks specially)
        if (!string.IsNullOrEmpty(purpose) && purpose.Equals("guest", StringComparison.OrdinalIgnoreCase))
        {
            return NetworkPurpose.Guest;
        }

        // Step 1: Name-based classification (primary)
        // Order matters: more specific patterns first
        NetworkPurpose nameBasedPurpose;

        // Security first to avoid false positives with "Security Devices" matching IoT
        if (SecurityPatterns.Any(p => networkName.Contains(p, StringComparison.OrdinalIgnoreCase)))
            nameBasedPurpose = NetworkPurpose.Security;
        // Word-boundary patterns for Security (e.g., "NoT" should not match "Hotspot")
        else if (SecurityWordBoundaryPatterns.Any(p => ContainsWord(networkName, p)))
            nameBasedPurpose = NetworkPurpose.Security;
        else if (IoTPatterns.Any(p => networkName.Contains(p, StringComparison.OrdinalIgnoreCase)))
            nameBasedPurpose = NetworkPurpose.IoT;
        else if (ManagementPatterns.Any(p => networkName.Contains(p, StringComparison.OrdinalIgnoreCase)))
            nameBasedPurpose = NetworkPurpose.Management;
        else if (GuestPatterns.Any(p => networkName.Contains(p, StringComparison.OrdinalIgnoreCase)))
            nameBasedPurpose = NetworkPurpose.Guest;
        else if (CorporatePatterns.Any(p => networkName.Contains(p, StringComparison.OrdinalIgnoreCase)))
            nameBasedPurpose = NetworkPurpose.Corporate;
        else if (HomePatterns.Any(p => networkName.Contains(p, StringComparison.OrdinalIgnoreCase)))
            nameBasedPurpose = NetworkPurpose.Home;
        // Fallback: if name starts with "default" or "main", or is exactly "lan", treat as Home
        else if (networkName.StartsWith("default", StringComparison.OrdinalIgnoreCase) ||
                 networkName.StartsWith("main", StringComparison.OrdinalIgnoreCase) ||
                 networkName.Equals("lan", StringComparison.OrdinalIgnoreCase))
            nameBasedPurpose = NetworkPurpose.Home;
        // For VLAN 1 (native) that doesn't match home/corporate patterns, assume Management
        else if (vlanId == 1)
            nameBasedPurpose = NetworkPurpose.Management;
        else
            nameBasedPurpose = NetworkPurpose.Unknown;

        // Step 2: Flag-based adjustments
        // Use UniFi's isolation and internet access flags to refine classification

        // Home/Corporate networks should have internet access
        // If they don't, the name-based classification is likely wrong
        if (nameBasedPurpose is NetworkPurpose.Home or NetworkPurpose.Corporate)
        {
            if (internetAccessEnabled == false)
            {
                // Network named like Home/Corporate but has no internet - suspicious
                if (networkIsolationEnabled == true)
                {
                    // VLAN 1 is special - it's the enterprise native/management VLAN
                    if (vlanId == 1)
                    {
                        _logger.LogDebug("Network '{NetworkName}' on VLAN 1 has unusual flags but respecting enterprise convention - classifying as Management",
                            networkName);
                        return NetworkPurpose.Management;
                    }

                    // Non-VLAN-1: Isolated + no internet = likely a security/camera VLAN
                    _logger.LogDebug("Network '{NetworkName}' matches Home/Corporate pattern but has no internet and is isolated - reclassifying as Security",
                        networkName);
                    return NetworkPurpose.Security;
                }
                else
                {
                    // No internet but not isolated - unusual config, can't determine
                    _logger.LogDebug("Network '{NetworkName}' matches Home/Corporate pattern but has no internet - reclassifying as Unknown",
                        networkName);
                    return NetworkPurpose.Unknown;
                }
            }
        }

        // For Unknown networks, use flags to infer purpose
        if (nameBasedPurpose == NetworkPurpose.Unknown)
        {
            if (networkIsolationEnabled == true)
            {
                if (internetAccessEnabled == false)
                {
                    // Isolated + no internet = likely security/camera VLAN
                    _logger.LogDebug("Network '{NetworkName}' is isolated with no internet - classifying as Security",
                        networkName);
                    return NetworkPurpose.Security;
                }
                else if (internetAccessEnabled == true)
                {
                    // Isolated + internet = likely IoT (needs internet for updates/cloud)
                    _logger.LogDebug("Network '{NetworkName}' is isolated with internet access - classifying as IoT",
                        networkName);
                    return NetworkPurpose.IoT;
                }
            }

            // Log unclassified networks for debugging and pattern improvement
            _logger.LogDebug("Network '{NetworkName}' (VLAN {VlanId}) could not be classified - consider adding a matching pattern",
                networkName, vlanId);
        }

        // Log when isolation confirms secure VLAN classification (positive indicator)
        if (nameBasedPurpose is NetworkPurpose.Security or NetworkPurpose.IoT or NetworkPurpose.Management)
        {
            if (networkIsolationEnabled == true)
            {
                _logger.LogDebug("Network '{NetworkName}' isolation setting confirms {Purpose} classification",
                    networkName, nameBasedPurpose);
            }
        }

        return nameBasedPurpose;
    }

    /// <summary>
    /// Check if a string contains a word with word boundaries (not as a substring).
    /// For example, "NoT" matches "NoT Network" but not "Hotspot".
    /// </summary>
    private static bool ContainsWord(string text, string word)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(word))
            return false;

        var textLower = text.ToLowerInvariant();
        var wordLower = word.ToLowerInvariant();
        var index = textLower.IndexOf(wordLower);

        while (index >= 0)
        {
            // Check if character before is a word boundary (start of string or non-letter)
            var beforeOk = index == 0 || !char.IsLetter(textLower[index - 1]);
            // Check if character after is a word boundary (end of string or non-letter)
            var afterIndex = index + wordLower.Length;
            var afterOk = afterIndex >= textLower.Length || !char.IsLetter(textLower[afterIndex]);

            if (beforeOk && afterOk)
                return true;

            // Look for next occurrence
            index = textLower.IndexOf(wordLower, index + 1);
        }

        return false;
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

        return SecurityPatterns.Any(p => networkName.Contains(p, StringComparison.OrdinalIgnoreCase))
            || SecurityWordBoundaryPatterns.Any(p => ContainsWord(networkName, p));
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
