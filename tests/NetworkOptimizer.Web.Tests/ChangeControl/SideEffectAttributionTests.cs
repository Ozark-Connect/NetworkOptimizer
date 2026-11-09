using System.Text.Json.Nodes;
using FluentAssertions;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services.ChangeControl;
using Xunit;

namespace NetworkOptimizer.Web.Tests.ChangeControl;

/// <summary>
/// Changes UniFi writes with no AUDIT event of their own: a zone gaining or losing a network, and a switch port's
/// excluded networks following a network create or delete (UDR7, Network 11, 2026-10-30). Only those two shapes
/// may borrow a network event's actor; everything else stays unattributed.
/// </summary>
public class SideEffectAttributionTests
{
    private static readonly DateTime Now = new(2026, 10, 30, 22, 11, 0, DateTimeKind.Utc);
    private const string TestIot = "0d3e6a9c-0000-4000-8000-000000000101";
    private const string Home = "0d3e6a9c-0000-4000-8000-000000000102";

    private static JsonArray Ids(params string[] ids) => new(ids.Select(i => (JsonNode?)i).ToArray());

    private static ObjectDiff Updated(string collection, string id, string name, params FieldChange[] fields) => new()
    {
        Collection = collection, ObjectId = id, Name = name, Action = ConfigChangeAction.Updated, Fields = [.. fields],
    };

    private static ObjectDiff InternalLoses(params string[] networks) =>
        Updated("firewall_zone", "z-int", "Internal", new FieldChange("network_ids", Ids([Home, .. networks]), Ids(Home)));

    private static ObjectDiff InternalGains(params string[] networks) =>
        Updated("firewall_zone", "z-int", "Internal", new FieldChange("network_ids", Ids(Home), Ids([Home, .. networks])));

    private static FieldChange PortExclusion(int port, string[] before, string[] after) =>
        new($"port_overrides[port_idx={port}].excluded_networkconf_ids", Ids(before), Ids(after));

    private static ObjectDiff Switch(params FieldChange[] fields) => Updated("device", "d-desk", "[Switch] Office - Desk", fields);

    private static AuditLogEvent NetEvent(string networkId, string key, string admin = "Alex", string? setting = null,
        int secondsAgo = 30, string section = "NETWORK_LOCAL", string adminId = "a-alex", string? ip = "192.0.2.10") => new()
    {
        Id = Guid.NewGuid().ToString(), Key = key, AdminId = adminId, AdminName = admin, Ip = ip, ObjectId = networkId, ObjectName = "Test IoT",
        Section = section, SettingCollection = setting == null ? null : "networkconf", SettingField = setting,
        Timestamp = new DateTimeOffset(Now.AddSeconds(-secondsAgo)).ToUnixTimeMilliseconds(),
    };

    private static AttributedChange GroupOf(List<AttributedChange> groups, ObjectDiff obj) => groups.Single(g => g.Objects.Contains(obj));

    private static void ShouldBeAlex(List<AttributedChange> groups, ObjectDiff obj)
    {
        var g = GroupOf(groups, obj);
        g.ActorKind.Should().Be(ConfigChangeActorKind.Person);
        g.ActorName.Should().Be("Alex");
    }

    private static void ShouldBeUnattributed(List<AttributedChange> groups, ObjectDiff obj) =>
        GroupOf(groups, obj).ActorKind.Should().Be(ConfigChangeActorKind.NoAdminRecorded);

    // --- Zones -------------------------------------------------------------------------------------------------

    [Fact]
    public void Zone_LosingANetwork_FollowsTheZoneCreateThatTookIt_RealEventShape()
    {
        // UniFi's own event for the network when a zone is created holding it (17:10:01 CDT).
        var evt = AuditLogReader.Parse("""
            [{"category":"AUDIT","event":"MADE_CHANGES","id":"e1","key":"MADE_CHANGES_MULTIPLE_SETTING_OBJECTS",
              "parameters":{"ADMIN":{"id":"a1","name":"Alex"},"ADMIN_ACTIVITY_ID":{"id":"act1"},"COUNT":{"name":"2"},
                "IP":{"id":"192.0.2.10","name":"192.0.2.10"},"OBJECT":{"id":"0d3e6a9c-0000-4000-8000-000000000101","name":"Test IoT"},
                "PLATFORM":{"id":"WEB","name":"web"},"SECTION":{"id":"NETWORK_LOCAL"}},
              "timestamp":__TS__,"title_raw":"Config Modified","type":"ADMIN_ACCESS"}]
            """.Replace("__TS__", new DateTimeOffset(Now.AddSeconds(-40)).ToUnixTimeMilliseconds().ToString())).Single();
        var zone = InternalLoses(TestIot);
        evt.Key.Should().Be("MADE_CHANGES_MULTIPLE_SETTING_OBJECTS");
        evt.Section.Should().Be("NETWORK_LOCAL");

        var groups = ChangeAttributor.Attribute([zone], [evt], [], Now, false);

        ShouldBeAlex(groups, zone);
    }

