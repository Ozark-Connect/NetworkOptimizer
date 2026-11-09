using System.Text.Json.Nodes;
using FluentAssertions;
using NetworkOptimizer.Web.Services.ChangeControl;
using Xunit;

namespace NetworkOptimizer.Web.Tests.ChangeControl;

/// <summary>
/// An uploaded file that differs from the console in objects UniFi generates (an older file whose zones differ) is not
/// refused for them, and Review Plan writes none of them; every other read-only rule still holds.
/// </summary>
public class EditGeneratedObjectsTests
{
    private static JsonObject Obj(string json) => (JsonObject)JsonNode.Parse(json)!;

    private static readonly (bool Ids, SecretMode Secrets)[] Modes =
        [(false, SecretMode.Redact), (false, SecretMode.Mask), (true, SecretMode.Mask)];

    private static JsonObject Side(string zone) => new()
    {
        ["zone_id"] = zone, ["matching_target"] = "ANY", ["port_matching_type"] = "ANY", ["match_opposite_ports"] = false,
    };

    private static JsonObject Policy(string id, string name, string src, string dst, string action, int index, bool predefined) => new()
    {
        ["_id"] = id, ["name"] = name, ["action"] = action, ["predefined"] = predefined, ["index"] = index,
        ["create_allow_respond"] = true, ["enabled"] = true, ["protocol"] = "all", ["ip_version"] = "BOTH",
        ["source"] = Side(src), ["destination"] = Side(dst),
    };

    private static ConfigState Site()
    {
        var state = new ConfigState();
        state.Collections["firewall_zone"] =
        [
            Obj("""{"_id":"z1","name":"Internal","zone_key":"internal","default_zone":true,"network_ids":[]}"""),
            Obj("""{"_id":"z3","name":"IoT","default_zone":false,"network_ids":[]}"""),
        ];
        state.Collections["firewall_policy"] =
        [
            Policy("p1", "Block All Traffic", "z3", "z1", "BLOCK", 2147483647, predefined: true),
            Policy("c1", "Allow Printer", "z1", "z3", "ALLOW", 10000, predefined: false),
        ];
        return state;
    }

    // As the Edit tab validates (ChangeControlService.ValidateYamlAsync): parse, then plan the touched collections.
    private static (List<YamlProblem> Problems, List<PlanOperation> Ops) Validate(ConfigState live, ConfigState file, (bool Ids, SecretMode Secrets) mode, params string[] collections)
    {
        var files = collections.Select(ConfigCollections.Get).GroupBy(d => d!.Group)
            .ToDictionary(g => g.First()!.Key, g => YamlConverter.Export(file, g.Select(d => d!.Key), mode.Secrets, mode.Ids));
        var (desired, problems, touched) = ChangeControlService.ParseEdits(live, files);
        var ops = touched.Count == 0 ? [] : PlanBuilder.Build(desired, live, new PlanOptions { Collections = touched }).Operations.ToList();
        return (problems.ToList(), ops);
    }

    private static string[] Errors(List<YamlProblem> problems) => problems.Where(p => p.Level == "Error").Select(p => p.Message).ToArray();

    [Fact]
    public void ChangedPredefinedPolicy_IsAnInfoLine_AndIsNotPlanned()
    {
        var older = Site();
        older.Get("firewall_policy").Single(p => ConfigState.IdOf(p) == "p1")["action"] = "ALLOW";
        foreach (var mode in Modes)
        {
            var (problems, ops) = Validate(Site(), older, mode, "firewall_zone", "firewall_policy");
            Errors(problems).Should().BeEmpty($"mode {mode}");
            problems.Should().ContainSingle(p => p.Level == "Info" && p.Message.Contains("Firewall policies: 1"), $"mode {mode}");
            ops.Should().BeEmpty($"mode {mode}");
        }
    }

    [Fact]
    public void MissingPredefinedPolicy_IsAnInfoLine_AndIsNotDeleted()
    {
        var older = Site();
        older.Collections["firewall_policy"] = older.Get("firewall_policy").Where(p => ConfigState.IdOf(p) != "p1").ToList();
        foreach (var mode in Modes)
        {
            var (problems, ops) = Validate(Site(), older, mode, "firewall_zone", "firewall_policy");
            Errors(problems).Should().BeEmpty($"mode {mode}");
            problems.Should().ContainSingle(p => p.Level == "Info" && p.Message.Contains("Firewall policies: 1"), $"mode {mode}");
            ops.Should().BeEmpty($"mode {mode}");
        }
    }

    private static JsonObject InternetBlock(string id, bool predefined)
    {
        // UniFi's "Internet Access" off policy for an older subnet, as an older file carries it.
        var p = Policy(id, "Block 192.0.2.0/24 Internet Access", "z1", "z3", "BLOCK", 30000, predefined);
        p["source"]!["matching_target"] = "IP";
        p["source"]!["ips"] = new JsonArray("192.0.2.0/24");
        return p;
    }

