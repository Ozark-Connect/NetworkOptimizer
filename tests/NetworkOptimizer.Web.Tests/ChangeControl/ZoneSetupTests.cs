using System.Text.Json.Nodes;
using FluentAssertions;
using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.Web.Services.ChangeControl;
using Xunit;

namespace NetworkOptimizer.Web.Tests.ChangeControl;

public class ZoneSetupTests
{
    private static NetworkInfo Net(string id, string name, int vlan, NetworkPurpose purpose) => new() { Id = id, Name = name, VlanId = vlan, Purpose = purpose };

    private static readonly List<NetworkInfo> Networks =
    [
        Net("n-home", "Home", 1, NetworkPurpose.Home),
        Net("n-work", "Work", 10, NetworkPurpose.Corporate),
        Net("n-iot", "IoT", 30, NetworkPurpose.IoT),
        Net("n-media", "Media", 35, NetworkPurpose.Media),
        Net("n-cam", "Cameras", 40, NetworkPurpose.Security),
        Net("n-guest", "Guest", 70, NetworkPurpose.Guest),
        Net("n-print", "Printers", 50, NetworkPurpose.Printer),
    ];

    private static JsonObject Zone(string id, string name, string? key, params string[] networks) => new()
    {
        ["_id"] = id, ["name"] = name, ["zone_key"] = key, ["default_zone"] = key != null,
        ["network_ids"] = new JsonArray(networks.Select(n => (JsonNode?)n).ToArray()),
    };

    private static ConfigState Live()
    {
        var s = new ConfigState();
        s.Collections["firewall_zone"] =
        [
            Zone("z-int", "Internal", "internal", "n-home", "n-work", "n-iot", "n-media", "n-cam", "n-print"),
            Zone("z-ext", "External", "external", "n-wan"),
            Zone("z-gw", "Gateway", "gateway"),
            Zone("z-hot", "Hotspot", "hotspot", "n-guest"),
            Zone("z-dmz", "Dmz", "dmz"),
        ];
        s.Collections["firewall_policy"] = [];
        s.Collections["networkconf"] = Networks.Select(n => new JsonObject { ["_id"] = n.Id, ["name"] = n.Name, ["vlan"] = n.VlanId }).ToList();
        return s;
    }

    private static List<string> Members(ConfigState s, string name) =>
        s.Get("firewall_zone").First(z => z["name"]!.ToString() == name)["network_ids"]!.AsArray().Select(n => n!.ToString()).ToList();

    private static JsonObject? Policy(ConfigState s, string srcZone, string dstZone)
    {
        var ids = s.Get("firewall_zone").ToDictionary(z => z["name"]!.ToString(), z => z["_id"]!.ToString());
        return s.Get("firewall_policy").FirstOrDefault(p => p["source"]!["zone_id"]!.ToString() == ids[srcZone] && p["destination"]!["zone_id"]!.ToString() == ids[dstZone]);
    }

    [Fact]
    public void TrustMode_GroupsByTrustLevel()
    {
        var board = ZoneSetup.Build(Live(), Networks, ZoneSetup.TrustMode);

        board.Zones.Single(z => z.Key == "internal").NetworkIds.Should().BeEquivalentTo(["n-home", "n-work"]);
        board.Zones.Single(z => z.Key == "iot").NetworkIds.Should().BeEquivalentTo(["n-iot", "n-media"]);
        board.Zones.Single(z => z.Key == "iot").IsNew.Should().BeTrue();
        board.Zones.Single(z => z.Key == "hotspot").ExistingId.Should().Be("z-hot");
        board.Zones.Single(z => z.Key == ZoneSetup.UnassignedKey).NetworkIds.Should().Equal("n-print");
        board.Exceptions.Single(e => e.Key == "printing").Applicable.Should().BeFalse();
    }

    [Fact]
    public void Desired_MovesNetworksAndWritesPairPolicies()
    {
        var live = Live();
        var board = ZoneSetup.Build(live, Networks, ZoneSetup.TrustMode);
        var desired = ZoneSetup.Desired(live, board);

        Members(desired, "Internal").Should().BeEquivalentTo(["n-home", "n-work", "n-print"], "unassigned networks keep their zone");
        Members(desired, "IoT").Should().BeEquivalentTo(["n-iot", "n-media"]);
        Members(desired, "External").Should().Equal("n-wan");
        Policy(desired, "IoT", "Internal")!["action"]!.ToString().Should().Be("BLOCK");
        Policy(desired, "Internal", "IoT")!["name"]!.ToString().Should().Be("Casting and mDNS", "the exception is on by default");
        Policy(desired, "Cameras", "External")!["action"]!.ToString().Should().Be("BLOCK");
        Policy(desired, "IoT", "Cameras")!["action"]!.ToString().Should().Be("BLOCK");
        desired.Get("firewall_policy").Should().NotContain(p => p["source"]!["zone_id"]!.ToString() == "z-hot", "Hotspot keeps UniFi's rules");
    }

    [Fact]
    public void MovesAndExceptions_AreHonored()
    {
        var live = Live();
        var board = ZoneSetup.Build(live, Networks, ZoneSetup.TrustMode,
            moves: new Dictionary<string, string> { ["n-print"] = "iot" }, exceptions: new Dictionary<string, bool> { ["casting"] = false });
        var desired = ZoneSetup.Desired(live, board);

        Members(desired, "IoT").Should().Contain("n-print");
        Members(desired, "Internal").Should().NotContain("n-print");
        Policy(desired, "Internal", "IoT")!["action"]!.ToString().Should().Be("BLOCK");
    }

    [Fact]
    public void Plan_CreatesZonesBeforeThePoliciesThatUseThem()
    {
        var live = Live();
        var desired = ZoneSetup.Desired(live, ZoneSetup.Build(live, Networks, ZoneSetup.TrustMode));

        var doc = PlanBuilder.Build(desired, live, ZoneSetup.Options());

        var zoneCreate = doc.Operations.Single(o => o.Collection == "firewall_zone" && o.Action == PlanAction.Create && o.ObjectName == "IoT");
        var iotPolicy = doc.Operations.First(o => o.Collection == "firewall_policy" && o.Action == PlanAction.Create && o.ObjectName == "Block IoT to Internal");
        iotPolicy.Body!["source"]!["zone_id"]!.ToString().Should().Be(PlanBuilder.RefPrefix + zoneCreate.Key);
        doc.Operations.IndexOf(zoneCreate).Should().BeLessThan(doc.Operations.IndexOf(iotPolicy));
        doc.Operations.Should().NotContain(o => o.Action == PlanAction.Delete);
    }

    [Fact]
    public void LegacySite_IsDetected()
    {
        var s = new ConfigState();
        s.Collections["firewall_zone"] = [];
        s.Collections["firewallrule"] = [new JsonObject { ["_id"] = "r1", ["name"] = "Block IoT" }];
        ZoneSetup.IsLegacy(s).Should().BeTrue();
        ZoneSetup.IsLegacy(Live()).Should().BeFalse();
    }
}
