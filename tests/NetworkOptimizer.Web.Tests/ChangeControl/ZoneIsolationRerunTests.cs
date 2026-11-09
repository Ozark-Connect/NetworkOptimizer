using System.Text.Json.Nodes;
using FluentAssertions;
using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.Web.Services.ChangeControl;
using Xunit;

namespace NetworkOptimizer.Web.Tests.ChangeControl;

public class ZoneIsolationRerunTests
{
    private static readonly List<NetworkInfo> Networks =
    [
        new() { Id = "n-iot", Name = "IoT", VlanId = 30, Purpose = NetworkPurpose.IoT },
        new() { Id = "n-media", Name = "Media", VlanId = 35, Purpose = NetworkPurpose.Media },
    ];

    private static ConfigState Site()
    {
        var state = new ConfigState();
        state.Collections["networkconf"] = Networks.Select(n => new JsonObject
        {
            ["_id"] = n.Id, ["name"] = n.Name, ["vlan"] = n.VlanId,
            ["network_isolation_enabled"] = n.Id == "n-iot",
        }).ToList();
        state.Collections["firewall_zone"] =
        [
            new JsonObject { ["_id"] = "z-int", ["name"] = "Internal", ["zone_key"] = "internal", ["network_ids"] = new JsonArray() },
            new JsonObject { ["_id"] = "z-iot", ["name"] = "IoT", ["network_ids"] = new JsonArray("n-iot", "n-media") },
        ];
        state.Collections["firewall_policy"] = [];
        return state;
    }

    private static ZonePlan Plan(ConfigState state) =>
        ZoneSetup.Plan(state, ZoneSetup.Build(state, Networks, ZoneSetup.TrustMode));

    private static JsonObject Within(ConfigState state) =>
        state.Get("firewall_policy").Single(p => p["name"]?.ToString() == "Allow IoT within IoT");

    private static JsonObject ScopedPolicy(bool generated) => new()
    {
        ["_id"] = "p-within", ["name"] = "Allow IoT within IoT", ["enabled"] = true,
        ["action"] = "ALLOW", ["protocol"] = "tcp", ["predefined"] = false,
        ["description"] = generated ? "Set Up Zones" : "Operator policy",
        ["source"] = new JsonObject
        {
            ["zone_id"] = "z-iot", ["matching_target"] = "IP", ["matching_target_type"] = "SPECIFIC",
            ["ips"] = new JsonArray("192.0.2.10"), ["port_matching_type"] = "ANY",
        },
        ["destination"] = new JsonObject
        {
            ["zone_id"] = "z-iot", ["matching_target"] = "IP", ["matching_target_type"] = "SPECIFIC",
            ["ips"] = new JsonArray("198.51.100.10"), ["port_matching_type"] = "SPECIFIC", ["port"] = "443",
        },
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExistingOperatorPolicy_KeepsItsSourceAndDestination(bool allIsolated)
    {
        var live = Site();
        if (allIsolated) live.Get("networkconf").Single(n => n["_id"]!.ToString() == "n-media")["network_isolation_enabled"] = true;
        var policy = ScopedPolicy(generated: false);
        var original = policy.DeepClone();
        live.Collections["firewall_policy"].Add(policy);

        var desired = Plan(live).Desired;

        JsonNode.DeepEquals(Within(desired), original).Should().BeTrue("a same-named operator policy does not belong to the wizard");
    }

    [Fact]
    public void GeneratedWithinZoneAllow_KeepsEditedDestinationScope()
    {
        var live = Site();
        var policy = ScopedPolicy(generated: true);
        policy["source"] = new JsonObject { ["zone_id"] = "z-iot", ["matching_target"] = "ANY", ["port_matching_type"] = "ANY" };
        var destination = policy["destination"]!.DeepClone();
        live.Collections["firewall_policy"].Add(policy);

        var desired = Plan(live).Desired;

        JsonNode.DeepEquals(Within(desired)["destination"], destination).Should().BeTrue();
        Within(desired)["protocol"]!.ToString().Should().Be("tcp");
    }

    [Theory]
    [InlineData("IP", false)]
    [InlineData("IP", true)]
    [InlineData("NETWORK", false)]
    [InlineData("PORT", false)]
    public void GeneratedWithinZoneAllow_KeepsEditedSourceScope(string target, bool allIsolated)
    {
        var live = Site();
        foreach (var network in live.Get("networkconf")) network["network_isolation_enabled"] = allIsolated;
        var policy = ScopedPolicy(generated: true);
        if (target == "NETWORK")
            policy["source"] = new JsonObject
            {
                ["zone_id"] = "z-iot", ["matching_target"] = "NETWORK", ["network_ids"] = new JsonArray("n-iot"),
                ["match_opposite_networks"] = true, ["port_matching_type"] = "ANY",
            };
        else if (target == "PORT")
            policy["source"] = new JsonObject
            {
                ["zone_id"] = "z-iot", ["matching_target"] = "ANY", ["port_matching_type"] = "SPECIFIC", ["port"] = "443",
            };
        var original = policy.DeepClone();
        live.Collections["firewall_policy"].Add(policy);

        var desired = Plan(live).Desired;

        JsonNode.DeepEquals(Within(desired), original).Should().BeTrue("the description alone does not make an edited source safe to replace");
    }

    [Fact]
    public void GeneratedWithinZoneAllow_RestoresSourceAfterIsolationIsDisabled()
    {
        var live = Plan(Site()).Desired;
        Within(live)["source"]!["network_ids"]!.AsArray().Select(n => n!.ToString()).Should().Equal("n-media");
        live.Get("networkconf").Single(n => n["_id"]!.ToString() == "n-iot")["network_isolation_enabled"] = false;

        var desired = Plan(live).Desired;

        Within(desired)["source"]!["matching_target"]!.ToString().Should().Be("ANY");
        Within(desired)["source"]!.AsObject().ContainsKey("network_ids").Should().BeFalse();
    }

    [Fact]
    public void GeneratedWithinZoneAllow_IsRemovedWhenAllNetworksBecomeIsolated()
    {
        var live = Plan(Site()).Desired;
        var policyId = Within(live)["_id"]!.ToString();
        live.Get("networkconf").Single(n => n["_id"]!.ToString() == "n-media")["network_isolation_enabled"] = true;

        var desired = Plan(live).Desired;
        var doc = PlanBuilder.Build(desired, live, ZoneSetup.Options());

        desired.Get("firewall_policy").Should().NotContain(p => p["_id"]!.ToString() == policyId);
        doc.Operations.Should().Contain(o => o.Action == PlanAction.Delete && o.ObjectId == policyId);
    }
}