    [Fact]
    public void PredefinedPolicyTheConsoleLacks_SkipsTheCreateChecks_AndIsNotPlanned()
    {
        var older = Site();
        older.Collections["firewall_policy"].Add(InternetBlock("old1", predefined: true));
        foreach (var mode in Modes.Where(m => !m.Ids))
        {
            var (problems, ops) = Validate(Site(), older, mode, "firewall_zone", "firewall_policy");
            Errors(problems).Should().BeEmpty($"mode {mode}");
            problems.Should().ContainSingle(p => p.Level == "Info" && p.Message.Contains("Firewall policies: 1"), $"mode {mode}");
            ops.Should().BeEmpty($"mode {mode}");
        }
    }

    [Fact]
    public void CustomBlockPolicyWithReturnTraffic_IsStillRefused()
    {
        var edited = Site();
        edited.Collections["firewall_policy"].Add(InternetBlock("new1", predefined: false));
        foreach (var mode in Modes.Where(m => !m.Ids))
            Errors(Validate(Site(), edited, mode, "firewall_zone", "firewall_policy").Problems)
                .Should().Contain(e => e.Contains("creates return traffic (create_allow_respond) only with action ALLOW"), $"mode {mode}");
    }

    [Fact]
    public void UnchangedFile_HasNoInfoLine()
    {
        foreach (var mode in Modes)
            Validate(Site(), Site(), mode, "firewall_zone", "firewall_policy").Problems.Should().BeEmpty($"mode {mode}");
    }

    [Fact]
    public void CustomPolicyEdit_BesideAChangedPredefinedOne_IsStillPlanned()
    {
        var older = Site();
        older.Get("firewall_policy").Single(p => ConfigState.IdOf(p) == "p1")["action"] = "ALLOW";
        older.Get("firewall_policy").Single(p => ConfigState.IdOf(p) == "c1")["action"] = "BLOCK";
        foreach (var mode in Modes)
        {
            var (problems, ops) = Validate(Site(), older, mode, "firewall_zone", "firewall_policy");
            Errors(problems).Should().BeEmpty($"mode {mode}");
            ops.Should().ContainSingle($"mode {mode}").Which.Should().Match<PlanOperation>(o => o.Collection == "firewall_policy" && o.Action == PlanAction.Update);
        }
    }

    [Fact]
    public void CustomPolicyLeftOut_IsStillDeleted()
    {
        var older = Site();
        older.Collections["firewall_policy"] = older.Get("firewall_policy").Where(p => ConfigState.IdOf(p) != "c1").ToList();
        foreach (var mode in Modes)
        {
            var (problems, ops) = Validate(Site(), older, mode, "firewall_zone", "firewall_policy");
            Errors(problems).Should().BeEmpty($"mode {mode}");
            problems.Should().NotContain(p => p.Message.Contains("UniFi generates"), $"mode {mode}");
            ops.Should().ContainSingle($"mode {mode}").Which.Action.Should().Be(PlanAction.Delete);
        }
    }

    [Fact]
    public void BuiltInZoneLeftOut_IsStillRefused()
    {
        var older = Site();
        older.Collections["firewall_zone"] = older.Get("firewall_zone").Where(z => ConfigState.IdOf(z) != "z1").ToList();
        foreach (var mode in Modes)
            Errors(Validate(Site(), older, mode, "firewall_zone").Problems)
                .Should().Contain(e => e.Contains("Internal is built into UniFi and cannot be deleted"), $"mode {mode}");
    }

    [Fact]
    public void ProtectedNetworkLeftOut_IsStillRefused()
    {
        var live = new ConfigState();
        live.Collections["networkconf"] =
        [
            Obj("""{"_id":"n1","name":"Default","purpose":"corporate","vlan_enabled":false,"attr_no_delete":true,"attr_hidden_id":"LAN"}"""),
            Obj("""{"_id":"n2","name":"IoT","purpose":"corporate","vlan_enabled":true,"vlan":30}"""),
        ];
        var older = live.Clone();
        older.Collections["networkconf"] = older.Get("networkconf").Where(n => ConfigState.IdOf(n) != "n1").ToList();
        foreach (var mode in Modes)
            Errors(Validate(live, older, mode, "networkconf").Problems)
                .Should().Contain(e => e.Contains("Default is built into UniFi and cannot be deleted"), $"mode {mode}");
    }

