using System.Text.Json.Nodes;
using FluentAssertions;
using NetworkOptimizer.Web.Services.ChangeControl;
using Xunit;

namespace NetworkOptimizer.Web.Tests.ChangeControl;

public class DeleteCascadeTests
{
    private static JsonObject Obj(string json) => (JsonObject)JsonNode.Parse(json)!;

    [Fact]
    public void Find_TellsListsFromSingleReferences()
    {
        var wlan = Obj("""{"_id":"w1","name":"Home","networkconf_id":"n1","ap_group_ids":["g1","g2"]}""");
        DeleteCascade.Find(wlan, "g1").Should().ContainSingle().Which.Should().Be(new DeleteCascade.Hit("ap_group_ids", true, 2));
        DeleteCascade.Find(wlan, "n1").Should().ContainSingle().Which.InList.Should().BeFalse();
        DeleteCascade.Find(wlan, "w1").Should().BeEmpty("an object's own ID is not a reference");
    }

    [Fact]
    public void CanCascade_OnlyWhenEveryListKeepsAnotherEntry()
    {
        DeleteCascade.CanCascade([new("ap_group_ids", true, 2)]).Should().BeTrue();
        DeleteCascade.CanCascade([new("ap_group_ids", true, 1)]).Should().BeFalse("an emptied match list can mean any");
        DeleteCascade.CanCascade([new("networkconf_id", false, 0)]).Should().BeFalse();
        DeleteCascade.CanCascade([new("ap_group_ids", true, 2), new("networkconf_id", false, 0)]).Should().BeFalse();
    }

    [Fact]
    public void Apply_RemovesListReferences_LeavesSingleOnes_AndNamesTheCollections()
    {
        var baseState = new ConfigState();
        baseState.Collections["apgroups"] = [Obj("""{"_id":"g1","name":"Upstairs"}"""), Obj("""{"_id":"g2","name":"All APs"}""")];
        baseState.Collections["wlanconf"] =
        [
            Obj("""{"_id":"w1","name":"Home","ap_group_ids":["g1","g2"]}"""),
            Obj("""{"_id":"w2","name":"Guest","ap_group_ids":["g1"]}"""),
        ];
        var desired = baseState.Clone();
        desired.Collections["apgroups"] = [Obj("""{"_id":"g2","name":"All APs"}""")];

        var changed = DeleteCascade.Apply(baseState, desired, ["apgroups"]);

        changed.Should().BeEquivalentTo(["wlanconf"]);
        desired.Get("wlanconf")[0]["ap_group_ids"]!.AsArray().Select(e => e!.ToString()).Should().Equal("g2");
        desired.Get("wlanconf")[1]["ap_group_ids"]!.AsArray().Select(e => e!.ToString()).Should().Equal("g1");
    }

    [Fact]
    public void Apply_NeverCascadesFromProtectedObjects()
    {
        var baseState = new ConfigState();
        baseState.Collections["apgroups"] = [Obj("""{"_id":"g1","name":"Default","attr_no_delete":true}"""), Obj("""{"_id":"g2","name":"B"}""")];
        baseState.Collections["wlanconf"] = [Obj("""{"_id":"w1","name":"Home","ap_group_ids":["g1","g2"]}""")];
        var desired = baseState.Clone();
        desired.Collections["apgroups"] = [Obj("""{"_id":"g2","name":"B"}""")];

        DeleteCascade.Apply(baseState, desired, ["apgroups"]).Should().BeEmpty();
    }

    [Fact]
    public void Lint_ReadOnlyCollection_SaysTheDeleteIsNotApplied()
    {
        var baseState = new ConfigState();
        baseState.Collections["wlangroup"] = [Obj("""{"_id":"wg1","name":"Default"}"""), Obj("""{"_id":"wg2","name":"Other"}""")];
        baseState.Collections["device"] = [Obj("""{"_id":"d1","name":"AP","mac":"aa:bb:cc:00:00:01","wlangroup_id_na":"wg1"}""")];
        var yamlName = YamlConverter.YamlName(ConfigCollections.Get("wlangroup")!);

        var result = YamlConverter.Parse($"{yamlName}:\n- name: Other\n", baseState, "wlans.yaml");

        result.Problems.Should().ContainSingle(p => p.Level == "Warning" && p.Message.Contains("read-only") && p.Message.Contains("Default"));
        result.Problems.Should().NotContain(p => p.Message.Contains("is still used by"));
    }
}
