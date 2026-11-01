using System.Text.Json.Nodes;
using FluentAssertions;
using NetworkOptimizer.Web.Services.ChangeControl;
using Xunit;

namespace NetworkOptimizer.Web.Tests.ChangeControl;

/// <summary>An Edit tab file exported and parsed back unchanged must parse clean.</summary>
public class EditRoundTripTests
{
    private static JsonObject Obj(string json) => (JsonObject)JsonNode.Parse(json)!;

    // Parsed as the Edit tab parses: one file per collection group, through ParseEdits. The download carries no IDs and
    // redacts secrets (<keep current>); the tab's own files carry every ID and masked secret tokens. All must parse clean.
    private static List<YamlProblem> RoundTrip(ConfigState state, params string[] collections) =>
        new[] { (Ids: false, Secrets: SecretMode.Redact), (Ids: false, Secrets: SecretMode.Mask), (Ids: true, Secrets: SecretMode.Mask) }
            .SelectMany(mode =>
            {
                var files = collections.Select(ConfigCollections.Get).GroupBy(d => d!.Group)
                    .ToDictionary(g => g.First()!.Key, g => YamlConverter.Export(state, g.Select(d => d!.Key), mode.Secrets, mode.Ids));
                return ChangeControlService.ParseEdits(state, files).Problems
                    .Select(p => new YamlProblem(p.Level, $"ids={mode.Ids} {mode.Secrets}", p.Line, p.Message));
            }).ToList();
    [Fact]
    public void PortProfileReference_IsNotTakenByAnLldpPortId()
    {
        // The first device carries an LLDP neighbor's port_id ("local Port 1"): it also strips to "port".
        var state = new ConfigState();
        state.Collections["portconf"] = [Obj("""{"_id":"pc1","name":"[Access] Home"}""")];
        state.Collections["device"] =
        [
            Obj("""{"_id":"d1","mac":"aa:bb:cc:00:00:01","name":"[AP] Upstairs","lldp_table":[{"local_port_idx":1,"port_id":"local Port 1"}]}"""),
            Obj("""{"_id":"d2","mac":"aa:bb:cc:00:00:02","name":"[Switch] Office","port_overrides":[{"port_idx":2,"portconf_id":"pc1"}]}"""),
        ];

        foreach (var ids in new[] { false, true })
        {
            var file = YamlConverter.Export(state, ["portconf", "device"], SecretMode.Redact, ids);
            var files = new Dictionary<string, string> { ["device"] = file };
            var (desired, problems, _) = ChangeControlService.ParseEdits(state, files);
            problems.Should().BeEmpty();
            var over = desired.Get("device").Single(d => ConfigState.IdOf(d) == "d2")["port_overrides"]![0]!.AsObject();
            over["portconf_id"]?.ToString().Should().Be("pc1");
            over.ContainsKey("port_id").Should().BeFalse();
        }
    }

    private static ConfigState PortOverrideSite()
    {
        var state = new ConfigState();
        state.Collections["portconf"] = [Obj("""{"_id":"pc1","name":"[Access] Home"}""")];
        state.Collections["device"] = [Obj("""{"_id":"d2","mac":"aa:bb:cc:00:00:02","name":"[Switch] Office","port_overrides":[{"port_idx":2,"portconf_id":"pc1"}]}""")];
        return state;
    }

    [Fact]
    public void PortProfileReference_IsWrittenAsPortProfile()
    {
        var yaml = YamlConverter.Export(PortOverrideSite(), ["device"], SecretMode.Redact);
        yaml.Should().Contain("port_profile: \"[Access] Home\"").And.NotMatchRegex(@"\n\s+port: ");
    }

    [Fact]
    public void OlderFile_PortInAPortOverride_StillReadsAsThePortProfile()
    {
        var state = PortOverrideSite();
        var older = YamlConverter.Export(state, ["device"], SecretMode.Redact).Replace("port_profile:", "port:");
        var (desired, problems, _) = ChangeControlService.ParseEdits(state, new Dictionary<string, string> { ["device"] = older });
        problems.Should().BeEmpty();
        desired.Get("device")[0]["port_overrides"]![0]!["portconf_id"]?.ToString().Should().Be("pc1");
    }

    [Fact]
    public void FirewallPolicyPort_IsNotTakenForAPortProfile()
    {
        var state = new ConfigState();
        state.Collections["portconf"] = [Obj("""{"_id":"pc1","name":"53"}""")];
        state.Collections["firewall_zone"] = [Obj("""{"_id":"z1","name":"Internal","zone_key":"internal","network_ids":[]}""")];
        state.Collections["firewall_policy"] =
        [
            Obj("""
                {"_id":"p1","name":"DNS","action":"ALLOW","enabled":true,"protocol":"udp","ip_version":"BOTH","index":10000,
                 "source":{"zone_id":"z1","matching_target":"ANY","port_matching_type":"ANY","match_opposite_ports":false},
                 "destination":{"zone_id":"z1","matching_target":"ANY","port_matching_type":"SPECIFIC","port":"53","match_opposite_ports":false}}
                """),
        ];
        var yaml = YamlConverter.Export(state, ["firewall_zone", "firewall_policy"], SecretMode.Redact);
        var (desired, problems, _) = ChangeControlService.ParseEdits(state, new Dictionary<string, string> { ["firewall_zone"] = yaml });
        problems.Should().BeEmpty();
        var dst = desired.Get("firewall_policy")[0]["destination"]!.AsObject();
        dst["port"]?.ToString().Should().Be("53");
        dst.ContainsKey("portconf_id").Should().BeFalse();
    }

