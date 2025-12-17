using System.Text.Json;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Audit.Models;

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
    private static readonly string[] ManagementPatterns = { "management", "mgmt", "admin" };
    private static readonly string[] GuestPatterns = { "guest", "visitor" };

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

        // Handle both single device and array of devices
        var devices = deviceData.ValueKind == JsonValueKind.Array
            ? deviceData.EnumerateArray().ToList()
            : new List<JsonElement> { deviceData };

        // Handle wrapped response with "data" property
        if (deviceData.ValueKind == JsonValueKind.Object && deviceData.TryGetProperty("data", out var dataArray))
        {
            devices = dataArray.EnumerateArray().ToList();
        }

        foreach (var device in devices)
        {
            // Look for gateway/UDM device with network_table
            if (!device.TryGetProperty("type", out var typeElement))
                continue;

            var deviceType = typeElement.GetString();
            var isGateway = deviceType is "ugw" or "udm" or "uxg";

            // Check for network_table
            if (!device.TryGetProperty("network_table", out var networkTable))
            {
                // Gateway might not have network_table in some cases
                if (!isGateway)
                    continue;
            }

            if (networkTable.ValueKind != JsonValueKind.Array)
                continue;

            _logger.LogInformation("Found network_table on {DeviceType} device", deviceType);

            foreach (var network in networkTable.EnumerateArray())
            {
                var networkInfo = ParseNetwork(network);
                if (networkInfo != null)
                {
                    networks.Add(networkInfo);
                    _logger.LogDebug("Discovered network: {Name} (VLAN {VlanId})", networkInfo.Name, networkInfo.VlanId);
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
        // Get network ID
        var networkId = network.TryGetProperty("_id", out var idProp)
            ? idProp.GetString()
            : network.TryGetProperty("network_id", out var netIdProp)
                ? netIdProp.GetString()
                : null;

        if (string.IsNullOrEmpty(networkId))
            return null;

        // Get network name
        var name = network.TryGetProperty("name", out var nameProp)
            ? nameProp.GetString() ?? "Unknown"
            : "Unknown";

        // Get VLAN ID (default to 1 for native VLAN)
        var vlanId = 1;
        if (network.TryGetProperty("vlan", out var vlanProp) && vlanProp.ValueKind == JsonValueKind.Number)
        {
            vlanId = vlanProp.GetInt32();
        }
        else if (network.TryGetProperty("vlan_id", out var vlanIdProp) && vlanIdProp.ValueKind == JsonValueKind.Number)
        {
            vlanId = vlanIdProp.GetInt32();
        }

        // Get purpose
        var purposeStr = network.TryGetProperty("purpose", out var purposeProp)
            ? purposeProp.GetString()
            : null;

        // Classify network
        var purpose = ClassifyNetwork(name, purposeStr);

        // Get subnet
        var subnet = network.TryGetProperty("ip_subnet", out var subnetProp)
            ? subnetProp.GetString()
            : null;

        // Get gateway
        var gateway = network.TryGetProperty("gateway_ip", out var gatewayProp)
            ? gatewayProp.GetString()
            : network.TryGetProperty("dhcpd_gateway", out var dhcpdGatewayProp)
                ? dhcpdGatewayProp.GetString()
                : null;

        // Get DNS servers
        var dnsServers = new List<string>();
        if (network.TryGetProperty("dhcpd_dns", out var dnsProp) && dnsProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var dns in dnsProp.EnumerateArray())
            {
                if (dns.ValueKind == JsonValueKind.String)
                {
                    var dnsStr = dns.GetString();
                    if (!string.IsNullOrEmpty(dnsStr))
                        dnsServers.Add(dnsStr);
                }
            }
        }

        return new NetworkInfo
        {
            Id = networkId,
            Name = name,
            VlanId = vlanId,
            Purpose = purpose,
            Subnet = subnet,
            Gateway = gateway,
            DnsServers = dnsServers.Any() ? dnsServers : null
        };
    }

    /// <summary>
    /// Classify a network based on its name and purpose
    /// </summary>
    public NetworkPurpose ClassifyNetwork(string networkName, string? purpose = null)
    {
        var nameLower = networkName.ToLowerInvariant();

        // Check explicit purpose first
        if (!string.IsNullOrEmpty(purpose))
        {
            var purposeLower = purpose.ToLowerInvariant();
            if (purposeLower == "guest")
                return NetworkPurpose.Guest;
            if (purposeLower == "corporate")
                return NetworkPurpose.Corporate;
        }

        // Check patterns - Security first to avoid false positives with "Security Devices" matching IoT
        if (SecurityPatterns.Any(p => nameLower.Contains(p)))
            return NetworkPurpose.Security;

        if (IoTPatterns.Any(p => nameLower.Contains(p)))
            return NetworkPurpose.IoT;

        if (ManagementPatterns.Any(p => nameLower.Contains(p)))
            return NetworkPurpose.Management;

        if (GuestPatterns.Any(p => nameLower.Contains(p)))
            return NetworkPurpose.Guest;

        // Default to corporate for named networks, unknown otherwise
        return networkName.ToLowerInvariant() == "default" || networkName.ToLowerInvariant() == "main"
            ? NetworkPurpose.Corporate
            : NetworkPurpose.Unknown;
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
}
