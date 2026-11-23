using FluentAssertions;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services.ChangeControl;
using Xunit;
using static NetworkOptimizer.Web.Tests.ChangeControl.ConfigDiffEngineTests;

namespace NetworkOptimizer.Web.Tests.ChangeControl;

/// <summary>
/// Network 11.1 Port Automations (v2 port-automations), in the shape the 11.1.58 web UI sends: recorded and
/// diffed with UniFi's labels, never written.
/// </summary>
public class PortAutomationTests
{
    private const string Profile = """{"_id":"pc-iot","name":"IoT Port","forward":"native"}""";

    private static string Automation(string vendors = "[42]", string ouis = """["aa:bb:cc"]""", string name = "Cameras") =>
        "{\"id\":\"pa-1\",\"name\":\"" + name + "\",\"enabled\":true,\"automation_index\":0,\"port_profile_id\":\"pc-iot\","
        + "\"match\":{\"vendor_ids\":" + vendors + ",\"mac_ouis\":" + ouis + "},"
        + "\"scope\":{\"switch_macs\":[],\"device_tag_ids\":[],\"port_range\":{\"from_port\":1,\"to_port\":8}}}";

    [Fact]
    public void AVendorAdded_IsAChange_NamedAsUniFiNamesIt()
    {
        var diff = ConfigDiffEngine.Compare(
            State(("portconf", Profile), ("port_automation", Automation())),
            State(("portconf", Profile), ("port_automation", Automation(vendors: "[42,7]"))));

        var obj = diff.Objects.Should().ContainSingle(o => o.Collection == "port_automation").Subject;
        obj.Action.Should().Be(ConfigChangeAction.Updated);
        obj.Fields.Should().ContainSingle().Which.Path.Should().Be("match.vendor_ids");
        ChangeDescriber.LabelFor("match.vendor_ids").Should().Be("Vendors");
        ChangeDescriber.LabelFor("scope.port_range.from_port").Should().Be("First Port");
    }

    [Fact]
    public void ReorderedVendorsOrOuis_AreNotAChange()
    {
        ConfigDiffEngine.Compare(
            State(("port_automation", Automation(vendors: "[7,42]", ouis: """["aa:bb:cc","dd:ee:ff"]"""))),
            State(("port_automation", Automation(vendors: "[42,7]", ouis: """["dd:ee:ff","aa:bb:cc"]""")))).IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void ItsPlaceInTheOrder_IsNotAFieldChange()
    {
        var after = Automation().Replace("\"automation_index\":0", "\"automation_index\":3");
        ConfigDiffEngine.Compare(State(("port_automation", Automation())), State(("port_automation", after))).IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void AConsoleWithoutPortAutomations_IsNotEverythingCreated()
    {
        // A pre-11.1 snapshot has no port_automation collection at all.
        var before = State(("portconf", Profile));
        before.Collections.Remove("port_automation");
        var diff = ConfigDiffEngine.Compare(before, State(("portconf", Profile), ("port_automation", Automation())));

        diff.Objects.Should().NotContain(o => o.Collection == "port_automation");
        diff.SkippedCollections.Should().Contain("port_automation");
    }

    [Fact]
    public void APlan_ListsTheDifference_AndDoesNotWriteIt()
    {
        var doc = PlanBuilder.Build(
            State(("portconf", Profile), ("port_automation", Automation(name: "Cameras v2"))),
            State(("portconf", Profile), ("port_automation", Automation())), new PlanOptions());

        doc.Operations.Should().BeEmpty();
        doc.Notes.Should().Contain("Port Automations: Network Optimizer does not write this collection; differences are listed only.");
    }

    [Fact]
    public void AProfileAnAutomationUses_IsNotDeletedFromUnderIt()
    {
        // UniFi refuses it too: "This Ethernet port profile is used by Port Automation ...".
        var doc = PlanBuilder.Build(
            State(("port_automation", Automation())),
            State(("portconf", Profile), ("port_automation", Automation())), new PlanOptions());

        doc.Errors.Should().Contain(e => e.Contains("Port Profile IoT Port cannot be deleted") && e.Contains("Port Automation Cameras"));
    }
}