    [Fact]
    public void ThreatIntelligencePolicy_EditIsStillRefused_AndRemovalStillWarned()
    {
        ConfigState Live()
        {
            var state = Site();
            state.Collections["firewall_policy"].Add(Policy("t1", ConfigCollections.NoOwnedPrefix + "Block Threats", "z3", "z1", "BLOCK", 9000, predefined: false));
            return state;
        }
        var edited = Live();
        edited.Get("firewall_policy").Single(p => ConfigState.IdOf(p) == "t1")["action"] = "ALLOW";
        var removed = Live();
        removed.Collections["firewall_policy"] = removed.Get("firewall_policy").Where(p => ConfigState.IdOf(p) != "t1").ToList();
        foreach (var mode in Modes)
        {
            Errors(Validate(Live(), edited, mode, "firewall_zone", "firewall_policy").Problems)
                .Should().Contain(e => e.Contains("belongs to Threat Intelligence"), $"mode {mode}");
            var (problems, ops) = Validate(Live(), removed, mode, "firewall_zone", "firewall_policy");
            problems.Should().Contain(p => p.Level == "Warning" && p.Message.Contains("belongs to Threat Intelligence"), $"mode {mode}");
            ops.Should().BeEmpty($"mode {mode}");
        }
    }

    [Fact]
    public void PredefinedObject_InACollectionWithoutIsGenerated_IsStillRefused()
    {
        // Review Plan would write it (only IsGenerated is skipped there), so the file check is its only guard.
        ConfigCollections.Get("trafficrule")!.IsGenerated.Should().BeNull();
        var live = new ConfigState();
        live.Collections["trafficrule"] = [Obj("""{"_id":"r1","description":"Built In","action":"BLOCK","enabled":true,"predefined":true,"matching_target":"INTERNET"}""")];
        var edited = live.Clone();
        edited.Get("trafficrule")[0]["action"] = "ALLOW";
        foreach (var mode in Modes)
            Errors(Validate(live, edited, mode, "trafficrule").Problems)
                .Should().Contain(e => e.Contains("is managed by UniFi and cannot be edited here"), $"mode {mode}");
    }

    [Fact]
    public void GeneratedObjects_StayLockedInTheEditor()
    {
        var schema = YamlConverter.EditSchema(Site(), [ConfigCollections.Get("firewall_policy")!]).ToJsonString();
        schema.Should().Contain("\"p1\":\"UniFi\"").And.NotContain("\"c1\":\"UniFi\"");
    }

    private static ConfigState RouteSite()
    {
        var state = new ConfigState();
        state.Collections["networkconf"] = [Obj("""{"_id":"n1","name":"VPN Client","purpose":"vpn-client"}""")];
        state.Collections["oon_config"] = [Obj("""{"id":"o1","name":"Phones -> VPN","target_type":"CLIENTS"}""")];
        state.Collections["trafficroute"] =
        [
            Obj("""
                {"_id":"obj_o1_1","description":"Phones -> VPN","enabled":true,"matching_target":"INTERNET","network_id":"n1",
                 "origin_id":"o1","origin_type":"object_pbr","predefined":true,"kill_switch_enabled":false,
                 "domains":[],"ip_addresses":[],"ip_ranges":[],"regions":[],"target_devices":[{"client_mac":"aa:bb:cc:dd:ee:01","type":"CLIENT"}]}
                """),
            Obj("""
                {"_id":"t2","description":"Office -> VPN","enabled":true,"matching_target":"INTERNET","network_id":"n1","kill_switch_enabled":false,
                 "domains":[],"ip_addresses":[],"ip_ranges":[],"regions":[],"target_devices":[{"client_mac":"aa:bb:cc:dd:ee:02","type":"CLIENT"}]}
                """),
        ];
        return state;
    }

    [Fact]
    public void DerivedTrafficRoute_ChangedOrLeftOut_IsNotPlanned_ButAUserRouteIs()
    {
        var changed = RouteSite();
        changed.Get("trafficroute")[0]["kill_switch_enabled"] = true;
        changed.Get("trafficroute")[1]["kill_switch_enabled"] = true;
        var leftOut = RouteSite();
        leftOut.Collections["trafficroute"] = [leftOut.Get("trafficroute")[1]];
        foreach (var mode in Modes)
        {
            var (problems, ops) = Validate(RouteSite(), changed, mode, "trafficroute");
            Errors(problems).Should().BeEmpty($"mode {mode}");
            problems.Should().ContainSingle(p => p.Level == "Info" && p.Message.Contains("Traffic routes: 1"), $"mode {mode}");
            ops.Should().ContainSingle($"mode {mode}").Which.Should().Match<PlanOperation>(o => o.Collection == "trafficroute" && o.Action == PlanAction.Update);

            (problems, ops) = Validate(RouteSite(), leftOut, mode, "trafficroute");
            Errors(problems).Should().BeEmpty($"mode {mode}");
            ops.Should().BeEmpty($"mode {mode}");
        }
    }
}
