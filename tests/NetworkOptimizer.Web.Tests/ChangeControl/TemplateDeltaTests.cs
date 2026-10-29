using System.Text.Json.Nodes;
using FluentAssertions;
using NetworkOptimizer.Web.Services.ChangeControl;
using Xunit;
using static NetworkOptimizer.Web.Tests.ChangeControl.ConfigDiffEngineTests;

namespace NetworkOptimizer.Web.Tests.ChangeControl;

/// <summary>
/// A template as a delta: a WLAN pared down to its PPSKs adds them at another site, with each PPSK's
/// network matched there by VLAN ID, nothing else written, and the site's own PPSKs kept.
/// </summary>
public class TemplateDeltaTests
{
    // The source site: the template is cut from it.
    private static ConfigState Source() => State(
        ("networkconf", """{"_id":"src-lan","name":"Default","purpose":"corporate","ip_subnet":"192.0.2.1/24"}"""),
        ("networkconf", """{"_id":"src-iot","name":"IoT","purpose":"corporate","vlan":30,"vlan_enabled":true,"ip_subnet":"198.51.100.1/24","x_secret_ish":"kept-out"}"""),
        ("networkconf", """{"_id":"src-cam","name":"Cameras","purpose":"corporate","vlan":40,"vlan_enabled":true,"ip_subnet":"203.0.113.1/24"}"""),
        ("wlanconf", """{"_id":"src-w","name":"Home","networkconf_id":"src-lan","private_preshared_keys_enabled":true,"private_preshared_keys":[{"password":"iot-pass-1","networkconf_id":"src-iot"},{"password":"cam-pass-1","networkconf_id":"src-cam"}]}"""));

    // The target site: same VLANs under other IDs and names, and a PPSK of its own.
    private static ConfigState Target() => State(
        ("networkconf", """{"_id":"t-lan","name":"Main LAN","purpose":"corporate","ip_subnet":"192.0.2.1/24"}"""),
        ("networkconf", """{"_id":"t-iot","name":"Things","purpose":"corporate","vlan":30,"vlan_enabled":true,"ip_subnet":"198.51.100.1/24"}"""),
        ("networkconf", """{"_id":"t-cam","name":"Cams","purpose":"corporate","vlan":40,"vlan_enabled":true,"ip_subnet":"203.0.113.1/24"}"""),
        ("networkconf", """{"_id":"t-guest","name":"Guest","purpose":"guest","vlan":50,"vlan_enabled":true,"ip_subnet":"192.0.2.129/25"}"""),
        ("wlanconf", """{"_id":"t-w","name":"Home","networkconf_id":"t-lan","security":"wpapsk","private_preshared_keys_enabled":true,"private_preshared_keys":[{"password":"guest-pass","networkconf_id":"t-guest"}]}"""));

    private static PlanOptions TemplateOptions(params string[] collections) => new()
    {
        SameSite = false, DeleteMissing = false, MergeLists = true, Collections = collections.ToHashSet(),
    };

    /// <summary>The template as the store loads it with references: the pared-down WLAN plus what it points at.</summary>
    private static ConfigState Template()
    {
        var template = new ConfigState();
        var wlan = Source().Get("wlanconf").Single();
        template.Collections["wlanconf"] = [new JsonObject
        {
            ["_id"] = "src-w", ["name"] = "Home",
            ["private_preshared_keys_enabled"] = true, ["private_preshared_keys"] = wlan["private_preshared_keys"]!.DeepClone(),
        }];
        // As CreateFromSiteAsync saves it: references come from the source site's whole state.
        var source = Source();
        source.Collections["wlanconf"] = template.Collections["wlanconf"];
        foreach (var (collection, list) in ConfigTemplateStore.References(source, ["wlanconf"])) template.Collections[collection] = list;
        return template;
    }

    [Fact]
    public void References_AreTheNetworksThePpsksNameOnly()
    {
        var template = new ConfigState();
        template.Collections["wlanconf"] = [(JsonObject)Source().Get("wlanconf").Single().DeepClone()];
        // A real source state: references are looked up there.
        var source = Source();
        var references = ConfigTemplateStore.References(source, ["wlanconf"]);

        references.Keys.Should().Equal("networkconf");
        references["networkconf"].Select(ConfigState.IdOf).Should().BeEquivalentTo("src-lan", "src-iot", "src-cam");
    }

