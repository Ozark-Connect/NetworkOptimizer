using System.Text.Json.Nodes;
using FluentAssertions;
using NetworkOptimizer.Web.Services.ChangeControl;
using Xunit;

namespace NetworkOptimizer.Web.Tests.ChangeControl;

public class LockoutProtocolTests
{
    [Theory]
    [InlineData("icmp", true, true)]
    [InlineData("udp", true, true)]
    [InlineData("tcp", true, false)]
    [InlineData("tcp_udp", true, false)]
    [InlineData("TCP_UDP", true, false)]
    [InlineData("TCP UDP", true, false)]
    [InlineData("all", true, false)]
    [InlineData(null, true, false)]
    [InlineData("tcp", false, true)]
    [InlineData("tcp_udp", false, true)]
    [InlineData("all", false, true)]
    [InlineData("icmp", false, false)]
    [InlineData("udp", false, false)]
    [InlineData("future-protocol", false, true)]
    [InlineData("future-protocol", true, true)]
    public void GatewayBlock_RespectsProtocolInversion(string? protocol, bool opposite, bool expectedFinding)
    {
        var state = new ConfigState();
        state.Collections["firewall_zone"] = [new JsonObject { ["_id"] = "zone-gateway-test", ["zone_key"] = "gateway" }];
        var policy = new JsonObject
        {
            ["_id"] = "policy-test", ["name"] = "Synthetic gateway block", ["enabled"] = true,
            ["action"] = "BLOCK", ["protocol"] = protocol, ["match_opposite_protocol"] = opposite,
            ["source"] = new JsonObject { ["matching_target"] = "ANY" },
            ["destination"] = new JsonObject { ["zone_id"] = "zone-gateway-test", ["port_matching_type"] = "SPECIFIC", ["port"] = "443" },
        };
        var doc = new PlanDocument
        {
            Operations = [new PlanOperation
            {
                Key = "firewall_policy:create:synthetic", Collection = "firewall_policy",
                Action = PlanAction.Create, ObjectName = "Synthetic gateway block", Body = policy,
            }],
        };

        var findings = LockoutChecker.Check(state, state, doc, new PlanOptions { ManagementAddresses = ["192.0.2.10"] });

        findings.Any(f => f.OperationKeys.Contains("firewall_policy:create:synthetic")).Should().Be(expectedFinding);
    }

    /// <summary>
    /// A block to the gateway from Internal, where Network Optimizer manages from 192.0.2.10 on Management
    /// (192.0.2.0/24): whether it is a lockout, by destination ports and source shape.
    /// </summary>
    [Theory]
    // Destination ports: management is 22, 80, 443, 8080, 8443
    [InlineData("443", "net-mgmt", true)]
    [InlineData("53", "net-mgmt", false)]
    [InlineData("8000-8100", "net-mgmt", true)]
    [InlineData("20-21", "net-mgmt", false)]
    [InlineData("!443", "net-mgmt", true)]
    [InlineData("!22,80,443,8080,8443", "net-mgmt", false)]   // every port but management's: today's code calls it a lockout
    [InlineData("group443", "net-mgmt", true)]
    [InlineData("group-missing", "net-mgmt", true)]
    // Sources
    [InlineData("443", "net-other", false)]
    [InlineData("443", "!net-mgmt", false)]                    // every network but Management: today's code calls it a lockout
    [InlineData("443", "!net-other", true)]                    // every network but another: today's code misses it
    [InlineData("443", "ip-host", true)]
    [InlineData("443", "ip-cidr", true)]
    [InlineData("443", "ip-range", true)]                      // a range holding our address: today's code misses it
    [InlineData("443", "ip-other", false)]
    [InlineData("443", "!ip-other", true)]                     // everyone but another host: today's code misses it
    public void GatewayBlock_ByPortsAndSource(string ports, string source, bool lockout)
    {
        var state = new ConfigState();
        state.Collections["networkconf"] =
        [
            new JsonObject { ["_id"] = "n-mgmt", ["name"] = "Management", ["ip_subnet"] = "192.0.2.1/24" },
            new JsonObject { ["_id"] = "n-other", ["name"] = "Other", ["ip_subnet"] = "198.51.100.1/24" },
        ];
        state.Collections["firewall_zone"] =
        [
            new JsonObject { ["_id"] = "z-gw", ["zone_key"] = "gateway", ["network_ids"] = new JsonArray() },
            new JsonObject { ["_id"] = "z-int", ["zone_key"] = "internal", ["network_ids"] = new JsonArray("n-mgmt", "n-other") },
        ];
        state.Collections["firewallgroup"] = [new JsonObject { ["_id"] = "g-web", ["name"] = "Web", ["group_type"] = "port-group", ["group_members"] = new JsonArray("443") }];
        var dst = new JsonObject { ["zone_id"] = "z-gw", ["matching_target"] = "ANY" };
        switch (ports)
        {
            case "group443": dst["port_matching_type"] = "OBJECT"; dst["port_group_id"] = "g-web"; break;
            case "group-missing": dst["port_matching_type"] = "OBJECT"; dst["port_group_id"] = "g-gone"; break;
            default:
                dst["port_matching_type"] = "SPECIFIC";
                dst["port"] = ports.TrimStart('!');
                dst["match_opposite_ports"] = ports.StartsWith('!');
                break;
        }
        var src = new JsonObject { ["zone_id"] = "z-int", ["port_matching_type"] = "ANY" };
        var opposite = source.StartsWith('!');
        switch (source.TrimStart('!'))
        {
            case "net-mgmt": src["matching_target"] = "NETWORK"; src["network_ids"] = new JsonArray("n-mgmt"); src["match_opposite_networks"] = opposite; break;
            case "net-other": src["matching_target"] = "NETWORK"; src["network_ids"] = new JsonArray("n-other"); src["match_opposite_networks"] = opposite; break;
            case "ip-host": src["matching_target"] = "IP"; src["ips"] = new JsonArray("192.0.2.10"); break;
            case "ip-cidr": src["matching_target"] = "IP"; src["ips"] = new JsonArray("192.0.2.0/24"); break;
            case "ip-range": src["matching_target"] = "IP"; src["ips"] = new JsonArray("192.0.2.5-192.0.2.20"); break;
            case "ip-other": src["matching_target"] = "IP"; src["ips"] = new JsonArray("192.0.2.99"); src["match_opposite_ips"] = opposite; break;
        }
        var policy = new JsonObject
        {
            ["_id"] = "policy-test", ["name"] = "Synthetic gateway block", ["enabled"] = true, ["action"] = "BLOCK", ["protocol"] = "tcp",
            ["ip_version"] = "BOTH", ["connection_state_type"] = "ALL", ["source"] = src, ["destination"] = dst,
        };
        var doc = new PlanDocument
        {
            Operations = [new PlanOperation { Key = "firewall_policy:create:synthetic", Collection = "firewall_policy", Action = PlanAction.Create, ObjectName = "Synthetic gateway block", Body = policy }],
        };

        var findings = LockoutChecker.Check(state, state, doc, new PlanOptions { ManagementAddresses = ["192.0.2.10"] });

        findings.Any(f => f.OperationKeys.Contains("firewall_policy:create:synthetic")).Should().Be(lockout, $"{ports} from {source}");
    }
}
