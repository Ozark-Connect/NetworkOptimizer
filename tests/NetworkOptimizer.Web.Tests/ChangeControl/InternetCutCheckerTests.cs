using System.Text.Json.Nodes;
using FluentAssertions;
using NetworkOptimizer.Web.Services.ChangeControl;
using Xunit;

namespace NetworkOptimizer.Web.Tests.ChangeControl;

/// <summary>A plan that takes a LAN network off the internet is acknowledged, judged by the path, not the policy's shape.</summary>
public class InternetCutCheckerTests
{
    private static JsonObject Zone(string id, string name, string? key, params string[] networks) => new()
    {
        ["_id"] = id, ["name"] = name, ["zone_key"] = key,
        ["network_ids"] = new JsonArray(networks.Select(n => (JsonNode?)n).ToArray()),
    };

    private static JsonObject Side(string zone, string[]? ips = null, string[]? networks = null)
    {
        var side = new JsonObject
        {
            ["zone_id"] = zone, ["matching_target"] = ips != null ? "IP" : networks != null ? "NETWORK" : "ANY", ["port_matching_type"] = "ANY",
        };
        if (ips != null) side["ips"] = new JsonArray(ips.Select(i => (JsonNode?)i).ToArray());
        if (networks != null) side["network_ids"] = new JsonArray(networks.Select(n => (JsonNode?)n).ToArray());
        return side;
    }

    private static JsonObject Policy(string id, string action, JsonObject src, JsonObject dst, long? index = null, bool predefined = false)
    {
        var p = new JsonObject
        {
            ["_id"] = id, ["name"] = id, ["action"] = action, ["enabled"] = true, ["predefined"] = predefined,
            ["protocol"] = "all", ["ip_version"] = "BOTH", ["connection_state_type"] = "ALL", ["source"] = src, ["destination"] = dst,
            ["schedule"] = new JsonObject { ["mode"] = "ALWAYS" },
        };
        if (index != null) p["index"] = index;
        return p;
    }

    /// <summary>Home and IoT in Internal, with UniFi's predefined Allow All to External.</summary>
    private static ConfigState Site()
    {
        var s = new ConfigState();
        s.Collections["networkconf"] =
        [
            new JsonObject { ["_id"] = "n-home", ["name"] = "Home", ["ip_subnet"] = "10.0.1.1/24" },
            new JsonObject { ["_id"] = "n-iot", ["name"] = "IoT", ["ip_subnet"] = "10.0.30.1/24" },
        ];
        s.Collections["firewall_zone"] =
        [
            Zone("z-int", "Internal", "internal", "n-home", "n-iot"),
            Zone("z-ext", "External", "external"),
            Zone("z-gw", "Gateway", "gateway"),
        ];
        s.Collections["firewall_policy"] = [Policy("int-ext", "ALLOW", Side("z-int"), Side("z-ext"), 2147483647, predefined: true)];
        s.Collections["firewallgroup"] = [];
        return s;
    }

    private static PlanDocument Build(ConfigState desired, ConfigState live) =>
        PlanBuilder.Build(desired, live, new PlanOptions { Collections = new HashSet<string> { "firewall_zone", "firewall_policy" }, DeleteMissing = false });

    private static PlanOperation Write(PlanDocument doc, string name) => doc.Operations.Single(o => o.ObjectName == name);

    [Fact]
    public void BlockAll_FromInternalToExternal_IsAcknowledged_NotWatched()
    {
        var live = Site();
        var desired = live.Clone();
        desired.Collections["firewall_policy"].Add(Policy("block-all", "BLOCK", Side("z-int"), Side("z-ext")));

        var doc = Build(desired, live);

        var op = Write(doc, "block-all");
        op.Tier.Should().Be(PlanTiers.Acknowledge);
        op.Reasons.Should().Contain("Blocks Home and IoT from the internet. The commit-confirmed watch does not catch this");
        doc.Lockouts.Should().BeEmpty();
        doc.WatchInternet.Should().BeFalse("the console's internet health never sees a LAN-side block");
    }

    [Fact]
    public void BlockListingTheNetworks_IsCaughtToo()
    {
        var live = Site();
        var desired = live.Clone();
        desired.Collections["firewall_policy"].Add(Policy("block-iot", "BLOCK", Side("z-int", networks: ["n-iot"]), Side("z-ext")));

        Write(Build(desired, live), "block-iot").Reasons.Should().Contain(r => r.StartsWith("Blocks IoT from the internet."));
    }

    [Fact]
    public void BlockBelowAnAllow_CutsNothing()
    {
        var live = Site();
        live.Collections["firewall_policy"].Add(Policy("allow-ext", "ALLOW", Side("z-int"), Side("z-ext"), 100));
        var desired = live.Clone();
        desired.Collections["firewall_policy"].Add(Policy("block-all", "BLOCK", Side("z-int"), Side("z-ext"), 200));

        Write(Build(desired, live), "block-all").Reasons.Should().NotContain(r => r.Contains("from the internet"));
    }

    [Fact]
    public void BlockThatAlreadyCut_IsNotFlaggedAgain()
    {
        var live = Site();
        live.Collections["firewall_policy"].Add(Policy("block-all", "BLOCK", Side("z-int"), Side("z-ext"), 100));
        var desired = live.Clone();
        desired.Get("firewall_policy").Single(p => p["_id"]!.ToString() == "block-all")["description"] = "renamed";

        var doc = Build(desired, live);

        doc.Operations.SelectMany(o => o.Reasons).Should().NotContain(r => r.Contains("from the internet"));
    }

    [Fact]
    public void BlockOfOneResolver_IsNotTheInternet()
    {
        var live = Site();
        var desired = live.Clone();
        desired.Collections["firewall_policy"].Add(Policy("block-cf", "BLOCK", Side("z-int"), Side("z-ext", ips: ["1.1.1.1"])));

        Write(Build(desired, live), "block-cf").Reasons.Should().NotContain(r => r.Contains("from the internet"));
    }

    [Fact]
    public void ScheduledBlock_SaysSo()
    {
        var live = Site();
        var desired = live.Clone();
        var bedtime = Policy("bedtime", "BLOCK", Side("z-int", networks: ["n-iot"]), Side("z-ext"));
        bedtime["schedule"] = new JsonObject { ["mode"] = "EVERY_DAY", ["time_range_start"] = "22:00", ["time_range_end"] = "06:00" };
        desired.Collections["firewall_policy"].Add(bedtime);

        Write(Build(desired, live), "bedtime").Reasons.Should().Contain(r => r.StartsWith("Blocks IoT from the internet on its schedule."));
    }
}
