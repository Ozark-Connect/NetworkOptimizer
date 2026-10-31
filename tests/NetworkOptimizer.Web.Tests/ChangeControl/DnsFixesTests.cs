using System.Text.Json.Nodes;
using FluentAssertions;
using NetworkOptimizer.Audit;
using NetworkOptimizer.Web.Services.ChangeControl;
using Xunit;

namespace NetworkOptimizer.Web.Tests.ChangeControl;

/// <summary>Fix it for me on the four DNS shapes seen on real sites.</summary>
public class DnsFixesTests
{
    private static JsonObject Net(string id, string name, int? vlan, string subnet, string purpose = "corporate")
    {
        var n = new JsonObject { ["_id"] = id, ["name"] = name, ["ip_subnet"] = subnet, ["purpose"] = purpose, ["enabled"] = true };
        if (vlan != null) { n["vlan"] = vlan.Value; n["vlan_enabled"] = true; }
        return n;
    }

    private static JsonObject Zone(string id, string name, string? key, params string[] networks) => new()
    {
        ["_id"] = id, ["name"] = name, ["zone_key"] = key, ["network_ids"] = new JsonArray(networks.Select(n => (JsonNode?)n).ToArray()),
    };

    private static JsonObject Block(string id, string name, string srcZone, string extZone, string port) => new()
    {
        ["_id"] = id, ["name"] = name, ["action"] = "BLOCK", ["enabled"] = true, ["predefined"] = false, ["index"] = 10000, ["protocol"] = "tcp_udp",
        ["ip_version"] = "BOTH", ["connection_state_type"] = "ALL",
        ["source"] = new JsonObject { ["zone_id"] = srcZone, ["matching_target"] = "ANY", ["port_matching_type"] = "ANY" },
        ["destination"] = new JsonObject { ["zone_id"] = extZone, ["matching_target"] = "ANY", ["port_matching_type"] = "SPECIFIC", ["port"] = port },
    };

    private static JsonObject Predefined(string src, string dst, string action) => new()
    {
        ["_id"] = $"pre-{src}-{dst}", ["name"] = "default", ["action"] = action, ["enabled"] = true, ["predefined"] = true, ["index"] = 2147483647L,
        ["protocol"] = "all", ["ip_version"] = "BOTH", ["connection_state_type"] = "ALL",
        ["source"] = new JsonObject { ["zone_id"] = src, ["matching_target"] = "ANY" },
        ["destination"] = new JsonObject { ["zone_id"] = dst, ["matching_target"] = "ANY" },
    };

    private static JsonObject NetworkDnat(string id, string name, string network, string excludeAddress, string target) => new()
    {
        ["_id"] = id, ["description"] = name, ["type"] = "DNAT", ["enabled"] = true, ["protocol"] = "tcp_udp", ["ip_version"] = "IPV4",
        ["in_interface"] = network, ["ip_address"] = target, ["rule_index"] = 1,
        ["source_filter"] = new JsonObject { ["filter_type"] = "NETWORK_CONF", ["network_conf_id"] = network, ["firewall_group_ids"] = new JsonArray(), ["invert_address"] = false },
        ["destination_filter"] = new JsonObject { ["filter_type"] = "ADDRESS_AND_PORT", ["address"] = excludeAddress, ["port"] = "53", ["invert_address"] = true, ["firewall_group_ids"] = new JsonArray() },
    };

    private static ConfigState State(List<JsonObject> networks, List<JsonObject> zones, List<JsonObject> policies, List<JsonObject>? nat = null)
    {
        var s = new ConfigState();
        s.Collections["networkconf"] = networks;
        s.Collections["firewall_zone"] = zones;
        s.Collections["firewall_policy"] = policies;
        s.Collections["nat"] = nat ?? [];
        s.Collections["firewallgroup"] = [];
        return s;
    }

    private static List<JsonObject> AllowOut(params string[] zones) =>
        zones.SelectMany(z => new[] { Predefined(z, "z-ext", "ALLOW") }).ToList();

    /// <summary>A LAN resolver in its own zone, blocks only on Internal, one custom zone uncovered.</summary>
    private static ConfigState PiHoleSite()
    {
        var networks = new List<JsonObject>
        {
            Net("n-home", "Home", null, "10.0.1.1/24"), Net("n-iot", "IoT", 64, "10.0.64.1/24"),
            Net("n-zt", "Isolated IoT", 200, "10.0.200.1/24"), Net("n-dns", "DNS Servers", 53, "10.0.53.1/24"),
        };
        var zones = new List<JsonObject>
        {
            Zone("z-int", "Internal", "internal", "n-home", "n-iot"), Zone("z-zt", "Isolated", null, "n-zt"),
            Zone("z-dns", "DNS Servers", null, "n-dns"), Zone("z-ext", "External", "external"), Zone("z-gw", "Gateway", "gateway"),
        };
        var policies = new List<JsonObject> { Block("p-dot", "Block External DNS over TLS / QUIC", "z-int", "z-ext", "853") };
        policies.AddRange(AllowOut("z-int", "z-zt", "z-dns"));
        var nat = new[] { "n-home", "n-iot", "n-zt" }.Select(n => NetworkDnat($"d-{n}", $"DNS DNAT {n}", n, "10.0.53.220", "10.0.53.220")).ToList();
        return State(networks, zones, policies, nat);
    }

