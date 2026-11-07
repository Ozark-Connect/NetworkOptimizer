using System.Text.Json.Nodes;
using FluentAssertions;
using NetworkOptimizer.Web.Services.ChangeControl;
using Xunit;
using static NetworkOptimizer.Web.Tests.ChangeControl.ConfigDiffEngineTests;

namespace NetworkOptimizer.Web.Tests.ChangeControl;

/// <summary>A network's LTE/5G backup modem list, written as UniFi Network's form writes it on the target console.</summary>
public class LteModemPlanTests
{
    private const string Field = PlanBuilder.LteDevicesField;
    private const string SourceModem = "aa:bb:cc:dd:ee:99";
    private const string ModemA = "aa:bb:cc:dd:ee:01";
    private const string ModemB = "aa:bb:cc:dd:ee:02";

    private static readonly PlanOptions OtherSite = new() { SameSite = false, Collections = new HashSet<string> { "networkconf" } };
    private static readonly PlanOptions SameSite = new() { Collections = new HashSet<string> { "networkconf" } };

    private static string Network(string id, bool on, string? list = null, bool mdns = false, string purpose = "corporate") =>
        $$"""{"_id":"{{id}}","name":"IoT","purpose":"{{purpose}}","vlan":30,"vlan_enabled":true,"mdns_enabled":{{(mdns ? "true" : "false")}},"lte_lan_enabled":{{(on ? "true" : "false")}}{{(list == null ? "" : $",\"{Field}\":{list}")}}}""";

    private static string Modem(string id, string mac, string model = "ULTE") =>
        $$"""{"_id":"{{id}}","mac":"{{mac}}","name":"Modem {{id}}","model":"{{model}}","type":"umbb"}""";

    private static ConfigState Target(string network, params string[] modems) =>
        State([("networkconf", network), .. modems.Select(m => ("device", m))]);

    // The source differs in mDNS so the network always has an update to carry the list.
    private static ConfigState Source(bool on) => State(("networkconf", Network("s1", on, $"[\"{SourceModem}\"]", mdns: true)));

    private static PlanOperation Update(PlanDocument doc) =>
        doc.Operations.Should().ContainSingle(o => o.Collection == "networkconf").Subject;

    private static List<string?> List(PlanOperation op) =>
        (op.Body![Field] as JsonArray)?.Select(e => e?.ToString()).ToList() ?? [];

    [Fact]
    public void NoModem_BackupOn_LeavesTheListOut_WithANote()
    {
        var doc = PlanBuilder.Build(Source(on: true), Target(Network("t1", on: false)), OtherSite);

        Update(doc).Body![Field].Should().BeNull();
        doc.Notes.Should().Contain(n => n.StartsWith("Network IoT: ") && n.Contains("no LAN-connected cellular modem"));
    }

    [Fact]
    public void NoModem_BackupOff_WritesItOff_WithNoList()
    {
        var doc = PlanBuilder.Build(Source(on: false), Target(Network("t1", on: true)), OtherSite);

        var op = Update(doc);
        op.Body!["lte_lan_enabled"]!.GetValue<bool>().Should().BeFalse();
        op.Body![Field].Should().BeNull();
        doc.Notes.Should().NotContain(n => n.Contains("cellular modem"));
    }

    [Fact]
    public void OneModem_BackupOn_ListsThatModem()
    {
        var doc = PlanBuilder.Build(Source(on: true), Target(Network("t1", on: false), Modem("d1", ModemA)), OtherSite);

        List(Update(doc)).Should().Equal(ModemA);
        doc.Notes.Should().NotContain(n => n.Contains("cellular modem"));
    }

    [Fact]
    public void OneModem_BackupOff_SendsNoList()
    {
        var doc = PlanBuilder.Build(Source(on: false), Target(Network("t1", on: false), Modem("d1", ModemA)), OtherSite);

        Update(doc).Body![Field].Should().BeNull();
    }

