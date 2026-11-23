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

    [Fact]
    public void RestoreBeforeThePush_LeavesTheFabricNetworkAndItsReferences()
    {
        var doc = PlanBuilder.Build(Before(), AfterPush(Locked), new PlanOptions());

        doc.Errors.Should().BeEmpty();
        doc.Operations.Should().BeEmpty();
        doc.Notes.Should().Contain("Network Test Network: created through Site Manager. Remove it from Site Manager, or it reappears after the next sync. Not deleted.");
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
    public void AnotherSite_GetsTheFabricNetworkAsItsOwn()
    {
        // A template from a Fabric site onto a site outside it: a plain network there.
        var target = Before();
        var doc = PlanBuilder.Build(AfterPush(Locked), target, new PlanOptions { SameSite = false });

        doc.Operations.Should().Contain(o => o.Action == PlanAction.Create && o.ObjectName == "Test Network");
        doc.Operations.Single(o => o.Action == PlanAction.Create && o.ObjectName == "Test Network").Body!.AsObject().ContainsKey("cloud_template").Should().BeFalse();
    }
}
