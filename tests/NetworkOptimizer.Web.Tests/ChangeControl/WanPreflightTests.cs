using System.Text.Json.Nodes;
using FluentAssertions;
using NetworkOptimizer.Web.Services.ChangeControl;
using Xunit;

namespace NetworkOptimizer.Web.Tests.ChangeControl;

/// <summary>WAN record writes on a direct site: the internet-cut flag and the External-zone refusal.</summary>
public class WanPreflightTests
{
    // The active WAN on WAN2; UniFi Network's unused default profile on WAN.
    private static ConfigState Site()
    {
        var s = new ConfigState();
        s.Collections["networkconf"] =
        [
            new JsonObject { ["_id"] = "n-lan", ["name"] = "Default", ["vlan"] = 1, ["purpose"] = "corporate", ["ip_subnet"] = "192.0.2.1/24", ["dhcpd_enabled"] = true },
            new JsonObject
            {
                ["_id"] = "n-wan1", ["name"] = "Internet 1", ["purpose"] = "wan", ["enabled"] = true, ["wan_networkgroup"] = "WAN",
                ["wan_type"] = "dhcp", ["firewall_zone_id"] = "z-ext", ["wan_failover_priority"] = 2, ["wan_load_balance_type"] = "failover-only",
            },
            new JsonObject
            {
                ["_id"] = "n-wan2", ["name"] = "Fiber", ["purpose"] = "wan", ["enabled"] = true, ["wan_networkgroup"] = "WAN2",
                ["wan_type"] = "dhcp", ["wan_vlan_enabled"] = true, ["wan_vlan"] = 201, ["firewall_zone_id"] = "z-ext",
                ["wan_failover_priority"] = 1, ["wan_load_balance_type"] = "weighted", ["wan_smartq_enabled"] = false,
            },
        ];
        s.Collections["firewall_zone"] =
        [
            new JsonObject { ["_id"] = "z-int", ["name"] = "Internal", ["zone_key"] = "internal", ["network_ids"] = new JsonArray("n-lan") },
            new JsonObject { ["_id"] = "z-ext", ["name"] = "External", ["zone_key"] = "external", ["network_ids"] = new JsonArray("n-wan1", "n-wan2") },
            new JsonObject { ["_id"] = "z-gw", ["name"] = "Gateway", ["zone_key"] = "gateway", ["network_ids"] = new JsonArray() },
        ];
        s.Collections["firewall_policy"] = [];
        s.Collections["device"] =
        [
            new JsonObject
            {
                ["_id"] = "d-gw", ["mac"] = "aa:bb:cc:dd:ee:01", ["name"] = "Gateway",
                [ConfigCollections.DeviceMetaField] = new JsonObject { ["type"] = "uxg", ["model"] = "UXG" },
            },
        ];
        return s;
    }

    private static PlanDocument Plan(Action<ConfigState> change, IReadOnlySet<string>? liveWans, bool agent = false, ConfigState? live = null)
    {
        var desired = live?.Clone() ?? Site();
        change(desired);
        return PlanBuilder.Build(desired, live ?? Site(), new PlanOptions { AgentSite = agent, ManagementAddresses = ["192.0.2.50"], LiveWanGroups = liveWans });
    }

    private static JsonObject Wan(ConfigState s, string id) => s.Collections["networkconf"].Single(n => (string?)n["_id"] == id);

    private static PlanOperation Op(PlanDocument doc, string id) => doc.Operations.Single(o => o.ObjectId == id);

    private static readonly HashSet<string> OnWan2 = new() { "WAN2" };

    [Theory]
    [InlineData("firewall_zone_id", "z-int")]
    [InlineData("wan_vlan", 202)]
    [InlineData("wan_vlan_enabled", false)]
    [InlineData("wan_type", "pppoe")]
    [InlineData("enabled", false)]
    public void LiveWan_UplinkChange_IsAcknowledged(string field, object value)
    {
        var op = Op(Plan(s => Wan(s, "n-wan2")[field] = JsonValue.Create(value), OnWan2), "n-wan2");

        op.Tier.Should().BeGreaterThanOrEqualTo(PlanTiers.Acknowledge);
        op.Reasons.Should().Contain(r => r.StartsWith("Changes how WAN Fiber connects (", StringComparison.Ordinal));
    }

    [Fact]
    public void LiveWan_ChangeOffTheUplink_IsNotFlagged()
    {
        var op = Op(Plan(s => Wan(s, "n-wan2")["wan_smartq_enabled"] = true, OnWan2), "n-wan2");

        op.Reasons.Should().NotContain(r => r.Contains("connects (", StringComparison.Ordinal));
    }

    [Fact]
    public void LiveWan_Delete_IsAcknowledged()
    {
        var op = Op(Plan(s => s.Collections["networkconf"].RemoveAll(n => (string?)n["_id"] == "n-wan2"), OnWan2), "n-wan2");

        op.Tier.Should().BeGreaterThanOrEqualTo(PlanTiers.Acknowledge);
        op.Reasons.Should().Contain("Deletes WAN Fiber, which the gateway carries traffic on");
    }

