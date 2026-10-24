using System.Text.Json.Nodes;
using FluentAssertions;
using NetworkOptimizer.Web.Services.ChangeControl;
using Xunit;

namespace NetworkOptimizer.Web.Tests.ChangeControl;

public class PlanOrderTests
{
    private static JsonObject Device(string profile) => new()
    {
        ["_id"] = "dev1", ["mac"] = "aa:bb:cc:00:00:01", ["name"] = "Switch", ["type"] = "usw",
        ["port_overrides"] = new JsonArray(new JsonObject { ["port_idx"] = 5, ["portconf_id"] = profile }),
    };

    [Fact]
    public void ProfileDelete_RunsAfterTheDeviceUpdateThatReleasesIt()
    {
        // Rolling back a change that created a profile and put a port on it.
        var live = new ConfigState();
        live.Collections["portconf"] = [new JsonObject { ["_id"] = "p-old", ["name"] = "Default" }, new JsonObject { ["_id"] = "p-new", ["name"] = "Cameras" }];
        live.Collections["device"] = [Device("p-new")];
        var desired = new ConfigState();
        desired.Collections["portconf"] = [new JsonObject { ["_id"] = "p-old", ["name"] = "Default" }];
        desired.Collections["device"] = [Device("p-old")];

        var doc = PlanBuilder.Build(desired, live, new PlanOptions { Collections = new HashSet<string> { "portconf", "device" } });

        var update = doc.Operations.FindIndex(o => o.Collection == "device" && o.Action == PlanAction.Update);
        var delete = doc.Operations.FindIndex(o => o.Collection == "portconf" && o.Action == PlanAction.Delete);
        update.Should().BeGreaterThanOrEqualTo(0);
        delete.Should().BeGreaterThan(update);
        doc.Operations[update].ManagementPath.Should().BeTrue();
    }
}