    [Fact]
    public void References_LeaveOutSecrets()
    {
        var source = Source();
        source.Collections["networkconf"][1]["x_wireguard_private_key"] = "secret";

        ConfigTemplateStore.References(source, ["wlanconf"])["networkconf"]
            .Should().OnlyContain(o => !o.ContainsKey("x_wireguard_private_key"));
    }

    [Fact]
    public void Apply_AddsThePpsks_OnTheTargetsNetworks_AndKeepsTheSitesOwn()
    {
        var doc = PlanBuilder.Build(Template(), Target(), TemplateOptions("wlanconf"));

        doc.Errors.Should().BeEmpty();
        var op = doc.Operations.Should().ContainSingle().Subject;
        op.Collection.Should().Be("wlanconf");
        op.Action.Should().Be(PlanAction.Update);
        op.ObjectId.Should().Be("t-w");
        var ppsks = op.Body!["private_preshared_keys"]!.AsArray().Select(e => (e!["password"]!.ToString(), e["networkconf_id"]!.ToString()));
        ppsks.Should().Equal(("guest-pass", "t-guest"), ("iot-pass-1", "t-iot"), ("cam-pass-1", "t-cam"));
    }

    [Fact]
    public void Apply_SameName_WinsOverSameVlan()
    {
        // "IoT" here is on VLAN 31; VLAN 30 is "Things". The name decides.
        var target = Target();
        target.Collections["networkconf"].Add(JsonNode.Parse("""{"_id":"t-iot2","name":"IoT","purpose":"corporate","vlan":31,"vlan_enabled":true,"ip_subnet":"198.51.100.129/25"}""")!.AsObject());

        var ppsks = PlanBuilder.Build(Template(), target, TemplateOptions("wlanconf")).Operations.Single().Body!["private_preshared_keys"]!.AsArray();

        ppsks.Single(e => e!["password"]!.ToString() == "iot-pass-1")!["networkconf_id"]!.ToString().Should().Be("t-iot2");
    }

    [Fact]
    public void Apply_NameSharedByTwoNetworks_FallsBackToTheVlan()
    {
        var target = Target();
        target.Collections["networkconf"].Add(JsonNode.Parse("""{"_id":"t-iot2","name":"IoT","purpose":"corporate","vlan":31,"vlan_enabled":true}""")!.AsObject());
        target.Collections["networkconf"].Add(JsonNode.Parse("""{"_id":"t-iot3","name":"IoT","purpose":"corporate","vlan":32,"vlan_enabled":true}""")!.AsObject());

        var doc = PlanBuilder.Build(Template(), target, TemplateOptions("wlanconf"));

        doc.Operations.Single().Body!["private_preshared_keys"]!.AsArray()
            .Single(e => e!["password"]!.ToString() == "iot-pass-1")!["networkconf_id"]!.ToString().Should().Be("t-iot");
    }

    [Fact]
    public void SameSite_StillMatchesById_NotByName()
    {
        var before = State(("networkconf", """{"_id":"n1","name":"IoT","purpose":"corporate","vlan":30,"vlan_enabled":true}"""),
            ("networkconf", """{"_id":"n2","name":"Lab","purpose":"corporate","vlan":31,"vlan_enabled":true}"""));
        var after = State(("networkconf", """{"_id":"n1","name":"Lab","purpose":"corporate","vlan":30,"vlan_enabled":true}"""),
            ("networkconf", """{"_id":"n2","name":"IoT","purpose":"corporate","vlan":31,"vlan_enabled":true}"""));
        var def = ConfigCollections.Get("networkconf")!;

        var match = ObjectMatcher.Match(def, before.Get("networkconf"), after.Get("networkconf"),
            new ConfigIdentityContext(before.NamesById()), new ConfigIdentityContext(after.NamesById()));

        match.Pairs.Should().OnlyContain(p => p.Left != null && p.Right != null && ConfigState.IdOf(p.Left) == ConfigState.IdOf(p.Right));
    }

