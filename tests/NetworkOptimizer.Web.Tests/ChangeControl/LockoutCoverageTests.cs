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

    [Theory]
    [InlineData("z-int", true)]
    [InlineData("z-iot", false)]
    public void BlockFromEveryAddressButAGroup_OnlyFlagsTheZoneWeManageFrom(string sourceZone, bool expectedLockout)
    {
        // IoT in its own zone; the block's source is every address except one that is not ours (Match Opposite).
        ConfigState Zoned()
        {
            var s = Site();
            s.Collections["firewall_zone"][0]["network_ids"] = new JsonArray("n-lan");
            s.Collections["firewall_zone"].Add(new JsonObject { ["_id"] = "z-iot", ["name"] = "IoT", ["network_ids"] = new JsonArray("n-iot") });
            return s;
        }
        var desired = Zoned();
        var policy = Block("new-1", new JsonObject
        {
            ["zone_id"] = sourceZone, ["matching_target"] = "IP", ["ips"] = new JsonArray("192.0.2.99"), ["match_opposite_ips"] = true,
        });
        desired.Collections["firewall_policy"] = [policy];

        var doc = PlanBuilder.Build(desired, Zoned(), new PlanOptions { ManagementAddresses = ["192.0.2.50"] });

        doc.Lockouts.Any(l => l.Title.Contains("blocks traffic to the gateway")).Should().Be(expectedLockout,
            "a policy sees traffic only from its source zone, and Network Optimizer's address is in Internal");
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void PolicyEdit_JudgesTheRoutedAddress_NotEveryHostLeg(bool routed, bool expectedLockout)
    {
        // The host has a leg on IoT (198.51.100.20) but reaches the site from Internal (192.0.2.50).
        ConfigState Zoned()
        {
            var s = Site();
            s.Collections["firewall_zone"][0]["network_ids"] = new JsonArray("n-lan");
            s.Collections["firewall_zone"].Add(new JsonObject { ["_id"] = "z-iot", ["name"] = "IoT", ["network_ids"] = new JsonArray("n-iot") });
            s.Collections["firewall_policy"] = [Block("p-1", new JsonObject
            {
                ["zone_id"] = "z-iot", ["matching_target"] = "IP", ["ips"] = new JsonArray("192.0.2.99"), ["match_opposite_ips"] = true,
            })];
            return s;
        }
        var desired = Zoned();
        desired.Collections["firewall_policy"][0]["name"] = "p-1 renamed";

        var doc = PlanBuilder.Build(desired, Zoned(), new PlanOptions
        {
            ManagementAddresses = ["192.0.2.50", "198.51.100.20"], ReachAddresses = routed ? ["192.0.2.50"] : [],
        });

        doc.Lockouts.Any(l => l.Title.Contains("blocks traffic to the gateway")).Should().Be(expectedLockout);
    }

    [Fact]
    public void GatewaySnmpBlock_IsNotFlaggedAsALockout()
    {
        var desired = Site();
        var policy = Block("new-1", new JsonObject { ["zone_id"] = "z-int", ["matching_target"] = "ANY" });
        policy["protocol"] = "udp";
        policy["destination"]!["port_matching_type"] = "SPECIFIC";
        policy["destination"]!["port"] = "161";
        desired.Collections["firewall_policy"] = [policy];

        Plan(desired, agent: true).Lockouts.Should().BeEmpty("SNMP does not carry the console or management path");
    }

    [Theory]
    [InlineData("22,80,443", false, true)]
    [InlineData("161", false, false)]
    [InlineData("161", true, true)]
    public void GatewayTcpPortList_OnlyFlagsManagementPorts(string ports, bool opposite, bool expectedLockout)
    {
        var desired = Site();
        var policy = Block("new-1", new JsonObject { ["zone_id"] = "z-int", ["matching_target"] = "ANY" });
        policy["protocol"] = "tcp";
        policy["destination"]!["port_matching_type"] = "SPECIFIC";
        policy["destination"]!["port"] = ports;
        policy["destination"]!["match_opposite_ports"] = opposite;
        desired.Collections["firewall_policy"] = [policy];

        Plan(desired, agent: true).Lockouts.Any(l => l.Title.Contains("blocks traffic to the gateway")).Should().Be(expectedLockout);
    }

    [Fact]
    public void GatewayPortGroupContainingManagementPort_IsFlagged()
    {
        var desired = Site();
        desired.Collections["firewallgroup"] =
        [
            new JsonObject { ["_id"] = "g-mgmt", ["name"] = "Management ports", ["group_type"] = "port-group", ["group_members"] = new JsonArray("161", "443") },
        ];
        var policy = Block("new-1", new JsonObject { ["zone_id"] = "z-int", ["matching_target"] = "ANY" });
        policy["protocol"] = "TCP UDP";
        policy["destination"]!["port_matching_type"] = "OBJECT";
        policy["destination"]!["port_group_id"] = "g-mgmt";
        desired.Collections["firewall_policy"] = [policy];

        Plan(desired, agent: true).Lockouts.Should().Contain(l => l.Title.Contains("blocks traffic to the gateway"));
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

    // A site with the active WAN on WAN2 and UniFi Network's unused default profile on WAN.
    private static ConfigState WanSite()
    {
        var s = Site();
        s.Collections["networkconf"].Add(new JsonObject
        {
            ["_id"] = "n-wan1", ["name"] = "Internet 1", ["purpose"] = "wan", ["enabled"] = true, ["wan_networkgroup"] = "WAN",
            ["wan_failover_priority"] = 2, ["wan_load_balance_type"] = "failover-only",
        });
        s.Collections["networkconf"].Add(new JsonObject
        {
            ["_id"] = "n-wan2", ["name"] = "Fiber", ["purpose"] = "wan", ["enabled"] = true, ["wan_networkgroup"] = "WAN2",
            ["wan_failover_priority"] = 1, ["wan_load_balance_type"] = "weighted",
        });
        return s;
    }

    private static PlanDocument WanPlan(Action<ConfigState> change, IReadOnlySet<string>? liveWans)
    {
        var desired = WanSite();
        change(desired);
        return PlanBuilder.Build(desired, WanSite(), new PlanOptions { AgentSite = true, ManagementAddresses = ["192.0.2.50"], LiveWanGroups = liveWans });
    }

    private static JsonObject Wan(ConfigState s, string id) => s.Collections["networkconf"].Single(n => (string?)n["_id"] == id);

    private static bool FlagsWan(PlanDocument doc, string name) => doc.Lockouts.Any(l => l.Title == $"WAN {name} changes on a site reached through the On-Site Agent");

    [Fact]
    public void UnusedWanProfile_ChangeThatMovesNoTraffic_IsNotALockout()
    {
        var doc = WanPlan(s => Wan(s, "n-wan1")["name"] = "Spare", new HashSet<string> { "WAN2" });
        doc.Operations.Should().Contain(o => o.ObjectId == "n-wan1");
        FlagsWan(doc, "Spare").Should().BeFalse();
    }

    [Theory]
    [InlineData("enabled", false)]
    [InlineData("wan_failover_priority", 1)]
    [InlineData("wan_load_balance_type", "weighted")]
    public void UnusedWanProfile_ChangeThatCanMoveTraffic_IsALockout(string field, object value)
    {
        var doc = WanPlan(s => Wan(s, "n-wan1")[field] = JsonValue.Create(value), new HashSet<string> { "WAN2" });
        FlagsWan(doc, "Internet 1").Should().BeTrue();
    }

    [Fact]
    public void LiveWan_AnyChange_IsALockout()
    {
        FlagsWan(WanPlan(s => Wan(s, "n-wan2")["name"] = "Fiber 2", new HashSet<string> { "WAN2" }), "Fiber 2").Should().BeTrue();
    }

    [Fact]
    public void UnknownLiveWans_EveryWanCounts()
    {
        FlagsWan(WanPlan(s => Wan(s, "n-wan1")["name"] = "Spare", liveWans: null), "Spare").Should().BeTrue();
    }
}