    [Fact]
    public void Zone_GainingANetwork_FollowsTheNetworksZoneSetting()
    {
        var zone = InternalGains(TestIot);

        var groups = ChangeAttributor.Attribute([zone], [NetEvent(TestIot, "MADE_CHANGES_ONE_SETTING_OBJECT", setting: "firewall_zone_id")], [], Now, false);

        ShouldBeAlex(groups, zone);
    }

    [Theory]
    [InlineData("GENERATED")]
    [InlineData("REMOVED_ENTITY")]
    public void Zone_FollowsANetworkCreateOrRemove(string key)
    {
        var zone = key == "GENERATED" ? InternalGains(TestIot) : InternalLoses(TestIot);

        var groups = ChangeAttributor.Attribute([zone], [NetEvent(TestIot, key)], [], Now, false);

        ShouldBeAlex(groups, zone);
    }

    [Fact]
    public void Zone_FollowsANetworkEventFromAnEarlierPass()
    {
        // The network was created in the 5:07 pass; the zone change landed in the 5:08 pass with no new events.
        var zone = InternalGains(TestIot);

        var groups = ChangeAttributor.Attribute([zone], [], [], Now, false, recentEvents: [NetEvent(TestIot, "GENERATED", secondsAgo: 70)]);

        ShouldBeAlex(groups, zone);
    }

    [Fact]
    public void Zone_NetworkEventOnAnotherSetting_IsNotACause()
    {
        var zone = InternalLoses(TestIot);

        var groups = ChangeAttributor.Attribute([zone], [NetEvent(TestIot, "MADE_CHANGES_ONE_SETTING_OBJECT", setting: "name")], [], Now, false);

        ShouldBeUnattributed(groups, zone);
    }

    [Fact]
    public void Zone_WithAnyOtherFieldChanged_IsNotInferred()
    {
        var zone = Updated("firewall_zone", "z-int", "Internal",
            new FieldChange("network_ids", Ids(Home, TestIot), Ids(Home)),
            new FieldChange("name", JsonValue.Create("Internal"), JsonValue.Create("Trusted")));

        var groups = ChangeAttributor.Attribute([zone], [NetEvent(TestIot, "MADE_CHANGES_MULTIPLE_SETTING_OBJECTS")], [], Now, false);

        ShouldBeUnattributed(groups, zone);
    }

    [Fact]
    public void Zone_EventForADifferentNetwork_IsNotACause()
    {
        var zone = InternalLoses(TestIot);

        var groups = ChangeAttributor.Attribute([zone], [NetEvent("other-network", "MADE_CHANGES_MULTIPLE_SETTING_OBJECTS")], [], Now, false);

        ShouldBeUnattributed(groups, zone);
    }

    [Fact]
    public void Zone_EveryMovedNetworkMustBeAccountedFor()
    {
        var zone = InternalLoses(TestIot, "n-second");

        var groups = ChangeAttributor.Attribute([zone], [NetEvent(TestIot, "MADE_CHANGES_MULTIPLE_SETTING_OBJECTS")], [], Now, false);

        ShouldBeUnattributed(groups, zone);
    }

    [Fact]
    public void Zone_NetworksMovedByTwoPeople_StaysUnattributed()
    {
        var zone = InternalLoses(TestIot, "n-second");
        var events = new List<AuditLogEvent>
        {
            NetEvent(TestIot, "MADE_CHANGES_MULTIPLE_SETTING_OBJECTS"),
            NetEvent("n-second", "MADE_CHANGES_MULTIPLE_SETTING_OBJECTS", admin: "Sam", adminId: "a-sam"),
        };

        var groups = ChangeAttributor.Attribute([zone], events, [], Now, false);

        ShouldBeUnattributed(groups, zone);
    }

    [Fact]
    public void Zone_TwoNetworksMovedByOnePerson_FollowsThem()
    {
        var zone = InternalLoses(TestIot, "n-second");
        var events = new List<AuditLogEvent>
        {
            NetEvent(TestIot, "MADE_CHANGES_MULTIPLE_SETTING_OBJECTS"),
            NetEvent("n-second", "MADE_CHANGES_ONE_SETTING_OBJECT", setting: "firewall_zone_id", secondsAgo: 20),
        };

        var groups = ChangeAttributor.Attribute([zone], events, [], Now, false);

        ShouldBeAlex(groups, zone);
    }