    [Fact]
    public void Apply_NameMatchOnAnotherVlan_IsNoted()
    {
        var target = Target();
        target.Collections["networkconf"].Add(JsonNode.Parse("""{"_id":"t-iot2","name":"IoT","purpose":"corporate","vlan":31,"vlan_enabled":true}""")!.AsObject());

        PlanBuilder.Build(Template(), target, TemplateOptions("wlanconf")).Notes
            .Should().Contain("Network IoT: matched by name; VLAN 30 in the source, VLAN 31 here.");
    }

    [Fact]
    public void Apply_ReplaceLists_MakesThePpsksExactlyTheTemplates()
    {
        var options = new PlanOptions { SameSite = false, DeleteMissing = false, MergeLists = false, Collections = new HashSet<string> { "wlanconf" } };

        var ppsks = PlanBuilder.Build(Template(), Target(), options).Operations.Single().Body!["private_preshared_keys"]!.AsArray();

        ppsks.Select(e => e!["password"]!.ToString()).Should().Equal("iot-pass-1", "cam-pass-1");
    }

    [Fact]
    public void Apply_NewWlanWithWpa3And6Ghz_IsACreateThenAnUpdate()
    {
        var template = Template();
        template.Collections["wlanconf"] = [JsonNode.Parse("""{"_id":"src-w2","name":"Six","security":"wpapsk","x_passphrase":"correct-horse","networkconf_id":"src-lan","wpa3_support":true,"wpa3_transition":false,"pmf_mode":"required","wlan_bands":["2g","5g","6g"]}""")!.AsObject()];

        var doc = PlanBuilder.Build(template, Target(), TemplateOptions("wlanconf"));

        doc.Errors.Should().BeEmpty();
        doc.Operations.Select(o => o.Action).Should().Equal(PlanAction.Create, PlanAction.Update);
        var (create, then) = (doc.Operations[0], doc.Operations[1]);
        create.Body!["wpa3_support"]!.GetValue<bool>().Should().BeFalse();
        create.Body!["wlan_bands"]!.AsArray().Select(b => b!.ToString()).Should().Equal("2g", "5g");
        then.ObjectId.Should().Be(PlanBuilder.RefPrefix + create.Key);
        then.Body!["wpa3_support"]!.GetValue<bool>().Should().BeTrue();
        then.Body!["pmf_mode"]!.ToString().Should().Be("required");
        then.Body!["wlan_bands"]!.AsArray().Select(b => b!.ToString()).Should().Equal("2g", "5g", "6g");
    }

    [Fact]
    public void Apply_NewPpskWlan_ShowsItsPpsks_AndSendsNoUniFiIds()
    {
        var template = Template();
        var wlan = template.Get("wlanconf").Single();
        wlan["name"] = "Secondary";
        wlan["legacy_id"] = "68ca29bd53d37844b10678ce";
        wlan["external_id"] = "11111111-2222-4333-8444-555555555555";

        var create = PlanBuilder.Build(template, Target(), TemplateOptions("wlanconf")).Operations.Single(o => o.Action == PlanAction.Create);

        create.Body!.AsObject().ContainsKey("legacy_id").Should().BeFalse();
        create.Body!.AsObject().ContainsKey("external_id").Should().BeFalse();
        create.Body!["private_preshared_keys"]!.AsArray().Should().HaveCount(2);
        create.Fields.Should().NotBeEmpty("a create lists what it writes");
        create.Fields.Should().OnlyContain(f => string.Equals(f.Old, "none", StringComparison.OrdinalIgnoreCase));
        // The template's PPSK networks, named as on the target (Things, Cams); never a password.
        create.Fields.Should().Contain(f => f.Label == "Private Preshared Keys 1" && f.New == "Network Things");
        create.Fields.Should().Contain(f => f.Label == "Private Preshared Keys 2" && f.New == "Network Cams");
        create.Fields.Should().NotContain(f => f.New != null && f.New.Contains("pass-1"));
    }

