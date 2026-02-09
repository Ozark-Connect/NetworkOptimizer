using System.Net;
using System.Net.Sockets;
using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.Core.Helpers;

using AuditSeverity = NetworkOptimizer.Audit.Models.AuditSeverity;

namespace NetworkOptimizer.Audit.Rules;

/// <summary>
/// Detects when a wired client's IP address doesn't match the subnet of their port's VLAN.
/// This typically happens when a device has a stale fixed IP from a previous VLAN assignment,
/// or when the port's VLAN was changed but the device hasn't renewed its DHCP lease.
/// </summary>
public class WiredSubnetMismatchRule : AuditRuleBase
{
    public override string RuleId => IssueTypes.WiredSubnetMismatch;
    public override string RuleName => "Wired Subnet Mismatch";
    public override string Description => "Wired client IP address should match their port's VLAN subnet";
    public override AuditSeverity Severity => AuditSeverity.Critical;
    public override int ScoreImpact => 10;

    public override AuditIssue? Evaluate(PortInfo port, List<NetworkInfo> networks, List<NetworkInfo>? allNetworks = null)
    {
        // Skip uplinks, WAN ports, trunk ports, and disabled ports
        if (port.ForwardMode != "native" || port.IsUplink || port.IsWan)
            return null;

        // Skip ports without a connected client
        var client = port.ConnectedClient;
        if (client == null)
            return null;

        // Get client IP - prefer current IP, fall back to fixed IP
        var clientIp = !string.IsNullOrEmpty(client.Ip)
            ? client.Ip
            : client.FixedIp;

        if (string.IsNullOrEmpty(clientIp))
            return null;

        // Parse the IP
        if (!IPAddress.TryParse(clientIp, out var ip))
            return null;

        // Only handle IPv4 for now
        if (ip.AddressFamily != AddressFamily.InterNetwork)
            return null;

        // Get the network for this port
        var network = GetNetwork(port.NativeNetworkId, networks);
        if (network == null)
            return null;

        // Check if network has subnet info
        if (string.IsNullOrEmpty(network.Subnet))
            return null;

        // Validate subnet format before checking membership
        if (!IsValidSubnetFormat(network.Subnet))
            return null;

        // Check if client IP is in the network's subnet
        if (NetworkUtilities.IsIpInSubnet(ip, network.Subnet))
            return null; // IP matches subnet, no issue

        // IP doesn't match subnet - this is a problem
        var metadata = new Dictionary<string, object>
        {
            ["clientIp"] = clientIp,
            ["expectedSubnet"] = network.Subnet,
            ["assignedVlan"] = network.VlanId,
            ["assignedNetwork"] = network.Name
        };

        if (!string.IsNullOrEmpty(client.FixedIp))
        {
            metadata["hasFixedIp"] = true;
            metadata["fixedIp"] = client.FixedIp;
        }

        // Determine the recommended action
        string recommendedAction;
        if (client.UseFixedIp && !string.IsNullOrEmpty(client.FixedIp))
        {
            recommendedAction = $"Update fixed IP to an address within {network.Subnet}";
        }
        else
        {
            recommendedAction = "Reconnect device to obtain new DHCP lease, or update fixed IP assignment.";
        }

        // Build device name
        var deviceName = GetDeviceName(port, client);

        return new AuditIssue
        {
            Type = RuleId,
            Severity = Severity,
            Message = $"IP address {clientIp} does not match port's VLAN subnet ({network.Name}: {network.Subnet})",
            DeviceName = deviceName,
            DeviceMac = port.Switch.MacAddress,
            Port = port.PortIndex.ToString(),
            PortName = port.Name,
            CurrentNetwork = network.Name,
            CurrentVlan = network.VlanId,
            RecommendedNetwork = network.Name, // Same network, just need correct IP
            RecommendedVlan = network.VlanId,
            RecommendedAction = recommendedAction,
            Metadata = metadata,
            RuleId = RuleId,
            ScoreImpact = ScoreImpact
        };
    }

    /// <summary>
    /// Get a display name for the device based on available information.
    /// </summary>
    private string GetDeviceName(PortInfo port, UniFi.Models.UniFiClientResponse client)
    {
        // Try client name/hostname first
        var clientName = !string.IsNullOrEmpty(client.Name) ? client.Name
            : !string.IsNullOrEmpty(client.Hostname) ? client.Hostname
            : null;

        if (!string.IsNullOrEmpty(clientName))
            return $"{clientName} on {port.Switch.Name}";

        // Try OUI with MAC suffix
        var oui = client.Oui;
        var mac = client.Mac;
        var macSuffix = !string.IsNullOrEmpty(mac) && mac.Length >= 8
            ? mac.Substring(mac.Length - 5).ToUpperInvariant()
            : null;

        if (!string.IsNullOrEmpty(oui) && !string.IsNullOrEmpty(macSuffix))
            return $"{oui} ({macSuffix}) on {port.Switch.Name}";

        // Fall back to MAC address
        if (!string.IsNullOrEmpty(mac))
            return $"{mac} on {port.Switch.Name}";

        // Last resort: port name or number
        return !string.IsNullOrEmpty(port.Name)
            ? $"{port.Name} on {port.Switch.Name}"
            : $"Port {port.PortIndex} on {port.Switch.Name}";
    }

    /// <summary>
    /// Check if a subnet string is in valid CIDR format
    /// </summary>
    private static bool IsValidSubnetFormat(string subnet)
    {
        var parts = subnet.Split('/');
        if (parts.Length != 2)
            return false;

        if (!int.TryParse(parts[1], out var prefixLength))
            return false;

        if (prefixLength < 0 || prefixLength > 32)
            return false;

        return IPAddress.TryParse(parts[0], out var networkAddress) &&
               networkAddress.AddressFamily == AddressFamily.InterNetwork;
    }
}