    [Fact]
    public void Zone_EventOutsideTheWindow_IsNotACause()
    {
        var zone = InternalLoses(TestIot);
        var old = NetEvent(TestIot, "MADE_CHANGES_MULTIPLE_SETTING_OBJECTS", secondsAgo: (int)ChangeAttributor.SideEffectWindow.TotalSeconds + 60);

        var groups = ChangeAttributor.Attribute([zone], [], [], Now, false, recentEvents: [old]);

        ShouldBeUnattributed(groups, zone);
    }

    [Fact]
    public void Zone_EventOutsideTheNetworkSections_IsNotACause()
    {
        var zone = InternalLoses(TestIot);

        var groups = ChangeAttributor.Attribute([zone], [NetEvent(TestIot, "GENERATED", section: "FIREWALL_POLICY")], [], Now, false);

        ShouldBeUnattributed(groups, zone);
    }

    [Fact]
    public void Zone_WhoseOwnEventNamesSomeoneElse_KeepsThatEvent()
    {
        var zone = InternalLoses(TestIot);
        var zoneEvent = new AuditLogEvent
        {
            Id = "z1", Key = "MADE_CHANGES_ONE_SETTING_OBJECT", AdminId = "a-sam", AdminName = "Sam", Ip = "192.0.2.11",
            ObjectId = "z-int", Section = "FIREWALL_ZONE", Timestamp = new DateTimeOffset(Now.AddSeconds(-10)).ToUnixTimeMilliseconds(),
        };

        var groups = ChangeAttributor.Attribute([zone], [zoneEvent, NetEvent(TestIot, "MADE_CHANGES_MULTIPLE_SETTING_OBJECTS")], [], Now, false);

        GroupOf(groups, zone).ActorName.Should().Be("Sam", "an event naming the object itself always wins");
    }

    [Fact]
    public void Zone_FollowingNetworkOptimizersOwnNetworkWrite_IsNetworkOptimizers()
    {
        var zone = InternalGains(TestIot);
        var evt = NetEvent(TestIot, "GENERATED", admin: "Network Optimizer", adminId: "a-key");
        var journal = new List<ConfigWriteJournalEntry>
        {
            new() { At = Now.AddSeconds(-31), Method = "POST", Path = "rest/networkconf", Collection = "networkconf", ObjectId = TestIot, Source = "change-control:plan:7" },
        };

        var groups = ChangeAttributor.Attribute([zone], [evt], journal, Now, false);

        GroupOf(groups, zone).ActorKind.Should().Be(ConfigChangeActorKind.NetworkOptimizer);
    }

    [Fact]
    public void Zone_ReorderedNetworksOnly_IsNotInferred()
    {
        var zone = Updated("firewall_zone", "z-int", "Internal", new FieldChange("network_ids", Ids(Home, TestIot), Ids(TestIot, Home)));

        ChangeAttributor.SideEffectCause(zone, [NetEvent(TestIot, "GENERATED")], Now).Should().BeNull("nothing was added or removed");
    }

    // --- Devices -----------------------------------------------------------------------------------------------

    [Fact]
    public void Device_PortExclusionsGainingANewNetwork_FollowTheCreate()
    {
        var device = Switch(PortExclusion(4, [Home], [Home, TestIot]), PortExclusion(9, [], [TestIot]));

        var groups = ChangeAttributor.Attribute([device], [NetEvent(TestIot, "GENERATED")], [], Now, false);

        ShouldBeAlex(groups, device);
    }

    [Fact]
    public void Device_PortExclusionsLosingADeletedNetwork_FollowTheRemove()
    {
        var device = Switch(PortExclusion(4, [Home, TestIot], [Home]));

        var groups = ChangeAttributor.Attribute([device], [NetEvent(TestIot, "REMOVED_ENTITY")], [], Now, false);

        ShouldBeAlex(groups, device);
    }

    [Fact]
    public void Device_FollowsACreateFromAnEarlierPass()
    {
        var device = Switch(PortExclusion(4, [], [TestIot]));

        var groups = ChangeAttributor.Attribute([device], [], [], Now, false, recentEvents: [NetEvent(TestIot, "GENERATED", secondsAgo: 70)]);

        ShouldBeAlex(groups, device);
    }

    [Theory]
    [InlineData("MADE_CHANGES_ONE_SETTING_OBJECT", "firewall_zone_id")]
    [InlineData("MADE_CHANGES_MULTIPLE_SETTING_OBJECTS", null)]
    public void Device_NetworkEditThatIsNotACreateOrRemove_IsNotACause(string key, string? setting)
    {
        var device = Switch(PortExclusion(4, [], [TestIot]));

        var groups = ChangeAttributor.Attribute([device], [NetEvent(TestIot, key, setting: setting)], [], Now, false);

        ShouldBeUnattributed(groups, device);
    }

