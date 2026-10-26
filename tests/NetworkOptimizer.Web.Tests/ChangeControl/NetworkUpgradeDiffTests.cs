using System.Text.Json.Nodes;
using FluentAssertions;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services.ChangeControl;
using Xunit;
using static NetworkOptimizer.Web.Tests.ChangeControl.ConfigDiffEngineTests;

namespace NetworkOptimizer.Web.Tests.ChangeControl;

/// <summary>
/// The Network 10.6 to 11 upgrade: every ID becomes a UUID, PostgreSQL writes every column, and
/// same-named policies only differ by their references. None of that is a change.
/// </summary>
public class NetworkUpgradeDiffTests
{
    private static ConfigState Versioned(string version, params (string Collection, string Json)[] objects)
    {
        var s = State(objects);
        s.NetworkAppVersion = version;
        return s;
    }

    // Two zones and two same-named policies told apart only by their source group.
    private static ConfigState Before() => Versioned("10.6.109",
        ("firewall_zone", """{"_id":"z1","name":"Internal","zone_key":"internal"}"""),
        ("firewall_zone", """{"_id":"z2","name":"External","zone_key":"external"}"""),
        ("firewallgroup", """{"_id":"g1","name":"Blocked A","group_type":"address-group","group_members":["192.0.2.1"]}"""),
        ("firewallgroup", """{"_id":"g2","name":"Blocked B","group_type":"address-group","group_members":["192.0.2.2"]}"""),
        ("firewall_policy", """{"_id":"p1","index":10000,"name":"Block","action":"BLOCK","source":{"zone_id":"z2","ip_group_id":"g1"},"destination":{"zone_id":"z1"}}"""),
        ("firewall_policy", """{"_id":"p2","index":10001,"name":"Block","action":"BLOCK","source":{"zone_id":"z2","ip_group_id":"g2"},"destination":{"zone_id":"z1"}}"""),
        ("networkconf", """{"_id":"n1","name":"IoT","vlan":30,"vlan_enabled":true,"firewall_zone_id":"z1"}"""),
        ("routing", """{"_id":"r1","name":"Modem Admin","static-route_network":"192.0.2.0/32"}"""),
        ("channelplan", """{"_id":"c1","date":"2026-03-14T03:57:12Z","method":"ISOLATE_NEIGHBOURS"}"""));

    private static ConfigState After() => Versioned("11.0.97",
        ("firewall_zone", """{"_id":"u-z1","name":"Internal","zone_key":"internal"}"""),
        ("firewall_zone", """{"_id":"u-z2","name":"External","zone_key":"external"}"""),
        ("firewallgroup", """{"_id":"u-g1","name":"Blocked A","group_type":"address-group","group_members":["192.0.2.1"],"short_id":1}"""),
        ("firewallgroup", """{"_id":"u-g2","name":"Blocked B","group_type":"address-group","group_members":["192.0.2.2"],"short_id":2}"""),
        ("firewall_policy", """{"_id":"u-p2","index":10001,"name":"Block","action":"BLOCK","source":{"zone_id":"u-z2","ip_group_id":"u-g2"},"destination":{"zone_id":"u-z1"},"report_flow":true}"""),
        ("firewall_policy", """{"_id":"u-p1","index":10000,"name":"Block","action":"BLOCK","source":{"zone_id":"u-z2","ip_group_id":"u-g1"},"destination":{"zone_id":"u-z1"},"report_flow":true}"""),
        ("networkconf", """{"_id":"u-n1","legacy_id":"n1","name":"IoT","vlan":30,"vlan_enabled":true,"firewall_zone_id":"u-z1","mdns_enabled":false,"dpi_enabled":false}"""),
        ("channelplan", """{"_id":"u-c1","date":"2026-03-14T03:57:12Z","method":"ISOLATE_NEIGHBOURS"}"""));

    [Fact]
    public void Upgrade_RecordsOnlyWhatUniFiDeleted()
    {
        var diff = ConfigDiffEngine.Compare(Before(), After());

        diff.Objects.Should().ContainSingle()
            .Which.Should().Match<ObjectDiff>(o => o.Collection == "routing" && o.Action == ConfigChangeAction.Deleted && o.Name == "Modem Admin");
    }

    [Fact]
    public void Upgrade_ReportsAddedColumnsOnce()
    {
        ConfigDiffEngine.Compare(Before(), After()).SchemaAdditions["firewall_policy"].Should().Equal("report_flow");
    }