    [Fact]
    public void ClientGroupReferences_ResolveToClientGroups()
    {
        var state = new ConfigState();
        state.Collections["networkconf"] = [Obj("""{"_id":"n1","name":"LAN","purpose":"corporate","vlan_enabled":false}""")];
        state.Collections["network_members_group"] = [Obj("""{"_id":"g1","name":"Gaming","type":"CLIENTS","members":["aa:bb:cc:dd:ee:01"]}""")];
        state.Collections["user"] = [Obj("""{"_id":"u1","mac":"aa:bb:cc:dd:ee:01","name":"Rig","network_members_group_ids":["g1"]}""")];

        RoundTrip(state, "network_members_group", "user").Should().BeEmpty();
    }

    [Fact]
    public void RadiusProfileReference_ResolvesWhenTheV2MirrorSharesTheId()
    {
        var state = new ConfigState();
        state.Collections["radiusprofile"] = [Obj("""{"_id":"r1","name":"Default","attr_hidden_id":"Default","attr_no_delete":true}""")];
        state.Collections["radius_profile_v2"] = [Obj("""{"_id":"r1","name":"Default","attr_hidden_id":"Default","attr_no_delete":true}""")];
        state.Collections["setting"] = [Obj("""{"_id":"s1","key":"global_switch","radiusprofile_id":"r1"}""")];

        RoundTrip(state, "setting", "radiusprofile", "radius_profile_v2").Should().BeEmpty();
    }

    [Fact]
    public void RadiusGroupFile_UnchangedBuiltInProfile_IsNotAnEdit()
    {
        var state = new ConfigState();
        const string servers = """ "auth_servers":[{"ip":"192.0.2.1","port":1812,"x_secret":"s3cret"}] """;
        // The v1 object carries fields its v2 mirror lacks (Network 11.0.99).
        state.Collections["radiusprofile"] = [Obj($$"""{"_id":"r1","name":"Default","attr_hidden_id":"Default","attr_no_delete":true,"vlan_enabled":false,"tls_enabled":false,"use_usg_auth_server":true,{{servers}}}""")];
        state.Collections["radius_profile_v2"] =
        [
            Obj($$"""{"_id":"r1","name":"Default","attr_hidden_id":"Default","attr_no_delete":true,"vlan_enabled":false,{{servers}}}"""),
            Obj("""{"_id":"r2","name":"UID","attr_hidden_id":"Uid","attr_no_delete":true}"""),
        ];

        RoundTrip(state, "radiusprofile", "radius_profile_v2").Should().BeEmpty();
    }

    [Fact]
    public void PredefinedPolicies_RoundTripWithoutBecomingNewPolicies()
    {
        var state = new ConfigState();
        state.Collections["firewall_zone"] =
        [
            Obj("""{"_id":"z1","name":"Internal","zone_key":"internal","default_zone":true,"network_ids":[]}"""),
            Obj("""{"_id":"z3","name":"IoT","default_zone":false,"network_ids":[]}"""),
        ];
        JsonObject Side(string zone) => new()
        {
            ["zone_id"] = zone, ["matching_target"] = "ANY", ["port_matching_type"] = "ANY", ["match_opposite_ports"] = false,
        };
        JsonObject Policy(string src, string dst, int index) => new()
        {
            ["_id"] = $"{src}{dst}{index}", ["name"] = "Block All Traffic", ["action"] = "BLOCK", ["predefined"] = true,
            ["index"] = index, ["create_allow_respond"] = true, ["enabled"] = true, ["protocol"] = "all",
            ["ip_version"] = "BOTH", ["source"] = Side(src), ["destination"] = Side(dst),
        };
        state.Collections["firewall_policy"] = [Policy("z1", "z3", 2147483647), Policy("z3", "z1", 2147483647), Policy("z3", "z3", 2147483647)];

        RoundTrip(state, "firewall_zone", "firewall_policy").Should().BeEmpty();
    }

    [Fact]
    public void DerivedTrafficRoute_SharingItsObjectConfigsName_RoundTrips()
    {
        // UniFi derives a traffic route from an object-oriented config, names it the same, and points origin_id at it.
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
        ];

        RoundTrip(state, "oon_config", "trafficroute", "networkconf").Should().BeEmpty();
    }
}
