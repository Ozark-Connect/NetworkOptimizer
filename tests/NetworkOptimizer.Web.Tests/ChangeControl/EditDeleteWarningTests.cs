using System.Text.Json.Nodes;
using FluentAssertions;
using NetworkOptimizer.Web.Services.ChangeControl;
using Xunit;

namespace NetworkOptimizer.Web.Tests.ChangeControl;

/// <summary>The Edit tab's delete warnings: checked against the whole edit, and one line per owning feature.</summary>
public class EditDeleteWarningTests
{
    private static JsonObject Obj(string json) => (JsonObject)JsonNode.Parse(json)!;

    private static JsonObject Side(string zone) => new()
    {
        ["zone_id"] = zone, ["matching_target"] = "ANY", ["port_matching_type"] = "ANY", ["match_opposite_ports"] = false,
    };

    private static JsonObject Policy(string id, string name, string src, string dst) => new()
    {
        ["_id"] = id, ["name"] = name, ["action"] = "ALLOW", ["enabled"] = true, ["protocol"] = "all", ["ip_version"] = "BOTH",
        ["index"] = 10000, ["source"] = Side(src), ["destination"] = Side(dst),
    };

    private static ConfigState Site()
    {
        var state = new ConfigState();
        state.Collections["firewall_zone"] =
        [
            Obj("""{"_id":"z1","name":"Internal","zone_key":"internal","default_zone":true,"network_ids":[]}"""),
            Obj("""{"_id":"z3","name":"IoT","default_zone":false,"network_ids":[]}"""),
        ];
        state.Collections["firewall_policy"] = [Policy("c1", "Allow Printer", "z1", "z3"), Policy("c2", "Allow Hub", "z3", "z1")];
        return state;
    }

    private static Dictionary<string, string> Files(ConfigState file, params string[] collections) =>
        collections.ToDictionary(c => c, c => YamlConverter.Export(file, [c], SecretMode.Redact));

    private static ConfigState WithoutIot(ConfigState state)
    {
        state.Collections["firewall_zone"] = state.Get("firewall_zone").Where(z => ConfigState.IdOf(z) != "z3").ToList();
        return state;
    }

    private static string[] StillUsed(List<YamlProblem> problems) =>
        problems.Where(p => p.Message.Contains("is still used by")).Select(p => p.Message).ToArray();

    [Fact]
    public void ZoneDeleted_WithItsPoliciesDeletedInThePolicyFile_IsNotStillUsed()
    {
        var file = WithoutIot(Site());
        file.Collections["firewall_policy"] = [];
        StillUsed(ChangeControlService.ParseEdits(Site(), Files(file, "firewall_zone", "firewall_policy")).Problems).Should().BeEmpty();
    }

    [Fact]
    public void ZoneDeleted_WithItsPoliciesRepointedInThePolicyFile_IsNotStillUsed()
    {
        var file = WithoutIot(Site());
        file.Collections["firewall_policy"] = [Policy("c1", "Allow Printer", "z1", "z1"), Policy("c2", "Allow Hub", "z1", "z1")];
        StillUsed(ChangeControlService.ParseEdits(Site(), Files(file, "firewall_zone", "firewall_policy")).Problems).Should().BeEmpty();
    }

    [Fact]
    public void ZoneDeleted_WithThePolicyFileNotUploaded_IsStillUsed()
    {
        var file = WithoutIot(Site());
        StillUsed(ChangeControlService.ParseEdits(Site(), Files(file, "firewall_zone")).Problems)
            .Should().ContainSingle().Which.Should().Contain("Allow Printer").And.Contain("Allow Hub");
    }

    [Fact]
    public void ZoneDeleted_WithOnePolicyStillInItInThePolicyFile_NamesOnlyThatOne()
    {
        var file = WithoutIot(Site());
        file.Collections["firewall_policy"] = [Policy("c1", "Allow Printer", "z1", "z1"), Policy("c2", "Allow Hub", "z3", "z1")];
        StillUsed(ChangeControlService.ParseEdits(Site(), Files(file, "firewall_zone", "firewall_policy")).Problems)
            .Should().ContainSingle().Which.Should().Contain("Allow Hub").And.NotContain("Allow Printer");
    }

    [Fact]
    public void ThreatIntelligencePolicies_LeftOut_AreOneWarning_WithCounts()
    {
        var live = Site();
        var prefix = ConfigCollections.NoOwnedPrefix;
        live.Collections["firewall_policy"].AddRange(
        [
            Policy("t1", prefix + "Exception", "z1", "z3"), Policy("t2", prefix + "Exception", "z3", "z1"),
            Policy("t3", prefix + "Blocklist", "z3", "z1"),
        ]);
        var file = Site();
        var warnings = ChangeControlService.ParseEdits(live, Files(file, "firewall_policy")).Problems
            .Where(p => p.Message.Contains("Threat Intelligence")).ToList();

        warnings.Should().ContainSingle().Which.Message.Should().Be(
            $"Firewall policies {prefix}Exception (2) and {prefix}Blocklist belong to Threat Intelligence and are not deleted here.");
    }

    [Fact]
    public void ThreatIntelligencePolicies_InAZoneTheEditDeletes_GoWithTheirZone_AndTheRestStay()
    {
        var prefix = ConfigCollections.NoOwnedPrefix;
        var live = Site();
        live.Collections["firewall_policy"].AddRange(
        [
            Policy("t1", prefix + "Exception", "z3", "z1"), Policy("t2", prefix + "Exception", "z1", "z3"),
            Policy("t3", prefix + "Exception", "z1", "z1"),
        ]);
        var file = WithoutIot(Site());
        file.Collections["firewall_policy"] = [];
        var (_, problems, touched) = ChangeControlService.ParseEdits(live, Files(file, "firewall_zone", "firewall_policy"));
        var lines = problems.Where(p => p.Message.Contains("Threat Intelligence")).Select(p => p.Message).ToList();

        lines.Should().BeEquivalentTo(
        [
            $"Firewall policies {prefix}Exception (2) belong to Threat Intelligence and are deleted with their zone; Threat Intelligence rewrites its policies for the zones left.",
            $"Firewall Policy {prefix}Exception belongs to Threat Intelligence and is not deleted here.",
        ]);
    }

    [Fact]
    public void OneThreatIntelligencePolicy_LeftOut_KeepsTheSingleSentence()
    {
        var live = Site();
        live.Collections["firewall_policy"].Add(Policy("t1", ConfigCollections.NoOwnedPrefix + "Exception", "z1", "z3"));
        ChangeControlService.ParseEdits(live, Files(Site(), "firewall_policy")).Problems
            .Should().ContainSingle(p => p.Level == "Warning").Which.Message
            .Should().Be($"Firewall Policy {ConfigCollections.NoOwnedPrefix}Exception belongs to Threat Intelligence and is not deleted here.");
    }
}