    [Fact]
    public void SameVersion_NewFieldIsAChange()
    {
        var before = Versioned("11.0.97", ("firewall_policy", """{"_id":"p1","name":"Allow","source":{"zone_id":"z"},"destination":{"zone_id":"z"}}"""));
        var after = Versioned("11.0.97", ("firewall_policy", """{"_id":"p1","name":"Allow","source":{"zone_id":"z"},"destination":{"zone_id":"z"},"logging":true}"""));
        ConfigDiffEngine.Compare(before, after).Objects.Should().ContainSingle().Which.Fields.Should().ContainSingle(f => f.Path == "logging");
    }

    [Fact]
    public void ReferenceToAnotherObject_StillCounts()
    {
        var after = After();
        after.Get("networkconf")[0]["firewall_zone_id"] = "u-z2";
        ConfigDiffEngine.Compare(Before(), after).Objects
            .Should().Contain(o => o.Collection == "networkconf" && o.Fields.Any(f => f.Path == "firewall_zone_id"));
    }

    [Fact]
    public void AbsentEqualsFalseAndZero_ButNotTrue()
    {
        var before = State(("networkconf", """{"_id":"n1","name":"IoT","vlan":30,"vlan_enabled":true}"""));
        var quiet = State(("networkconf", """{"_id":"n1","name":"IoT","vlan":30,"vlan_enabled":true,"igmp_snooping":false,"dhcpd_dns_count":0}"""));
        var loud = State(("networkconf", """{"_id":"n1","name":"IoT","vlan":30,"vlan_enabled":true,"igmp_snooping":true}"""));

        ConfigDiffEngine.Compare(before, quiet).IsEmpty.Should().BeTrue();
        ConfigDiffEngine.Compare(before, loud).IsEmpty.Should().BeFalse();
    }

