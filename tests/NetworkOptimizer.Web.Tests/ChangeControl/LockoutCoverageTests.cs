using System.Text.Json.Nodes;
using FluentAssertions;
using NetworkOptimizer.Web.Services.ChangeControl;
using Xunit;

namespace NetworkOptimizer.Web.Tests.ChangeControl;

public class LockoutCoverageTests
{
    private static ConfigState Site()
    {
        var s = new ConfigState();
        s.Collections["networkconf"] =
        [
            new JsonObject { ["_id"] = "n-lan", ["name"] = "Default", ["vlan"] = 1, ["purpose"] = "corporate", ["ip_subnet"] = "192.0.2.1/24", ["dhcpd_enabled"] = true },
            new JsonObject { ["_id"] = "n-iot", ["name"] = "IoT", ["vlan"] = 30, ["purpose"] = "corporate", ["ip_subnet"] = "198.51.100.1/24", ["dhcpd_enabled"] = true },
        ];
        s.Collections["firewall_zone"] =
        [
            new JsonObject { ["_id"] = "z-int", ["name"] = "Internal", ["zone_key"] = "internal", ["network_ids"] = new JsonArray("n-lan", "n-iot") },
            new JsonObject { ["_id"] = "z-gw", ["name"] = "Gateway", ["zone_key"] = "gateway", ["network_ids"] = new JsonArray() },
        ];
        s.Collections["firewall_policy"] = [];
        s.Collections["device"] = [];
        return s;
    }

    private static JsonObject Block(string id, JsonObject source) => new()
    {
        ["_id"] = id, ["name"] = id, ["enabled"] = true, ["action"] = "BLOCK", ["index"] = 1, ["ip_version"] = "BOTH", ["protocol"] = "all",
        ["schedule"] = new JsonObject { ["mode"] = "ALWAYS" }, ["source"] = source,
        ["destination"] = new JsonObject { ["zone_id"] = "z-gw", ["matching_target"] = "ANY" },
    };

    private static PlanDocument Plan(ConfigState desired, bool agent) =>
        PlanBuilder.Build(desired, Site(), new PlanOptions { AgentSite = agent, ManagementAddresses = ["192.0.2.50", "fe80::1"] });

    [Fact]
    public void BlockFromTheAgentsNetwork_IsCertain()
    {
        var desired = Site();
        desired.Collections["firewall_policy"] = [Block("new-1", new JsonObject { ["zone_id"] = "z-int", ["matching_target"] = "NETWORK", ["network_ids"] = new JsonArray("n-lan") })];

        var doc = Plan(desired, agent: true);

        doc.Lockouts.Should().ContainSingle(l => l.Kind == "certain" && l.Title.Contains("blocks traffic to the gateway"));
    }

    [Fact]
    public void BlockFromAnotherNetwork_IsNotFlagged()
    {
        var desired = Site();
        desired.Collections["firewall_policy"] = [Block("new-1", new JsonObject { ["zone_id"] = "z-int", ["matching_target"] = "NETWORK", ["network_ids"] = new JsonArray("n-iot") })];

        Plan(desired, agent: true).Lockouts.Should().BeEmpty("an IPv6 management address must not match every network");
    }

    [Fact]
    public void AgentNetworkVlanChange_IsCertain()
    {
        var desired = Site();
        desired.Collections["networkconf"][0]["vlan"] = 10;

        Plan(desired, agent: true).Lockouts.Should().Contain(l => l.Kind == "certain" && l.Title.Contains("On-Site Agent's network"));
    }

    private static ConfigState WithSettings(string informHost, string osHostname)
    {
        var s = Site();
        s.Collections["setting"] =
        [
            new JsonObject { ["_id"] = "s-mgmt", ["key"] = "super_mgmt", ["override_inform_host"] = true, ["override_inform_host_location"] = informHost },
            new JsonObject { ["_id"] = "s-id", ["key"] = "super_identity", ["name"] = "Console", ["hostname"] = osHostname },
        ];
        return s;
    }

    [Fact]
    public void InformHostOverrideChange_IsFlagged()
    {
        var doc = PlanBuilder.Build(WithSettings("inform.example.net", "uos"), WithSettings("inform.example.com", "uos"), new PlanOptions());

        doc.Lockouts.Should().ContainSingle(l => l.Title == "Inform Host Override changes" && l.Detail.Contains("inform.example.net"));
    }

    [Fact]
    public void OsHostnameChange_IsNotAChange()
    {
        var before = WithSettings("inform.example.com", "9f6b864b64a5");
        var after = WithSettings("inform.example.com", "984ffa9ff8f2");

        ConfigDiffEngine.Compare(before, after).IsEmpty.Should().BeTrue("super_identity.hostname is the console's OS hostname");
        PlanBuilder.Build(after, before, new PlanOptions()).Lockouts.Should().BeEmpty();
    }
}
