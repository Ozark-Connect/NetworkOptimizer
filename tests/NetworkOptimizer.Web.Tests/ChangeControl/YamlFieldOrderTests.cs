using FluentAssertions;
using NetworkOptimizer.Web.Services.ChangeControl;
using Xunit;
using static NetworkOptimizer.Web.Tests.ChangeControl.ConfigDiffEngineTests;

namespace NetworkOptimizer.Web.Tests.ChangeControl;

public class YamlFieldOrderTests
{
    // Keys deliberately alphabetical, as snapshots store them.
    private static ConfigState Site() => State(
        ("firewall_zone", """{"_id":"z1","name":"Internal","zone_key":"internal","network_ids":["n1"]}"""),
        ("networkconf", """{"_id":"n1","dhcpd_dns_1":"192.0.2.53","dhcpd_enabled":true,"dhcpd_start":"192.0.2.10","enabled":true,"firewall_zone_id":"z1","internet_access_enabled":true,"ip_subnet":"192.0.2.1/24","mdns_enabled":true,"name":"IoT","purpose":"corporate","unknown_new_field":1,"vlan":30,"vlan_enabled":true}"""),
        ("firewall_policy", """{"_id":"p1","action":"BLOCK","destination":{"matching_target":"ANY","zone_id":"z1"},"enabled":true,"ip_version":"BOTH","name":"Block IoT","protocol":"all","source":{"port_matching_type":"ANY","matching_target":"NETWORK","network_ids":["n1"],"zone_id":"z1"}}"""));

    private static List<string> Lines(string collection) =>
        YamlConverter.Export(Site(), [collection], SecretMode.Mask).Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

    [Fact]
    public void Network_IdentityFirst_ThenUiSections_WithHeadings()
    {
        Lines("networkconf").Should().ContainInOrder(
            "- name: IoT", "purpose: corporate", "enabled: true",
            "# VLAN", "vlan_enabled: true", "vlan: 30",
            "# IPv4", "ip_subnet: 192.0.2.1/24",
            "# DHCP", "dhcpd_enabled: true", "dhcpd_start: 192.0.2.10", "dhcpd_dns_1: 192.0.2.53",
            "# Access", "firewall_zone: Internal", "internet_access_enabled: true",
            "# Multicast and discovery", "mdns_enabled: true",
            "# Other", "unknown_new_field: 1");
    }

    [Fact]
    public void Network_NoHeadingForAGroupWithNoFields()
    {
        Lines("networkconf").Should().NotContain(["# IPv6", "# WAN connection", "# Identity"]);
    }

    [Fact]
    public void FirewallPolicy_SourceAndDestinationFirst_NestedKeysInOrder()
    {
        var lines = Lines("firewall_policy");
        lines.Should().ContainInOrder("- name: Block IoT", "enabled: true", "action: BLOCK",
            "# Match", "source:", "zone: Internal", "matching_target: NETWORK", "networks: [IoT]", "port_matching_type: ANY",
            "destination:", "protocol: all", "ip_version: BOTH");
    }

    [Fact]
    public void Headings_RoundTrip_AsComments()
    {
        var yaml = YamlConverter.Export(Site(), ["networkconf"], SecretMode.Mask);
        var parsed = YamlConverter.Parse(yaml, Site(), "networks.yaml");
        parsed.Problems.Should().NotContain(p => p.Level == "Error");
        var plan = PlanBuilder.Build(Merge(Site(), parsed), Site(), new PlanOptions { Collections = new HashSet<string> { "networkconf" } });
        plan.Operations.Should().BeEmpty("an unedited export changes nothing");
    }

    [Fact]
    public void EveryCollection_ListsEachFieldOnce()
    {
        foreach (var def in ConfigCollections.All)
        {
            var listed = def.FieldGroups.SelectMany(g => g.Fields).ToList();
            listed.Should().OnlyHaveUniqueItems($"{def.Key} places each field in one group");
        }
    }

    [Fact]
    public void Settings_FollowTheirSectionOrder()
    {
        var order = ConfigFieldOrder.SettingSections["mgmt"].Take(4).ToList();
        var obj = new System.Text.Json.Nodes.JsonObject { ["_id"] = "s1", ["key"] = "mgmt" };
        foreach (var f in order.AsEnumerable().Reverse()) obj[f] = "x";
        var state = new ConfigState();
        state.Collections["setting"] = [obj];
        var lines = YamlConverter.Export(state, ["setting"], SecretMode.Mask).Split('\n').Select(l => l.Trim()).ToList();
        lines.Should().ContainInOrder(["- key: mgmt", .. order.Select(f => $"{f}: x")]);
    }

    [Fact]
    public void DevicePortOverride_PortIndexThenNameThenProfile()
    {
        var state = State(("device", """{"_id":"d1","mac":"aa:bb:cc:00:00:01","name":"Switch","port_overrides":[{"poe_mode":"auto","portconf_id":"pc1","name":"AP","port_idx":4}]}"""));
        var lines = YamlConverter.Export(state, ["device"], SecretMode.Mask).Split('\n').Select(l => l.Trim()).ToList();
        lines.Should().ContainInOrder("- port_idx: 4", "name: AP", "portconf_id: pc1", "poe_mode: auto");
    }

    [Fact]
    public void AmbiguousObject_NoHeadingRightAfterTheIdLine()
    {
        // Port profiles have no enforced key, so a name clash writes id: first.
        var state = State(
            ("portconf", """{"_id":"p1","name":"Same","poe_mode":"off"}"""),
            ("portconf", """{"_id":"p2","name":"Same","poe_mode":"auto"}"""));
        var lines = YamlConverter.Export(state, ["portconf"], SecretMode.Mask).Split('\n').Select(l => l.Trim()).ToList();
        lines.Should().Contain("- id: p1");
        lines[lines.IndexOf("- id: p1") + 1].Should().Be("name: Same");
        lines.Should().ContainInOrder("- id: p1", "name: Same", "# PoE", "poe_mode: \"off\"");
    }

    private static ConfigState Merge(ConfigState baseState, YamlParseResult parsed)
    {
        var desired = baseState.Clone();
        foreach (var (key, list) in parsed.Collections) desired.Collections[key] = list;
        return desired;
    }
}