    [Fact]
    public void PiHoleSite_CopiesTheDotBlockToZeroTrust_AndLeavesTheResolverAlone()
    {
        var fix = DnsFixes.Build(PiHoleSite(), IssueTypes.DnsNoDotBlock)!;

        var copy = fix.Desired.Get("firewall_policy").Single(p => p["_id"]!.ToString().StartsWith("fix-"));
        copy["name"]!.ToString().Should().Be("Block External DNS over TLS / QUIC (Isolated)");
        copy["source"]!["zone_id"]!.ToString().Should().Be("z-zt");
        copy["source"]!["matching_target"]!.ToString().Should().Be("ANY", "every network in Isolated is uncovered");
        copy.ContainsKey("index").Should().BeFalse();
        fix.Desired.Get("firewall_policy").Should().NotContain(p => p["source"]!["zone_id"]!.ToString() == "z-dns" && p["_id"]!.ToString().StartsWith("fix-"));
        fix.Notes.Should().Contain(n => n.StartsWith("DNS Servers: holds the DNS resolver"));
    }

    /// <summary>A block copied by hand per zone with one zone missed, and Guest on its own.</summary>
    private static ConfigState PerZoneSite()
    {
        var networks = new List<JsonObject>
        {
            Net("n-mgmt", "Management", null, "10.0.1.1/24"), Net("n-home", "Home", 10, "10.0.10.1/24"),
            Net("n-iot", "IoT", 11, "10.0.11.1/24"), Net("n-guest", "Guest", 100, "10.0.100.1/24", "guest"),
        };
        var zones = new List<JsonObject>
        {
            Zone("z-int", "Internal", "internal", "n-home"), Zone("z-mgmt", "Management", null, "n-mgmt"), Zone("z-untr", "Untrusted", null, "n-iot"),
            Zone("z-hot", "Hotspot", "hotspot", "n-guest"), Zone("z-ext", "External", "external"), Zone("z-gw", "Gateway", "gateway"),
        };
        var policies = new List<JsonObject>
        {
            Block("p-home", "[DNS] Block DoQ / DoT from Home", "z-int", "z-ext", "853"),
            Block("p-iot", "[DNS] Block DoQ / DoT from IoT", "z-untr", "z-ext", "853"),
        };
        policies.AddRange(AllowOut("z-int", "z-mgmt", "z-untr", "z-hot"));
        var nat = new List<JsonObject>
        {
            NetworkDnat("d-home", "[DNAT] Home DNS Redir", "n-home", "10.0.1.1", "10.0.1.1"),
            NetworkDnat("d-iot", "[DNAT] IoT DNS Redir", "n-iot", "10.0.1.1", "10.0.1.1"),
            NetworkDnat("d-mgmt", "[DNAT] Mgmt DNS Redir", "n-mgmt", "10.0.1.1", "10.0.1.1"),
        };
        return State(networks, zones, policies, nat);
    }

    [Fact]
    public void PerZoneSite_NamesTheCopyAfterTheOperatorsPattern_AndCoversGuest()
    {
        var fix = DnsFixes.Build(PerZoneSite(), IssueTypes.DnsNoDotBlock)!;

        var copies = fix.Desired.Get("firewall_policy").Where(p => p["_id"]!.ToString().StartsWith("fix-")).ToList();
        copies.Select(p => p["source"]!["zone_id"]!.ToString()).Should().BeEquivalentTo(["z-mgmt", "z-hot"], "guest DNS is controlled like any other network's");
        copies.Single(p => p["source"]!["zone_id"]!.ToString() == "z-mgmt")["name"]!.ToString()
            .Should().Be("[DNS] Block DoQ / DoT from Management", "a gateway resolver excludes nothing, the native network included");
    }

    [Fact]
    public void PerZoneSite_LeavesGuestAlone_WhenGuestsAreAllowedPublicDns()
    {
        var fix = DnsFixes.Build(PerZoneSite(), IssueTypes.DnsNoDotBlock, allowGuestPublicDns: true)!;

        fix.Desired.Get("firewall_policy").Where(p => p["_id"]!.ToString().StartsWith("fix-"))
            .Select(p => p["source"]!["zone_id"]!.ToString()).Should().Equal("z-mgmt");
        fix.Notes.Should().Contain("Guest: a guest network, left alone (Allow Guest / Hotspot Public DNS is on in Settings).");
    }

