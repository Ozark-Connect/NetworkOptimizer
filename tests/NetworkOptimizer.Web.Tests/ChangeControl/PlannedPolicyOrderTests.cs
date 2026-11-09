using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkOptimizer.Audit.Analyzers;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.Web.Services.ChangeControl;
using Xunit;

namespace NetworkOptimizer.Web.Tests.ChangeControl;

/// <summary>
/// The planned config carries the policy order the plan's reorder writes, so the Security Audit, the path checks,
/// and the agent's revert all see the order that lands, and nothing else moves.
/// </summary>
public class PlannedPolicyOrderTests
{
    private static readonly PlanOptions Options = new() { Collections = new HashSet<string> { "firewall_zone", "firewall_policy" }, DeleteMissing = false };

    private static JsonObject Zone(string id, string name, string? key, params string[] networks) => new()
    {
        ["_id"] = id, ["name"] = name, ["zone_key"] = key,
        ["network_ids"] = new JsonArray(networks.Select(n => (JsonNode?)n).ToArray()),
    };

    private static JsonObject Side(string zone, string? port = null, string[]? ips = null)
    {
        var side = new JsonObject { ["zone_id"] = zone, ["matching_target"] = ips == null ? "ANY" : "IP", ["port_matching_type"] = port == null ? "ANY" : "SPECIFIC" };
        if (port != null) side["port"] = port;
        if (ips != null) side["ips"] = new JsonArray(ips.Select(i => (JsonNode?)i).ToArray());
        return side;
    }

    private static JsonObject Policy(string id, string action, JsonObject src, JsonObject dst, long? index = null, bool predefined = false)
    {
        var p = new JsonObject
        {
            ["_id"] = id, ["name"] = $"Policy {id}", ["action"] = action, ["enabled"] = true, ["predefined"] = predefined,
            ["protocol"] = "all", ["ip_version"] = "BOTH", ["connection_state_type"] = "ALL", ["source"] = src, ["destination"] = dst,
            ["schedule"] = new JsonObject { ["mode"] = "ALWAYS" },
        };
        if (index != null) p["index"] = index;
        return p;
    }

    /// <summary>IoT to External as a real site has it: a Threat Intelligence exception first, a DNS block, then UniFi's Allow All.</summary>
    private static ConfigState Site()
    {
        var s = new ConfigState();
        s.Collections["networkconf"] = [new JsonObject { ["_id"] = "n-iot", ["name"] = "IoT", ["ip_subnet"] = "10.0.30.1/24" }];
        s.Collections["firewall_zone"] =
        [
            Zone("z-iot", "IoT", null, "n-iot"),
            Zone("z-ext", "External", "external"),
            Zone("z-gw", "Gateway", "gateway"),
        ];
        s.Collections["firewall_policy"] =
        [
            Policy("exception", "ALLOW", Side("z-iot", ips: ["10.0.30.5"]), Side("z-ext", ips: ["192.0.2.10"]), 10000),
            Policy("dns-block", "BLOCK", Side("z-iot"), Side("z-ext", port: "53"), 10004),
            Policy("allow-all", "ALLOW", Side("z-iot"), Side("z-ext"), 2147483647, predefined: true),
        ];
        s.Collections["firewallgroup"] = [];
        return s;
    }

    private static long Index(ConfigState s, Func<JsonObject, bool> which) => s.Get("firewall_policy").Single(which)["index"]!.GetValue<long>();

    private static bool Named(JsonObject p, string id) => p["name"]?.ToString() == $"Policy {id}";

    private static (ConfigState Planned, PlanDocument Doc) AddBlock(ConfigState live)
    {
        var desired = live.Clone();
        desired.Collections["firewall_policy"].Add(Policy("new-block", "BLOCK", Side("z-iot"), Side("z-ext")));
        var doc = PlanBuilder.Build(desired, live, Options);
        return (PlanBuilder.PlannedState(live, doc), doc);
    }

    [Fact]
    public void NewPolicy_TakesTheSlotTheReorderWrites_AndNothingElseMoves()
    {
        var live = Site();
        var (planned, doc) = AddBlock(live);

        doc.Operations.Should().Contain(o => o.Action == PlanAction.Reorder, "the console is told where the new policy goes");
        Index(planned, p => Named(p, "new-block")).Should().Be(10005, "after the pair's last custom policy, as the reorder writes it");
        Index(planned, p => Named(p, "exception")).Should().Be(10000);
        Index(planned, p => Named(p, "dns-block")).Should().Be(10004);
        Index(planned, p => Named(p, "allow-all")).Should().Be(2147483647, "UniFi's own policies are never in a reorder");
    }

