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

    /// <summary>
    /// One policy ahead of Block All in Home to DNS Servers, shaped by <paramref name="shape"/>: whether Home reaches the
    /// Pi-hole on UDP 53. Every matching rule of the zone-based firewall, judged by the Audit's engine.
    /// </summary>
    [Theory]
    // Destination port
    [InlineData("port 53", true)]
    [InlineData("port 853", false)]
    [InlineData("port range 50-60", true)]
    [InlineData("port 53 opposite", false)]
    [InlineData("port group with 53", true)]
    [InlineData("port group missing", false)]
    // Destination address
    [InlineData("dst ip", true)]
    [InlineData("dst ip range", true)]
    [InlineData("dst ip cidr", true)]
    [InlineData("dst other ip", false)]
    [InlineData("dst other ip opposite", true)]
    [InlineData("dst ip group", true)]
    [InlineData("dst network", true)]
    [InlineData("dst network opposite", false)]
    // Source
    [InlineData("src network", true)]
    [InlineData("src other network", false)]
    [InlineData("src other network opposite", true)]
    [InlineData("src some hosts", false)]
    [InlineData("src whole subnet", true)]
    [InlineData("src all but some hosts", true)]
    [InlineData("src all but the subnet", false)]
    [InlineData("src client", false)]
    [InlineData("src port", false)]
    // Protocol and family
    [InlineData("tcp", false)]
    [InlineData("udp", true)]
    [InlineData("all but tcp", true)]
    [InlineData("all but udp", false)]
    [InlineData("ipv6 only", false)]
    // State and order
    [InlineData("disabled", false)]
    [InlineData("return only", false)]
    [InlineData("established only", false)]
    [InlineData("invalid-only block, allow after", true)]
    [InlineData("no index", true)]
    public void EveryMatchingRule_IsJudgedByTheAudit(string shape, bool reaches)
    {
        var s = Site();
        s.Collections["firewallgroup"] =
        [
            new JsonObject { ["_id"] = "g-dns", ["name"] = "DNS", ["group_type"] = "port-group", ["group_members"] = new JsonArray("53", "853") },
            new JsonObject { ["_id"] = "g-pihole", ["name"] = "Pi-hole", ["group_type"] = "address-group", ["group_members"] = new JsonArray("10.0.53.10") },
        ];
        var policies = s.Collections["firewall_policy"];
        policies.RemoveAll(p => p["_id"]!.ToString() == "pihole");
        var p = Policy("test", "ALLOW", Side("z-int"), Side("z-dns"), 10000, protocol: "tcp_udp");
        var src = (JsonObject)p["source"]!;
        var dst = (JsonObject)p["destination"]!;
        void Ports(string ports, bool opposite = false) { dst["port_matching_type"] = "SPECIFIC"; dst["port"] = ports; dst["match_opposite_ports"] = opposite; }
        void DstIps(bool opposite, params string[] ips) { dst["matching_target"] = "IP"; dst["ips"] = new JsonArray(ips.Select(i => (JsonNode?)i).ToArray()); dst["match_opposite_ips"] = opposite; }
        void SrcIps(bool opposite, params string[] ips) { src["matching_target"] = "IP"; src["ips"] = new JsonArray(ips.Select(i => (JsonNode?)i).ToArray()); src["match_opposite_ips"] = opposite; }
        void Networks(JsonObject side, bool opposite, string id) { side["matching_target"] = "NETWORK"; side["network_ids"] = new JsonArray(id); side["match_opposite_networks"] = opposite; }
        switch (shape)
        {
            case "port 53": Ports("53"); break;
            case "port 853": Ports("853"); break;
            case "port range 50-60": Ports("50-60"); break;
            case "port 53 opposite": Ports("53", opposite: true); break;
            case "port group with 53": dst["port_matching_type"] = "OBJECT"; dst["port_group_id"] = "g-dns"; break;
            case "port group missing": dst["port_matching_type"] = "OBJECT"; dst["port_group_id"] = "g-gone"; break;
            case "dst ip": DstIps(false, "10.0.53.10"); break;
            case "dst ip range": DstIps(false, "10.0.53.5-10.0.53.20"); break;
            case "dst ip cidr": DstIps(false, "10.0.53.0/28"); break;
            case "dst other ip": DstIps(false, "10.0.53.99"); break;
            case "dst other ip opposite": DstIps(true, "10.0.53.99"); break;
            case "dst ip group": dst["matching_target"] = "IP"; dst["matching_target_type"] = "OBJECT"; dst["ip_group_id"] = "g-pihole"; break;
            case "dst network": Networks(dst, false, "n-dns"); break;
            case "dst network opposite": Networks(dst, true, "n-dns"); break;
            case "src network": Networks(src, false, "n-home"); break;
            case "src other network": Networks(src, false, "n-iot"); break;
            case "src other network opposite": Networks(src, true, "n-iot"); break;
            case "src some hosts": SrcIps(false, "10.0.1.50"); break;
            case "src whole subnet": SrcIps(false, "10.0.1.0/24"); break;
            case "src all but some hosts": SrcIps(true, "10.0.1.50"); break;
            case "src all but the subnet": SrcIps(true, "10.0.1.0/24"); break;
            case "src client": src["matching_target"] = "CLIENT"; src["client_macs"] = new JsonArray("aa:bb:cc:dd:ee:ff"); break;
            case "src port": src["port_matching_type"] = "SPECIFIC"; src["port"] = "5353"; break;
            case "tcp": p["protocol"] = "tcp"; break;
            case "udp": p["protocol"] = "udp"; break;
            case "all but tcp": p["protocol"] = "tcp"; p["match_opposite_protocol"] = true; break;
            case "all but udp": p["protocol"] = "udp"; p["match_opposite_protocol"] = true; break;
            case "ipv6 only": p["ip_version"] = "IPV6"; break;
            case "disabled": p["enabled"] = false; break;
            case "return only": p["connection_state_type"] = "RESPOND_ONLY"; break;
            case "established only": p["connection_state_type"] = "CUSTOM"; p["connection_states"] = new JsonArray("ESTABLISHED"); break;
            case "invalid-only block, allow after":
                var invalid = Policy("invalid", "BLOCK", Side("z-int"), Side("z-dns"), 9999);
                invalid["connection_state_type"] = "CUSTOM";
                invalid["connection_states"] = new JsonArray("INVALID");
                policies.Add(invalid);
                break;
            case "no index": p.Remove("index"); break;
            default: throw new ArgumentException(shape);
        }
        policies.Add(p);

        ZonePaths.Allows(s, "n-home", "10.0.53.10", "udp", 53).Should().Be(reaches, shape);
    }

    [Fact]
    public void Decides_ReturnsThePolicyObjectThatDecided()
    {
        var s = Site();
        ZonePaths.Decides(s, "n-home", "10.0.53.10", "udp", 53).Should().BeSameAs(s.Get("firewall_policy").Single(p => p["_id"]!.ToString() == "pihole"));
        ZonePaths.Decides(s, "n-home", "10.0.53.10", "tcp", 443)!["_id"]!.ToString().Should().Be("int-dns");
        ZonePaths.Allows(s, "n-unknown", "10.0.53.10", "udp", 53).Should().BeNull("a network in no zone cannot be told");
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
                ["_id"] = "dnat", ["type"] = "DNAT", ["enabled"] = true, ["protocol"] = "tcp_udp", ["in_interface"] = "n-home", ["ip_address"] = "10.0.53.10",
                ["destination_filter"] = new JsonObject { ["filter_type"] = "ADDRESS_AND_PORT", ["port"] = "53" },
            },
        ];

        LockoutChecker.Resolvers(s, home).Should().Equal("10.0.53.10");
        s.Collections["nat"].Clear();
        LockoutChecker.Resolvers(s, home).Should().Equal("1.1.1.1");
        home["dhcpd_dns_enabled"] = false;
        LockoutChecker.Resolvers(s, home).Should().Equal("10.0.1.1");
    }

    /// <summary>Home's DNS is redirected only by what the DNS audit counts as a DNS redirect.</summary>
    [Theory]
    [InlineData("tcp_udp", "53", true)]
    [InlineData("udp", "50-60", true)]
    [InlineData("tcp", "53", false)]
    [InlineData("tcp_udp", "443", false)]
    [InlineData("tcp_udp", null, false)]
    public void Resolvers_FollowOnlyTheAuditsDnsRedirects(string protocol, string? port, bool redirected)
    {
        var s = Site();
        var home = s.Get("networkconf")[0];
        home["dhcpd_dns_enabled"] = true;
        home["dhcpd_dns_1"] = "1.1.1.1";
        var filter = new JsonObject { ["filter_type"] = "ADDRESS_AND_PORT" };
        if (port != null) filter["port"] = port;
        s.Collections["nat"] =
        [
            new JsonObject
            {
                ["_id"] = "dnat", ["type"] = "DNAT", ["enabled"] = true, ["protocol"] = protocol, ["in_interface"] = "n-home", ["ip_address"] = "10.0.53.10",
                ["destination_filter"] = filter,
            },
        ];

        LockoutChecker.Resolvers(s, home).Should().Equal(redirected ? "10.0.53.10" : "1.1.1.1");
    }

    [Fact]
    public void Holds_ComparesStoredFields_NotNamesOrIds()
    {
        var a = Policy("a", "BLOCK", Side("z-int"), Side("z-ext", "53"), 10000, protocol: "udp");
        var b = (JsonObject)a.DeepClone();
        b["_id"] = "b"; b["name"] = "Other name"; b["index"] = 10001; b["description"] = "notes";
        b["report_flow"] = true;

        ZonePaths.Holds(b, a).Should().BeTrue("name, ID, order, and description aside, and an extra stored field is fine");
        b["destination"]!["port"] = "853";
        ZonePaths.Holds(b, a).Should().BeFalse();
        b["destination"]!["port"] = "53";
        b["enabled"] = false;
        ZonePaths.Holds(b, a).Should().BeFalse("a disabled twin does nothing");
        b["enabled"] = true;
        b["predefined"] = true;
        ZonePaths.Holds(b, a).Should().BeFalse("UniFi's own policies are never a match");
    }

    [Fact]
    public void DnsCoverage_IsTheAuditsCoverage()
    {
        var s = Site();
        foreach (var n in s.Get("networkconf")) n["internet_access_enabled"] = true;
        s.Collections["firewall_policy"].Add(Policy("dot", "BLOCK", Side("z-int"), Side("z-ext", "853"), 10001, protocol: "tcp_udp"));
        s.Collections["nat"] = [];

        var coverage = ZonePaths.DnsCoverage(s, allowGuestPublicDns: false);

        coverage.DotCoveredNetworkIds.Should().BeEquivalentTo(["n-home", "n-iot"], "the DoT block covers Internal's networks");
        coverage.Dns53CoveredNetworkIds.Should().BeEmpty();
    }
}
