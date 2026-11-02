using System.Text.Json.Nodes;
using FluentAssertions;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.Web.Services.ChangeControl;
using Xunit;
using static NetworkOptimizer.Web.Tests.ChangeControl.ConfigDiffEngineTests;

namespace NetworkOptimizer.Web.Tests.ChangeControl;

/// <summary>
/// Removing a client from Edit forgets it in UniFi (cmd/stamgr forget-sta; DELETE rest/user/{id} is a 404). Nothing
/// else ever forgets one: a Roll Back or Restore would forget every client that joined after its snapshot.
/// </summary>
public class ClientForgetTests
{
    private static readonly DateTime Now = new(2026, 11, 1, 23, 0, 0, DateTimeKind.Utc);

    private static ConfigState Site() => State(
        ("user", """{"_id":"u1","mac":"aa:bb:cc:dd:ee:01","name":"Printer"}"""),
        ("user", """{"_id":"u2","mac":"aa:bb:cc:dd:ee:02","name":"Rig"}"""));

    private static ConfigState WithoutPrinter()
    {
        var desired = Site();
        desired.Collections["user"].RemoveAll(u => u["_id"]!.ToString() == "u1");
        return desired;
    }

    private static readonly HashSet<string> Clients = ["user"];

    [Fact]
    public void Edit_AClientRemoved_IsForgottenByMac_AsAnItemToAcknowledge()
    {
        var doc = PlanBuilder.Build(WithoutPrinter(), Site(), new PlanOptions { Collections = Clients, ForgetClients = true });

        var op = doc.Operations.Should().ContainSingle().Subject;
        op.Action.Should().Be(PlanAction.Delete);
        op.ObjectId.Should().Be("u1");
        op.Body!["mac"]!.ToString().Should().Be("aa:bb:cc:dd:ee:01");
        op.Tier.Should().Be(PlanTiers.Acknowledge, "each forget is reviewed, and can be unticked");
        op.Reasons.Should().Contain("Forgets the client in UniFi Network");
    }

    [Fact]
    public void Edit_AClientWithNoConfig_IsNeverForgotten_NorCutFromLists()
    {
        // The Clients file lists only clients with config, so every plain device is absent from it.
        var live = Site();
        live.Collections["user"].Add(new JsonObject { ["_id"] = "u3", ["mac"] = "aa:bb:cc:dd:ee:03" });
        live.Collections["network_members_group"] = [new JsonObject { ["_id"] = "g1", ["name"] = "Kids", ["members"] = new JsonArray("u3", "u2") }];
        var desired = live.Clone();
        desired.Collections["user"].RemoveAll(u => u["_id"]!.ToString() == "u3");

        DeleteCascade.Removed(ConfigCollections.Get("user")!, live, desired).Should().BeEmpty();
        DeleteCascade.Apply(live, desired, ["user"]).Should().BeEmpty();
        desired.Get("network_members_group")[0]["members"]!.AsArray().Should().HaveCount(2);
        PlanBuilder.Build(desired, live, new PlanOptions { Collections = Clients, ForgetClients = true }).Operations.Should().BeEmpty();
    }

    [Fact]
    public void Restore_AClientMissing_IsLeftInPlace_AndSaysHowToForgetIt()
    {
        // DeleteMissing alone never forgets a client: Restore, and a revert, which can apply unreviewed.
        var doc = PlanBuilder.Build(WithoutPrinter(), Site(), new PlanOptions { Collections = Clients, KeptClientsBy = "Restore" });

        doc.Operations.Should().BeEmpty();
        doc.Notes.Should().Contain("Client Printer: not in the source; Restore never forgets clients. Remove it in Edit to forget it.");
    }

    [Fact]
    public void Revert_AClientMissing_IsLeftInPlace()
    {
        var doc = PlanBuilder.Build(WithoutPrinter(), Site(), new PlanOptions { Collections = Clients });

        doc.Operations.Should().BeEmpty();
        doc.Notes.Should().Contain("Client Printer: not in the source; this plan never forgets clients. Remove it in Edit to forget it.");
    }

    [Fact]
    public void Editor_OffersRemoveOnClients()
    {
        var schema = YamlConverter.EditSchema(Site(), [ConfigCollections.Get("user")!]);
        var name = YamlConverter.YamlName(ConfigCollections.Get("user")!);

        schema["collections"]![name]!["readOnly"].Should().BeNull("a client can be forgotten from Edit");
    }

    [Fact]
    public void Journal_FilesAForgetUnderClients()
    {
        ChangeControlSiteEngine.MapJournalPath(UniFiApiScope.SiteV1, "cmd/stamgr").Should().Be(("user", (string?)null));
    }

    private static ObjectDiff ForgottenPrinter() => new()
    {
        Collection = "user", ObjectId = "u1", Name = "Printer", Action = ConfigChangeAction.Deleted,
    };

    private static ConfigWriteJournalEntry ForgetWrite() => new()
    {
        At = Now.AddSeconds(-20), Method = "POST", Path = "cmd/stamgr", Collection = "user", ObjectId = "u1",
        FieldsJson = """["cmd","macs"]""", Source = "change-control:plan:160",
    };

    [Fact]
    public void Attribution_AForget_WithItsRemovedEntity_IsThePlans()
    {
        // UniFi audits a forget as REMOVED_ENTITY in CLIENT, naming the client by MAC only (main console, 2026-11-01).
        var removed = new AuditLogEvent
        {
            Id = "e1", Key = "REMOVED_ENTITY", AdminName = "Network Optimizer", ObjectId = "aa:bb:cc:dd:ee:01",
            ObjectName = "aa:bb:cc:dd:ee:01", Section = "CLIENT", Timestamp = new DateTimeOffset(Now.AddSeconds(-19)).ToUnixTimeMilliseconds(),
        };

        var g = ChangeAttributor.Attribute([ForgottenPrinter()], [removed], [ForgetWrite()], Now, false, beforeReadAt: Now.AddSeconds(-30)).Single();
        g.ActorKind.Should().Be(ConfigChangeActorKind.NetworkOptimizer);
        g.PlanId.Should().Be(160);
    }

    [Fact]
    public void Attribution_AForget_BeforeItsAuditEntryArrives_IsThePlans()
    {
        var g = ChangeAttributor.Attribute([ForgottenPrinter()], [], [ForgetWrite()], Now, false, beforeReadAt: Now.AddSeconds(-30)).Single();
        g.ActorKind.Should().Be(ConfigChangeActorKind.NetworkOptimizer);
        g.PlanId.Should().Be(160);
    }
}
