using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.Web.Services.ChangeControl;
using Xunit;

namespace NetworkOptimizer.Web.Tests.ChangeControl;

public class AgentRevertCallsTests
{
    private static readonly PlanOptions Options = new() { Collections = new HashSet<string> { "networkconf", "wlanconf" } };

    private static UniFiApiClient Client() =>
        new(NullLogger<UniFiApiClient>.Instance, "192.0.2.1", "", "", apiKey: "test-key");

    private static JsonObject Net(string id, string name, int vlan) => new() { ["_id"] = id, ["name"] = name, ["vlan"] = vlan, ["purpose"] = "corporate" };

    private static ConfigState State(IEnumerable<JsonObject> networks, IEnumerable<JsonObject>? wlans = null)
    {
        var s = new ConfigState();
        s.Collections["networkconf"] = networks.ToList();
        s.Collections["wlanconf"] = (wlans ?? []).ToList();
        return s;
    }

    [Fact]
    public void DeletedObject_IsRecreatedAndLaterCallsUseItsRef()
    {
        var before = State([Net("net-main", "Main", 1), Net("net-iot", "IoT", 30)],
            [new JsonObject { ["_id"] = "w1", ["name"] = "Home", ["networkconf_id"] = "net-iot" }]);
        var current = State([Net("net-main", "Main", 1)],
            [new JsonObject { ["_id"] = "w1", ["name"] = "Home", ["networkconf_id"] = "net-main" }]);

        var calls = AgentRevertArmer.Calls(PlanBuilder.Build(before, current, Options), Client());

        var create = calls.Should().ContainSingle(c => c.Method == "POST" && c.Url.EndsWith("rest/networkconf")).Subject;
        create.CaptureId.Should().BeTrue();
        var update = calls.Should().ContainSingle(c => c.Url.EndsWith("rest/wlanconf/w1")).Subject;
        update.Body.Should().Contain("@ref:" + create.Key);
        calls.IndexOf(create).Should().BeLessThan(calls.IndexOf(update));
    }

    [Fact]
    public void ForwardCreatedObject_IsLeftForTheRearm()
    {
        var before = State([Net("net-main", "Main", 1)]);
        var current = State([Net("net-main", "Main", 1), Net("@ref:fwd-create", "Lab", 80)]);

        var calls = AgentRevertArmer.Calls(PlanBuilder.Build(before, current, Options), Client());

        calls.Should().BeEmpty("only the forward apply knows that ID; the re-arm after apply carries the delete");
    }

    [Fact]
    public void AfterApply_CreatedObjectIsDeletedByRealId()
    {
        var before = State([Net("net-main", "Main", 1)]);
        var after = State([Net("net-main", "Main", 1), Net("real-lab-id", "Lab", 80)]);

        var calls = AgentRevertArmer.Calls(PlanBuilder.Build(before, after, Options), Client());

        calls.Should().ContainSingle(c => c.Method == "DELETE" && c.Url.EndsWith("rest/networkconf/real-lab-id"));
    }
}
