using System.Text.Json.Nodes;
using FluentAssertions;
using NetworkOptimizer.Web.Services.ChangeControl;
using Xunit;
using static NetworkOptimizer.Web.Tests.ChangeControl.ConfigDiffEngineTests;

namespace NetworkOptimizer.Web.Tests.ChangeControl;

public class NewObjectsTests
{
    private static ConfigState Site() => State(
        ("networkconf", """{"_id":"n1","name":"Default","purpose":"corporate","ip_subnet":"192.0.2.1/24"}"""),
        ("networkconf", """{"_id":"n2","name":"IoT","purpose":"corporate","vlan":30,"vlan_enabled":true,"ip_subnet":"198.51.100.1/24"}"""),
        ("usergroup", """{"_id":"g1","name":"Default","attr_hidden_id":"Default"}"""),
        ("apgroups", """{"_id":"a1","name":"All APs","attr_hidden_id":"default","device_macs":[]}"""),
        ("firewall_zone", """{"_id":"z1","name":"Internal","zone_key":"internal","network_ids":["n1"]}"""),
        ("firewall_zone", """{"_id":"z2","name":"External","zone_key":"external","network_ids":[]}"""),
        ("wlanconf", """{"_id":"w1","name":"Home","security":"wpapsk","x_passphrase":"hunter2","networkconf_id":"n1"}"""),
        ("firewall_policy", """{"_id":"p1","name":"Allow DNS","action":"ALLOW","source":{"zone_id":"z1"},"destination":{"zone_id":"z2"}}"""));

    private const string WlanDefaults = """
        [{"bss_transition":true,"enabled":true,"pmf_mode":"optional","security":"wpapsk","wlan_bands":["2g","5g","6g"],"wpa3_support":true,"wpa_mode":"wpa2"},
         {"enhanced_iot":true,"wlan_bands":["2g"]}]
        """;

    private static YamlParseResult AddAndParse(ConfigState site, string collection, JsonObject? defaults, Func<string, string>? fill = null)
    {
        var def = ConfigCollections.Get(collection)!;
        var item = NewObjects.Yaml(def, site, defaults);
        var file = YamlConverter.Export(site, [collection], SecretMode.Clear, ids: true) + (fill?.Invoke(item) ?? item);
        return YamlConverter.Parse(file, site, YamlConverter.FileName(def));
    }

    [Theory]
    [InlineData("wlanconf", true)]
    [InlineData("networkconf", true)]
    [InlineData("portconf", true)]
    [InlineData("firewall_policy", true)]
    [InlineData("firewall_zone", true)]
    [InlineData("firewallgroup", true)]
    [InlineData("usergroup", true)]
    [InlineData("network_members_group", true)]
    [InlineData("static_dns", true)]
    [InlineData("user", false)]
    [InlineData("device", false)]
    [InlineData("routing", false)]
    [InlineData("wlangroup", false)]
    public void CanAdd_OnlyProvenCreates(string collection, bool expected) =>
        NewObjects.CanAdd(ConfigCollections.Get(collection)!).Should().Be(expected);

    [Fact]
    public void Wlan_TakesUniFiDefaults_AndTheSiteDefaults_WithBlanks()
    {
        var item = NewObjects.Yaml(ConfigCollections.Get("wlanconf")!, Site(), NewObjects.ParseDefaults(WlanDefaults));

        item.Should().StartWith("- ").And.NotContain("id:").And.NotContain("hunter2");
        item.Should().Contain("bss_transition: true");
        item.Should().Contain("network: Default").And.Contain("usergroup: Default").And.Contain("ap_groups: [All APs]");
        item.Should().Contain("name: \n").And.Contain("x_passphrase: \n");
        item.Should().NotContain("enhanced_iot", "the IoT entry is the second of wlan/defaults");
    }

    [Fact]
    public void Wlan_KeepsUniFisWpa3And6GhzDefaults()
    {
        var item = NewObjects.Yaml(ConfigCollections.Get("wlanconf")!, Site(), NewObjects.ParseDefaults(WlanDefaults));

        item.Should().Contain("wlan_bands: [2g, 5g, 6g]").And.Contain("wpa3_support: true").And.Contain("pmf_mode: optional");
    }

    [Fact]
    public void Policy_IsOneListItem_WithNoZonePairHeading()
    {
        var defaults = NewObjects.ParseDefaults("""{"action":"BLOCK","enabled":true,"source":{"matching_target":"ANY"},"destination":{"matching_target":"ANY"}}""");

        var item = NewObjects.Yaml(ConfigCollections.Get("firewall_policy")!, Site(), defaults);
        item.Should().StartWith("- ");
        item.Split('\n').Should().NotContain(l => l.StartsWith('#'));
    }

    [Fact]
    public void Policy_Block_CreatesNoReturnTraffic()
    {
        var defaults = NewObjects.ParseDefaults("""{"action":"BLOCK","create_allow_respond":true,"enabled":true}""");

        NewObjects.Yaml(ConfigCollections.Get("firewall_policy")!, Site(), defaults).Should().Contain("create_allow_respond: false");
    }

    [Fact]
    public void Policy_BlockWithReturnTraffic_IsAnError()
    {
        var parsed = AddAndParse(Site(), "firewall_policy", null, i => i.Replace("- name: \n", "- name: Block IoT\n")
            .Replace("zone: \n", "zone: Internal\n").Replace("create_allow_respond: false", "create_allow_respond: true"));

        parsed.Problems.Should().ContainSingle(p => p.Level == "Error")
            .Which.Message.Should().Be("A new Firewall Policy creates return traffic (create_allow_respond) only with action ALLOW.");
    }