    [Fact]
    public void Device_WithAnyOtherFieldChanged_IsNotInferred()
    {
        var device = Switch(PortExclusion(4, [], [TestIot]),
            new FieldChange("port_overrides[port_idx=4].name", JsonValue.Create("Desk"), JsonValue.Create("Desk 2")));

        var groups = ChangeAttributor.Attribute([device], [NetEvent(TestIot, "GENERATED")], [], Now, false);

        ShouldBeUnattributed(groups, device);
    }

    [Theory]
    [InlineData("port_overrides[port_idx=4].native_networkconf_id")]
    [InlineData("port_overrides[port_idx=4].tagged_networkconf_ids")]
    [InlineData("excluded_networkconf_ids")]
    public void Device_OtherNetworkFields_AreNotInferred(string path)
    {
        var device = Switch(new FieldChange(path, Ids(), Ids(TestIot)));

        var groups = ChangeAttributor.Attribute([device], [NetEvent(TestIot, "GENERATED")], [], Now, false);

        ShouldBeUnattributed(groups, device);
    }

    [Fact]
    public void Device_ExclusionOfANetworkNobodyCreated_StaysUnattributed()
    {
        var device = Switch(PortExclusion(4, [], [TestIot, "n-unrelated"]));

        var groups = ChangeAttributor.Attribute([device], [NetEvent(TestIot, "GENERATED")], [], Now, false);

        ShouldBeUnattributed(groups, device);
    }

    // --- Shape limits ------------------------------------------------------------------------------------------

    [Fact]
    public void OtherCollections_WithTheSameShape_AreNotInferred()
    {
        var profile = Updated("portconf", "pc-1", "[Trunk] Main 10 GbE", new FieldChange("excluded_networkconf_ids", Ids(), Ids(TestIot)));

        ChangeAttributor.SideEffectCause(profile, [NetEvent(TestIot, "GENERATED")], Now).Should().BeNull("port profiles get their own AUDIT events");
    }

    [Fact]
    public void CreatedOrDeletedObjects_AreNotInferred()
    {
        var zone = new ObjectDiff
        {
            Collection = "firewall_zone", ObjectId = "z-new", Name = "Test", Action = ConfigChangeAction.Created,
            Fields = [new FieldChange("network_ids", null, Ids(TestIot))],
        };

        ChangeAttributor.SideEffectCause(zone, [NetEvent(TestIot, "MADE_CHANGES_MULTIPLE_SETTING_OBJECTS")], Now).Should().BeNull();
    }

    [Fact]
    public void NonListValues_AreNotInferred()
    {
        var zone = Updated("firewall_zone", "z-int", "Internal", new FieldChange("network_ids", JsonValue.Create(TestIot), Ids()));

        ChangeAttributor.SideEffectCause(zone, [NetEvent(TestIot, "GENERATED")], Now).Should().BeNull();
    }

    [Fact]
    public void AbsentList_ReadsAsEmpty()
    {
        var device = Switch(new FieldChange("port_overrides[port_idx=4].excluded_networkconf_ids", null, Ids(TestIot)));

        ChangeAttributor.SideEffectCause(device, [NetEvent(TestIot, "GENERATED")], Now).Should().NotBeNull();
    }

    [Fact]
    public void Scenario_NetworkCreatedThenMovedThenDeleted_EveryRowHasItsActor()
    {
        // The manual run of 2026-10-30 (5:07 to 5:18 PM CDT), pass by pass.
        var created = NetEvent(TestIot, "GENERATED", secondsAgo: 600 - 5);
        var internalGains = InternalGains(TestIot);
        var desk = Switch(PortExclusion(4, [], [TestIot]));
        var pass1 = ChangeAttributor.Attribute([internalGains, desk], [], [], Now, false, recentEvents: [created]);
        ShouldBeAlex(pass1, internalGains);
        ShouldBeAlex(pass1, desk);

        var moved = NetEvent(TestIot, "MADE_CHANGES_MULTIPLE_SETTING_OBJECTS", secondsAgo: 20);
        var internalLoses = InternalLoses(TestIot);
        ShouldBeAlex(ChangeAttributor.Attribute([internalLoses], [moved], [], Now, false, recentEvents: [created]), internalLoses);

        var removed = NetEvent(TestIot, "REMOVED_ENTITY", secondsAgo: 10);
        var deskBack = Switch(PortExclusion(4, [TestIot], []));
        ShouldBeAlex(ChangeAttributor.Attribute([deskBack], [removed], [], Now, false), deskBack);
    }
}
