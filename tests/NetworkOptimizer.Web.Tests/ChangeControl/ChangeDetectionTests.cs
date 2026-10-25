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
        rows.Should().Contain(r => r.Label == "Security protocol" && r.Old == "WPA2" && r.New == "WPA2/WPA3");
        rows.Should().Contain(r => r.Label == "PMF" && r.Old == "Disabled" && r.New == "Optional");
        ChangeDescriber.Summary(diff).Should().Be("WLAN Home: security WPA2 to WPA2/WPA3");
    }

    [Fact]
    public void Describer_DisabledPolicy()
    {
        var diff = ConfigDiffEngine.CompareObjects(ConfigCollections.Get("firewall_policy")!,
            Obj("""{"_id":"p","name":"Block IoT","enabled":true}"""), Obj("""{"_id":"p","name":"Block IoT","enabled":false}"""))!;
        ChangeDescriber.Summary(diff).Should().Be("Firewall policy Block IoT: disabled");
    }
}
