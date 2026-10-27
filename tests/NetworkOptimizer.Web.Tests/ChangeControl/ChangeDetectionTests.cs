using System.Text.Json.Nodes;
using FluentAssertions;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.Web.Services.ChangeControl;
using Xunit;
using static NetworkOptimizer.Web.Tests.ChangeControl.ConfigDiffEngineTests;

namespace NetworkOptimizer.Web.Tests.ChangeControl;

public class ChangeDetectionTests
{
    private static readonly DateTime Now = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);

    private static ObjectDiff Diff(string collection, string id, string name, params FieldChange[] fields) => new()
    {
        Collection = collection, ObjectId = id, Name = name, Action = ConfigChangeAction.Updated, Fields = [.. fields],
    };

    private static AuditLogEvent Event(string objectId, string admin = "Alex", int secondsAgo = 30) => new()
    {
        Id = Guid.NewGuid().ToString(), Key = "MADE_CHANGES_ONE_SETTING_OBJECT", AdminId = "a1", AdminName = admin,
        Ip = "203.0.113.24", ObjectId = objectId, Timestamp = new DateTimeOffset(Now.AddSeconds(-secondsAgo)).ToUnixTimeMilliseconds(),
    };

    [Fact]
    public void AuditParse_ReadsParameters()
    {
        var events = AuditLogReader.Parse("""
            {"data":[{"id":"e1","key":"MADE_CHANGES_ONE_SETTING_OBJECT","timestamp":1760000000000,
              "parameters":{"ADMIN":{"id":"a1","name":"Alex"},"IP":{"id":"203.0.113.24"},"OBJECT":{"id":"w1","name":"Home"},
                "SECTION":{"id":"WIFI"},"SETTING_NAME":{"collection":"wlanconf","id":"security"}}}]}
            """);
        var e = events.Should().ContainSingle().Subject;
        e.AdminName.Should().Be("Alex");
        e.ObjectId.Should().Be("w1");
        e.SettingCollection.Should().Be("wlanconf");
        e.SettingField.Should().Be("security");
        e.IsChange.Should().BeTrue();
    }

    [Fact]
    public void AuditParse_ToleratesScalarParameters()
    {
        var events = AuditLogReader.Parse("""[{"key":"MADE_CHANGES_ONE_SETTING_OBJECT","timestamp":1,"parameters":{"ADMIN":"Alex","COUNT":"3"}}]""");
        events.Single().AdminName.Should().Be("Alex");
        events.Single().Count.Should().Be(3);
    }

    [Fact]
    public void Attribute_PersonFromAuditEvent()
    {
        var groups = ChangeAttributor.Attribute([Diff("wlanconf", "w1", "Home")], [Event("w1")], [], Now, false);
        var g = groups.Should().ContainSingle().Subject;
        g.ActorKind.Should().Be(ConfigChangeActorKind.Person);
        g.ActorName.Should().Be("Alex");
    }

    [Fact]
    public void Attribute_OwnWrite_IsNetworkOptimizer_EvenThoughTheAuditAdminIsTheKeyOwner()
    {
        var journal = new List<ConfigWriteJournalEntry>
        {
            new() { At = Now.AddSeconds(-25), Method = "PUT", Path = "rest/wlanconf/w1", Collection = "wlanconf", ObjectId = "w1", Source = "change-control" },
        };
        var groups = ChangeAttributor.Attribute([Diff("wlanconf", "w1", "Home")], [Event("w1", admin: "TJ")], journal, Now, false);
        groups.Single().ActorKind.Should().Be(ConfigChangeActorKind.NetworkOptimizer);
    }

    [Fact]
    public void Attribute_OwnWrite_OccursAtItsNewestWrite_NotTheWindowsOldest()
    {
        var journal = new List<ConfigWriteJournalEntry>
        {
            new() { At = Now.AddMinutes(-12), Method = "PUT", Path = "rest/networkconf/n1", Collection = "networkconf", ObjectId = "n1", Source = "change-control" },
            new() { At = Now.AddMinutes(-8), Method = "PUT", Path = "rest/wlanconf/w1", Collection = "wlanconf", ObjectId = "w1", Source = "change-control" },
            new() { At = Now.AddSeconds(-5), Method = "PUT", Path = "rest/wlanconf/w1", Collection = "wlanconf", ObjectId = "w1", Source = "change-control" },
        };
        ChangeAttributor.Attribute([Diff("wlanconf", "w1", "Home")], [], journal, Now, false)
            .Single().OccurredAt.Should().Be(Now.AddSeconds(-5));
    }

    [Fact]
    public void Labels_PortSecuritySettings_UseUniFiNames()
    {
        ChangeDescriber.LabelFor("port_overrides[port_idx=2].trusted_port_mac").Should().Be("Port 2 Lock Port to UniFi Device");
        ChangeDescriber.LabelFor("port_overrides[port_idx=2].port_security_mac_address").Should().Be("Port 2 MAC Address Filter");
        ChangeDescriber.LabelFor("port_overrides[port_idx=2].port_security_enabled").Should().Be("Port 2 Port Security");
        ChangeDescriber.LabelFor("port_overrides[port_idx=2].name").Should().Be("Port 2 Name");
    }

    [Fact]
    public void Labels_PortShorthand_IsTitleCaseWithRealNames()
    {
        ChangeDescriber.LabelFor("port_overrides[port_idx=5].autoneg").Should().Be("Port 5 Auto Negotiate Link Speed");
        ChangeDescriber.LabelFor("port_overrides[port_idx=5].dot1x_ctrl").Should().Be("Port 5 Client Authentication (802.1X)");
        ChangeDescriber.LabelFor("port_overrides[port_idx=5].lldpmed_enabled").Should().Be("Port 5 LLDP-MED");
        ChangeDescriber.LabelFor("port_overrides[port_idx=5].stp_port_mode").Should().Be("Port 5 STP");
        ChangeDescriber.LabelFor("port_overrides[port_idx=5].op_mode").Should().Be("Port 5 Operation");
        ChangeDescriber.DisplayValue("aggregate", "port_overrides[port_idx=5].op_mode").Should().Be("Aggregating");
        ChangeDescriber.LabelFor("port_overrides[port_idx=5].flow_control_enabled").Should().Be("Port 5 Flow Control");
        ChangeDescriber.LabelFor("port_overrides[port_idx=5].dot1x_idle_timeout").Should().Be("Port 5 802.1X Idle Timeout");
    }

    [Fact]
    public void ForDisplay_PortModeAndTaggedVlans_UseUniFiOptionNames()
    {
        var shown = ChangeDescriber.ForDisplay(
        [
            new() { Path = "port_overrides[port_idx=4].forward", Label = "Port 4 Tagged VLAN Management", Old = "all", New = "native" },
            new() { Path = "port_overrides[port_idx=4].stp_edge_state", Label = "Port 4 Port Mode", Old = "Disabled", New = "enabled" },
            new() { Path = "port_overrides[port_idx=4].tagged_vlan_mgmt", Label = "Port 4 Tagged VLAN Management", Old = "auto", New = "block_all" },
        ]);

        shown.Select(f => $"{f.Label}: {f.Old} to {f.New}").Should().Equal(
            "Port 4 Port Mode: Infrastructure to Edge",
            "Port 4 Tagged VLAN Management: Allow All to Block All");
    }

    [Fact]
    public void DeviceConfigHash_MovesWithAPortSetting_NotWithStats()
    {
        var before = (JsonObject)JsonNode.Parse("""{"mac":"aa:bb:cc:dd:ee:ff","uptime":10,"port_overrides":[{"port_idx":4,"stp_edge_state":"disabled"}]}""")!;
        var laterStats = (JsonObject)JsonNode.Parse("""{"mac":"aa:bb:cc:dd:ee:ff","uptime":70,"port_overrides":[{"port_idx":4,"stp_edge_state":"disabled"}]}""")!;
        var edited = (JsonObject)JsonNode.Parse("""{"mac":"aa:bb:cc:dd:ee:ff","uptime":70,"port_overrides":[{"port_idx":4,"stp_edge_state":"enabled"}]}""")!;

        ChangeControlSiteEngine.DeviceConfigHash(laterStats).Should().Be(ChangeControlSiteEngine.DeviceConfigHash(before));
        ChangeControlSiteEngine.DeviceConfigHash(edited).Should().NotBe(ChangeControlSiteEngine.DeviceConfigHash(before));
    }

    [Theory]
    [InlineData("abc", "abc|111", false)]
    [InlineData("abc", "abd|111", true)]
    [InlineData("abc|111", "abc|111", false)]
    [InlineData("abc|111", "abc|222", true)]
    public void CfgDiffers_ValueSavedBeforeTheHash_ComparesCfgversionOnly(string stored, string current, bool differs) =>
        ChangeControlSiteEngine.CfgDiffers(stored, current).Should().Be(differs);

    [Theory]
    [InlineData("WEB", "Web")]
    [InlineData("MOBILE", "Mobile")]
    [InlineData("IOS", "iOS")]
    [InlineData("ANDROID", "Android")]
    [InlineData("API", "API")]
    [InlineData("SSH", "SSH")]
    [InlineData("Web", "Web")]
    public void Platform_AuditAccessMethod_IsTitleCasedWithAcronymsKept(string stored, string shown) =>
        ChangeControlService.Platform(stored).Should().Be(shown);

    [Theory]
    [InlineData("abc", "abd|111", false)]
    [InlineData("abc|111", "abd|111", false)]
    [InlineData("abc|111", "abc|222", true)]
    public void HashDiffers_OnlyTheConfigHashCounts(string stored, string current, bool differs) =>
        ChangeControlSiteEngine.HashDiffers(stored, current).Should().Be(differs);

    [Theory]
    [InlineData("on", "On")]
    [InlineData("none", "None")]
    [InlineData("NONE", "None")]
    [InlineData("force_authorized", "Force Authorized")]
    [InlineData("Main Network", "Main Network")]
    [InlineData("[Access] Main Network", "[Access] Main Network")]
    [InlineData("300", "300")]
    [InlineData("guest", "guest")]
    public void DisplayValue_TitleCasesEnumsOnly(string stored, string shown) =>
        ChangeDescriber.DisplayValue(stored).Should().Be(shown);

    [Theory]
    [InlineData("loft_ap")]
    [InlineData("auto")]
    [InlineData("desk")]
    public void DisplayValue_TypedNames_AreNeverRecased(string typed) =>
        ChangeDescriber.DisplayValue(typed, "port_overrides[port_idx=5].name").Should().Be(typed);

    [Fact]
    public void ForDisplay_ManualPortToProfile_KeepsOnlyTheProfileRow()
    {
        var fields = new List<StoredField>
        {
            new() { Path = "port_overrides[port_idx=5].autoneg", Label = "Port 5 Auto-Negotiation", Old = "on", New = "none" },
            new() { Path = "port_overrides[port_idx=5].native_networkconf_id", Label = "Port 5 Native Network", Old = "Main Network", New = "none" },
            new() { Path = "port_overrides[port_idx=5].portconf_id", Label = "Port 5 Port Profile", Old = "none", New = "[Access] Main Network" },
            new() { Path = "port_overrides[port_idx=6].autoneg", Label = "Port 6 Auto-Negotiation", Old = "on", New = "none" },
        };

        var shown = ChangeDescriber.ForDisplay(fields);

        shown.Select(f => f.Label).Should().Equal("Port 5 Port Profile", "Port 6 Auto-Negotiation");
        shown[0].Old.Should().Be("None");
        shown[1].New.Should().Be("None");

        ChangeDescriber.SummaryLine("Device", "Desk Switch", ChangeDescriber.ChangeText(shown))
            .Should().Be("Device Desk Switch: Port 5 Port Profile, Port 6 Auto-Negotiation");
        ChangeDescriber.SummaryLine("Device", "Desk Switch", ChangeDescriber.ChangeText(shown.Take(1).ToList()))
            .Should().Be("Device Desk Switch: Port 5 Port Profile: None to [Access] Main Network");
    }

    [Fact]
    public void Labels_NameAPortByItsGivenName()
    {
        var after = (JsonObject)JsonNode.Parse("""{"port_overrides":[{"port_idx":2,"name":"Desk"},{"port_idx":3,"name":"Port 3"}]}""")!;
        ChangeDescriber.LabelFor("port_overrides[port_idx=2].trusted_port_mac", after).Should().Be("Port 2 (Desk) Lock Port to UniFi Device");
        ChangeDescriber.LabelFor("port_overrides[port_idx=3].trusted_port_mac", after).Should().Be("Port 3 Lock Port to UniFi Device");
    }

    [Fact]
    public void Attribute_WlanApGroupDeletedAfterItsWlan_FollowsTheWlanDelete()
    {
        var group = new ObjectDiff
        {
            Collection = "apgroups", ObjectId = "g9", Name = "devices_ap_group", Action = ConfigChangeAction.Deleted,
            Before = (JsonObject)JsonNode.Parse("""{"_id":"g9","name":"devices_ap_group","for_wlanconf":true}""")!,
        };
        var journal = new List<ConfigWriteJournalEntry>
        {
            new() { At = Now.AddSeconds(-12), Method = "DELETE", Path = "rest/wlanconf/w9", Collection = "wlanconf", ObjectId = "w9", Source = "change-control" },
        };
        ChangeAttributor.Attribute([group], [], journal, Now, false).Single().ActorKind.Should().Be(ConfigChangeActorKind.NetworkOptimizer);
        ChangeAttributor.Attribute([group], [], [], Now, false).Single().ActorKind.Should().Be(ConfigChangeActorKind.LikelyUniFi);
    }

    [Fact]
    public void Attribute_OwnWrite_CarriesThePlanThatMadeIt()
    {
        var journal = new List<ConfigWriteJournalEntry>
        {
            new() { At = Now.AddSeconds(-5), Method = "PUT", Path = "rest/wlanconf/w1", Collection = "wlanconf", ObjectId = "w1", Source = "change-control:plan:12" },
        };
        ChangeAttributor.Attribute([Diff("wlanconf", "w1", "Home")], [], journal, Now, false).Single().PlanId.Should().Be(12);
        ChangeAttributor.PlanOf("change-control").Should().BeNull();
    }

    [Fact]
    public void Attribute_NoEvent_IsNoAdminRecorded()
    {
        var groups = ChangeAttributor.Attribute([Diff("portconf", "p1", "Cameras")], [], [], Now, false);
        groups.Single().ActorKind.Should().Be(ConfigChangeActorKind.NoAdminRecorded);
    }

    [Fact]
    public void Attribute_InUpgradeWindow_IsLikelyUniFi()
    {
        var groups = ChangeAttributor.Attribute([Diff("portconf", "p1", "Cameras")], [], [], Now, inUpgradeWindow: true);
        groups.Single().ActorKind.Should().Be(ConfigChangeActorKind.LikelyUniFi);
    }

    [Fact]
    public void Attribute_ChannelChangeOnly_IsLikelyUniFi()
    {
        var d = Diff("device", "d1", "AP", new FieldChange("radio_table[radio=na].channel", null, null));
        ChangeAttributor.Attribute([d], [], [], Now, false).Single().ActorKind.Should().Be(ConfigChangeActorKind.LikelyUniFi);
    }

    [Theory]
    [InlineData(UniFiApiScope.SiteV1, "rest/networkconf/abc", "networkconf", "abc")]
    [InlineData(UniFiApiScope.SiteV1, "rest/setting/mgmt/s1", "setting", "s1")]
    [InlineData(UniFiApiScope.SiteV2, "firewall-policies/p9", "firewall_policy", "p9")]
    [InlineData(UniFiApiScope.SiteV2, "firewall-policies/batch-reorder", "firewall_policy", null)]
    [InlineData(UniFiApiScope.SiteV1, "rest/device/d1", "device", "d1")]
    [InlineData(UniFiApiScope.SiteV1, "rest/firewallgroup", "firewallgroup", null)]
    public void JournalPath_MapsToCollectionAndId(UniFiApiScope scope, string path, string collection, string? id)
    {
        ChangeControlSiteEngine.MapJournalPath(scope, path).Should().Be((collection, id));
    }

    [Fact]
    public void Retention_KeepsEverythingRecent_ThenOnePerDay_ThenOnePerMonth_AndNeverPinned()
    {
        var settings = new ChangeControlSettings { KeepEveryChangeDays = 30, KeepDailyDays = 365, KeepMonthlyMonths = null };
        var id = 0;
        ConfigSnapshot S(DateTime at, bool pinned = false) => new() { Id = ++id, TakenAt = at, Pinned = pinned };
        var snaps = new List<ConfigSnapshot>
        {
            S(Now.AddDays(-1)), S(Now.AddDays(-1).AddHours(1)),                    // recent: both kept
            S(Now.AddDays(-40)), S(Now.AddDays(-40).AddHours(2)),                  // daily tier: one kept
            S(Now.AddDays(-400)), S(Now.AddDays(-401)), S(Now.AddDays(-402), true), // monthly tier, one pinned
        };
        var delete = SnapshotRetention.ToDelete(snaps, settings, Now);
        delete.Should().BeEquivalentTo([3, 6]);
    }

    [Fact]
    public void Describer_WlanSecurity_IsOneRow()
    {
        var before = Obj("""{"_id":"w1","name":"Home","security":"wpapsk","wpa_mode":"wpa2","wpa3_support":false,"pmf_mode":"disabled"}""");
        var after = Obj("""{"_id":"w1","name":"Home","security":"wpapsk","wpa_mode":"wpa2","wpa3_support":true,"wpa3_transition":true,"pmf_mode":"optional"}""");
        var diff = ConfigDiffEngine.CompareObjects(ConfigCollections.Get("wlanconf")!, before, after)!;
        var rows = ChangeDescriber.Rows(diff);
        rows.Should().Contain(r => r.Label == "Security Protocol" && r.Old == "WPA2" && r.New == "WPA2/WPA3");
        rows.Should().Contain(r => r.Label == "PMF" && r.Old == "Disabled" && r.New == "Optional");
        ChangeDescriber.Summary(diff).Should().Be("WLAN Home: security WPA2 to WPA2/WPA3");
    }

    [Fact]
    public void Describer_DisabledPolicy()
    {
        var diff = ConfigDiffEngine.CompareObjects(ConfigCollections.Get("firewall_policy")!,
            Obj("""{"_id":"p","name":"Block IoT","enabled":true}"""), Obj("""{"_id":"p","name":"Block IoT","enabled":false}"""))!;
        ChangeDescriber.Summary(diff).Should().Be("Firewall Policy Block IoT: disabled");
    }
}
