using System.Text.Json.Nodes;
using FluentAssertions;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services.ChangeControl;
using Xunit;
using static NetworkOptimizer.Web.Tests.ChangeControl.ConfigDiffEngineTests;

namespace NetworkOptimizer.Web.Tests.ChangeControl;

/// <summary>
/// Objects a Site Manager Fabric pushes (Network 11.1+, cloud_template): left in place with every reference to them,
/// never edited locally when locked, and matched across a re-sync. Every case has a twin without cloud_template that
/// must plan exactly what it did before Fabric support.
/// </summary>
public class FabricObjectTests
{
    private const string Locked = """{"detached":false,"allow_overrides":false,"config_id":"cfg-1","template_id":"tpl-1","orchestration_id":"orc-1"}""";
    private const string Overridable = """{"detached":false,"allow_overrides":true,"config_id":"cfg-1","template_id":"tpl-1","orchestration_id":"orc-1"}""";
    private const string Detached = """{"detached":true,"allow_overrides":false,"config_id":"cfg-1","template_id":"tpl-1","orchestration_id":"orc-1"}""";

    // The before snapshot: one LAN, a trunk profile with exclusions, one with none, and the Internal zone.
    private static ConfigState Before() => State(
        ("networkconf", """{"_id":"n-lan","name":"Default","purpose":"corporate","vlan":1,"ip_subnet":"192.0.2.1/24"}"""),
        ("portconf", """{"_id":"p-ap","name":"AP Trunk","forward":"customize","native_networkconf_id":"n-lan","excluded_networkconf_ids":["n-lan"]}"""),
        ("portconf", """{"_id":"p-core","name":"Core Trunk","forward":"customize","native_networkconf_id":"n-lan"}"""),
        ("firewall_zone", """{"_id":"z-int","name":"Internal","zone_key":"internal","network_ids":["n-lan"]}"""));

    // Live after a push of "Test Network": UniFi excludes it from both trunks and adds it to Internal.
    private static ConfigState AfterPush(string? cloudTemplate)
    {
        var template = cloudTemplate == null ? "" : $",\"cloud_template\":{cloudTemplate}";
        return State(
            ("networkconf", """{"_id":"n-lan","name":"Default","purpose":"corporate","vlan":1,"ip_subnet":"192.0.2.1/24"}"""),
            ("networkconf", "{\"_id\":\"n-fab\",\"name\":\"Test Network\",\"purpose\":\"corporate\",\"vlan\":250,\"ip_subnet\":\"198.51.100.1/24\",\"attr_no_edit\":true" + template + "}"),
            ("portconf", """{"_id":"p-ap","name":"AP Trunk","forward":"customize","native_networkconf_id":"n-lan","excluded_networkconf_ids":["n-lan","n-fab"]}"""),
            ("portconf", """{"_id":"p-core","name":"Core Trunk","forward":"customize","native_networkconf_id":"n-lan","excluded_networkconf_ids":["n-fab"]}"""),
            ("firewall_zone", """{"_id":"z-int","name":"Internal","zone_key":"internal","network_ids":["n-lan","n-fab"]}"""));
    }

    [Theory]
    [InlineData(Locked, true)]
    [InlineData(Overridable, true)]
    [InlineData(Detached, false)]
    [InlineData("null", false)]
    [InlineData(null, false)]
    public void Editor_OffersNoRemove_OnAFabricObject(string? cloudTemplate, bool fabric)
    {
        var schema = YamlConverter.EditSchema(AfterPush(cloudTemplate), [ConfigCollections.Get("networkconf")!]);

        var undeletable = schema["collections"]!["networks"]!["undeletable"]?.AsArray().Select(i => i!.ToString()).ToList() ?? [];
        undeletable.Contains("n-fab").Should().Be(fabric);
    }

    [Theory]
    [InlineData(Locked, "Remove it from Site Manager, or it reappears after the next sync.")]
    [InlineData(Overridable, "To permanently delete it, remove it from Site Manager.")]
    [InlineData(null, null)]
    public void Editor_RemovingAFabricObject_IsAnError_InUniFisWording(string? cloudTemplate, string? expected)
    {
        var site = AfterPush(cloudTemplate);
        var def = ConfigCollections.Get("networkconf")!;
        var file = YamlConverter.Export(site, ["networkconf"], SecretMode.Clear, ids: true);
        var start = file.IndexOf("- id: n-fab", StringComparison.Ordinal);
        var end = file.IndexOf("\n- ", start + 1, StringComparison.Ordinal);
        var edited = file.Remove(start, (end < 0 ? file.Length : end + 1) - start);

        var problems = YamlConverter.Parse(edited, site, YamlConverter.FileName(def)).Problems;

        var fabricErrors = problems.Where(p => p.Message.Contains("Site Manager")).ToList();
        if (expected == null) fabricErrors.Should().BeEmpty();
        else fabricErrors.Should().ContainSingle(p => p.Level == "Error" && p.Message.Contains(expected));
    }

