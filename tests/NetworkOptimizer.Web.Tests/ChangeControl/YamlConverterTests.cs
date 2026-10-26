using FluentAssertions;
using NetworkOptimizer.Web.Services.ChangeControl;
using Xunit;
using static NetworkOptimizer.Web.Tests.ChangeControl.ConfigDiffEngineTests;

namespace NetworkOptimizer.Web.Tests.ChangeControl;

public class YamlConverterTests
{
    private static ConfigState Site() => State(
        ("networkconf", """{"_id":"n1","name":"Default","purpose":"corporate"}"""),
        ("networkconf", """{"_id":"n2","name":"IoT","purpose":"corporate","vlan":30,"vlan_enabled":true}"""),
        ("networkconf", """{"_id":"n3","name":"IoT","purpose":"corporate","vlan":31,"vlan_enabled":true}"""),
        ("wlanconf", """{"_id":"w1","name":"Home","security":"wpapsk","wpa_mode":"wpa2","x_passphrase":"hunter2","networkconf_id":"n1","wlan_bands":["2g","5g","6g"]}"""));

    [Fact]
    public void SplitByCollection_KeepsExportedObjects_InTheirCollection()
    {
        var yaml = YamlConverter.Export(Site(), ["networkconf", "wlanconf"], includeSecrets: false) + "bogus: []\n";
        var unknown = new List<string>();

        var segments = YamlConverter.SplitByCollection(yaml, unknown);

        segments.Select(s => s.Def.Key).Should().Equal("networkconf", "wlanconf");
        segments[0].Text.Should().Contain("name: Default").And.Contain("vlan: 31");
        segments[1].Text.Should().Contain("name: Home");
        unknown.Should().Equal("bogus");
    }

    [Fact]
    public void Export_StripsReferenceSuffix_AndRedactsSecrets()
    {
        var yaml = YamlConverter.Export(Site(), ["wlanconf"], includeSecrets: false);
        yaml.Should().Contain("wlans:");
        yaml.Should().Contain("network: Default");
        yaml.Should().Contain("x_passphrase: <keep current>");
        yaml.Should().Contain("wlan_bands: [2g, 5g, 6g]");
        yaml.Should().NotContain("hunter2");
    }

    [Fact]
    public void Export_IncludeSecrets_WritesThem()
    {
        YamlConverter.Export(Site(), ["wlanconf"], includeSecrets: true).Should().Contain("hunter2");
    }

    [Fact]
    public void Export_CollidingName_UsesTheVlan()
    {
        var state = Site();
        state.Collections["wlanconf"][0]["networkconf_id"] = "n2";
        YamlConverter.Export(state, ["wlanconf"], false).Should().Contain("network: {vlan: 30}");
    }

    [Fact]
    public void RoundTrip_ReproducesTheObjects()
    {
        var state = Site();
        var yaml = YamlConverter.Export(state, ["wlanconf"], includeSecrets: true);
        var parsed = YamlConverter.Parse(yaml, state, "wlans.yaml");
        parsed.HasErrors.Should().BeFalse(string.Join("; ", parsed.Problems.Select(p => p.Message)));
        var w = parsed.Collections["wlanconf"].Single();
        w["networkconf_id"]!.GetValue<string>().Should().Be("n1");
        w["x_passphrase"]!.GetValue<string>().Should().Be("hunter2");
        w["wlan_bands"]!.AsArray().Should().HaveCount(3);
        var desired = state.Clone();
        desired.Collections["wlanconf"] = parsed.Collections["wlanconf"];
        PlanBuilder.Build(desired, state, new PlanOptions()).Operations.Should().BeEmpty();
    }

    [Fact]
    public void Parse_AmbiguousName_IsAnError_SuggestingTheVlan()
    {
        var yaml = """
            wlans:
              - name: IoT
                security: wpapsk
                network: IoT
            """;
        var parsed = YamlConverter.Parse(yaml, Site(), "wlans.yaml");
        var problem = parsed.Problems.Should().ContainSingle(p => p.Level == "Error").Subject;
        problem.Line.Should().Be(4);
        problem.Message.Should().Contain("{vlan: 30}");
    }

    [Fact]
    public void Parse_EnforcedKeyReference_Resolves()
    {
        var yaml = """
            wlans:
              - name: IoT
                network: {vlan: 31}
            """;
        YamlConverter.Parse(yaml, Site(), "wlans.yaml").Collections["wlanconf"].Single()["networkconf_id"]!.GetValue<string>().Should().Be("n3");
    }

    [Fact]
    public void Parse_Wpa3Transition_IsANote()
    {
        var yaml = """
            wlans:
              - name: Home
                wpa3_transition: true
                network: Default
            """;
        YamlConverter.Parse(yaml, Site(), "wlans.yaml").Problems.Should().Contain(p => p.Level == "Info" && p.Message.Contains("WPA3"));
    }

    [Fact]
    public void Parse_BadYaml_ReportsTheLine()
    {
        YamlConverter.Parse("wlans:\n  - name: [unclosed\n", Site(), "wlans.yaml").Problems.Should().ContainSingle(p => p.Level == "Error");
    }
}
