using System.Text.Json.Nodes;
using FluentAssertions;
using NetworkOptimizer.Web.Services.ChangeControl;
using Xunit;

namespace NetworkOptimizer.Web.Tests.ChangeControl;

/// <summary>Rule shapes follow legacy-full-firewallrules.json; zone shapes follow the zone-based captures.</summary>
public class LegacyFirewallTranslatorTests
{
    private static JsonObject Rule(string id, string ruleset, string action, string name, Action<JsonObject>? set = null)
    {
        var r = new JsonObject
        {
            ["_id"] = id, ["ruleset"] = ruleset, ["action"] = action, ["name"] = name, ["enabled"] = true, ["rule_index"] = 2000 + int.Parse(id[1..]),
            ["protocol"] = "all", ["protocol_match_excepted"] = false, ["logging"] = false, ["icmp_typename"] = "", ["ipsec"] = "",
            ["state_new"] = false, ["state_established"] = false, ["state_related"] = false, ["state_invalid"] = false,
            ["src_networkconf_id"] = "", ["src_networkconf_type"] = "NETv4", ["src_address"] = "", ["src_mac_address"] = "", ["src_firewallgroup_ids"] = new JsonArray(), ["src_port"] = "",
            ["dst_networkconf_id"] = "", ["dst_networkconf_type"] = "NETv4", ["dst_address"] = "", ["dst_firewallgroup_ids"] = new JsonArray(), ["dst_port"] = "",
        };
        set?.Invoke(r);
        return r;
    }

    private static ConfigState Legacy(bool guest = false, params JsonObject[] rules)
    {
        var s = new ConfigState();
        s.Collections["networkconf"] =
        [
            new JsonObject { ["_id"] = "s-lan", ["name"] = "Default", ["purpose"] = "corporate", ["ip_subnet"] = "10.0.1.1/24" },
            new JsonObject { ["_id"] = "s-iot", ["name"] = "IoT", ["purpose"] = "corporate", ["vlan_enabled"] = true, ["vlan"] = 30, ["ip_subnet"] = "10.0.30.1/24" },
        ];
        if (guest)
            s.Collections["networkconf"].Add(new JsonObject { ["_id"] = "s-guest", ["name"] = "Guest", ["purpose"] = "guest", ["vlan_enabled"] = true, ["vlan"] = 50, ["ip_subnet"] = "10.0.50.1/24" });
        s.Collections["firewallgroup"] =
        [
            new JsonObject { ["_id"] = "g-rfc", ["name"] = "Private", ["group_type"] = "address-group", ["group_members"] = new JsonArray("10.0.0.0/8") },
            new JsonObject { ["_id"] = "g-gw", ["name"] = "Gateways", ["group_type"] = "address-group", ["group_members"] = new JsonArray("10.0.1.1", "10.0.30.1") },
            new JsonObject { ["_id"] = "g-web", ["name"] = "Web", ["group_type"] = "port-group", ["group_members"] = new JsonArray("80", "443") },
        ];
        s.Collections["firewallrule"] = [.. rules];
        return s;
    }

    private static ConfigState Live()
    {
        var s = new ConfigState();
        s.Collections["networkconf"] = [new JsonObject { ["_id"] = "l-lan", ["name"] = "Default", ["purpose"] = "corporate", ["ip_subnet"] = "10.0.1.1/24" }];
        s.Collections["firewall_zone"] =
        [
            new JsonObject { ["_id"] = "z-int", ["name"] = "Internal", ["zone_key"] = "internal", ["network_ids"] = new JsonArray("l-lan") },
            new JsonObject { ["_id"] = "z-ext", ["name"] = "External", ["zone_key"] = "external", ["network_ids"] = new JsonArray() },
            new JsonObject { ["_id"] = "z-gw", ["name"] = "Gateway", ["zone_key"] = "gateway", ["network_ids"] = new JsonArray() },
            new JsonObject { ["_id"] = "z-vpn", ["name"] = "Vpn", ["zone_key"] = "vpn", ["network_ids"] = new JsonArray() },
            new JsonObject { ["_id"] = "z-hot", ["name"] = "Hotspot", ["zone_key"] = "hotspot", ["network_ids"] = new JsonArray() },
        ];
        s.Collections["firewall_policy"] =
        [
            new JsonObject { ["_id"] = "p-pre", ["name"] = "Allow All Traffic", ["predefined"] = true, ["index"] = 30000,
                ["source"] = new JsonObject { ["zone_id"] = "z-int" }, ["destination"] = new JsonObject { ["zone_id"] = "z-int" } },
        ];
        s.Collections["firewallrule"] = [];
        return s;
    }

    private static List<JsonObject> Policies(LegacyTranslation t, string name) =>
        t.Desired.Get("firewall_policy").Where(p => p["name"]?.ToString() == name).ToList();

    [Fact]
    public void StateRules_AreCoveredByTheZoneFirewall()
    {
        var t = LegacyFirewallTranslator.Translate(Legacy(false,
            Rule("r1", "LAN_IN", "accept", "Allow Established/Related", r => { r["state_established"] = true; r["state_related"] = true; }),
            Rule("r2", "LAN_IN", "drop", "Drop Invalid State", r => r["state_invalid"] = true)), Live(), deleteMissing: true);

        t.Rules.Should().OnlyContain(r => r.Status == LegacyRuleStatus.Covered);
        t.Desired.Get("firewall_policy").Should().ContainSingle(p => p["predefined"]!.GetValue<bool>());
    }