    [Fact]
    public void RestoreBeforeThePush_LeavesTheFabricNetworkAndItsReferences()
    {
        var doc = PlanBuilder.Build(Before(), AfterPush(Locked), new PlanOptions());

        doc.Errors.Should().BeEmpty();
        doc.Operations.Should().BeEmpty();
        doc.Notes.Should().Contain("Network Test Network: created through Site Manager. Remove it from Site Manager, or it reappears after the next sync. Not deleted.");
    }

    [Fact]
    public void RestoreBeforeThePush_OverridableNetwork_IsKept_WithUniFisOverridableWording()
    {
        var doc = PlanBuilder.Build(Before(), AfterPush(Overridable), new PlanOptions());

        doc.Operations.Should().BeEmpty();
        doc.Notes.Should().Contain("Network Test Network: created through Site Manager. To permanently delete it, remove it from Site Manager. Not deleted.");
    }

    [Fact]
    public void RestoreFromAnotherSite_OntoAFabricSite_KeepsTheFabricNetworkAndItsReferences()
    {
        var doc = PlanBuilder.Build(Before(), AfterPush(Locked), new PlanOptions { SameSite = false });

        doc.Errors.Should().BeEmpty();
        doc.Operations.Should().NotContain(o => o.ObjectName == "Test Network");
        doc.Operations.Should().NotContain(o => o.Collection == "portconf" || o.Collection == "firewall_zone");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    public void RestoreFromAnotherSite_WithoutAFabric_PlansAsBefore(string? cloudTemplate)
    {
        var doc = PlanBuilder.Build(Before(), AfterPush(cloudTemplate), new PlanOptions { SameSite = false });

        doc.Notes.Should().NotContain(n => n.Contains("Site Manager"));
        doc.Operations.Should().Contain(o => o.Action == PlanAction.Delete && o.ObjectName == "Test Network");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    public void RestoreBeforeThePush_WithoutAFabric_DeletesTheNetworkAsBefore(string? cloudTemplate)
    {
        var doc = PlanBuilder.Build(Before(), AfterPush(cloudTemplate), new PlanOptions());

        doc.Errors.Should().BeEmpty();
        doc.Operations.Should().Contain(o => o.Action == PlanAction.Delete && o.ObjectName == "Test Network");
        doc.Notes.Should().NotContain(n => n.Contains("Site Manager"));
    }

    [Fact]
    public void RestoreBeforeThePush_DoesNotChangeTheSnapshotItWasGiven()
    {
        var before = Before();
        PlanBuilder.Build(before, AfterPush(Locked), new PlanOptions());
        before.Get("portconf").Single(p => (string?)p["_id"] == "p-core").ContainsKey("excluded_networkconf_ids").Should().BeFalse();
    }

    private static ConfigState Edited(ConfigState s, string field, JsonNode value)
    {
        s.Get("networkconf").Single(n => (string?)n["_id"] == "n-fab")[field] = value;
        return s;
    }

    [Fact]
    public void EditOfALockedFabricNetwork_IsNotWritten()
    {
        var doc = PlanBuilder.Build(Edited(AfterPush(Locked), "vlan", 251), AfterPush(Locked), new PlanOptions());

        doc.Operations.Should().BeEmpty();
        doc.Notes.Should().Contain("Network Test Network: centrally managed by the Fabric and cannot be edited locally. Change it in Site Manager.");
    }

    [Theory]
    [InlineData(Overridable)]
    [InlineData(Detached)]
    [InlineData(null)]
    [InlineData("null")]
    public void EditOfAnEditableNetwork_IsWritten(string? cloudTemplate)
    {
        var doc = PlanBuilder.Build(Edited(AfterPush(cloudTemplate), "vlan", 251), AfterPush(cloudTemplate), new PlanOptions());

        doc.Operations.Should().ContainSingle(o => o.Action == PlanAction.Update && o.ObjectName == "Test Network");
        doc.Notes.Should().NotContain(n => n.Contains("Site Manager"));
    }

    // Every Canvas type and Device Templates put cloud_template on the object itself (11.1.58 web UI: WiFi,
    // RADIUS, Content Filter, global switch settings, and the device port panel all read e.cloud_template).
    [Theory]
    [InlineData("wlanconf", """{"_id":"w1","name":"Guest","enabled":true}""", "enabled", false)]
    [InlineData("radiusprofile", """{"_id":"r1","name":"Corp","vlan_enabled":true}""", "vlan_enabled", false)]
    [InlineData("setting", """{"_id":"s1","key":"global_switch","flowctrl_enabled":true}""", "flowctrl_enabled", false)]
    [InlineData("device", """{"_id":"d1","mac":"aa:bb:cc:dd:ee:01","name":"Switch","stp_priority":"32768"}""", "stp_priority", "4096")]
    public void EveryCollection_LockedFabricEdit_IsNotWritten_ButWithoutAFabricItIs(string collection, string json, string field, object value)
    {
        ConfigState Live(string? template)
        {
            var o = JsonNode.Parse(json)!.AsObject();
            if (template != null) o["cloud_template"] = JsonNode.Parse(template);
            return State((collection, o.ToJsonString()));
        }
        ConfigState Desired(string? template)
        {
            var s = Live(template);
            s.Get(collection)[0][field] = JsonValue.Create(value);
            return s;
        }

        PlanBuilder.Build(Desired(Locked), Live(Locked), new PlanOptions()).Operations.Should().BeEmpty();
        foreach (var template in new[] { null, "null", Overridable, Detached })
            PlanBuilder.Build(Desired(template), Live(template), new PlanOptions()).Operations
                .Should().ContainSingle(o => o.Action == PlanAction.Update, $"cloud_template {template ?? "absent"} on {collection} is editable");
    }

    [Fact]
    public void ResyncedFabricNetwork_UnderANewIdAndName_IsTheSameNetwork()
    {
        // A local delete is undone at the next sync under a new _id; Site Manager may have renamed it meanwhile.
        var live = AfterPush(Locked);
        var net = live.Get("networkconf").Single(n => (string?)n["_id"] == "n-fab");
        net["_id"] = "n-fab-2";
        net["name"] = "Test Network Renamed";
        net["vlan"] = 260;

        var diff = ConfigDiffEngine.Compare(AfterPush(Locked), live);

        diff.Objects.Should().NotContain(o => o.Collection == "networkconf" && o.Action != ConfigChangeAction.Updated);
    }

    [Fact]
    public void FabricTemplate_IsReadOnly_SoATemplateChangeAloneIsNotAChange()
    {
        var after = AfterPush(Locked);
        after.Get("networkconf").Single(n => (string?)n["_id"] == "n-fab")["cloud_template"]!["revision_id"] = "rev-2";

        ConfigDiffEngine.Compare(AfterPush(Locked), after).IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void ManagedBy_NamesSiteManager_OnlyForALockedFabricObject()
    {
        var def = ConfigCollections.Get("networkconf")!;
        JsonObject Net(string? t) => JsonNode.Parse("{\"_id\":\"n\",\"name\":\"X\"" + (t == null ? "" : ",\"cloud_template\":" + t) + "}")!.AsObject();

        ConfigCollections.ManagedBy(def, Net(Locked)).Should().Be(ConfigCollections.SiteManager);
        ConfigCollections.ManagedBy(def, Net(Overridable)).Should().BeNull();
        ConfigCollections.ManagedBy(def, Net(Detached)).Should().BeNull();
        ConfigCollections.ManagedBy(def, Net(null)).Should().BeNull();
        ConfigCollections.ManagedBy(def, Net("null")).Should().BeNull();
    }

    [Fact]
    public void FabricsBadge_State_ReachesTheDiff_TheChangeRecord_AndThePlanRow()
    {
        var live = AfterPush(Overridable);
        var diff = ConfigDiffEngine.Compare(Before(), live);
        var net = diff.Objects.Single(o => o.Collection == "networkconf" && o.Name == "Test Network");
        net.Fabric.Should().Be(ConfigCollections.FabricOverridableState);
        var names = new ConfigIdentityContext(live.NamesById());
        ChangeControlSiteEngine.ToRecord(net, names, names).Fabric
            .Should().Be(ConfigCollections.FabricOverridableState);

        var lockedDiff = ConfigDiffEngine.Compare(Before(), AfterPush(Locked));
        lockedDiff.Objects.Single(o => o.Name == "Test Network").Fabric.Should().Be(ConfigCollections.FabricLockedState);

        PlanBuilder.Build(Edited(AfterPush(Overridable), "vlan", 251), AfterPush(Overridable), new PlanOptions()).Operations
            .Single().Fabric.Should().Be(ConfigCollections.FabricOverridableState);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    public void FabricsBadge_IsAbsent_WithoutAFabric(string? cloudTemplate)
    {
        ConfigDiffEngine.Compare(Before(), AfterPush(cloudTemplate)).Objects.Should().OnlyContain(o => o.Fabric == null);
        PlanBuilder.Build(Edited(AfterPush(cloudTemplate), "vlan", 251), AfterPush(cloudTemplate), new PlanOptions()).Operations
            .Should().OnlyContain(o => o.Fabric == null);
    }

    [Fact]
    public void TemplateFromAnotherSite_DoesNotEditALockedFabricObjectHere()
    {
        // The target's object is locked on its console whatever the plan's source.
        var doc = PlanBuilder.Build(Edited(AfterPush(null), "vlan", 251), AfterPush(Locked), new PlanOptions { SameSite = false });

        doc.Operations.Should().NotContain(o => o.ObjectName == "Test Network");
        doc.Notes.Should().Contain(n => n.Contains("centrally managed by the Fabric"));
    }

    [Fact]
    public void AnotherSite_GetsTheFabricNetworkAsItsOwn()
    {
        // A template from a Fabric site onto a site outside it: a plain network there.
        var target = Before();
        var doc = PlanBuilder.Build(AfterPush(Locked), target, new PlanOptions { SameSite = false });

        doc.Operations.Should().Contain(o => o.Action == PlanAction.Create && o.ObjectName == "Test Network");
        doc.Operations.Single(o => o.Action == PlanAction.Create && o.ObjectName == "Test Network").Body!.AsObject().ContainsKey("cloud_template").Should().BeFalse();
    }
}