    [Fact]
    public void UniFisHotspotAuthorizationBlocks_DoNotCoverGuest()
    {
        // Post-Authorization Restrictions and Block Unauthorized Traffic hold only until a guest signs in; an authorized
        // guest goes on to Allow All Traffic, so they are no DoT block for Guest.
        var site = PerZoneSite();
        var postAuth = Predefined("z-hot", "z-ext", "BLOCK");
        postAuth["_id"] = "pre-hot-postauth";
        postAuth["index"] = 30002L;
        site.Collections["firewall_policy"].Add(postAuth);
        site.Collections["firewall_policy"].Add(Block("p-mgmt", "[DNS] Block DoQ / DoT from Management", "z-mgmt", "z-ext", "853"));

        var fix = DnsFixes.Build(site, IssueTypes.DnsNoDotBlock)!;

        fix.Desired.Get("firewall_policy").Where(p => p["_id"]!.ToString().StartsWith("fix-"))
            .Select(p => p["source"]!["zone_id"]!.ToString()).Should().Equal("z-hot");
        fix.Desired.Get("firewall_policy").Should().Contain(p => p["_id"]!.ToString() == "pre-hot-postauth", "the fix writes onto the config as it is");
    }

    [Fact]
    public void NoDohBlock_UsesUniFisShape_PerZone()
    {
        var fix = DnsFixes.Build(PerZoneSite(), IssueTypes.DnsNoDohBlock, ["dns.google", "cloudflare-dns.com"])!;

        var built = fix.Desired.Get("firewall_policy").Where(p => p["_id"]!.ToString().StartsWith("fix-new-")).ToList();
        built.Select(p => p["source"]!["zone_id"]!.ToString()).Should().BeEquivalentTo(["z-int", "z-mgmt", "z-untr", "z-hot"]);
        built.Should().OnlyContain(p => p["destination"]!["matching_target"]!.ToString() == "WEB" && p["destination"]!["port"]!.ToString() == "443");
        built.Select(p => p["name"]!.ToString()).Should().Contain("Block External DoH and QUIC (Management)");
    }

    [Fact]
    public void Doh_SkipsAZoneWhoseNetworksHaveInternetAccessOff()
    {
        var site = PerZoneSite();
        site.Get("networkconf").First(n => n["_id"]!.ToString() == "n-mgmt")["internet_access_enabled"] = false;

        var fix = DnsFixes.Build(site, IssueTypes.DnsNoDohBlock, ["dns.google", "cloudflare-dns.com"])!;

        fix.Desired.Get("firewall_policy").Where(p => p["_id"]!.ToString().StartsWith("fix-new-"))
            .Select(p => p["source"]!["zone_id"]!.ToString()).Should().BeEquivalentTo(["z-int", "z-untr", "z-hot"]);
    }

    /// <summary>Per-network DNAT naming its own network three times, a new network uncovered.</summary>
    [Fact]
    public void DnatCopy_RewritesEveryPlaceTheTemplateNamesItsNetwork()
    {
        var networks = new List<JsonObject>
        {
            Net("n-def", "Default", null, "10.0.1.1/24"), Net("n-iot", "IoT", 30, "10.0.30.1/24"), Net("n-cam", "Cams", 40, "10.0.40.1/24"),
        };
        var zones = new List<JsonObject>
        {
            Zone("z-int", "Internal", "internal", "n-def", "n-cam"), Zone("z-iot", "IoT", null, "n-iot"),
            Zone("z-ext", "External", "external"), Zone("z-gw", "Gateway", "gateway"),
        };
        var nat = new List<JsonObject>
        {
            NetworkDnat("d-def", "[DNAT] DNS Redir Home", "n-def", "10.0.1.1", "10.0.1.1"),
            NetworkDnat("d-iot", "[DNAT] DNS Redir IoT", "n-iot", "10.0.30.1", "10.0.1.1"),
        };
        var site = State(networks, zones, AllowOut("z-int", "z-iot"), nat);

        var fix = DnsFixes.Build(site, IssueTypes.DnsDnatPartialCoverage)!;

        var copy = fix.Desired.Get("nat").Single(r => r["_id"]!.ToString() == "fix-dnat-n-cam");
        copy["description"]!.ToString().Should().Be("[DNAT] DNS Redir Cams", "the template is the non-native IoT rule");
        copy["in_interface"]!.ToString().Should().Be("n-cam");
        copy["source_filter"]!["network_conf_id"]!.ToString().Should().Be("n-cam");
        copy["destination_filter"]!["address"]!.ToString().Should().Be("10.0.40.1", "the template excluded its own network's gateway");
        copy["ip_address"]!.ToString().Should().Be("10.0.1.1", "the native gateway resolves for every network");
        copy.ContainsKey("rule_index").Should().BeFalse();
    }