    [Fact]
    public void Wlan_Blank_NameIsAnError_PassphraseAWarning()
    {
        var parsed = AddAndParse(Site(), "wlanconf", null);

        parsed.Problems.Where(p => p.Level == "Error").Select(p => p.Message).Should().Equal("A new WLAN needs name.");
        parsed.Problems.Where(p => p.Level == "Warning").Select(p => p.Message)
            .Should().Equal("A new WLAN without x_passphrase is created, but does nothing until it has one.");
    }

    [Fact]
    public void Wlan_WithoutApGroups_IsAnError()
    {
        var site = Site();
        site.Collections["apgroups"].Clear();

        AddAndParse(site, "wlanconf", null, i => i.Replace("name: \n", "name: Guest\n"))
            .Problems.Should().Contain(p => p.Level == "Error" && p.Message == "A new WLAN needs ap_groups.");
    }

    [Fact]
    public void PortProfile_LeavesOutNullDefaults()
    {
        var item = NewObjects.Yaml(ConfigCollections.Get("portconf")!, Site(), NewObjects.ParseDefaults("""{"autoneg":true,"poe_mode":null}"""));

        item.Should().Contain("autoneg: true").And.NotContain("poe_mode").And.Contain("native_network: Default");
    }

    [Fact]
    public void Wlan_Filled_IsACreate_WithReferencesResolved()
    {
        var parsed = AddAndParse(Site(), "wlanconf", NewObjects.ParseDefaults(WlanDefaults),
            item => item.Replace("name: \n", "name: Guest\n").Replace("x_passphrase: \n", "x_passphrase: correct-horse\n"));

        parsed.HasErrors.Should().BeFalse(string.Join("; ", parsed.Problems.Select(p => p.Message)));
        var created = parsed.Collections["wlanconf"].Single(o => o["name"]?.ToString() == "Guest");
        ConfigState.IdOf(created).Should().StartWith(YamlConverter.TempIdPrefix);
        created["networkconf_id"]!.ToString().Should().Be("n1");
        created["usergroup_id"]!.ToString().Should().Be("g1");
        created["ap_group_ids"]!.AsArray().Select(a => a!.ToString()).Should().Equal("a1");
        created["x_passphrase"]!.ToString().Should().Be("correct-horse");
    }

    [Fact]
    public void Policy_ZonesAreRequired_AndNamedAsTheFileWritesThem()
    {
        var defaults = NewObjects.ParseDefaults("""
            {"action":"BLOCK","enabled":true,"predefined":false,"report_flow":true,"protocol":"all",
             "source":{"matching_target":"ANY","matching_target_type":"SPECIFIC","port_matching_type":"ANY"},
             "destination":{"matching_target":"ANY","matching_target_type":"SPECIFIC","port_matching_type":"ANY"},
             "schedule":{"mode":"ALWAYS"}}
            """);
        var item = NewObjects.Yaml(ConfigCollections.Get("firewall_policy")!, Site(), defaults);
        item.Should().NotContain("predefined").And.NotContain("report_flow", "the policy allowlist leaves them out");
        item.Should().Contain("matching_target_type: SPECIFIC");

        var parsed = AddAndParse(Site(), "firewall_policy", defaults, i => i.Replace("- name: \n", "- name: Block IoT\n"));
        parsed.Problems.Where(p => p.Level == "Error").Select(p => p.Message)
            .Should().BeEquivalentTo("A new Firewall Policy needs source.zone.", "A new Firewall Policy needs destination.zone.");

        var filled = AddAndParse(Site(), "firewall_policy", defaults, i => i.Replace("- name: \n", "- name: Block IoT\n")
            .Replace("zone: \n", "zone: Internal\n", StringComparison.Ordinal));
        filled.Problems.Where(p => p.Level == "Error").Should().BeEmpty();
    }

    [Fact]
    public void Network_VlanIsAnError_SubnetAndDhcpRangeAreWarnings()
    {
        var parsed = AddAndParse(Site(), "networkconf", null, i => i.Replace("name: \n", "name: Lab\n"));

        parsed.Problems.Where(p => p.Level == "Error").Select(p => p.Message).Should().Equal("A new Network needs vlan.");
        parsed.Problems.Where(p => p.Level == "Warning").Select(p => p.Message).Should().BeEquivalentTo(
            "A new Network without ip_subnet is created, but does nothing until it has one.",
            "A new Network without dhcpd_start is created, but does nothing until it has one.",
            "A new Network without dhcpd_stop is created, but does nothing until it has one.");
    }

    [Fact]
    public void ExistingObjects_AreNeverCheckedForRequiredFields()
    {
        var site = Site();
        site.Collections["wlanconf"][0].Remove("x_passphrase");
        var yaml = YamlConverter.Export(site, ["wlanconf"], SecretMode.Mask, ids: true);

        YamlConverter.Parse(yaml, site, "wlans.yaml").HasErrors.Should().BeFalse();
    }

    [Fact]
    public void ParseDefaults_BadBody_IsNull()
    {
        NewObjects.ParseDefaults("not json").Should().BeNull();
        NewObjects.ParseDefaults("[]").Should().BeNull();
    }
}