    [Fact]
    public void UnusedWanProfile_UplinkChange_IsNotFlagged()
    {
        var op = Op(Plan(s => Wan(s, "n-wan1")["wan_type"] = "pppoe", OnWan2), "n-wan1");

        op.Reasons.Should().NotContain(r => r.Contains("WAN Internet 1", StringComparison.Ordinal));
    }

    [Fact]
    public void UnusedWanProfile_RoutingChange_IsAcknowledged()
    {
        var op = Op(Plan(s => Wan(s, "n-wan1")["wan_failover_priority"] = 1, OnWan2), "n-wan1");

        op.Tier.Should().BeGreaterThanOrEqualTo(PlanTiers.Acknowledge);
        op.Reasons.Should().Contain(r => r.StartsWith("Changes which WAN carries traffic (", StringComparison.Ordinal));
    }

    [Fact]
    public void UnknownLiveWans_EveryWanCounts()
    {
        var op = Op(Plan(s => Wan(s, "n-wan1")["wan_type"] = "pppoe", liveWans: null), "n-wan1");

        op.Reasons.Should().Contain(r => r.StartsWith("Changes how WAN Internet 1 connects (", StringComparison.Ordinal));
    }

    [Fact]
    public void AgentSite_IsLeftToTheLockoutCheck()
    {
        var doc = Plan(s => Wan(s, "n-wan2")["wan_vlan"] = 202, OnWan2, agent: true);

        doc.Lockouts.Should().ContainSingle(l => l.Title == "WAN Fiber changes on a site reached through the On-Site Agent");
        Op(doc, "n-wan2").Reasons.Should().NotContain(r => r.Contains("connects (", StringComparison.Ordinal));
    }

    [Fact]
    public void WanZoneToGateway_IsRefused()
    {
        var doc = Plan(s => Wan(s, "n-wan2")["firewall_zone_id"] = "z-gw", OnWan2);

        doc.Errors.Should().ContainSingle(e => e == "WAN Fiber: UniFi Network keeps every WAN in the External zone. Set its Firewall Zone back to External.");
    }

    [Fact]
    public void WanZoneBackToExternal_IsAllowed()
    {
        var dangling = Site();
        Wan(dangling, "n-wan2")["firewall_zone_id"] = "z-gone";

        var doc = Plan(s => Wan(s, "n-wan2")["firewall_zone_id"] = "z-ext", OnWan2, live: dangling);

        doc.Errors.Should().BeEmpty();
        doc.Operations.Should().Contain(o => o.ObjectId == "n-wan2");
    }

    [Fact]
    public void WanAlreadyOutsideExternal_OtherEdit_IsNotRefused()
    {
        var dangling = Site();
        Wan(dangling, "n-wan2")["firewall_zone_id"] = "z-gone";

        var doc = Plan(s => Wan(s, "n-wan2")["wan_smartq_enabled"] = true, OnWan2, live: dangling);

        doc.Errors.Should().BeEmpty("the plan does not write the zone");
    }

    [Fact]
    public void NetworkWrite_IsAGatewayProvision()
    {
        var doc = Plan(s => Wan(s, "n-lan")["name"] = "LAN", OnWan2);

        doc.WriteOrder.Should().Contain(s => s.Detail == "One gateway provision");
    }

    [Fact]
    public void NetworkWrite_WithoutAGateway_IsNotAGatewayProvision()
    {
        var desired = Site();
        Wan(desired, "n-lan")["name"] = "LAN";

        var doc = PlanBuilder.Build(desired, Site(), new PlanOptions { TargetHasGateway = false });

        doc.WriteOrder.Should().NotContain(s => s.Detail.Contains("gateway provision"));
    }

    [Fact]
    public void NetworkWrite_OnASwitchOnlySite_IsNotAGatewayProvision()
    {
        static ConfigState SwitchOnly()
        {
            var s = Site();
            s.Collections["device"] = [new JsonObject { ["_id"] = "d-sw", ["mac"] = "aa:bb:cc:dd:ee:02", [ConfigCollections.DeviceMetaField] = new JsonObject { ["type"] = "usw" } }];
            return s;
        }
        var desired = SwitchOnly();
        Wan(desired, "n-lan")["name"] = "LAN";

        var doc = PlanBuilder.Build(desired, SwitchOnly(), new PlanOptions());

        doc.WriteOrder.Should().NotContain(s => s.Detail.Contains("gateway provision"));
    }

    [Fact]
    public void LanNetworkZoneField_IsNotRefused()
    {
        var doc = Plan(s => Wan(s, "n-lan")["firewall_zone_id"] = "z-gw", OnWan2);

        doc.Errors.Should().BeEmpty();
    }
}
