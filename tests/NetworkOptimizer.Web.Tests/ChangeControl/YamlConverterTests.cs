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
        ("apgroups", """{"_id":"a1","name":"All APs","attr_hidden_id":"default","device_macs":[]}"""),
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

    private static ConfigState PolicySite() => State(
        ("firewall_zone", """{"_id":"z1","name":"Internal","zone_key":"internal","network_ids":[]}"""),
        ("firewall_zone", """{"_id":"z2","name":"External","zone_key":"external","network_ids":[]}"""),
        ("firewall_policy", """{"_id":"p1","name":"Block IoT","action":"BLOCK","source":{"zone_id":"z1"},"destination":{"zone_id":"z2"}}"""),
        ("firewall_policy", """{"_id":"p2","name":"Allow All Traffic","predefined":true,"action":"ALLOW","source":{"zone_id":"z1"},"destination":{"zone_id":"z2"}}"""));

    [Fact]
    public void FirewallPolicies_BatchDeleteOnly_AreDeletableInTheEditor()
    {
        var schema = YamlConverter.EditSchema(PolicySite(), [ConfigCollections.Get("firewall_policy")!]);

        schema["collections"]!["firewall_policies"]!["readOnly"].Should().BeNull("firewall-policies/batch-delete is a delete");
        schema["collections"]!["firewall_policies"]!["undeletable"]!.AsArray().Select(i => i!.ToString()).Should().Equal("p2");
    }

    [Fact]
    public void RestoreStylePlan_NeverDeletesBuiltInObjects()
    {
        var live = State(
            ("firewall_zone", """{"_id":"z1","name":"Internal","zone_key":"internal","default_zone":true,"network_ids":[]}"""),
            ("firewall_zone", """{"_id":"z3","name":"Lab","network_ids":[]}"""),
            ("firewall_policy", """{"_id":"p2","name":"Allow All Traffic","predefined":true,"action":"ALLOW","source":{"zone_id":"z1"},"destination":{"zone_id":"z1"}}"""),
            ("networkconf", """{"_id":"n1","name":"Default","purpose":"corporate","attr_no_delete":true}"""),
            ("nat", """{"_id":"r1","description":"Masquerade","is_predefined":true}"""));
        var empty = new ConfigState();
        foreach (var key in live.Collections.Keys) empty.Collections[key] = [];

        var doc = PlanBuilder.Build(empty, live, new PlanOptions { DeleteMissing = true });

        doc.Operations.Where(o => o.Action == PlanAction.Delete).Select(o => o.ObjectId).Should().Equal("z3");
    }

    [Fact]
    public void RemovingACustomPolicy_IsADelete()
    {
        var site = PolicySite();
        var yaml = YamlConverter.Export(site, ["firewall_policy"], SecretMode.Mask, ids: true);
        var kept = string.Join("\n", yaml.Split('\n').SkipWhile(l => !l.StartsWith("- id: p2", StringComparison.Ordinal)));
        var (desired, problems, touched) = ChangeControlService.ParseEdits(site, new Dictionary<string, string> { ["firewall_policy"] = "firewall_policies:\n" + kept });

        problems.Should().NotContain(p => p.Level == "Error" || p.Message.Contains("read-only"));
        PlanBuilder.Build(desired, site, new PlanOptions { Collections = touched }).Operations
            .Should().ContainSingle(o => o.Action == PlanAction.Delete && o.ObjectId == "p1");
    }

    [Fact]
    public void DeletingOnePolicy_IsOneDelete_NoReorders()
    {
        // Index order differs from name order in both zone pairs, as on a real console.
        var site = State(
            ("firewall_zone", """{"_id":"z1","name":"Internal","zone_key":"internal","network_ids":[]}"""),
            ("firewall_zone", """{"_id":"z2","name":"External","zone_key":"external","network_ids":[]}"""),
            // Stored out of index order, as a snapshot can hold them.
            ("firewall_policy", """{"_id":"pc","name":"Mid","index":10002,"action":"ALLOW","source":{"zone_id":"z1"},"destination":{"zone_id":"z2"}}"""),
            ("firewall_policy", """{"_id":"pe","name":"Bravo","index":10004,"action":"BLOCK","source":{"zone_id":"z2"},"destination":{"zone_id":"z1"}}"""),
            ("firewall_policy", """{"_id":"pa","name":"Zeta","index":10000,"action":"ALLOW","source":{"zone_id":"z1"},"destination":{"zone_id":"z2"}}"""),
            ("firewall_policy", """{"_id":"pd","name":"Yankee","index":10003,"action":"ALLOW","source":{"zone_id":"z2"},"destination":{"zone_id":"z1"}}"""),
            ("firewall_policy", """{"_id":"pb","name":"Alpha","index":10001,"action":"BLOCK","source":{"zone_id":"z1"},"destination":{"zone_id":"z2"}}"""));
        var yaml = YamlConverter.Export(site, ["firewall_policy"], SecretMode.Mask, ids: true);
        var lines = yaml.Split('\n').ToList();
        var at = lines.FindIndex(l => l == "- id: pb");
        var end = lines.FindIndex(at + 1, l => l.StartsWith("- ", StringComparison.Ordinal));
        lines.RemoveRange(at, end - at);
        var (desired, problems, touched) = ChangeControlService.ParseEdits(site, new Dictionary<string, string> { ["firewall_policy"] = string.Join("\n", lines) });

        problems.Should().NotContain(p => p.Level == "Error");
        PlanBuilder.Build(desired, site, new PlanOptions { Collections = touched }).Operations
            .Should().ContainSingle().Which.Should().Match<PlanOperation>(o => o.Action == PlanAction.Delete && o.ObjectId == "pb");
    }

    [Fact]
    public void ReorderAndDeleteInOnePair_TheReorderStillListsThePolicyBeingDeleted()
    {
        var site = State(
            ("firewall_zone", """{"_id":"z1","name":"Internal","zone_key":"internal","network_ids":[]}"""),
            ("firewall_zone", """{"_id":"z2","name":"External","zone_key":"external","network_ids":[]}"""),
            ("firewall_policy", """{"_id":"pa","name":"A","index":10000,"action":"ALLOW","source":{"zone_id":"z1"},"destination":{"zone_id":"z2"}}"""),
            ("firewall_policy", """{"_id":"pb","name":"B","index":10001,"action":"BLOCK","source":{"zone_id":"z1"},"destination":{"zone_id":"z2"}}"""),
            ("firewall_policy", """{"_id":"pc","name":"C","index":10002,"action":"ALLOW","source":{"zone_id":"z1"},"destination":{"zone_id":"z2"}}"""));
        // C moved above A, and B deleted.
        var yaml = YamlConverter.Export(site, ["firewall_policy"], SecretMode.Mask, ids: true);
        var blocks = yaml.Split("\n- ").ToList();
        var header = blocks[0];
        var items = blocks.Skip(1).Select(b => "- " + b.TrimEnd('\n')).ToList();
        var edited = header + "\n" + string.Join("\n", new[] { items[2], items[0] }) + "\n";
        var (desired, problems, touched) = ChangeControlService.ParseEdits(site, new Dictionary<string, string> { ["firewall_policy"] = edited });

        problems.Should().NotContain(p => p.Level == "Error");
        var ops = PlanBuilder.Build(desired, site, new PlanOptions { Collections = touched }).Operations;
        ops.Should().ContainSingle(o => o.Action == PlanAction.Delete && o.ObjectId == "pb");
        var reorder = ops.Should().ContainSingle(o => o.Action == PlanAction.Reorder).Subject;
        var listed = reorder.Body!["before_predefined_ids"]!.AsArray().Concat(reorder.Body!["after_predefined_ids"]!.AsArray()).Select(i => i!.ToString());
        listed.Should().BeEquivalentTo(["pc", "pa", "pb"]);
    }

    [Fact]
    public void ReorderFromTheEditor_KeepsEachPolicyOnItsSideOfThePredefinedOnes()
    {
        var site = State(
            ("firewall_zone", """{"_id":"z1","name":"Internal","zone_key":"internal","network_ids":[]}"""),
            ("firewall_zone", """{"_id":"z2","name":"External","zone_key":"external","network_ids":[]}"""),
            ("firewall_policy", """{"_id":"pa","name":"A","index":10000,"action":"ALLOW","source":{"zone_id":"z1"},"destination":{"zone_id":"z2"}}"""),
            ("firewall_policy", """{"_id":"pb","name":"B","index":10001,"action":"BLOCK","source":{"zone_id":"z1"},"destination":{"zone_id":"z2"}}"""),
            ("firewall_policy", """{"_id":"pp","name":"Allow All Traffic","index":20000,"predefined":true,"action":"ALLOW","source":{"zone_id":"z1"},"destination":{"zone_id":"z2"}}"""),
            ("firewall_policy", """{"_id":"pz","name":"Z","index":30000,"action":"BLOCK","source":{"zone_id":"z1"},"destination":{"zone_id":"z2"}}"""));
        // B moved above A in the file.
        var yaml = YamlConverter.Export(site, ["firewall_policy"], SecretMode.Mask, ids: true);
        var items = yaml.Split("\n- ").Skip(1).Select(b => "- " + b.TrimEnd('\n')).ToList();
        var edited = "firewall_policies:\n" + string.Join("\n", new[] { items[1], items[0], items[2], items[3] }) + "\n";
        var (desired, problems, touched) = ChangeControlService.ParseEdits(site, new Dictionary<string, string> { ["firewall_policy"] = edited });

        problems.Should().NotContain(p => p.Level == "Error");
        var reorder = PlanBuilder.Build(desired, site, new PlanOptions { Collections = touched }).Operations
            .Should().ContainSingle(o => o.Action == PlanAction.Reorder).Subject;
        reorder.Body!["before_predefined_ids"]!.AsArray().Select(i => i!.ToString()).Should().Equal("pb", "pa");
        reorder.Body!["after_predefined_ids"]!.AsArray().Select(i => i!.ToString()).Should().Equal("pz");
    }

    [Fact]
    public void RollBackOfAReorder_PutsPoliciesBackAboveThePredefinedOnes()
    {
        // The console after a bad reorder moved A and B below the predefined policy; the snapshot had them above.
        ConfigState Site(long a, long b) => State(
            ("firewall_zone", """{"_id":"z1","name":"Internal","zone_key":"internal","network_ids":[]}"""),
            ("firewall_zone", """{"_id":"z2","name":"External","zone_key":"external","network_ids":[]}"""),
            ("firewall_policy", """{"_id":"pa","name":"A","index":""" + a + ""","action":"ALLOW","source":{"zone_id":"z1"},"destination":{"zone_id":"z2"}}"""),
            ("firewall_policy", """{"_id":"pb","name":"B","index":""" + b + ""","action":"BLOCK","source":{"zone_id":"z1"},"destination":{"zone_id":"z2"}}"""),
            ("firewall_policy", """{"_id":"pp","name":"Allow All Traffic","index":20000,"predefined":true,"action":"ALLOW","source":{"zone_id":"z1"},"destination":{"zone_id":"z2"}}"""));
        var reorder = PlanBuilder.Build(Site(10000, 10001), Site(30001, 30000), new PlanOptions()).Operations
            .Should().ContainSingle(o => o.Action == PlanAction.Reorder).Subject;
        reorder.Body!["before_predefined_ids"]!.AsArray().Select(i => i!.ToString()).Should().Equal("pa", "pb");
    }

    [Fact]
    public void ReorderBackToASnapshot_LeavesOutAPolicyDeletedSince()
    {
        // A Roll Back of a reorder, scoped to order: the snapshot still has D, deleted on the console since.
        ConfigState Site(bool withD, params (string Id, long Index)[] order) => State([
            ("firewall_zone", """{"_id":"z1","name":"Internal","zone_key":"internal","network_ids":[]}"""),
            ("firewall_zone", """{"_id":"z2","name":"External","zone_key":"external","network_ids":[]}"""),
            .. order.Where(o => withD || o.Id != "pd").Select(o => ("firewall_policy",
                "{\"_id\":\"" + o.Id + "\",\"name\":\"" + o.Id + "\",\"index\":" + o.Index
                + ",\"action\":\"ALLOW\",\"source\":{\"zone_id\":\"z1\"},\"destination\":{\"zone_id\":\"z2\"}}"))]);
        var snapshot = Site(true, ("pa", 10000), ("pd", 10001), ("pb", 10002));
        var live = Site(false, ("pb", 10000), ("pa", 10001));
        // As Roll Back narrows it: only the policies the change moved.
        var only = new HashSet<(string, string)> { ("firewall_policy", "pa"), ("firewall_policy", "pb") };
        var ops = PlanBuilder.Build(snapshot, live, new PlanOptions { OnlyObjects = only, Collections = new HashSet<string> { "firewall_policy" } }).Operations;

        var reorder = ops.Should().ContainSingle(o => o.Action == PlanAction.Reorder).Subject;
        reorder.Body!["before_predefined_ids"]!.AsArray().Concat(reorder.Body!["after_predefined_ids"]!.AsArray())
            .Select(i => i!.ToString()).Should().Equal("pa", "pb");
    }

    [Fact]
    public void RemovingAPredefinedPolicy_IsAnError()
    {
        var site = PolicySite();
        var yaml = YamlConverter.Export(site, ["firewall_policy"], SecretMode.Mask, ids: true);
        var kept = string.Join("\n", yaml.Split('\n').TakeWhile(l => !l.StartsWith("- id: p2", StringComparison.Ordinal)));
        var parsed = YamlConverter.Parse(kept + "\n", site, "firewall_policies.yaml");

        parsed.Problems.Should().ContainSingle(p => p.Level == "Error")
            .Which.Message.Should().Be("Firewall Policy Allow All Traffic is built into UniFi and cannot be deleted.");
    }

    [Fact]
    public void EditingAPredefinedPolicy_IsAnError_AndAnUnchangedOneIsNot()
    {
        var site = PolicySite();
        var yaml = YamlConverter.Export(site, ["firewall_policy"], SecretMode.Mask, ids: true);

        YamlConverter.Parse(yaml, site, "firewall_policies.yaml").Problems.Should().NotContain(p => p.Level == "Error");

        var edited = yaml.Replace("name: Allow All Traffic", "name: Allow Everything");
        YamlConverter.Parse(edited, site, "firewall_policies.yaml").Problems.Should().ContainSingle(p => p.Level == "Error")
            .Which.Message.Should().Be("Firewall Policy Allow All Traffic is built into UniFi and cannot be edited here.");

        var custom = yaml.Replace("name: Block IoT", "name: Block Cameras");
        YamlConverter.Parse(custom, site, "firewall_policies.yaml").Problems.Should().NotContain(p => p.Level == "Error");
    }

    [Fact]
    public void EditSchema_MarksManagedObjects_ButNotTheEditableBuiltIns()
    {
        var site = State(
            ("firewall_zone", """{"_id":"z1","name":"Internal","zone_key":"internal","default_zone":true,"network_ids":[]}"""),
            ("firewall_policy", """{"_id":"p2","name":"Allow All Traffic","predefined":true,"action":"ALLOW","source":{"zone_id":"z1"},"destination":{"zone_id":"z1"}}"""),
            ("networkconf", """{"_id":"n1","name":"Default","purpose":"corporate","attr_no_delete":true}"""));
        var schema = YamlConverter.EditSchema(site, ConfigCollections.All.Where(d => d.Key is "firewall_zone" or "firewall_policy" or "networkconf"));

        schema["collections"]!["firewall_policies"]!["managed"]!["p2"]!.ToString().Should().Be("UniFi");
        // Not deletable, but the user's to edit: a zone's networks, the Default network's subnet.
        schema["collections"]!.AsObject().Where(c => c.Key != "firewall_policies").Should().OnlyContain(c => c.Value!["managed"] == null);
    }

    [Fact]
    public void Parse_AmbiguousName_IsAnError_SuggestingTheVlan()
    {
        var yaml = """
            wlans:
              - name: IoT
                security: wpapsk
                network: IoT
                ap_groups: [All APs]
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
        YamlConverter.Parse(yaml, Site(), "wlans.yaml").Problems.Should().Contain(p => p.Level == "Info" && p.Message == "WPA3 transition on Home: some IoT devices cannot join.");
    }

    [Fact]
    public void Parse_Wpa3TransitionAlreadyOn_IsNoNote()
    {
        var site = Site();
        site.Collections["wlanconf"][0]["wpa3_transition"] = true;

        YamlConverter.Parse(YamlConverter.Export(site, ["wlanconf"], SecretMode.Mask, ids: true), site, "wlans.yaml")
            .Problems.Should().NotContain(p => p.Message.Contains("WPA3 transition"));
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
                + "- name: Lab Wi-Fi\n  security: wpapsk\n  x_passphrase: lab-passphrase\n  network: Lab\n  ap_groups: [All APs]\n",
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