    [Fact]
    public void SeveralModems_BackupOn_KeepsTheNetworksList()
    {
        var target = Target(Network("t1", on: true, $"[\"{ModemB}\"]"), Modem("d1", ModemA), Modem("d2", ModemB));

        var doc = PlanBuilder.Build(Source(on: true), target, OtherSite);

        List(Update(doc)).Should().Equal(ModemB);
    }

    [Fact]
    public void SeveralModems_BackupOn_NoListHere_BlocksThatNetwork()
    {
        var target = Target(Network("t1", on: false), Modem("d1", ModemA), Modem("d2", ModemB));

        var doc = PlanBuilder.Build(Source(on: true), target, OtherSite);

        doc.Operations.Should().NotContain(o => o.Collection == "networkconf");
        doc.Notes.Should().Contain(n => n.StartsWith("Network IoT: not written.") && n.Contains("Applies To"));
    }

    [Fact]
    public void SeveralModems_BackupOn_VlanOnly_IsNotBlocked()
    {
        var source = State(("networkconf", Network("s1", on: true, $"[\"{SourceModem}\"]", mdns: true, purpose: "vlan-only")));
        var target = Target(Network("t1", on: false, purpose: "vlan-only"), Modem("d1", ModemA), Modem("d2", ModemB));

        var doc = PlanBuilder.Build(source, target, OtherSite);

        Update(doc).Body![Field].Should().BeNull();
    }

    [Fact]
    public void SeveralModems_BackupOff_SendsNoList()
    {
        var target = Target(Network("t1", on: false), Modem("d1", ModemA), Modem("d2", ModemB));

        var doc = PlanBuilder.Build(Source(on: false), target, OtherSite);

        Update(doc).Body![Field].Should().BeNull();
    }

    [Fact]
    public void Create_OneModem_ListsThatModem()
    {
        var doc = PlanBuilder.Build(Source(on: true), State(("device", Modem("d1", ModemA))), OtherSite);

        var op = doc.Operations.Should().ContainSingle(o => o.Collection == "networkconf" && o.Action == PlanAction.Create).Subject;
        List(op).Should().Equal(ModemA);
    }

    [Fact]
    public void SameConsoleRestore_DropsAModemRemovedSinceTheSnapshot()
    {
        var snapshot = State(("networkconf", Network("n1", on: true, $"[\"{ModemA}\"]")));
        var live = Target(Network("n1", on: true, $"[\"{ModemA}\"]"), Modem("d2", ModemB));

        var doc = PlanBuilder.Build(snapshot, live, SameSite);

        List(Update(doc)).Should().Equal(ModemB);
    }

    [Fact]
    public void LanModems_AreCellularModemsOnly()
    {
        var live = State(("device", Modem("d1", ModemA)), ("device", Modem("d2", ModemB, model: "UMBBE630")),
            ("device", """{"_id":"g1","mac":"aa:bb:cc:dd:ee:03","name":"Gateway","model":"UDMA6B9","type":"udm"}"""),
            ("device", """{"_id":"s1","mac":"aa:bb:cc:dd:ee:04","name":"Switch","model":"USL8LP","type":"usw"}"""));

        PlanBuilder.LanCellularModems(live).Should().BeEquivalentTo([ModemA, ModemB]);
    }

    [Fact]
    public void TheList_IsComparedAsASet_AndIsNotReadOnly()
    {
        var before = State(("networkconf", Network("n1", on: true, $"[\"{ModemA}\",\"{ModemB}\"]")));
        var reordered = State(("networkconf", Network("n1", on: true, $"[\"{ModemB}\",\"{ModemA}\"]")));
        var changed = State(("networkconf", Network("n1", on: true, $"[\"{ModemA}\"]")));

        ConfigDiffEngine.Compare(before, reordered).IsEmpty.Should().BeTrue();
        ConfigDiffEngine.Compare(before, changed).Objects.Single().Fields.Should().ContainSingle(f => f.Path == Field);
        ConfigCollections.Get("networkconf")!.IsReadOnly(null, Field).Should().BeFalse();
    }
}