    [Fact]
    public void Dnat_NotBuiltFromNothing()
    {
        var site = PiHoleSite();
        site.Collections["nat"].Clear();

        DnsFixes.Build(site, IssueTypes.DnsDnatPartialCoverage).Should().BeNull("the redirect target is the operator's choice");
    }

    [Fact]
    public void Port53_LeavesANetworkWithAPublicResolverAlone()
    {
        var site = PiHoleSite();
        site.Collections["nat"].RemoveAll(r => r["in_interface"]!.ToString() == "n-zt");
        var zt = site.Get("networkconf").First(n => n["_id"]!.ToString() == "n-zt");
        zt["dhcpd_dns_enabled"] = true;
        zt["dhcpd_dns_1"] = "198.51.100.53";

        DnsFixes.Build(site, IssueTypes.Dns53PartialCoverage).Should().BeNull();
    }

    [Theory]
    [InlineData(IssueTypes.DnsNoDotBlock)]
    [InlineData(IssueTypes.DnsNoDoqBlock)]
    public void ANetworkWithInternetAccessOff_GetsNoDotOrDoqCopy(string issue)
    {
        // UniFi's own "Block <subnet> Internet Access" policy keeps it off the Internet; no copy for its zone.
        var site = PerZoneSite();
        site.Get("networkconf").First(n => n["_id"]!.ToString() == "n-mgmt")["internet_access_enabled"] = false;

        DnsFixes.Build(site, issue, allowGuestPublicDns: true).Should().BeNull("Management, the only network without a block, has Internet Access off");
    }

    [Fact]
    public void Port53_LeavesANetworkWithInternetAccessOffAlone()
    {
        var site = PiHoleSite();
        site.Collections["nat"].RemoveAll(r => r["in_interface"]!.ToString() == "n-zt");
        site.Get("networkconf").First(n => n["_id"]!.ToString() == "n-zt")["internet_access_enabled"] = false;

        DnsFixes.Build(site, IssueTypes.Dns53PartialCoverage).Should().BeNull();
    }

    [Fact]
    public void AZoneWithOneNetworkOffline_ListsOnlyTheOneThatIsOnline()
    {
        var site = PerZoneSite();
        var cams = Net("n-cams", "Cameras", 12, "10.0.12.1/24");
        cams["internet_access_enabled"] = false;
        site.Collections["networkconf"].Add(cams);
        site.Get("firewall_zone").First(z => z["_id"]!.ToString() == "z-mgmt")["network_ids"]!.AsArray().Add("n-cams");

        var fix = DnsFixes.Build(site, IssueTypes.DnsNoDotBlock, allowGuestPublicDns: true)!;

        var copy = fix.Desired.Get("firewall_policy").Single(p => p["_id"]!.ToString().StartsWith("fix-"));
        copy["source"]!["matching_target"]!.ToString().Should().Be("NETWORK");
        copy["source"]!["network_ids"]!.AsArray().Select(n => n!.ToString()).Should().Equal("n-mgmt");
    }

    [Fact]
    public void FullyCovered_HasNothingToFix() =>
        DnsFixes.Build(PerZoneSite(), IssueTypes.DnsDnatPartialCoverage, allowGuestPublicDns: true).Should().BeNull("Guest is out of scope and the rest are redirected");

    [Fact]
    public void ThePlan_WritesOnlyTheFix()
    {
        var live = PiHoleSite();
        var fix = DnsFixes.Build(live, IssueTypes.DnsNoDotBlock)!;

        var doc = PlanBuilder.Build(fix.Desired, live, new PlanOptions { Collections = new HashSet<string> { "firewall_policy", "nat", "firewallgroup" }, DeleteMissing = false });

        doc.Errors.Should().BeEmpty();
        doc.Operations.Where(o => o.Collection == "firewall_policy" && o.Action != PlanAction.Reorder).Should().ContainSingle(o => o.Action == PlanAction.Create);
    }

    [Theory]
    [InlineData("[DNS] Block DoQ / DoT from Home", "Internal", "Management", "[DNS] Block DoQ / DoT from Management")]
    [InlineData("Block External DNS over TLS / QUIC", "Internal", "Isolated", "Block External DNS over TLS / QUIC (Isolated)")]
    public void CopyName_FollowsThePattern(string template, string zone, string to, string expected) =>
        DnsFixes.CopyName(template, zone, ["Home"], to).Should().Be(expected);
}
