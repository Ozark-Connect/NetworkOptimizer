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

    private const string LabNetworkFile = "networks:\n- name: Default\n  purpose: corporate\n- name: IoT\n  vlan: 30\n  vlan_enabled: true\n  purpose: corporate\n"
        + "- name: IoT\n  vlan: 31\n  vlan_enabled: true\n  purpose: corporate\n- name: Lab\n  vlan: 40\n  vlan_enabled: true\n  purpose: corporate\n";

    [Fact]
    public void Edit_WlanOnANetworkAnotherFileCreates_ResolvesToTheCreate()
    {
        // The WLAN file comes first, so the reference has to see the networks file's create.
        var files = new Dictionary<string, string>
        {
            ["wlanconf"] = YamlConverter.Export(Site(), ["wlanconf"], SecretMode.Mask, ids: true)
                + "- name: Lab Wi-Fi\n  security: wpapsk\n  x_passphrase: lab-passphrase\n  network: Lab\n",
            ["networkconf"] = LabNetworkFile,
        };
        var (desired, problems, touched) = ChangeControlService.ParseEdits(Site(), files);
        problems.Should().NotContain(p => p.Level == "Error", string.Join("; ", problems.Select(p => p.Message)));

        var doc = PlanBuilder.Build(desired, Site(), new PlanOptions { Collections = touched });

        doc.Errors.Should().BeEmpty();
        doc.Operations.Should().HaveCount(2).And.OnlyContain(o => o.Action == PlanAction.Create);
        var network = doc.Operations.Single(o => o.Collection == "networkconf");
        var wlan = doc.Operations.Single(o => o.Collection == "wlanconf");
        wlan.Body!["networkconf_id"]!.GetValue<string>().Should().Be(PlanBuilder.RefPrefix + network.Key);
        doc.Operations.IndexOf(network).Should().BeLessThan(doc.Operations.IndexOf(wlan));
        network.Body!.AsObject().ContainsKey("_id").Should().BeFalse("a temporary ID never reaches the console");
    }

    [Fact]
    public void Edit_ChangeToAnExistingObject_IsOneUpdate_WithRealIds()
    {
        var files = new Dictionary<string, string>
        {
            ["wlanconf"] = "wlans:\n- name: Home\n  security: wpapsk\n  network: {vlan: 30}\n",
        };
        var (desired, problems, touched) = ChangeControlService.ParseEdits(Site(), files);
        problems.Should().NotContain(p => p.Level == "Error");

        var op = PlanBuilder.Build(desired, Site(), new PlanOptions { Collections = touched }).Operations.Should().ContainSingle().Subject;

        op.Action.Should().Be(PlanAction.Update);
        op.ObjectId.Should().Be("w1");
        op.Body!["networkconf_id"]!.GetValue<string>().Should().Be("n2");
    }

    [Fact]
    public void Edit_SsidRenameWithItsId_IsOneUpdate_NotACreate()
    {
        var files = new Dictionary<string, string>
        {
            ["wlanconf"] = "wlans:\n- id: w1\n  name: Home Renamed\n  security: wpapsk\n  x_passphrase: <secret:w1/x_passphrase>\n  network: Default\n",
        };
        var (desired, problems, touched) = ChangeControlService.ParseEdits(Site(), files);
        problems.Should().NotContain(p => p.Level == "Error", string.Join("; ", problems.Select(p => p.Message)));

        var op = PlanBuilder.Build(desired, Site(), new PlanOptions { Collections = touched }).Operations.Should().ContainSingle().Subject;

        op.Action.Should().Be(PlanAction.Update);
        op.ObjectId.Should().Be("w1");
        op.Body!["name"]!.GetValue<string>().Should().Be("Home Renamed");
        op.Body!["x_passphrase"]!.GetValue<string>().Should().Be("hunter2", "a masked secret keeps its value");
    }

    [Fact]
    public void Edit_PassphraseChange_IsOneUpdate_WithTheNewValue()
    {
        var files = new Dictionary<string, string>
        {
            ["wlanconf"] = "wlans:\n- id: w1\n  name: Home\n  security: wpapsk\n  x_passphrase: new-passphrase-1\n  network: Default\n",
        };
        var (desired, _, touched) = ChangeControlService.ParseEdits(Site(), files);

        var op = PlanBuilder.Build(desired, Site(), new PlanOptions { Collections = touched }).Operations.Should().ContainSingle().Subject;

        op.Action.Should().Be(PlanAction.Update);
        op.ObjectId.Should().Be("w1");
        op.Body!["x_passphrase"]!.GetValue<string>().Should().Be("new-passphrase-1");
    }

    [Fact]
    public void Edit_ReferenceToANameNothingHas_IsStillAnError()
    {
        var files = new Dictionary<string, string> { ["wlanconf"] = "wlans:\n- name: Home\n  network: Nowhere\n" };
        ChangeControlService.ParseEdits(Site(), files).Problems.Should().Contain(p => p.Level == "Error" && p.Message.Contains("Nowhere"));
    }

    [Fact]
    public void AssignPermanentIds_ReplacesTemporaryIds_AndTheirReferences()
    {
        var files = new Dictionary<string, string>
        {
            ["networkconf"] = LabNetworkFile,
            ["wlanconf"] = "wlans:\n- name: Home\n  security: wpapsk\n  network: Default\n- name: Lab Wi-Fi\n  network: Lab\n",
        };
        var desired = ChangeControlService.ParseEdits(Site(), files).Desired;

        YamlConverter.AssignPermanentIds(desired);

        var lab = desired.Get("networkconf").Single(n => n["name"]!.ToString() == "Lab");
        ConfigState.IdOf(lab).Should().NotStartWith(YamlConverter.TempIdPrefix);
        desired.Get("wlanconf").Single(w => w["name"]!.ToString() == "Lab Wi-Fi")["networkconf_id"]!.GetValue<string>().Should().Be(ConfigState.IdOf(lab));
        ConfigState.IdOf(desired.Get("networkconf").Single(n => n["name"]!.ToString() == "Default")).Should().Be("n1");
    }

    [Fact]
    public void Parse_BadYaml_ReportsTheLine()
    {
        YamlConverter.Parse("wlans:\n  - name: [unclosed\n", Site(), "wlans.yaml").Problems.Should().ContainSingle(p => p.Level == "Error");
    }
}
