using System.Text.Json;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.Audit.Services;
using NetworkOptimizer.Core.Helpers;
using NetworkOptimizer.UniFi.Models;
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
    // Entertainment patterns (streaming, theater, etc.) classify as IoT - isolated but internet-enabled
    private static readonly string[] IoTPatterns = { "iot", "smart", "automation", "zero trust", "entertainment", "streaming", "theater", "theatre", "recreation", "living room", "a/v" };
    // IoT patterns requiring word boundary matching (to avoid "Dave" matching "av", etc.)
    private static readonly string[] IoTWordBoundaryPatterns = { "media", "av", "tv" };
    private static readonly string[] SecurityPatterns = { "camera", "security", "nvr", "surveillance", "protect", "cctv" };
    // Patterns that require word boundary matching (to avoid false positives like "Hotspot" matching "not")
    private static readonly string[] SecurityWordBoundaryPatterns = { "not" }; // "NoT" = "Network of Things"
    private static readonly string[] ManagementPatterns = { "management", "mgmt", "admin", "infrastructure" };
    private static readonly string[] GuestPatterns = { "guest", "visitor", "hotspot" };
    // Gaming patterns classify as Home - game consoles need UPnP and full network access
    private static readonly string[] HomePatterns = { "home", "main", "primary", "personal", "family", "trusted", "private", "gaming", "gamer", "games", "xbox", "playstation", "nintendo", "console", "lan party" };
    // Home patterns requiring word boundary matching (to avoid "GameChanger" matching "game")
    private static readonly string[] HomeWordBoundaryPatterns = { "game" };
    private static readonly string[] CorporatePatterns = { "corporate", "office", "business", "enterprise", "warehouse" };
    // Word boundary patterns for Corporate (to avoid "network" matching "work")
    private static readonly string[] CorporateWordBoundaryPatterns = { "work", "biz", "branch", "shop", "staff", "employee", "hq", "store" };
    private static readonly string[] PrinterPatterns = { "print" };
    // DMZ patterns - fallback name-based detection (zone-based is primary)
    private static readonly string[] DmzPatterns = { "dmz" };
    private static readonly string[] ServerPatterns = { "server", "datacenter", "data center", "hypervisor", "hosting" };
    // Server patterns requiring word boundary matching (to avoid "domain controller" matching "domain" in other contexts)
    private static readonly string[] ServerWordBoundaryPatterns = { "compute", "data", "domain", "vm", "lab", "services", "controllers", "rack", "cluster", "backend", "virtual" };

    public VlanAnalyzer(ILogger<VlanAnalyzer> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Extract network map from UniFi device JSON data
    /// </summary>
    public List<NetworkInfo> ExtractNetworks(JsonElement deviceData, FirewallZoneLookup? zoneLookup = null)
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
                var networkInfo = ParseNetwork(network, zoneLookup);
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
                    AllowsRouting = vlan1Network.AllowsRouting,
                    DhcpEnabled = vlan1Network.DhcpEnabled,
                    NetworkIsolationEnabled = vlan1Network.NetworkIsolationEnabled,
                    InternetAccessEnabled = vlan1Network.InternetAccessEnabled,
                    IsUniFiGuestNetwork = vlan1Network.IsUniFiGuestNetwork,
                    FirewallZoneId = vlan1Network.FirewallZoneId,
                    NetworkGroup = vlan1Network.NetworkGroup,
                    UpnpLanEnabled = vlan1Network.UpnpLanEnabled,
                    Enabled = vlan1Network.Enabled
                };
            }
        }

        return networks;
    }

    /// <summary>
    /// Apply user purpose overrides to a list of networks.
    /// Rebuilds NetworkInfo objects (since Purpose is init-only) and sets HasPurposeOverride = true.
    /// </summary>
    public void ApplyPurposeOverrides(List<NetworkInfo> networks, Dictionary<string, string>? overrides)
    {
        if (overrides is not { Count: > 0 })
            return;

        for (var i = 0; i < networks.Count; i++)
        {
            var network = networks[i];
            if (overrides.TryGetValue(network.Id, out var purposeStr) &&
                Enum.TryParse<NetworkPurpose>(purposeStr, ignoreCase: true, out var purpose))
            {
                var oldPurpose = network.Purpose;
                networks[i] = new NetworkInfo
                {
                    Id = network.Id,
                    Name = network.Name,
                    VlanId = network.VlanId,
                    Purpose = purpose,
                    Subnet = network.Subnet,
                    Gateway = network.Gateway,
                    DnsServers = network.DnsServers,
                    AllowsRouting = network.AllowsRouting,
                    DhcpEnabled = network.DhcpEnabled,
                    NetworkIsolationEnabled = network.NetworkIsolationEnabled,
                    InternetAccessEnabled = network.InternetAccessEnabled,
                    IsUniFiGuestNetwork = network.IsUniFiGuestNetwork,
                    FirewallZoneId = network.FirewallZoneId,
                    NetworkGroup = network.NetworkGroup,
                    UpnpLanEnabled = network.UpnpLanEnabled,
                    Enabled = network.Enabled,
                    HasPurposeOverride = true
                };
                if (oldPurpose != purpose)
                {
                    _logger.LogInformation("Applied user override: Network '{Name}' ({Id}) purpose changed from {OldPurpose} to {NewPurpose}",
                        network.Name, network.Id, oldPurpose, purpose);
                }
            }
        }
    }

    /// <summary>
    /// Parse a single network from JSON
    /// </summary>
    private NetworkInfo? ParseNetwork(JsonElement network, FirewallZoneLookup? zoneLookup = null)
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
        var upnpLanEnabled = network.GetBoolOrDefault("upnp_lan_enabled");
        var networkEnabled = network.GetBoolOrDefault("enabled", true); // Defaults to true if not specified
        var firewallZoneId = network.GetStringOrNull("firewall_zone_id");
        // Network group: "LAN" for internal networks, "WAN"/"WAN2" for external
        var networkGroup = network.GetStringFromAny("networkgroup", "wan_networkgroup");

        // Check if this is an official UniFi Guest network (has implicit isolation at switch/AP level)
        var isUniFiGuestNetwork = purposeStr?.Equals("guest", StringComparison.OrdinalIgnoreCase) == true
            || network.GetBoolOrDefault("is_guest");

        var purpose = ClassifyNetwork(name, purposeStr, vlanId, dhcpEnabled, networkIsolationEnabled, internetAccessEnabled, firewallZoneId, zoneLookup);

        _logger.LogDebug("Network '{Name}' classified as: {Purpose}, DHCP: {DhcpEnabled}, Isolated: {Isolated}, Internet: {Internet}, UniFiGuest: {UniFiGuest}, ZoneId: {ZoneId}",
            name, purpose, dhcpEnabled, networkIsolationEnabled, internetAccessEnabled, isUniFiGuestNetwork, firewallZoneId);

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

        // DNS servers are in separate fields: dhcpd_dns_1, dhcpd_dns_2, dhcpd_dns_3, dhcpd_dns_4
        var dnsServers = ExtractDnsServers(network);
        if (dnsServers.Count > 0)
        {
            _logger.LogDebug("Network '{Name}' has DNS servers: {DnsServers}", name, string.Join(", ", dnsServers));
        }

        return new NetworkInfo
        {
            Id = networkId,
            Name = name,
            VlanId = vlanId,
            Purpose = purpose,
            Subnet = NormalizeSubnet(rawSubnet),
            Gateway = gateway,
            DnsServers = dnsServers.Count > 0 ? dnsServers : null,
            DhcpEnabled = dhcpEnabled,
            NetworkIsolationEnabled = networkIsolationEnabled,
            InternetAccessEnabled = internetAccessEnabled,
            IsUniFiGuestNetwork = isUniFiGuestNetwork,
            FirewallZoneId = firewallZoneId,
            NetworkGroup = networkGroup,
            UpnpLanEnabled = upnpLanEnabled,
            Enabled = networkEnabled
        };
    }

    /// <summary>
    /// Extract DNS servers from network config.
    /// UniFi stores these in separate fields: dhcpd_dns_1, dhcpd_dns_2, dhcpd_dns_3, dhcpd_dns_4
    /// Only returns DNS servers if dhcpd_dns_enabled is true (custom DNS configured).
    /// When dhcpd_dns_enabled is false, the network uses gateway DNS and these fields are ignored.
    /// </summary>
    private static List<string> ExtractDnsServers(JsonElement network)
    {
        var dnsServers = new List<string>();

        // Check if custom DNS is enabled - if not, network uses gateway DNS
        if (!network.GetBoolOrDefault("dhcpd_dns_enabled", false))
        {
            return dnsServers;
        }

        for (int i = 1; i <= 4; i++)
        {
            var dns = network.GetStringOrNull($"dhcpd_dns_{i}");
            if (!string.IsNullOrEmpty(dns))
            {
                dnsServers.Add(dns);
            }
        }

        return dnsServers;
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
    /// Classify a network based on its firewall zone, name, purpose, and UniFi configuration flags.
    /// Priority: 1) Firewall zone (authoritative), 2) UniFi purpose field, 3) Name patterns, 4) Flag-based adjustments.
    /// </summary>
    public NetworkPurpose ClassifyNetwork(string networkName, string? purpose = null, int? vlanId = null,
        bool? dhcpEnabled = null, bool? networkIsolationEnabled = null, bool? internetAccessEnabled = null,
        string? firewallZoneId = null, FirewallZoneLookup? zoneLookup = null)
    {
        // Step 0: Zone-based classification (authoritative - highest priority)
        if (zoneLookup?.HasZoneData == true && !string.IsNullOrEmpty(firewallZoneId))
        {
            if (zoneLookup.IsDmzZone(firewallZoneId))
            {
                _logger.LogDebug("Network '{NetworkName}' classified as DMZ based on firewall zone", networkName);
                return NetworkPurpose.Dmz;
            }
            if (zoneLookup.IsHotspotZone(firewallZoneId))
            {
                _logger.LogDebug("Network '{NetworkName}' classified as Guest based on Hotspot firewall zone", networkName);
                return NetworkPurpose.Guest;
            }
        }

        // Check explicit UniFi "guest" purpose (UniFi marks guest networks specially)
        if (!string.IsNullOrEmpty(purpose) && purpose.Equals("guest", StringComparison.OrdinalIgnoreCase))
        {
            return NetworkPurpose.Guest;
        }

        // Step 1: Name-based classification
        // Order matters: more specific patterns first
        NetworkPurpose nameBasedPurpose;

        // Security first to avoid false positives with "Security Devices" matching IoT
        if (SecurityPatterns.Any(p => networkName.Contains(p, StringComparison.OrdinalIgnoreCase)))
            nameBasedPurpose = NetworkPurpose.Security;
        // Word-boundary patterns for Security (e.g., "NoT" should not match "Hotspot")
        else if (SecurityWordBoundaryPatterns.Any(p => ContainsWord(networkName, p)))
            nameBasedPurpose = NetworkPurpose.Security;
        // DMZ networks - isolated zone with internet but restricted LAN access
        else if (DmzPatterns.Any(p => networkName.Contains(p, StringComparison.OrdinalIgnoreCase)))
            nameBasedPurpose = NetworkPurpose.Dmz;
        // Printer networks before IoT (more specific)
        else if (PrinterPatterns.Any(p => networkName.Contains(p, StringComparison.OrdinalIgnoreCase)))
            nameBasedPurpose = NetworkPurpose.Printer;
        else if (IoTPatterns.Any(p => networkName.Contains(p, StringComparison.OrdinalIgnoreCase)))
            nameBasedPurpose = NetworkPurpose.IoT;
        // Word-boundary patterns for IoT (e.g., "Media Room" but not "Dave")
        else if (IoTWordBoundaryPatterns.Any(p => ContainsWord(networkName, p)))
            nameBasedPurpose = NetworkPurpose.IoT;
        else if (ManagementPatterns.Any(p => networkName.Contains(p, StringComparison.OrdinalIgnoreCase)))
            nameBasedPurpose = NetworkPurpose.Management;
        else if (ServerPatterns.Any(p => networkName.Contains(p, StringComparison.OrdinalIgnoreCase)))
            nameBasedPurpose = NetworkPurpose.Server;
        // Word-boundary patterns for Server (e.g., "VM VLAN" but not "ViewModel")
        else if (ServerWordBoundaryPatterns.Any(p => ContainsWord(networkName, p)))
            nameBasedPurpose = NetworkPurpose.Server;
        else if (GuestPatterns.Any(p => networkName.Contains(p, StringComparison.OrdinalIgnoreCase)))
            nameBasedPurpose = NetworkPurpose.Guest;
        else if (CorporatePatterns.Any(p => networkName.Contains(p, StringComparison.OrdinalIgnoreCase)))
            nameBasedPurpose = NetworkPurpose.Corporate;
        // Word-boundary patterns for Corporate (e.g., "Work Devices" but not "Network")
        else if (CorporateWordBoundaryPatterns.Any(p => ContainsWord(networkName, p)))
            nameBasedPurpose = NetworkPurpose.Corporate;
        else if (HomePatterns.Any(p => networkName.Contains(p, StringComparison.OrdinalIgnoreCase)))
            nameBasedPurpose = NetworkPurpose.Home;
        // Word-boundary patterns for Home (e.g., "Game Room" but not "GameChanger")
        else if (HomeWordBoundaryPatterns.Any(p => ContainsWord(networkName, p)))
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
                    // VLAN 1 is special - it's UniFi's default/native VLAN used for device adoption
                    if (vlanId == 1)
                    {
                        _logger.LogDebug("Network '{NetworkName}' on VLAN 1 has unusual flags - classifying as Management (UniFi default VLAN)",
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
    /// Check if a network name suggests IoT usage (includes entertainment/media networks)
    /// </summary>
    public bool IsIoTNetwork(string? networkName)
    {
        if (string.IsNullOrEmpty(networkName))
            return false;

        return IoTPatterns.Any(p => networkName.Contains(p, StringComparison.OrdinalIgnoreCase))
            || IoTWordBoundaryPatterns.Any(p => ContainsWord(networkName, p));
    }

    /// <summary>
    /// Check if a network name suggests home/gaming usage
    /// </summary>
    public bool IsHomeNetwork(string? networkName)
    {
        if (string.IsNullOrEmpty(networkName))
            return false;

        return HomePatterns.Any(p => networkName.Contains(p, StringComparison.OrdinalIgnoreCase))
            || HomeWordBoundaryPatterns.Any(p => ContainsWord(networkName, p));
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
    /// Find the first printer network in the list
    /// </summary>
    public NetworkInfo? FindPrinterNetwork(List<NetworkInfo> networks)
    {
        return networks.FirstOrDefault(n => n.Purpose == NetworkPurpose.Printer);
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
                        Type = IssueTypes.DnsSharedServers,
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
    /// Analyze management VLAN DHCP configuration.
    /// DHCP is fine on management VLANs as long as all clients have fixed IP (DHCP reservation) assignments.
    /// Only flags networks where clients exist without fixed IPs.
    /// </summary>
    public List<AuditIssue> AnalyzeManagementVlanDhcp(
        List<NetworkInfo> networks,
        List<UniFiClientResponse>? clients,
        string gatewayName = "Gateway")
    {
        var issues = new List<AuditIssue>();

        // Find management networks (native VLAN included if classified or overridden as Management)
        var managementNetworks = networks.Where(n =>
            n.Purpose == NetworkPurpose.Management).ToList();

        foreach (var network in managementNetworks)
        {
            if (!network.DhcpEnabled)
                continue;

            // Find clients on this management network
            var networkClients = clients?.Where(c =>
                c.EffectiveNetworkId == network.Id).ToList() ?? [];

            // No clients on network - nothing to flag
            if (networkClients.Count == 0)
                continue;

            var clientsWithoutFixedIp = networkClients
                .Where(c => !c.UseFixedIp)
                .ToList();

            // All clients have fixed IPs - DHCP with full reservations is fine
            if (clientsWithoutFixedIp.Count == 0)
                continue;

            var deviceNames = clientsWithoutFixedIp
                .Select(c => !string.IsNullOrEmpty(c.Name) ? c.Name :
                             !string.IsNullOrEmpty(c.Hostname) ? c.Hostname : c.Mac)
                .ToList();

            var deviceList = string.Join(", ", deviceNames.Take(5));
            if (deviceNames.Count > 5)
                deviceList += $" (+{deviceNames.Count - 5} more)";

            issues.Add(new AuditIssue
            {
                Type = IssueTypes.MgmtNoFixedIps,
                Severity = AuditSeverity.Recommended,
                Message = $"Management VLAN '{network.Name}' has {clientsWithoutFixedIp.Count} of {networkClients.Count} device(s) without DHCP reservations: {deviceList}",
                DeviceName = gatewayName,
                CurrentNetwork = network.Name,
                CurrentVlan = network.VlanId,
                Metadata = new Dictionary<string, object>
                {
                    { "network", network.Name },
                    { "vlan", network.VlanId },
                    { "totalClients", networkClients.Count },
                    { "clientsWithoutFixedIp", clientsWithoutFixedIp.Count },
                    { "devicesWithoutFixedIp", deviceNames }
                },
                RuleId = "MGMT-DHCP-001",
                ScoreImpact = 3,
                RecommendedAction = "Configure DHCP reservations (fixed IPs) for all management devices in the UniFi client settings."
            });
        }

        return issues;
    }

    /// <summary>
    /// Analyze network isolation configuration.
    /// Security, Management, and IoT networks should have network isolation enabled,
    /// or have an equivalent firewall rule blocking outbound to other internal networks.
    /// </summary>
    public List<AuditIssue> AnalyzeNetworkIsolation(
        List<NetworkInfo> networks,
        string gatewayName = "Gateway",
        List<FirewallRule>? firewallRules = null,
        FirewallZoneLookup? zoneLookup = null)
    {
        var issues = new List<AuditIssue>();

        foreach (var network in networks)
        {
            // Skip native VLAN unless it's classified as a purpose that needs isolation
            if (network.IsNative && !network.HasPurposeOverride
                && network.Purpose != NetworkPurpose.Management)
                continue;

            // Check if network is effectively isolated (via setting or firewall rule)
            var isEffectivelyIsolated = network.NetworkIsolationEnabled ||
                IsIsolatedViaFirewall(network, networks, firewallRules);

            // Check Security/Camera networks
            if (network.Purpose == NetworkPurpose.Security && !isEffectivelyIsolated)
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
                    RecommendedAction = "Enable network isolation to prevent cameras from accessing other network segments. If incorrect, set a different Purpose for the network in Network Reference below."
                });
            }

            // Check Management networks
            if (network.Purpose == NetworkPurpose.Management && !isEffectivelyIsolated)
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
                    RecommendedAction = network.VlanId == 1
                        ? "Add inbound/outbound inter-VLAN blocking Firewall Rules to protect management infrastructure. If incorrect, set a different Purpose for the network in Network Reference below."
                        : "Enable Isolate Network or add inbound/outbound inter-VLAN blocking Firewall Rules to protect management infrastructure. If incorrect, set a different Purpose for the network in Network Reference below."
                });
            }

            // Check IoT networks
            if (network.Purpose == NetworkPurpose.IoT && !isEffectivelyIsolated)
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
                    RecommendedAction = "Enable network isolation to contain potentially insecure IoT devices. If incorrect, set a different Purpose for the network in Network Reference below."
                });
            }
        }

        return issues;
    }

    /// <summary>
    /// Check if a network is isolated via firewall rules.
    /// A network is considered isolated if one or more firewall rules collectively block it
    /// from reaching all other internal networks (outbound isolation).
    /// Supports both network-based rules and zone-based rules (e.g., custom zone → Internal zone).
    /// </summary>
    private bool IsIsolatedViaFirewall(
        NetworkInfo network,
        List<NetworkInfo> allNetworks,
        List<FirewallRule>? firewallRules)
    {
        if (firewallRules == null || firewallRules.Count == 0)
            return false;

        var otherNetworks = allNetworks.Where(n => n.Id != network.Id).ToList();

        // Collect all enabled block rules that apply to this network as source
        // and block all protocols/ports (not port-specific)
        var qualifyingBlockRules = new List<FirewallRule>();
        foreach (var rule in firewallRules)
        {
            if (!rule.Enabled)
                continue;
            if (!rule.ActionType.IsBlockAction())
                continue;
            if (!rule.AppliesToSourceNetwork(network))
                continue;

            // Must block all protocols
            if (!string.IsNullOrEmpty(rule.Protocol) &&
                !rule.Protocol.Equals("all", StringComparison.OrdinalIgnoreCase))
                continue;

            // Must not be port-specific
            if (!string.IsNullOrEmpty(rule.SourcePort) || !string.IsNullOrEmpty(rule.DestinationPort))
                continue;

            qualifyingBlockRules.Add(rule);
        }

        if (qualifyingBlockRules.Count == 0)
            return false;

        // Check if collectively these rules block traffic to ALL other networks
        var allBlocked = otherNetworks.All(otherNet =>
            qualifyingBlockRules.Any(rule => RuleBlocksToNetwork(rule, otherNet)));

        if (allBlocked)
        {
            _logger.LogDebug(
                "Network '{NetworkName}' is isolated via {Count} firewall rule(s)",
                network.Name, qualifyingBlockRules.Count);
        }

        return allBlocked;
    }

    /// <summary>
    /// Check if a single firewall rule blocks traffic to a specific destination network.
    /// Considers destination zone scoping, network ID matching (with Match Opposite), and IP/CIDR coverage.
    /// </summary>
    private static bool RuleBlocksToNetwork(FirewallRule rule, NetworkInfo targetNetwork)
    {
        // If rule specifies a destination zone and target has a zone, they must match.
        // A zone-scoped rule only blocks traffic to networks within that zone.
        if (!string.IsNullOrEmpty(rule.DestinationZoneId) && !string.IsNullOrEmpty(targetNetwork.FirewallZoneId))
        {
            if (!string.Equals(rule.DestinationZoneId, targetNetwork.FirewallZoneId, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        var destTarget = rule.DestinationMatchingTarget?.ToUpperInvariant();

        // ANY destination blocks to everything (within the destination zone, if specified)
        if (destTarget == "ANY" || string.IsNullOrEmpty(destTarget))
            return true;

        // NETWORK destination - check if target network is in the block list
        if (destTarget == "NETWORK")
        {
            var destNetworkIds = rule.DestinationNetworkIds ?? [];
            var isInList = destNetworkIds.Contains(targetNetwork.Id, StringComparer.OrdinalIgnoreCase);

            // Match Opposite: "block to all networks EXCEPT those in the list"
            return rule.DestinationMatchOppositeNetworks ? !isInList : isInList;
        }

        // IP destination - check if CIDRs cover the target network's subnet
        if (destTarget == "IP" && rule.DestinationIps?.Count > 0 && !string.IsNullOrEmpty(targetNetwork.Subnet))
        {
            return NetworkUtilities.AnyCidrCoversSubnet(rule.DestinationIps, targetNetwork.Subnet);
        }

        return false;
    }

    /// <summary>
    /// Analyze internet access configuration.
    /// Security/Camera and Management networks should not have internet access enabled.
    /// </summary>
    /// <param name="networks">List of networks to analyze</param>
    /// <param name="gatewayName">Name of the gateway device</param>
    /// <param name="firewallRules">Optional firewall rules to check for internet-blocking rules</param>
    /// <param name="externalZoneId">The External/WAN firewall zone ID (pre-computed from network configs)</param>
    public List<AuditIssue> AnalyzeInternetAccess(
        List<NetworkInfo> networks,
        string gatewayName = "Gateway",
        List<FirewallRule>? firewallRules = null,
        string? externalZoneId = null,
        FirewallRuleAnalyzer? firewallAnalyzer = null)
    {
        var issues = new List<AuditIssue>();

        if (externalZoneId != null)
        {
            _logger.LogDebug("Using External Zone ID: {ExternalZoneId}", externalZoneId);
        }

        foreach (var network in networks)
        {
            // Skip native VLAN unless it's classified as a purpose that needs internet checks
            if (network.IsNative && !network.HasPurposeOverride
                && network.Purpose != NetworkPurpose.Management)
                continue;

            // Check if internet is effectively enabled (not disabled via setting OR firewall rule)
            var hasEffectiveInternetAccess = HasEffectiveInternetAccess(network, firewallRules, externalZoneId, firewallAnalyzer);

            // Check Security/Camera networks - should NOT have internet access
            if (network.Purpose == NetworkPurpose.Security && hasEffectiveInternetAccess)
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
                    RecommendedAction = "Disable internet access to prevent cameras from phoning home to unknown servers."
                });
            }

            // Check Management networks - should NOT have internet access (with exceptions for UniFi cloud)
            if (network.Purpose == NetworkPurpose.Management && hasEffectiveInternetAccess)
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
                    RecommendedAction = "Consider disabling internet access and using firewall rules to allow specific traffic (UniFi cloud, AFC, etc.)."
                });
            }
        }

        return issues;
    }

    /// <summary>
    /// Determine if a network has effective internet access.
    /// Internet is considered blocked if EITHER:
    /// 1. internet_access_enabled is false in network config, OR
    /// 2. A firewall rule blocks all traffic from this network to the External zone
    /// </summary>
    private bool HasEffectiveInternetAccess(
        NetworkInfo network,
        List<FirewallRule>? firewallRules,
        string? externalZoneId,
        FirewallRuleAnalyzer? firewallAnalyzer = null)
    {
        // If internet access is disabled in network config, it's blocked
        if (!network.InternetAccessEnabled)
        {
            _logger.LogDebug("Network '{Name}' has internet_access_enabled=false", network.Name);
            return false;
        }

        // If no firewall rules provided or no External zone detected, use the config setting
        if (firewallRules == null || firewallRules.Count == 0 || string.IsNullOrEmpty(externalZoneId))
        {
            return network.InternetAccessEnabled;
        }

        // Delegate to FirewallRuleAnalyzer which uses FirewallRuleEvaluator for correct
        // rule ordering and connection state checks (e.g., skipping INVALID-only rules)
        if (firewallAnalyzer != null)
        {
            var isBlockedByFirewall = firewallAnalyzer.IsInternetBlockedViaFirewall(network, firewallRules, externalZoneId);
            if (isBlockedByFirewall)
            {
                _logger.LogDebug("Network '{Name}' has internet blocked via firewall rule", network.Name);
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Analyze infrastructure device VLAN placement.
    /// Switches and APs should be on a Management VLAN, not on user/IoT networks.
    /// </summary>
    public List<AuditIssue> AnalyzeInfrastructureVlanPlacement(JsonElement deviceData, List<NetworkInfo> networks, string gatewayName = "Gateway")
    {
        var issues = new List<AuditIssue>();

        // Find management network
        var managementNetwork = networks.FirstOrDefault(n => n.Purpose == NetworkPurpose.Management);
        if (managementNetwork == null)
        {
            _logger.LogDebug("No Management network found - skipping infrastructure VLAN check");
            return issues;
        }

        foreach (var device in deviceData.UnwrapDataArray())
        {
            var deviceType = device.GetStringOrNull("type");
            if (string.IsNullOrEmpty(deviceType))
                continue;

            var parsedType = FromUniFiApiType(deviceType);

            // Skip gateways - they're typically on VLAN 1 by default and that's OK
            if (parsedType.IsGateway())
                continue;

            // Check all UniFi network infrastructure devices (switches, APs, cellular modems, building bridges, cloud keys)
            if (!parsedType.IsUniFiNetworkDevice())
                continue;

            var name = device.GetStringFromAny("name", "mac") ?? "Unknown Device";
            var ip = device.GetStringOrNull("ip");

            if (string.IsNullOrEmpty(ip))
            {
                _logger.LogDebug("Device {Name} has no IP address - skipping", name);
                continue;
            }

            // Find which network this device is on based on its IP
            var deviceNetwork = FindNetworkByIp(ip, networks);

            if (deviceNetwork == null)
            {
                _logger.LogDebug("Could not determine network for {Name} ({Ip})", name, ip);
                continue;
            }

            // Check if device is on Management network
            if (deviceNetwork.Purpose != NetworkPurpose.Management)
            {
                var deviceTypeLabel = parsedType.ToDisplayName();

                issues.Add(new AuditIssue
                {
                    Type = IssueTypes.InfraNotOnMgmt,
                    Severity = AuditSeverity.Critical,
                    Message = $"{deviceTypeLabel} '{name}' is on {deviceNetwork.Name} VLAN - should be on Management VLAN",
                    DeviceName = name,
                    CurrentNetwork = deviceNetwork.Name,
                    CurrentVlan = deviceNetwork.VlanId,
                    RecommendedNetwork = managementNetwork.Name,
                    RecommendedVlan = managementNetwork.VlanId,
                    Metadata = new Dictionary<string, object>
                    {
                        { "device_type", deviceTypeLabel },
                        { "device_ip", ip },
                        { "current_network_purpose", deviceNetwork.Purpose.ToString() }
                    },
                    RuleId = "INFRA-VLAN-001",
                    ScoreImpact = 10,
                    RecommendedAction = Rules.VlanPlacementChecker.GetMoveRecommendation($"{managementNetwork.Name} ({managementNetwork.VlanId})", includeReclassifyHint: false)
                        + " Network purposes can be reassigned below in Network Reference."
                });

                _logger.LogInformation("{DeviceType} '{Name}' on {Network} VLAN - should be on Management",
                    deviceTypeLabel, name, deviceNetwork.Name);
            }
        }

        return issues;
    }

    /// <summary>
    /// Find which network an IP address belongs to based on subnet matching.
    /// </summary>
    private NetworkInfo? FindNetworkByIp(string ip, List<NetworkInfo> networks)
    {
        if (!System.Net.IPAddress.TryParse(ip, out var ipAddress))
            return null;

        foreach (var network in networks)
        {
            if (string.IsNullOrEmpty(network.Subnet))
                continue;

            if (NetworkUtilities.IsIpInSubnet(ipAddress, network.Subnet))
                return network;
        }

        return null;
    }
}
