using System.Net;
using System.Net.Sockets;
using NetworkOptimizer.Audit.Models;

using AuditSeverity = NetworkOptimizer.Audit.Models.AuditSeverity;

namespace NetworkOptimizer.Audit.Rules;

/// <summary>
/// Detects when a client's IP address doesn't match the subnet of their assigned VLAN.
/// This typically happens when a device has a stale fixed IP from a previous VLAN assignment,
/// or when a virtual network override was configured but DHCP hasn't renewed the lease.
/// </summary>
public class VlanSubnetMismatchRule : WirelessAuditRuleBase
{
    public override string RuleId => "WIFI-VLAN-SUBNET-001";
    public override string RuleName => "VLAN Subnet Mismatch";
    public override string Description => "Client IP address should match their assigned VLAN's subnet";
    public override AuditSeverity Severity => AuditSeverity.Critical;
    public override int ScoreImpact => 10;

    public override AuditIssue? Evaluate(WirelessClientInfo client, List<NetworkInfo> networks)
    {
        // Only check clients with virtual network override enabled
        // These are the cases where subnet mismatch is most likely
        if (!client.Client.VirtualNetworkOverrideEnabled)
            return null;

        // Get client IP - prefer current IP, fall back to fixed IP
        var clientIp = !string.IsNullOrEmpty(client.Client.Ip)
            ? client.Client.Ip
            : client.Client.FixedIp;

        if (string.IsNullOrEmpty(clientIp))
            return null;

        // Parse the IP
        if (!IPAddress.TryParse(clientIp, out var ip))
            return null;

        // Only handle IPv4 for now
        if (ip.AddressFamily != AddressFamily.InterNetwork)
            return null;

        // Get the effective network (the one the client should be on via override)
        var effectiveNetwork = client.Network;
        if (effectiveNetwork == null)
        {
            // Try to find network by effective network ID
            var effectiveNetworkId = client.Client.EffectiveNetworkId;
            effectiveNetwork = networks.FirstOrDefault(n => n.Id == effectiveNetworkId);
        }

        // If still no network, try to find by VLAN number
        if (effectiveNetwork == null && client.Client.Vlan.HasValue)
        {
            effectiveNetwork = networks.FirstOrDefault(n => n.VlanId == client.Client.Vlan.Value);
        }

        if (effectiveNetwork == null)
            return null;

        // Check if network has subnet info
        if (string.IsNullOrEmpty(effectiveNetwork.Subnet))
            return null;

        // Validate subnet format before checking membership
        if (!IsValidSubnetFormat(effectiveNetwork.Subnet))
            return null;

        // Check if client IP is in the network's subnet
        if (IsIpInSubnet(ip, effectiveNetwork.Subnet))
            return null; // IP matches subnet, no issue

        // IP doesn't match subnet - this is a problem
        var metadata = new Dictionary<string, object>
        {
            ["clientIp"] = clientIp,
            ["expectedSubnet"] = effectiveNetwork.Subnet,
            ["assignedVlan"] = effectiveNetwork.VlanId,
            ["assignedNetwork"] = effectiveNetwork.Name,
            ["virtualNetworkOverrideEnabled"] = true
        };

        if (!string.IsNullOrEmpty(client.Client.FixedIp))
        {
            metadata["hasFixedIp"] = true;
            metadata["fixedIp"] = client.Client.FixedIp;
        }

        // Determine the recommended action
        string recommendedAction;
        if (client.Client.UseFixedIp && !string.IsNullOrEmpty(client.Client.FixedIp))
        {
            recommendedAction = $"Update fixed IP to an address within {effectiveNetwork.Subnet}";
        }
        else
        {
            recommendedAction = "Reconnect device to obtain new DHCP lease, or update fixed IP assignment";
        }

        // Create a client info with the effective network set for proper issue creation
        var clientWithNetwork = new WirelessClientInfo
        {
            Client = client.Client,
            Network = effectiveNetwork,
            Detection = client.Detection,
            AccessPointName = client.AccessPointName,
            AccessPointMac = client.AccessPointMac,
            AccessPointModel = client.AccessPointModel,
            AccessPointModelName = client.AccessPointModelName
        };

        return CreateIssue(
            $"IP address {clientIp} does not match assigned VLAN subnet ({effectiveNetwork.Name}: {effectiveNetwork.Subnet})",
            clientWithNetwork,
            recommendedNetwork: effectiveNetwork.Name,
            recommendedVlan: effectiveNetwork.VlanId,
            recommendedAction: recommendedAction,
            metadata: metadata
        );
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

    /// <summary>
    /// Check if an IP address is within a given subnet (CIDR notation like "192.168.1.0/24").
    /// </summary>
    private static bool IsIpInSubnet(IPAddress ip, string subnet)
    {
        var parts = subnet.Split('/');
        if (parts.Length != 2 || !int.TryParse(parts[1], out var prefixLength))
            return false;

        if (!IPAddress.TryParse(parts[0], out var networkAddress))
            return false;

        // Only handle IPv4
        if (ip.AddressFamily != AddressFamily.InterNetwork ||
            networkAddress.AddressFamily != AddressFamily.InterNetwork)
            return false;

        var ipBytes = ip.GetAddressBytes();
        var networkBytes = networkAddress.GetAddressBytes();

        // Create mask from prefix length
        var maskBytes = new byte[4];
        var remainingBits = prefixLength;
        for (int i = 0; i < 4; i++)
        {
            if (remainingBits >= 8)
            {
                maskBytes[i] = 0xFF;
                remainingBits -= 8;
            }
            else if (remainingBits > 0)
            {
                maskBytes[i] = (byte)(0xFF << (8 - remainingBits));
                remainingBits = 0;
            }
            else
            {
                maskBytes[i] = 0;
            }
        }

        // Check if masked IP equals masked network
        for (int i = 0; i < 4; i++)
        {
            if ((ipBytes[i] & maskBytes[i]) != (networkBytes[i] & maskBytes[i]))
                return false;
        }

        return true;
    }
}