    [Fact]
    public void SecurityAudit_ReadsTheNewBlockAfterTheException()
    {
        var (planned, _) = AddBlock(Site());
        var json = JsonSerializer.SerializeToElement(new JsonArray(planned.Get("firewall_policy").Select(p => (JsonNode?)p.DeepClone()).ToArray()));

        var rules = new FirewallRuleParser(NullLogger<FirewallRuleParser>.Instance).ExtractFirewallPolicies(json);

        rules.Single(r => r.Name == "Policy new-block").Index.Should().BeGreaterThan(rules.Single(r => r.Name == "Policy exception").Index,
            "an index of 0 read the block as first, which made the exception look ineffective");
    }

    [Fact]
    public void PlanWithoutAReorder_LeavesEveryIndexAsItIs()
    {
        var live = Site();
        var desired = live.Clone();
        desired.Get("firewall_policy").Single(p => Named(p, "dns-block"))["description"] = "edited";

        var doc = PlanBuilder.Build(desired, live, Options);
        var planned = PlanBuilder.PlannedState(live, doc);

        doc.Operations.Should().NotContain(o => o.Action == PlanAction.Reorder);
        planned.Get("firewall_policy").Select(p => p["index"]!.GetValue<long>())
            .Should().Equal(live.Get("firewall_policy").Select(p => p["index"]!.GetValue<long>()));
    }

    [Fact]
    public void ReorderOfExistingPolicies_IsTheWrittenOrder_RenumberingOnlyWhatMoves()
    {
        var live = Site();
        live.Collections["firewall_policy"].Add(Policy("vpn-block", "BLOCK", Side("z-iot"), Side("z-ext", ips: ["198.51.100.7"]), 10007));
        var desired = live.Clone();
        // A Roll Back carries the snapshot's indexes: the DNS block goes last.
        desired.Get("firewall_policy").Single(p => Named(p, "dns-block"))["index"] = 10009;

        var doc = PlanBuilder.Build(desired, live, Options);
        var planned = PlanBuilder.PlannedState(live, doc);

        Index(planned, p => Named(p, "exception")).Should().Be(10000);
        Index(planned, p => Named(p, "vpn-block")).Should().Be(10007, "it was already in order");
        Index(planned, p => Named(p, "dns-block")).Should().Be(10008, "it moved behind the VPN block");
    }

    [Fact]
    public void InternetCut_NewBlockBehindAnAllowAll_IsNotFlagged()
    {
        var live = Site();
        live.Collections["firewall_policy"].Add(Policy("custom-allow", "ALLOW", Side("z-iot"), Side("z-ext"), 10005));

        var (_, doc) = AddBlock(live);

        doc.Operations.Single(o => o.ObjectName == "Policy new-block").Reasons.Should().NotContain(r => r.Contains("from the internet"),
            "the console puts the block after the allow, so IoT keeps the internet");
    }

    [Fact]
    public void InternetCut_NewBlockAheadOfUniFisAllowAll_IsStillFlagged()
    {
        var (_, doc) = AddBlock(Site());

        doc.Operations.Single(o => o.ObjectName == "Policy new-block").Reasons.Should().Contain(r => r.StartsWith("Blocks IoT from the internet."));
    }

    private static UniFiApiClient Client() => new(NullLogger<UniFiApiClient>.Instance, "192.0.2.1", "", "", apiKey: "test-key");

    [Fact]
    public void AgentRevert_OfAPlanThatAddsAPolicy_IsUnchanged()
    {
        var live = Site();
        var (planned, _) = AddBlock(live);

        var calls = AgentRevertArmer.Calls(PlanBuilder.Build(live, planned, Options), Client());

        calls.Should().BeEmpty("the new policy's ID is known only to the forward apply; the re-arm after apply carries its delete");
    }

    [Fact]
    public void AgentRevert_OfAReorder_PutsTheOrderBack()
    {
        var live = Site();
        live.Collections["firewall_policy"].Add(Policy("vpn-block", "BLOCK", Side("z-iot"), Side("z-ext", ips: ["198.51.100.7"]), 10007));
        var desired = live.Clone();
        desired.Get("firewall_policy").Single(p => Named(p, "dns-block"))["index"] = 10009;
        var planned = PlanBuilder.PlannedState(live, PlanBuilder.Build(desired, live, Options));

        var calls = AgentRevertArmer.Calls(PlanBuilder.Build(live, planned, Options), Client());

        var reorder = calls.Should().ContainSingle(c => c.Url.EndsWith("firewall-policies/batch-reorder")).Subject;
        var before = JsonNode.Parse(reorder.Body)!["before_predefined_ids"]!.AsArray().Select(n => n!.ToString());
        before.Should().Equal("exception", "dns-block", "vpn-block");
    }
}
