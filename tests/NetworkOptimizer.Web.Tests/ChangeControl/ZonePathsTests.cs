using System.Text.Json.Nodes;
using FluentAssertions;
using NetworkOptimizer.Web.Services.ChangeControl;
using Xunit;

namespace NetworkOptimizer.Web.Tests.ChangeControl;

public class ZonePathsTests
{
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

    private static JsonObject Policy(string id, string action, JsonObject src, JsonObject dst, long index, bool predefined = false, string protocol = "all") => new()
    {
        ["_id"] = id, ["name"] = id, ["action"] = action, ["enabled"] = true, ["predefined"] = predefined, ["index"] = index,
        ["protocol"] = protocol, ["ip_version"] = "BOTH", ["connection_state_type"] = "ALL", ["source"] = src, ["destination"] = dst,
    };

    /// <summary>Home (10.0.1.0/24) in Internal, Pi-hole's network (10.0.53.0/24) in a custom DNS zone.</summary>
    private static ConfigState Site()
    {
        var s = new ConfigState();
        s.Collections["networkconf"] =
        [
            new JsonObject { ["_id"] = "n-home", ["name"] = "Home", ["ip_subnet"] = "10.0.1.1/24" },
            new JsonObject { ["_id"] = "n-iot", ["name"] = "IoT", ["ip_subnet"] = "10.0.30.1/24" },
            new JsonObject { ["_id"] = "n-dns", ["name"] = "DNS", ["ip_subnet"] = "10.0.53.1/24" },
        ];
        s.Collections["firewall_zone"] =
        [
            Zone("z-int", "Internal", "internal", "n-home", "n-iot"),
            Zone("z-dns", "DNS Servers", null, "n-dns"),
            Zone("z-ext", "External", "external"),
            Zone("z-gw", "Gateway", "gateway"),
        ];
        s.Collections["firewall_policy"] =
        [
            Policy("pihole", "ALLOW", Side("z-int"), Side("z-dns", "53"), 10000, protocol: "tcp_udp"),
            Policy("int-int", "ALLOW", Side("z-int"), Side("z-int"), 2147483647, predefined: true),
            Policy("int-dns", "BLOCK", Side("z-int"), Side("z-dns"), 2147483647, predefined: true),
            Policy("dns-int", "BLOCK", Side("z-dns"), Side("z-int"), 2147483647, predefined: true),
            Policy("int-gw", "ALLOW", Side("z-int"), Side("z-gw"), 2147483647, predefined: true),
        ];
        s.Collections["firewallgroup"] = [];
        return s;
    }

    [Fact]
    public void FirstMatchingPolicyDecides()
    {
        var s = Site();
        ZonePaths.Allows(s, "n-home", "10.0.53.10", "udp", 53).Should().BeTrue("the Pi-hole allow sits ahead of Block All");
        ZonePaths.Allows(s, "n-home", "10.0.53.10", "tcp", 443).Should().BeFalse("only port 53 is allowed");
        ZonePaths.Allows(s, "n-home", "10.0.1.1", "tcp", 22).Should().BeTrue("a network's own gateway address is the Gateway zone");
    }

    [Fact]
    public void NewCustomZone_GetsUniFisDefaults()
    {
        var s = Site();
        s.Collections["firewall_zone"][0]["network_ids"] = new JsonArray("n-home");
        s.Collections["firewall_zone"].Add(Zone("z-iot", "IoT", null, "n-iot"));

        ZonePaths.Allows(s, "n-iot", "10.0.1.20", "tcp", 80).Should().BeFalse("UniFi blocks a new custom zone from Internal");
        ZonePaths.Allows(s, "n-home", "10.0.30.20", "tcp", 80).Should().BeFalse("and Internal from it");
        ZonePaths.Allows(s, "n-iot", "10.0.53.10", "udp", 53).Should().BeFalse("and every other custom zone");
        ZonePaths.Allows(s, "n-iot", "10.0.30.1", "udp", 53).Should().BeTrue("the gateway stays reachable");
        ZonePaths.Allows(s, "n-iot", "203.0.113.5", "tcp", 443).Should().BeTrue("and the internet");
    }

    [Fact]
    public void Defaults_AreNotAddedToAZoneThatHasThem()
    {
        var s = Site();
        ZonePaths.WithCustomZoneDefaults(s).Get("firewall_policy").Should().HaveCount(s.Get("firewall_policy").Count, "DNS Servers already has UniFi's policies");
    }

    [Fact]
    public void PathCheck_FlagsADnsBreak()
    {
        var live = Site();
        var desired = live.Clone();
        desired.Collections["firewall_zone"][0]["network_ids"] = new JsonArray("n-home");
        desired.Collections["firewall_zone"].Add(Zone("z-iot", "IoT", null, "n-iot"));
        desired.Get("networkconf").First(n => n["_id"]!.ToString() == "n-iot")["dhcpd_dns_enabled"] = true;
        desired.Get("networkconf").First(n => n["_id"]!.ToString() == "n-iot")["dhcpd_dns_1"] = "10.0.53.10";
        live.Get("networkconf").First(n => n["_id"]!.ToString() == "n-iot")["dhcpd_dns_enabled"] = true;
        live.Get("networkconf").First(n => n["_id"]!.ToString() == "n-iot")["dhcpd_dns_1"] = "10.0.53.10";

        var doc = PlanBuilder.Build(desired, live, new PlanOptions { Collections = new HashSet<string> { "firewall_zone", "firewall_policy" }, DeleteMissing = false });

        doc.Lockouts.Should().ContainSingle(l => l.Title == "DNS stops resolving on IoT");
    }

    [Fact]
    public void PathCheck_SkipsASubnetMove()
    {
        var live = Site();
        var desired = live.Clone();
        desired.Get("networkconf").First(n => n["_id"]!.ToString() == "n-home")["ip_subnet"] = "10.0.2.1/24";
        desired.Get("firewall_policy").First(p => p["_id"]!.ToString() == "pihole")["enabled"] = false;

        var doc = PlanBuilder.Build(desired, live, new PlanOptions { Collections = new HashSet<string> { "networkconf", "firewall_policy" }, DeleteMissing = false });

        doc.Lockouts.Should().NotContain(l => l.Title.StartsWith("DNS stops resolving"), "Re-address plans are compared at different addresses");
    }

    [Fact]
    public void Resolvers_PreferADnatRedirect()
    {
        var s = Site();
        var home = s.Get("networkconf")[0];
        home["dhcpd_dns_enabled"] = true;
        home["dhcpd_dns_1"] = "1.1.1.1";
        s.Collections["nat"] =
        [
            new JsonObject
            {
                ["_id"] = "dnat", ["type"] = "DNAT", ["enabled"] = true, ["in_interface"] = "n-home", ["ip_address"] = "10.0.53.10",
                ["destination_filter"] = new JsonObject { ["filter_type"] = "ADDRESS_AND_PORT", ["port"] = "53" },
            },
        ];

        LockoutChecker.Resolvers(s, home).Should().Equal("10.0.53.10");
        s.Collections["nat"].Clear();
        LockoutChecker.Resolvers(s, home).Should().Equal("1.1.1.1");
        home["dhcpd_dns_enabled"] = false;
        LockoutChecker.Resolvers(s, home).Should().Equal("10.0.1.1");
    }
}