    [Fact]
    public void Apply_NewWpa2Wlan_IsOneCreate()
    {
        var template = Template();
        template.Collections["wlanconf"] = [JsonNode.Parse("""{"_id":"src-w2","name":"Plain","security":"wpapsk","x_passphrase":"correct-horse","networkconf_id":"src-lan","wpa3_support":false,"wlan_bands":["2g","5g"]}""")!.AsObject()];

        PlanBuilder.Build(template, Target(), TemplateOptions("wlanconf")).Operations.Should().ContainSingle(o => o.Action == PlanAction.Create);
    }

    [Fact]
    public void Apply_NeverWritesTheReferencedNetworks()
    {
        var doc = PlanBuilder.Build(Template(), Target(), TemplateOptions("wlanconf"));

        doc.Operations.Should().NotContain(o => o.Collection == "networkconf");
    }

    [Fact]
    public void Apply_SamePassword_TakesTheTemplatesNetwork()
    {
        var target = Target();
        target.Collections["wlanconf"][0]["private_preshared_keys"] = JsonNode.Parse("""[{"password":"iot-pass-1","networkconf_id":"t-guest"}]""");

        var ppsks = PlanBuilder.Build(Template(), target, TemplateOptions("wlanconf")).Operations.Single().Body!["private_preshared_keys"]!.AsArray();

        ppsks.Select(e => (e!["password"]!.ToString(), e["networkconf_id"]!.ToString()))
            .Should().Equal(("iot-pass-1", "t-iot"), ("cam-pass-1", "t-cam"));
    }

    [Fact]
    public void Apply_NetworkWithNoMatchOnTheTarget_IsAnError()
    {
        var target = Target();
        target.Collections["networkconf"].RemoveAll(o => ConfigState.IdOf(o) == "t-cam");

        PlanBuilder.Build(Template(), target, TemplateOptions("wlanconf")).Errors
            .Should().Contain(e => e.Contains("Cameras") && e.Contains("not on the console"));
    }

    [Fact]
    public void Apply_TemplateSavedWithoutReferences_IsAnError_NotADanglingWrite()
    {
        // A UniFi-shaped ID from the source site, with no reference saved for it.
        var template = Template();
        template.Collections.Remove("networkconf");
        template.Get("wlanconf").Single()["private_preshared_keys"]![0]!["networkconf_id"] = "11111111-2222-4333-8444-555555555555";

        var doc = PlanBuilder.Build(template, Target(), TemplateOptions("wlanconf"));

        doc.Errors.Should().ContainSingle(e => e.Contains("names an object that is not on this console"));
    }

    [Fact]
    public void Apply_AlreadyThere_IsNothing()
    {
        var target = Target();
        target.Collections["wlanconf"][0]["private_preshared_keys"] = JsonNode.Parse(
            """[{"password":"guest-pass","networkconf_id":"t-guest"},{"password":"iot-pass-1","networkconf_id":"t-iot"},{"password":"cam-pass-1","networkconf_id":"t-cam"}]""");

        PlanBuilder.Build(Template(), target, TemplateOptions("wlanconf")).Operations.Should().BeEmpty();
    }

    [Fact]
    public void Drift_SitesOwnPpsksAreNotDrift_MissingTemplateOnesAre()
    {
        var target = Target();
        DriftCalculator.Drift(Template(), target, TemplateOptions("wlanconf"))
            .Should().ContainSingle(o => o.Collection == "wlanconf", "the template's PPSKs are not on the site yet");

        target.Collections["wlanconf"][0]["private_preshared_keys"] = JsonNode.Parse(
            """[{"password":"guest-pass","networkconf_id":"t-guest"},{"password":"iot-pass-1","networkconf_id":"t-iot"},{"password":"cam-pass-1","networkconf_id":"t-cam"}]""");
        DriftCalculator.Drift(Template(), target, TemplateOptions("wlanconf")).Should().BeEmpty();
    }

    [Fact]
    public void Drift_NeverReportsTheReferences()
    {
        var target = Target();
        target.Collections["networkconf"][1]["ip_subnet"] = "198.51.100.65/26";

        DriftCalculator.Drift(Template(), target, TemplateOptions("wlanconf")).Should().NotContain(o => o.Collection == "networkconf");
    }
}
