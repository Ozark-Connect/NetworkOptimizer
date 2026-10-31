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

    private static JsonObject Policy(string id, long index, string src = "z-ext", string dst = "z-vpn", string? name = null) => new()
    {
        ["_id"] = id, ["name"] = name ?? id, ["action"] = "BLOCK", ["enabled"] = true, ["predefined"] = false, ["index"] = index,
        ["source"] = new JsonObject { ["zone_id"] = src, ["matching_target"] = "ANY" },
        ["destination"] = new JsonObject { ["zone_id"] = dst, ["matching_target"] = "ANY" },
    };

    private static JsonObject Predefined(long index) => new()
    {
        ["_id"] = $"pre-{index}", ["name"] = "Block All Traffic", ["action"] = "BLOCK", ["predefined"] = true, ["index"] = index,
        ["source"] = new JsonObject { ["zone_id"] = "z-ext" }, ["destination"] = new JsonObject { ["zone_id"] = "z-vpn" },
    };

    private static ConfigState Policies(params JsonObject[] policies)
    {
        var state = new ConfigState();
        state.Collections["firewall_zone"] = [new JsonObject { ["_id"] = "z-ext", ["name"] = "External" }, new JsonObject { ["_id"] = "z-vpn", ["name"] = "Vpn" }];
        state.Collections["firewall_policy"] = [.. policies];
        return state;
    }

    private static List<string> Before(PlanDocument doc) =>
        doc.Operations.Single(o => o.Action == PlanAction.Reorder).Body!["before_predefined_ids"]!.AsArray().Select(n => n!.ToString()).ToList();

    [Fact]
    public void Reorder_MatchesAThreatIntelligencePolicyReCreatedSinceTheBaseline_ByName()
    {
        // Return to Baseline: a Threat Intelligence policy re-created since the Baseline (a new ID) matches the Baseline's
        // by name, so it takes the Baseline's spot (after the exception) and is listed: nothing is left out of the reorder.
        var baseline = Policies(Policy("bl", 10001, name: "Net. Opt. Blocklist"), Policy("exc", 10005, name: "Net. Opt. Exception"),
            Policy("pl-old", 10006, name: "Net. Opt. Public Lists"), Predefined(30000));
        var live = Policies(Policy("bl", 10001, name: "Net. Opt. Blocklist"), Policy("pl-new", 10002, name: "Net. Opt. Public Lists"),
            Policy("exc", 10003, name: "Net. Opt. Exception"), Predefined(30000));

        var doc = PlanBuilder.Build(baseline, live, new PlanOptions { Collections = new HashSet<string> { "firewall_policy" } });

        doc.Errors.Should().BeEmpty();
        doc.Operations.Should().NotContain(o => o.Action == PlanAction.Delete);
        Before(doc).Should().Equal("bl", "exc", "pl-new");
    }

    [Fact]
    public void Reorder_SurvivesAConsoleThatRepeatsAPolicyId()
    {
        // Seen on a UDM Pro: the console lists a derived policy's synthetic id twice (issue #1333).
        static JsonObject Derived() => new()
        {
            ["_id"] = "obj_1040bfb7-6a65-4954-b383-5904c590c836_1", ["name"] = "Derived", ["action"] = "ALLOW", ["predefined"] = true,
            ["index"] = 30001, ["source"] = new JsonObject { ["zone_id"] = "z-ext" }, ["destination"] = new JsonObject { ["zone_id"] = "z-vpn" },
        };
        var desired = Policies(Policy("a", 10001), Policy("b", 10002), Predefined(30000), Derived(), Derived());
        var live = Policies(Policy("b", 10001), Policy("a", 10002), Predefined(30000), Derived(), Derived());

        var doc = PlanBuilder.Build(desired, live, new PlanOptions { Collections = new HashSet<string> { "firewall_policy" } });

        doc.Errors.Should().BeEmpty();
        Before(doc).Should().Equal("a", "b");
    }

    [Fact]
    public void Reorder_LeavesOutAThreatIntelligencePolicyTheConsoleDoesNotHaveYet()
    {
        // Return to Baseline mid-provision: Threat Intelligence has not re-created its policy yet. The plan never creates it
        // (Threat Intelligence does), so the reorder must not name it: it would wait on a create that never comes.
        var baseline = Policies(Policy("a", 10001), Policy("bl", 10002, name: "Net. Opt. Blocklist"), Policy("b", 10003), Predefined(30000));
        var live = Policies(Policy("b", 10001), Policy("a", 10002), Predefined(30000));

        var doc = PlanBuilder.Build(baseline, live, new PlanOptions { Collections = new HashSet<string> { "firewall_policy" } });

        doc.Operations.Should().NotContain(o => o.Action == PlanAction.Create);
        doc.Operations.Should().NotContain(o => o.Body != null && o.Body.ToJsonString().Contains(PlanBuilder.RefPrefix));
        Before(doc).Should().Equal("a", "b");
    }

    [Fact]
    public void Reorder_IsNotWritten_WhenOnlyAThreatIntelligencePolicyTheConsoleLacksDiffers()
    {
        var baseline = Policies(Policy("a", 10001), Policy("bl", 10002, name: "Net. Opt. Blocklist"), Policy("b", 10003), Predefined(30000));
        var live = Policies(Policy("a", 10001), Policy("b", 10002), Predefined(30000));

        var doc = PlanBuilder.Build(baseline, live, new PlanOptions { Collections = new HashSet<string> { "firewall_policy" } });

        doc.Operations.Should().BeEmpty("the order the plan writes is already the console's");
    }

    [Fact]
    public void Reorder_InARollBack_ListsPoliciesOutsideItsObjects()
    {
        var before = Policies(Policy("a", 10001), Policy("b", 10002), Predefined(30000));
        var live = Policies(Policy("x", 10001), Policy("b", 10002), Policy("a", 10003), Predefined(30000));

        var doc = PlanBuilder.Build(before, live, new PlanOptions
        {
            Collections = new HashSet<string> { "firewall_policy" },
            OnlyObjects = new HashSet<(string, string)> { ("firewall_policy", "a"), ("firewall_policy", "b") },
        });

        // x was first and stays first: the Roll Back never wrote it.
        Before(doc).Should().Equal("x", "a", "b");
    }

    [Fact]
    public void Reorder_LeavesOutAPolicyTheEditMovesToAnotherPair()
    {
        var live = Policies(Policy("a", 10001), Policy("b", 10002), Policy("c", 10003), Predefined(30000));
        live.Collections["firewall_zone"].Add(new JsonObject { ["_id"] = "z-iot", ["name"] = "IoT" });
        var desired = live.Clone();
        var list = desired.Collections["firewall_policy"];
        list[0]["index"] = 10003L;
        list[2]["index"] = 10001L;
        list[1]["destination"]!["zone_id"] = "z-iot";

        var doc = PlanBuilder.Build(desired, live, new PlanOptions { Collections = new HashSet<string> { "firewall_policy", "firewall_zone" } });

        doc.Operations.Where(o => o.Action == PlanAction.Reorder && o.Key.EndsWith("z-ext>z-vpn")).Should().ContainSingle()
            .Which.Body!["before_predefined_ids"]!.AsArray().Select(n => n!.ToString()).Should().Equal("c", "a");
    }

    [Fact]
    public void Reorder_StillListsAPolicyAboutToBeDeleted()
    {
        var desired = Policies(Policy("b", 10001), Policy("a", 10002), Predefined(30000));
        var live = Policies(Policy("a", 10001), Policy("gone", 10002), Policy("b", 10003), Predefined(30000));

        var doc = PlanBuilder.Build(desired, live, new PlanOptions { Collections = new HashSet<string> { "firewall_policy" } });

        doc.Operations.Should().Contain(o => o.Action == PlanAction.Delete && o.ObjectId == "gone");
        Before(doc).Should().BeEquivalentTo(["b", "a", "gone"]);
    }
}
