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
}