    [Fact]
    public void NetworkToNetworkBlock_BecomesOnePolicyInTheirZones()
    {
        var t = LegacyFirewallTranslator.Translate(Legacy(false,
            Rule("r3", "LAN_IN", "drop", "Block IoT to LAN", r => { r["src_networkconf_id"] = "s-iot"; r["dst_networkconf_id"] = "s-lan"; })), Live(), true);

        var p = Policies(t, "Block IoT to LAN").Should().ContainSingle().Subject;
        p["action"]!.ToString().Should().Be("BLOCK");
        p["ip_version"]!.ToString().Should().Be("IPV4");
        p["source"]!["zone_id"]!.ToString().Should().Be("z-int");
        p["source"]!["network_ids"]![0]!.ToString().Should().Be("s-iot");
        p["destination"]!["network_ids"]![0]!.ToString().Should().Be("s-lan");
        // The new IoT network joins Internal under its source ID; the plan maps it to the created network.
        t.Desired.Get("firewall_zone").First(z => z["_id"]!.ToString() == "z-int")["network_ids"]!.AsArray().Select(n => n!.ToString())
            .Should().BeEquivalentTo("s-lan", "s-iot");
    }

    [Fact]
    public void AddressGroupBlock_CoversEveryZoneItCanReach()
    {
        var t = LegacyFirewallTranslator.Translate(Legacy(false,
            Rule("r4", "LAN_IN", "drop", "Block Inter-Network Routing", r =>
            {
                r["src_firewallgroup_ids"] = new JsonArray("g-rfc");
                r["dst_firewallgroup_ids"] = new JsonArray("g-rfc", "g-web");
            })), Live(), true);

        var policies = Policies(t, "Block Inter-Network Routing");
        policies.Select(p => p["source"]!["zone_id"]!.ToString()).Distinct().Should().Equal("z-int");
        // 10.0.0.0/8 reaches past the LANs, so the part outside leaves by WAN or VPN.
        policies.Select(p => p["destination"]!["zone_id"]!.ToString()).Should().BeEquivalentTo("z-int", "z-ext", "z-vpn");
        policies[0]["source"]!["ip_group_id"]!.ToString().Should().Be("g-rfc");
        policies[0]["destination"]!["port_group_id"]!.ToString().Should().Be("g-web");
        policies[0]["destination"]!["port_matching_type"]!.ToString().Should().Be("OBJECT");
    }

    [Fact]
    public void LanLocal_TargetsTheGatewayZone()
    {
        var t = LegacyFirewallTranslator.Translate(Legacy(false,
            Rule("r5", "LAN_LOCAL", "drop", "Block IoT to Gateways", r => { r["src_networkconf_id"] = "s-iot"; r["dst_firewallgroup_ids"] = new JsonArray("g-gw"); })), Live(), true);

        var p = Policies(t, "Block IoT to Gateways").Should().ContainSingle().Subject;
        p["destination"]!["zone_id"]!.ToString().Should().Be("z-gw");
        p["destination"]!["ip_group_id"]!.ToString().Should().Be("g-gw");
    }

    [Fact]
    public void LanOut_AndGuestAmbiguity_AreUnproven()
    {
        var t = LegacyFirewallTranslator.Translate(Legacy(true,
            Rule("r6", "LAN_OUT", "drop", "Block All from Sea", r => r["src_firewallgroup_ids"] = new JsonArray("g-gw")),
            Rule("r7", "LAN_IN", "drop", "Block Everything", r => r["dst_networkconf_id"] = "s-iot")), Live(), true);

        t.Unproven.Select(r => r.Name).Should().BeEquivalentTo("Block All from Sea", "Block Everything");
        t.Desired.Get("firewall_zone").First(z => z["_id"]!.ToString() == "z-hot")["network_ids"]!.AsArray().Select(n => n!.ToString())
            .Should().Equal("s-guest");
    }

    [Fact]
    public void UnprovenRules_StopAtReviewPlan()
    {
        var t = LegacyFirewallTranslator.Translate(Legacy(false,
            Rule("r6", "LAN_OUT", "drop", "Block All from Sea"),
            Rule("r8", "LAN_OUT", "drop", "Old Rule", r => r["enabled"] = false)), Live(), true);
        var doc = PlanBuilder.Build(t.Desired, Live(), new PlanOptions { SameSite = false });

        ChangeControlService.AddTranslation(doc, t);

        doc.Regressions.Should().ContainSingle(r => r.Key == "legacy:r6");
        doc.Notes.Should().Contain(n => n.Contains("Old Rule"));
        doc.Errors.Should().BeEmpty();
    }

    [Fact]
    public void TranslatedRestore_PlansPolicyCreatesBeforeThePredefinedOnes()
    {
        var t = LegacyFirewallTranslator.Translate(Legacy(false,
            Rule("r3", "LAN_IN", "drop", "Block IoT to LAN", r => { r["src_networkconf_id"] = "s-iot"; r["dst_networkconf_id"] = "s-lan"; })), Live(), true);

        var doc = PlanBuilder.Build(t.Desired, Live(), new PlanOptions { SameSite = false });

        doc.Errors.Should().BeEmpty();
        doc.Operations.Should().Contain(o => o.Collection == "networkconf" && o.Action == PlanAction.Create && o.ObjectName == "IoT");
        doc.Operations.Should().Contain(o => o.Collection == "firewall_policy" && o.Action == PlanAction.Create && o.ObjectName == "Block IoT to LAN");
        doc.Operations.Should().NotContain(o => o.Collection == "firewall_policy" && o.Action == PlanAction.Delete);
    }
}
