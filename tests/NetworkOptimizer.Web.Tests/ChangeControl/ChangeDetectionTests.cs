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

    // A plan wrote the WLAN's PPSKs, the post-apply read took that in, then someone edited them in UniFi
    // before UniFi's audit event for it arrived.
    private static ConfigWriteJournalEntry PpskWrite(int secondsAgo) => new()
    {
        At = Now.AddSeconds(-secondsAgo), Method = "PUT", Path = "rest/wlanconf/w1", Collection = "wlanconf", ObjectId = "w1",
        Source = "change-control:plan:66", FieldsJson = """["name","private_preshared_keys"]""",
    };

    private static ObjectDiff PpskDiff(string path = "private_preshared_keys") =>
        Diff("wlanconf", "w1", "Home", new FieldChange(path, JsonValue.Create(3), JsonValue.Create(4)));

    [Fact]
    public void Attribute_WriteAlreadyInTheBeforeState_DoesNotClaimAFieldItSent()
    {
        var groups = ChangeAttributor.Attribute([PpskDiff()], [], [PpskWrite(65)], Now, false, beforeReadAt: Now.AddSeconds(-64));
        groups.Single().ActorKind.Should().NotBe(ConfigChangeActorKind.NetworkOptimizer);
    }

    [Fact]
    public void Attribute_WriteAlreadyInTheBeforeState_StillClaimsAFieldUniFiFilledInLater()
    {
        var groups = ChangeAttributor.Attribute([PpskDiff("x_iapp_key")], [], [PpskWrite(65)], Now, false, beforeReadAt: Now.AddSeconds(-64));
        var g = groups.Single();
        g.ActorKind.Should().Be(ConfigChangeActorKind.NetworkOptimizer);
        g.PlanId.Should().Be(66);
    }

    [Fact]
    public void Attribute_WriteAfterTheBeforeRead_ClaimsTheFieldsItSent()
    {
        var groups = ChangeAttributor.Attribute([PpskDiff("private_preshared_keys[0].password")], [], [PpskWrite(5)], Now, false, beforeReadAt: Now.AddSeconds(-64));
        groups.Single().ActorKind.Should().Be(ConfigChangeActorKind.NetworkOptimizer);
    }

    [Fact]
    public void Attribute_AnAdminsEditAMinuteAfterAPlanWroteTheObject_IsTheAdmins()
    {
        // The plan created the WLAN 98 s before the admin renamed it in UniFi; the snapshot after the
        // apply (the before state) already holds the plan's write.
        var journal = new List<ConfigWriteJournalEntry>
        {
            new() { At = Now.AddSeconds(-98), Method = "POST", Path = "rest/wlanconf", Collection = "wlanconf", ObjectId = "w1", Source = "change-control:plan:80" },
        };
        var rename = new AuditLogEvent
        {
            Id = "e1", Timestamp = new DateTimeOffset(Now).ToUnixTimeMilliseconds(), Key = "MADE_CHANGES_ONE_SETTING_OBJECT",
            AdminName = "TJ", Ip = "192.0.2.5", ObjectId = "w1", ObjectName = "Skynet-IoT", Section = "WIFI", SettingField = "name",
        };
        var diff = new ObjectDiff
        {
            Collection = "wlanconf", ObjectId = "w1", Name = "Skynet-IoT", Action = ConfigChangeAction.Updated,
            Fields = [new FieldChange("name", "SSID-Here-IoT", "Skynet-IoT")],
        };

        var g = ChangeAttributor.Attribute([diff], [rename], journal, Now, false, beforeReadAt: Now.AddSeconds(-60)).Single();
        g.ActorKind.Should().Be(ConfigChangeActorKind.Person);
        g.ActorName.Should().Be("TJ");
    }

    [Fact]
    public void Attribute_APlansOwnEvent_BeforeTheBeforeRead_IsStillOurs()
    {
        // The write and its AUDIT event both land before the read; the event must not become a person's.
        var journal = new List<ConfigWriteJournalEntry>
        {
            new() { At = Now.AddSeconds(-30), Method = "PUT", Path = "rest/wlanconf/w1", Collection = "wlanconf", ObjectId = "w1", FieldsJson = "[\"name\"]", Source = "change-control:plan:81" },
        };
        var own = new AuditLogEvent
        {
            Id = "e2", Timestamp = new DateTimeOffset(Now.AddSeconds(-29)).ToUnixTimeMilliseconds(), Key = "MADE_CHANGES_ONE_SETTING_OBJECT",
            AdminName = "netopt", ObjectId = "w1", ObjectName = "Home", Section = "WIFI", SettingField = "name",
        };
        var diff = new ObjectDiff
        {
            Collection = "wlanconf", ObjectId = "w1", Name = "Home", Action = ConfigChangeAction.Updated,
            Fields = [new FieldChange("name", "Old", "Home")],
        };

        var g = ChangeAttributor.Attribute([diff], [own], journal, Now, false, beforeReadAt: Now.AddSeconds(-40)).Single();
        g.ActorKind.Should().Be(ConfigChangeActorKind.NetworkOptimizer);
    }

    [Fact]
    public void Attribute_BatchDelete_IsTheDeletingPlans_NotTheCreatesBeforeIt()
    {
        // Plan 76 created the policy; plan 77's batch delete (no object ID in the journal) removed it.
        var journal = new List<ConfigWriteJournalEntry>
        {
            new() { At = Now.AddMinutes(-3), Method = "POST", Path = "firewall-policies", Collection = "firewall_policy", ObjectId = "p1", Source = "change-control:plan:76" },
            new() { At = Now.AddSeconds(-1), Method = "POST", Path = "firewall-policies/batch-delete", Collection = "firewall_policy", FieldsJson = "[]", Source = "change-control:plan:77" },
        };
        var deleted = new ObjectDiff { Collection = "firewall_policy", ObjectId = "p1", Name = "Test", Action = ConfigChangeAction.Deleted };

        var g = ChangeAttributor.Attribute([deleted], [], journal, Now, false, beforeReadAt: Now.AddSeconds(-2)).Single();
        g.ActorKind.Should().Be(ConfigChangeActorKind.NetworkOptimizer);
        g.PlanId.Should().Be(77);
        g.OccurredAt.Should().Be(Now.AddSeconds(-1));
    }

    [Fact]
    public void Attribute_AnOldBatchDelete_DoesNotClaimALaterDelete()
    {
        var journal = new List<ConfigWriteJournalEntry>
        {
            new() { At = Now.AddMinutes(-5), Method = "POST", Path = "firewall-policies/batch-delete", Collection = "firewall_policy", FieldsJson = "[]", Source = "change-control:plan:70" },
        };
        var deleted = new ObjectDiff { Collection = "firewall_policy", ObjectId = "p9", Name = "Someone's", Action = ConfigChangeAction.Deleted };

        ChangeAttributor.Attribute([deleted], [], journal, Now, false, beforeReadAt: Now.AddMinutes(-1)).Single()
            .ActorKind.Should().NotBe(ConfigChangeActorKind.NetworkOptimizer);
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

    private static ObjectDiff OrderDiff() => ChangeControlSiteEngine.PolicyOrderObject(
        new PolicyOrderDiff("z1", "z2", "Internal to IoT", ["p1", "p2", "p3"], ["p1", "p3", "p2"]),
        new ConfigIdentityContext(new Dictionary<string, string>()));

    private static AuditLogEvent IndexEvent(string policyId, string admin = "TJ") => Event(policyId, admin) with
    {
        Section = "FIREWALL_POLICY", SettingCollection = "firewall_policy", SettingField = "index",
    };

    [Fact]
    public void Attribute_PolicyReorder_FromThePerPolicyIndexEvents()
    {
        var groups = ChangeAttributor.Attribute([OrderDiff()], [IndexEvent("p3"), IndexEvent("p2")], [], Now, false);

        var g = groups.Should().ContainSingle().Subject;
        g.ActorKind.Should().Be(ConfigChangeActorKind.Person);
        g.ActorName.Should().Be("TJ");
    }

    [Fact]
    public void Attribute_PolicyReorder_OwnBatchReorder_IsNetworkOptimizer()
    {
        var journal = new List<ConfigWriteJournalEntry>
        {
            new() { At = Now.AddSeconds(-25), Method = "PUT", Path = "firewall-policies/batch-reorder", Collection = "firewall_policy", Source = "change-control:plan:7" },
        };
        var groups = ChangeAttributor.Attribute([OrderDiff()], [IndexEvent("p3", admin: "Network Optimizer key")], journal, Now, false);

        var g = groups.Should().ContainSingle().Subject;
        g.ActorKind.Should().Be(ConfigChangeActorKind.NetworkOptimizer);
        g.PlanId.Should().Be(7);
    }

    [Fact]
    public void Attribute_PolicyReorder_WithNoIndexEvent_StaysUnattributed()
    {
        ChangeAttributor.Attribute([OrderDiff()], [Event("p9")], [], Now, false)
            .Single().ActorKind.Should().Be(ConfigChangeActorKind.NoAdminRecorded);
    }

    private static ObjectDiff Reorder(params string[] after) => ChangeControlSiteEngine.PolicyOrderObject(
        new PolicyOrderDiff("z1", "z1", "Internal to Internal", ["Allow A", "Allow B", "Allow C", "Allow D"], after),
        new ConfigIdentityContext(new Dictionary<string, string>()));

    private static ObjectDiff Renamed(string name) => new()
    {
        Collection = "firewall_policy", ObjectId = "p4", Name = name, Action = ConfigChangeAction.Updated,
        Fields = [new FieldChange("name", JsonValue.Create("Old name"), JsonValue.Create(name))],
    };

    [Fact]
    public void Summary_PolicyEditedAndMovedInOneSave_IsOnePolicy()
    {
        ChangeDescriber.GroupSummary([Renamed("Allow D"), Reorder("Allow D", "Allow A", "Allow B", "Allow C")])
            .Should().Be("Firewall Policy Allow D: renamed from Old name, moved from #4 to #1");
    }

    [Fact]
    public void Summary_PureReorder_NamesTheRuleThatMoved()
    {
        ChangeDescriber.GroupSummary([Reorder("Allow D", "Allow A", "Allow B", "Allow C")])
            .Should().Be("Firewall Policy Allow D: moved from #4 to #1");
    }

    /// <summary>Readers find upgrade changes by the context; a one-object group once dropped it.</summary>
    [Fact]
    public void Summary_OneObjectInAnUpgrade_KeepsTheUpgradeContext()
    {
        var summary = ChangeDescriber.GroupSummary([Renamed("Allow X")], ChangeDescriber.UpgradeContext);

        summary.Should().EndWith(ChangeDescriber.UpgradeContext);
        ChangeDescriber.IsUpgradeSummary(summary).Should().BeTrue();
    }

    [Fact]
    public void IsUpgradeSummary_ReadsRowsStoredBeforeTheConstant()
    {
        ChangeDescriber.IsUpgradeSummary("35 objects changed during Network app upgrade (plus 12 new fields the upgrade added, not listed)")
            .Should().BeTrue();
        ChangeDescriber.IsUpgradeSummary("Firewall Policy Allow X: Name changed").Should().BeFalse();
    }

    [Fact]
    public void Summary_OtherEditsWithAReorder_CountOnlyRealPolicies()
    {
        ChangeDescriber.GroupSummary([Renamed("Allow X"), Reorder("Allow D", "Allow A", "Allow B", "Allow C")])
            .Should().Be("Firewall Policy Allow X: renamed from Old name; Allow D moved from #4 to #1");
    }

    private static ConfigChangeObject Stored(string collection, string name, params StoredField[] fields) => new()
    {
        Collection = collection, ObjectId = name, ObjectName = name, Action = ConfigChangeAction.Updated,
        FieldsJson = System.Text.Json.JsonSerializer.Serialize(fields.ToList()),
    };

    private static ConfigChangeObject StoredCreated(string collection, string name) => new()
    {
        Collection = collection, ObjectId = name, ObjectName = name, Action = ConfigChangeAction.Created, FieldsJson = "[]",
    };

    [Fact]
    public void StoredSummary_ARemovedValue_SaysRemoved_UnderTheSettingsPage()
    {
        var teleport = Stored("setting", "teleport", new StoredField { Path = "subnet_cidr", Label = "Subnet CIDR", Old = "192.168.2.1/24", New = "none" });

        ChangeControlService.StoredSummary([teleport], null, null)
            .Should().Be("VPN Server - Teleport: Subnet CIDR 192.168.2.1/24 removed");
    }

    [Fact]
    public void StoredSummary_ASetValue_SaysSetTo()
    {
        var network = Stored("networkconf", "Guest", new StoredField { Path = "dhcpd_dns_1", Label = "DNS Server", Old = "none", New = "192.0.2.53" });

        ChangeControlService.StoredSummary([network], null, null).Should().Be("Network Guest: DNS Server set to 192.0.2.53");
    }

    [Fact]
    public void StoredSummary_TheIpsSetting_IsHoneypotOnlyWhenEveryRowIsOne()
    {
        var honeypot = Stored("setting", "ips",
            new StoredField { Path = "honeypot[entry=1].ip_address", Label = "Honeypot 1 IP Address", Old = "192.0.2.2", New = "198.51.100.2" },
            new StoredField { Path = "honeypot[entry=2].ip_address", Label = "Honeypot 2 IP Address", Old = "192.0.2.3", New = "198.51.100.3" });
        var ips = Stored("setting", "ips", new StoredField { Path = "ips_mode", Label = "IPS Mode", Old = "ids", New = "ips" });

        ChangeControlService.StoredSummary([honeypot], null, null).Should().StartWith("CyberSecure - Honeypot: ");
        ChangeControlService.StoredSummary([ips], null, null).Should().StartWith("CyberSecure - Intrusion Prevention: ");
    }

    [Fact]
    public void StoredSummary_OnePortsRows_SayThePortOnce()
    {
        var device = Stored("device", "Desk Switch",
            new StoredField { Path = "port_overrides[port_idx=4].autoneg", Label = "Port 4 Auto Negotiate Link Speed", Old = "on", New = "off" },
            new StoredField { Path = "port_overrides[port_idx=4].dot1x_ctrl", Label = "Port 4 Client Authentication (802.1X)", Old = "auto", New = "force_authorized" });

        ChangeControlService.StoredSummary([device], null, null)
            .Should().Be("Device Desk Switch: Port 4: Auto Negotiate Link Speed, Client Authentication (802.1X)");
    }

    [Fact]
    public void StoredSummary_UpToThreeObjects_AreNamed()
    {
        ChangeControlService.StoredSummary([StoredCreated("networkconf", "IoT"), StoredCreated("networkconf", "Cameras")], null, null)
            .Should().Be("Networks IoT and Cameras created");
        ChangeControlService.StoredSummary([StoredCreated("networkconf", "IoT"), StoredCreated("user", "Desk PC")], null, null)
            .Should().Be("Network IoT and Client Desk PC changed");
    }

    [Fact]
    public void StoredSummary_SameNamesAreCounted_AndRenamesSayRenamed()
    {
        ChangeControlService.StoredSummary([StoredCreated("firewall_policy", "Allow DNS"), StoredCreated("firewall_policy", "Allow DNS")], null, null)
            .Should().Be("2 Firewall Policies Allow DNS created");

        var a = Stored("firewall_policy", "New A", new StoredField { Path = "name", Label = "Name", Old = "Old A", New = "New A" });
        var b = Stored("firewall_policy", "New B", new StoredField { Path = "name", Label = "Name", Old = "Old B", New = "New B" });
        ChangeControlService.StoredSummary([a, b], null, null).Should().Be("Firewall Policies New A and New B: renamed");
    }

    [Fact]
    public void StoredSummary_ASharedSignatureIsSaidOnce()
    {
        var suppression = Stored("setting", "ips_suppression",
            new StoredField { Path = "alerts[entry=1].tracking[entry=1].value", Label = "x", Old = "192.0.2.1", New = "198.51.100.1" },
            new StoredField { Path = "alerts[entry=1].tracking[entry=2].value", Label = "x", Old = "192.0.2.2", New = "198.51.100.2" });

        ChangeControlService.StoredSummary([suppression], null, null)
            .Should().Be("CyberSecure - Suppressed Signatures: Suppressed Signature 1: Tracked Address 1, Tracked Address 2");
    }

    [Fact]
    public void StoredSummary_PastFourTypes_CountsTheObjectsLeft()
    {
        var objects = new List<ConfigChangeObject>
        {
            StoredCreated("user", "a"), StoredCreated("user", "b"), StoredCreated("user", "c"),
            StoredCreated("networkconf", "n1"), StoredCreated("networkconf", "n2"),
            StoredCreated("static_dns", "d1"), StoredCreated("firewallgroup", "g1"), StoredCreated("firewallgroup", "g2"), StoredCreated("portconf", "p1"),
        };

        ChangeControlService.StoredSummary(objects, null, null).Should().EndWith(", and 2 more");
    }

    [Fact]
    public void StoredSummary_MoreThanThreeAcrossCollections_BreaksDownLargestFirst_LedByThePlan()
    {
        var objects = new List<ConfigChangeObject>
        {
            StoredCreated("user", "a"), StoredCreated("user", "b"), StoredCreated("user", "c"),
            StoredCreated("networkconf", "n1"), StoredCreated("networkconf", "n2"),
        };

        ChangeControlService.StoredSummary(objects, null, null).Should().Be("5 changes: 3 Clients, 2 Networks");
        ChangeControlService.StoredSummary(objects, null, ChangeDescriber.PlanLead(ConfigPlanKind.Readdress))
            .Should().Be("Re-address Networks applied: 3 Clients, 2 Networks");
    }

    [Fact]
    public void StoredSummary_KeepsTheUpgradeContextReadersLookFor()
    {
        var stored = "35 objects changed during Network app upgrade (plus 12 new fields the upgrade added, not listed)";
        var context = ChangeControlService.UpgradeContextOf(stored);

        var rebuilt = ChangeControlService.StoredSummary([StoredCreated("user", "a"), StoredCreated("networkconf", "n")], context, null);

        rebuilt.Should().EndWith("during Network app upgrade (plus 12 new fields the upgrade added, not listed)");
        ChangeDescriber.IsUpgradeSummary(rebuilt).Should().BeTrue();
        ChangeControlService.UpgradeContextOf("Firewall Policy X: renamed from Y").Should().BeNull();
    }

    [Fact]
    public void Labels_ASuppressedAlert_ByItsSignature()
    {
        var setting = (JsonObject)JsonNode.Parse("""{"alerts":[{"id":2003068,"signature":"ET SCAN Potential SSH Scan OUTBOUND","tracking":[{"value":"192.0.2.10"}]}]}""")!;

        ChangeDescriber.LabelFor("alerts[entry=1].tracking[entry=2].value", setting)
            .Should().Be("ET SCAN Potential SSH Scan OUTBOUND: Tracked Address 2");
        ChangeDescriber.LabelFor("alerts[entry=1].tracking[entry=2].value")
            .Should().Be("Suppressed Signature 1: Tracked Address 2");
    }

    [Fact]
    public void ObjectView_KeepsASignatureFromTheSavedLabel_AndNamesPolicyOrderAsReviewPlanDoes()
    {
        var suppression = Stored("setting", "ips_suppression", new StoredField
        {
            Path = "alerts[entry=1].tracking[entry=2].value", Label = "ET SCAN Potential SSH Scan OUTBOUND: Tracked Address 2",
            Old = "192.0.2.10", New = "198.51.100.10",
        });
        var view = ChangeControlService.ObjectView(suppression);
        view.Name.Should().Be("CyberSecure - Suppressed Signatures");
        view.Fields.Single().Label.Should().Be("ET SCAN Potential SSH Scan OUTBOUND: Tracked Address 2");

        ChangeDescriber.PolicyOrderDisplayName("order, Internal to Internal").Should().Be("Policy order, Internal to Internal");
    }

    [Fact]
    public void Drift_FromASnapshotBaseline_IncludesPolicyOrder()
    {
        var baseline = State(
            ("firewall_policy", """{"_id":"p1","name":"A","index":10000,"source":{"zone_id":"z1"},"destination":{"zone_id":"z2"}}"""),
            ("firewall_policy", """{"_id":"p2","name":"B","index":10001,"source":{"zone_id":"z1"},"destination":{"zone_id":"z2"}}"""));
        var current = State(
            ("firewall_policy", """{"_id":"p1","name":"A","index":10001,"source":{"zone_id":"z1"},"destination":{"zone_id":"z2"}}"""),
            ("firewall_policy", """{"_id":"p2","name":"B","index":10000,"source":{"zone_id":"z1"},"destination":{"zone_id":"z2"}}"""));

        var drift = DriftCalculator.Drift(baseline, current, new PlanOptions());

        var order = drift.Should().ContainSingle(o => ChangeControlSiteEngine.IsPolicyOrder(o.Collection, o.ObjectId)).Subject;
        ChangeControlService.ObjectView(order).OrderMoves.Should().ContainSingle();
    }

    [Fact]
    public void Labels_MeshAsTheAPSettingsNameIt()
    {
        ChangeDescriber.LabelFor("radio_table[radio=na].vwire_enabled").Should().Be("5 GHz Mesh Parent");
        ChangeDescriber.LabelFor("mesh_sta_vap_enabled").Should().Be("Mesh Connect");
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

    [Fact]
    public void CarriesDevice_OnlyTheUplinkAndPortsWithADeviceBelow()
    {
        var device = new JsonObject
        {
            ["mac"] = "aa:bb:cc:dd:ee:ff",
            [ConfigCollections.DeviceMetaField] = JsonNode.Parse("""
                {"ports":[{"port_idx":1,"is_uplink":true},{"port_idx":4,"is_uplink":false},{"port_idx":6,"is_uplink":false}],
                 "lldp":[{"local_port_idx":6,"chassis_id":"11:22:33:44:55:66"}]}
                """),
        };

        ConfigDiffEngine.CarriesDevice(device, 1).Should().BeTrue("the uplink");
        ConfigDiffEngine.CarriesDevice(device, 6).Should().BeTrue("an AP below it");
        ConfigDiffEngine.CarriesDevice(device, 4).Should().BeFalse("a client port");
        ConfigDiffEngine.CarriesDevice((JsonObject)JsonNode.Parse("""{"mac":"aa:bb:cc:dd:ee:ff"}""")!, 4)
            .Should().BeTrue("no port facts in an older snapshot keeps the old rule");
    }

    [Fact]
    public void Row_PlanChange_ShowsThePersonWhoAppliedIt()
    {
        var change = new ConfigChange { Id = 1, Kind = ConfigChangeKind.Config, ActorKind = ConfigChangeActorKind.NetworkOptimizer, ActorName = "Network Optimizer", PlanId = 7, Summary = "s" };
        var automatic = new ConfigChange { Id = 2, Kind = ConfigChangeKind.Config, ActorKind = ConfigChangeActorKind.NetworkOptimizer, ActorName = "Network Optimizer", Summary = "s" };
        var appliers = new Dictionary<int, string> { [7] = "TJ" };

        var row = ChangeControlService.Row(change, "main", "Main", 0, appliers: appliers);
        row.ActorKind.Should().Be(ConfigChangeActorKind.Person);
        row.Who.Should().Be("TJ (plan)");

        ChangeControlService.Row(automatic, "main", "Main", 0, appliers: appliers).ActorKind
            .Should().Be(ConfigChangeActorKind.NetworkOptimizer, "an automatic write stays under the Network Optimizer filter");
    }

    [Fact]
    public void IsOwnEvent_DeviceEntryByMac_MatchesOurWriteByDeviceId()
    {
        var at = new DateTime(2026, 10, 27, 3, 27, 32, DateTimeKind.Utc);
        var journal = new List<ConfigWriteJournalEntry>
        {
            new() { At = at, Method = "PUT", Path = "rest/device/db7e2347-6983-4e50-b144-91edb5fc5c4b", Collection = "device", ObjectId = "db7e2347-6983-4e50-b144-91edb5fc5c4b" },
        };
        AuditLogEvent Event(string section, string? objectId) => new()
        {
            Key = "MADE_CHANGES_ONE_SETTING_OBJECT", Timestamp = new DateTimeOffset(at.AddSeconds(1)).ToUnixTimeMilliseconds(),
            AdminName = "Network Optimizer", ObjectId = objectId, Section = section, SettingCollection = "device",
        };

        ChangeAttributor.IsOwnEvent(Event("DEVICE", "aa:bb:cc:dd:ee:ff"), journal).Should().BeTrue();
        ChangeAttributor.IsOwnEvent(Event("NETWORK", "aa:bb:cc:dd:ee:ff"), journal).Should().BeFalse("another collection's object must match by id");
    }

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
            .Should().Be("Device Desk Switch: Port 5: profile set to [Access] Main Network");
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