    [Fact]
    public void NumberStoredAsString_IsTheSameValue()
    {
        var before = State(("user", """{"_id":"u1","mac":"aa:bb:cc:dd:ee:ff","name":"Laptop","dev_id_override":"4109"}"""));
        var after = State(("user", """{"_id":"u1","mac":"aa:bb:cc:dd:ee:ff","name":"Laptop","dev_id_override":4109}"""));
        ConfigDiffEngine.Compare(before, after).IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Attribution_OwnedObjectDuringUpgrade_IsUniFi()
    {
        var owned = new ObjectDiff
        {
            Collection = "firewall_policy", ObjectId = "p1", Name = "Net. Opt. Blocklist", Action = ConfigChangeAction.Updated,
            OwnedBy = "Threat Intelligence",
        };
        var now = new DateTime(2026, 10, 26, 20, 0, 0, DateTimeKind.Utc);

        ChangeAttributor.Attribute([owned], [], [], now, inUpgradeWindow: true)
            .Should().ContainSingle().Which.ActorKind.Should().Be(ConfigChangeActorKind.LikelyUniFi);
        ChangeAttributor.Attribute([owned], [], [], now, inUpgradeWindow: false)
            .Should().ContainSingle().Which.ActorKind.Should().Be(ConfigChangeActorKind.NetworkOptimizer);
    }

    [Fact]
    public void ReferenceUnchangedWhileItsTargetWasRenumbered_IsNotAChange()
    {
        // Network 11 gave the Default WLAN group a new ID; the AP kept the old one.
        var before = Versioned("10.6.109",
            ("wlangroup", """{"_id":"old-default","name":"Default"}"""),
            ("device", """{"_id":"d1","mac":"aa:bb:cc:dd:ee:01","name":"AP","wlangroup_id_na":"old-default"}"""));
        var after = Versioned("11.0.97",
            ("wlangroup", """{"_id":"000000000000000000000001","name":"Default"}"""),
            ("device", """{"_id":"u-d1","mac":"aa:bb:cc:dd:ee:01","name":"AP","wlangroup_id_na":"old-default"}"""));
        ConfigDiffEngine.Compare(before, after).IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void DnsFilter_NoneEntryDropped_IsNotAChange_ButARealFilterIs()
    {
        var before = State(("setting", """{"_id":"s1","key":"ips","dns_filters":[{"network_id":"n1","filter":"none","allowed_sites":[],"blocked_sites":[]},{"network_id":"n2","filter":"security"}]}"""));
        var none = State(("setting", """{"_id":"s1","key":"ips","dns_filters":[{"network_id":"n2","filter":"security"}]}"""));
        var real = State(("setting", """{"_id":"s1","key":"ips","dns_filters":[{"network_id":"n1","filter":"none","allowed_sites":[],"blocked_sites":[]}]}"""));

        ConfigDiffEngine.Compare(before, none).IsEmpty.Should().BeTrue();
        ConfigDiffEngine.Compare(before, real).Objects.Single().Fields.Should().ContainSingle(f => f.Path == "dns_filters[network_id=n2]");
    }

    [Fact]
    public void ClientGroupReference_FollowsTheGroupAcrossTheUpgrade()
    {
        var before = Versioned("10.6.109",
            ("network_members_group", """{"_id":"g-old","name":"PC Gaming","type":"CLIENTS","members":["aa:bb:cc:dd:ee:01"]}"""),
            ("user", """{"_id":"u1","mac":"aa:bb:cc:dd:ee:01","name":"Rig","network_members_group_ids":["g-old"]}"""));
        var after = Versioned("11.0.97",
            ("network_members_group", """{"id":"g-new","name":"PC Gaming","type":"CLIENTS","members":["aa:bb:cc:dd:ee:01"]}"""),
            ("user", """{"_id":"u-u1","mac":"aa:bb:cc:dd:ee:01","name":"Rig","network_members_group_ids":["g-new"]}"""));
        ConfigDiffEngine.Compare(before, after).IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void BeforeStateInAfterIds_ReferencesTheUpgradedObjects()
    {
        var mapped = ConfigDiffEngine.InAfterIds(Before(), After());
        mapped.Get("networkconf")[0]["_id"]!.ToString().Should().Be("u-n1");
        mapped.Get("networkconf")[0]["firewall_zone_id"]!.ToString().Should().Be("u-z1");
        mapped.Get("routing")[0]["_id"]!.ToString().Should().Be("r1");
    }

    [Fact]
    public void RestoreOfPreUpgradeSnapshot_KeepsReferencesItNeverCaptured_AndLeavesGeneratedPolicies()
    {
        // The 10.6 snapshot predates client groups, so the client's old group ID resolves to nothing.
        var before = Versioned("10.6.109",
            ("user", """{"_id":"u1","mac":"aa:bb:cc:dd:ee:01","name":"Rig","network_members_group_ids":["0123456789abcdef01234567"]}"""),
            ("firewall_policy", """{"_id":"p1","index":10000,"name":"Allow","action":"ALLOW","source":{"zone_id":"z"},"destination":{"zone_id":"z"}}"""));
        var after = Versioned("11.0.97",
            ("user", """{"_id":"u-u1","mac":"aa:bb:cc:dd:ee:01","name":"Rig","network_members_group_ids":["2cb64e2c-eb5f-4af9-bb9f-44750f948594"]}"""),
            ("firewall_policy", """{"_id":"u-p1","index":10000,"name":"Allow","action":"ALLOW","source":{"zone_id":"z"},"destination":{"zone_id":"z"}}"""),
            ("firewall_policy", """{"_id":"u-r1","index":30000,"name":"Allow (Return)","action":"ALLOW","predefined":true,"origin_type":"custom_firewall_rule","origin_id":"u-p1","source":{"zone_id":"z"},"destination":{"zone_id":"z"}}"""));

        var doc = PlanBuilder.Build(before, after, new PlanOptions());

        doc.Errors.Should().BeEmpty();
        doc.Operations.Should().BeEmpty();
        doc.Notes.Should().Contain(n => n.Contains("did not capture"));
    }

    [Fact]
    public void SameVersionRollBack_StillRestoresAChangedReference()
    {
        var before = Versioned("11.0.97",
            ("network_members_group", """{"id":"g1","name":"Gaming","type":"CLIENTS","members":[]}"""),
            ("network_members_group", """{"id":"g2","name":"Servers","type":"CLIENTS","members":[]}"""),
            ("user", """{"_id":"u1","mac":"aa:bb:cc:dd:ee:01","name":"Rig","network_members_group_ids":["g1"]}"""));
        var after = Versioned("11.0.97",
            ("network_members_group", """{"id":"g1","name":"Gaming","type":"CLIENTS","members":[]}"""),
            ("network_members_group", """{"id":"g2","name":"Servers","type":"CLIENTS","members":[]}"""),
            ("user", """{"_id":"u1","mac":"aa:bb:cc:dd:ee:01","name":"Rig","network_members_group_ids":["g2"]}"""));

        var doc = PlanBuilder.Build(before, after, new PlanOptions());

        doc.Operations.Should().ContainSingle(o => o.Collection == "user" && o.Action == PlanAction.Update);
        doc.Notes.Should().NotContain(n => n.Contains("did not capture"));
    }

    [Fact]
    public void RestoreOfPreUpgradeSnapshot_RecreatesOnlyTheDeletedRoute()
    {
        var doc = PlanBuilder.Build(Before(), After(), new PlanOptions());

        doc.Errors.Should().BeEmpty();
        doc.Operations.Should().ContainSingle()
            .Which.Should().Match<PlanOperation>(o => o.Collection == "routing" && o.Action == PlanAction.Create);
    }
}
