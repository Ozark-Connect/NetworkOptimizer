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
        Policy(desired, "IoT", "Internal").Should().BeNull("UniFi blocks a custom zone from Internal by default");
        Policy(desired, "IoT", "Cameras").Should().BeNull("and from every other custom zone");
        var casting = Policy(desired, "Internal", "IoT")!;
        casting["name"]!.ToString().Should().Be("Casting and mDNS", "the exception is on by default");
        casting["protocol"]!.ToString().Should().Be("tcp_udp", "an any-protocol allow is the audit's any-to-any rule");
        casting["destination"]!["port_matching_type"]!.ToString().Should().Be("SPECIFIC");
        casting["destination"]!["port"]!.ToString().Should().Contain("8008-8009").And.Contain("49152-65535");
        Policy(desired, "Internal", "Cameras").Should().BeNull("NVR access is off by default: Protect on the gateway needs none");
        Policy(desired, "IoT", "IoT")!["name"]!.ToString().Should().Be("Allow IoT within IoT", "UniFi blocks networks sharing a custom zone");
        var gatewayBlock = Policy(desired, "IoT", "Gateway")!;
        gatewayBlock["action"]!.ToString().Should().Be("BLOCK");
        gatewayBlock["protocol"]!.ToString().Should().Be("tcp");
        gatewayBlock["destination"]!["port"]!.ToString().Should().Be("22,80,443");
        Policy(desired, "Internal", "Gateway").Should().BeNull("Internal keeps the console's management");
        Policy(desired, "Cameras", "External")!["action"]!.ToString().Should().Be("BLOCK");
        var lanZones = new[] { "z-int", "new-zone-iot", "new-zone-cameras" };
        desired.Get("firewall_policy").Should().NotContain(p => p["action"]!.ToString() == "BLOCK"
            && lanZones.Contains(p["destination"]!["zone_id"]!.ToString()), "no block between LAN zones");
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
        Policy(desired, "Internal", "IoT").Should().BeNull("with casting off, UniFi's Block All stands");
    }

    [Fact]
    public void Plan_CreatesZonesBeforeThePoliciesThatUseThem()
    {
        var live = Live();
        var desired = ZoneSetup.Desired(live, ZoneSetup.Build(live, Networks, ZoneSetup.TrustMode));

        var doc = PlanBuilder.Build(desired, live, ZoneSetup.Options());

        var zoneCreate = doc.Operations.Single(o => o.Collection == "firewall_zone" && o.Action == PlanAction.Create && o.ObjectName == "IoT");
        var iotPolicy = doc.Operations.First(o => o.Collection == "firewall_policy" && o.Action == PlanAction.Create && o.ObjectName == "Block IoT to Gateway Management");
        iotPolicy.Body!["source"]!["zone_id"]!.ToString().Should().Be(PlanBuilder.RefPrefix + zoneCreate.Key);
        doc.Operations.IndexOf(zoneCreate).Should().BeLessThan(doc.Operations.IndexOf(iotPolicy));
        iotPolicy.Body!.AsObject().ContainsKey("index").Should().BeFalse("UniFi ignores it on a create");
        doc.Operations.Should().NotContain(o => o.Action == PlanAction.Delete);
    }

    [Fact]
    public void Plan_WritesTheZoneGivingUpANetworkAfterTheZoneTakingIt()
    {
        var live = Live();
        var desired = ZoneSetup.Desired(live, ZoneSetup.Build(live, Networks, ZoneSetup.TrustMode));

        var doc = PlanBuilder.Build(desired, live, ZoneSetup.Options());

        int At(PlanAction action, string name) => doc.Operations.FindIndex(o => o.Collection == "firewall_zone" && o.Action == action && o.ObjectName == name);
        var internalUpdate = At(PlanAction.Update, "Internal");
        internalUpdate.Should().BeGreaterThan(At(PlanAction.Create, "IoT"), "UniFi refuses to leave a network in no zone");
        internalUpdate.Should().BeGreaterThan(At(PlanAction.Create, "Cameras"));
    }

    [Fact]
    public void RollBack_DeletesAZoneWhoseNetworksThePlanRehomes()
    {
        var before = Live();
        var after = Live();
        after.Collections["firewall_zone"] =
        [
            Zone("z-int", "Internal", "internal", "n-home", "n-work", "n-cam", "n-print"),
            Zone("z-iot", "IoT", null, "n-iot", "n-media"),
            Zone("z-ext", "External", "external", "n-wan"),
            Zone("z-gw", "Gateway", "gateway"),
            Zone("z-hot", "Hotspot", "hotspot", "n-guest"),
            Zone("z-dmz", "Dmz", "dmz"),
        ];
        // UniFi mirrors membership onto the network.
        foreach (var n in after.Get("networkconf").Where(n => n["_id"]!.ToString() is "n-iot" or "n-media"))
            n["firewall_zone_id"] = "z-iot";

        var doc = PlanBuilder.Build(before, after, new PlanOptions { Collections = new HashSet<string> { "firewall_zone", "firewall_policy" } });

        doc.Errors.Should().BeEmpty();
        var delete = doc.Operations.FindIndex(o => o.Action == PlanAction.Delete && o.ObjectName == "IoT");
        delete.Should().BeGreaterThan(doc.Operations.FindIndex(o => o.Action == PlanAction.Update && o.ObjectName == "Internal"));
    }

    [Fact]
    public void ZoneDelete_StillRefusedWhileANetworkHasNoNewZone()
    {
        var live = Live();
        live.Collections["firewall_zone"].Add(Zone("z-iot", "IoT", null, "n-iot"));
        live.Get("networkconf").First(n => n["_id"]!.ToString() == "n-iot")["firewall_zone_id"] = "z-iot";
        var desired = live.Clone();
        desired.Collections["firewall_zone"] = desired.Get("firewall_zone").Where(z => z["_id"]!.ToString() != "z-iot").ToList();

        var doc = PlanBuilder.Build(desired, live, new PlanOptions { Collections = new HashSet<string> { "firewall_zone" } });

        doc.Errors.Should().ContainSingle(e => e.Contains("Network IoT"));
    }

    private static ConfigState WithSubnetsAndGateway()
    {
        var live = Live();
        live.Collections["firewall_zone"].Add(Zone("z-gw2", "Gateway", "gateway"));
        live.Collections["firewall_zone"].RemoveAll(z => z["_id"]!.ToString() == "z-gw");
        foreach (var n in live.Get("networkconf"))
            n["ip_subnet"] = $"10.0.{n["vlan"]}.1/24";
        return live;
    }

    [Fact]
    public void GatewayBlock_FromANewZoneWithoutOurNetwork_IsNoLockout()
    {
        var live = WithSubnetsAndGateway();
        var desired = ZoneSetup.Desired(live, ZoneSetup.Build(live, Networks, ZoneSetup.TrustMode));

        var doc = PlanBuilder.Build(desired, live, new PlanOptions
        {
            Collections = new HashSet<string> { "firewall_zone", "firewall_policy" }, DeleteMissing = false,
            ManagementAddresses = ["10.0.1.50"],
        });

        doc.Lockouts.Should().NotContain(l => l.Title.Contains("Block IoT to Gateway Management"));
    }

    [Fact]
    public void GatewayBlock_StillALockout_WhenOurAddressIsUnknown()
    {
        var live = WithSubnetsAndGateway();
        var desired = ZoneSetup.Desired(live, ZoneSetup.Build(live, Networks, ZoneSetup.TrustMode));

        var doc = PlanBuilder.Build(desired, live, new PlanOptions { Collections = new HashSet<string> { "firewall_zone", "firewall_policy" }, DeleteMissing = false });

        doc.Lockouts.Should().Contain(l => l.Title.Contains("Block IoT to Gateway Management"));
    }

    [Fact]
    public void GatewayBlock_StillALockout_WhenTheNewZoneHoldsOurNetwork()
    {
        var live = WithSubnetsAndGateway();
        var desired = ZoneSetup.Desired(live, ZoneSetup.Build(live, Networks, ZoneSetup.TrustMode));

        var doc = PlanBuilder.Build(desired, live, new PlanOptions
        {
            Collections = new HashSet<string> { "firewall_zone", "firewall_policy" }, DeleteMissing = false,
            ManagementAddresses = ["10.0.30.50"],
        });

        doc.Lockouts.Should().Contain(l => l.Title.Contains("Block IoT to Gateway Management"));
    }

    private static JsonObject Side(string zone, string target = "ANY", string[]? networks = null, string[]? ips = null, string? port = null)
    {
        var side = new JsonObject { ["zone_id"] = zone, ["matching_target"] = target, ["port_matching_type"] = port == null ? "ANY" : "SPECIFIC" };
        if (port != null) side["port"] = port;
        if (networks != null) side["network_ids"] = new JsonArray(networks.Select(n => (JsonNode?)n).ToArray());
        if (ips != null) side["ips"] = new JsonArray(ips.Select(n => (JsonNode?)n).ToArray());
        if (target == "CLIENT") side["client_macs"] = new JsonArray("aa:bb:cc:dd:ee:ff");
        return side;
    }

    private static JsonObject Rule(string id, string name, string action, JsonObject src, JsonObject dst, long index = 10000, string protocol = "all") => new()
    {
        ["_id"] = id, ["name"] = name, ["action"] = action, ["enabled"] = true, ["predefined"] = false, ["index"] = index,
        ["protocol"] = protocol, ["ip_version"] = "BOTH", ["connection_state_type"] = "ALL", ["source"] = src, ["destination"] = dst,
    };

    private static JsonObject Predefined(string src, string dst, string action) => new()
    {
        ["_id"] = $"pre-{src}-{dst}", ["name"] = action == "ALLOW" ? "Allow All Traffic" : "Block All Traffic", ["action"] = action, ["enabled"] = true,
        ["predefined"] = true, ["index"] = 2147483647L, ["protocol"] = "all", ["ip_version"] = "BOTH", ["connection_state_type"] = "ALL",
        ["source"] = Side(src), ["destination"] = Side(dst),
    };

    private static ZonePlan PlanFor(ConfigState live, IReadOnlyList<string>? inform = null, IReadOnlyDictionary<string, bool>? exceptions = null) =>
        ZoneSetup.Plan(live, ZoneSetup.Build(live, Networks, ZoneSetup.TrustMode, exceptions: exceptions), inform);

    private static JsonObject? Named(ConfigState s, string name) => s.Get("firewall_policy").FirstOrDefault(p => p["name"]!.ToString() == name);

    [Fact]
    public void Rehome_MovesAPolicyNamingAMovedNetwork_KeepingItsId()
    {
        var live = WithSubnetsAndGateway();
        live.Collections["firewall_policy"].Add(Rule("p-phone", "[IoT] Allow Phone Inbound Access", "ALLOW", Side("z-int", "IP", ips: ["10.0.1.20"]), Side("z-int", "NETWORK", ["n-iot"])));

        var plan = PlanFor(live);

        var moved = Named(plan.Desired, "[IoT] Allow Phone Inbound Access")!;
        moved["_id"]!.ToString().Should().Be("p-phone", "it is updated in place");
        moved["destination"]!["zone_id"]!.ToString().Should().Be("new-zone-iot");
        moved["destination"]!["network_ids"]!.AsArray().Select(n => n!.ToString()).Should().Equal(["n-iot"], "the network UniFi strips is written back");
        moved["source"]!["zone_id"]!.ToString().Should().Be("z-int");
        plan.Rehomed.Should().Contain("p-phone");
        Policy(plan.Desired, "Internal", "IoT")!["name"]!.ToString().Should().Be("[IoT] Allow Phone Inbound Access");
        plan.Notes.Should().Contain(n => n.Contains("Casting and mDNS") && n.Contains("covered by [IoT] Allow Phone Inbound Access"));
        Named(plan.Desired, "Casting and mDNS").Should().BeNull("the operator's own allow covers the pair");
    }

    [Fact]
    public void Rehome_ByAddress_FollowsTheSubnet()
    {
        var live = WithSubnetsAndGateway();
        live.Collections["firewall_policy"].Add(Rule("p-nvr", "[Security] Allow NVR Direct", "ALLOW", Side("z-int", "IP", ips: ["10.0.1.20"]), Side("z-int", "IP", ips: ["10.0.40.5"]), protocol: "tcp_udp"));

        var plan = PlanFor(live);

        Named(plan.Desired, "[Security] Allow NVR Direct")!["destination"]!["zone_id"]!.ToString().Should().Be("new-zone-cameras");
    }

    [Fact]
    public void Rehome_SplitsAPolicyWhoseNetworksLandInSeveralZones()
    {
        var live = WithSubnetsAndGateway();
        live.Collections["firewall_policy"].Add(Rule("p-pcs", "PCs Access to Sensitive VLANs", "ALLOW", Side("z-int", "IP", ips: ["10.0.1.10-10.0.1.14"]), Side("z-int", "NETWORK", ["n-iot", "n-cam"])));

        var plan = PlanFor(live);

        var parts = plan.Desired.Get("firewall_policy").Where(p => p["name"]!.ToString().StartsWith("PCs Access")).ToList();
        parts.Should().HaveCount(2);
        parts.Select(p => p["destination"]!["zone_id"]!.ToString()).Should().BeEquivalentTo(["new-zone-iot", "new-zone-cameras"]);
        parts.Should().OnlyContain(p => p["destination"]!["network_ids"]!.AsArray().Count == 1);
        parts.Should().Contain(p => p["_id"]!.ToString() == "p-pcs");
        plan.Notes.Should().Contain(n => n.Contains("split into 2 policies"));
    }

    [Fact]
    public void Rehome_DeletesABlockUniFiNowDoesItself()
    {
        var live = WithSubnetsAndGateway();
        live.Collections["firewall_policy"].Add(Rule("p-iso", "[VLAN] Block Access to Isolated VLANs", "BLOCK", Side("z-int"), Side("z-int", "NETWORK", ["n-cam"])));

        var plan = PlanFor(live);
        var doc = PlanBuilder.Build(plan.Desired, live, ZoneSetup.Options());

        Named(plan.Desired, "[VLAN] Block Access to Isolated VLANs").Should().BeNull();
        doc.Operations.Should().ContainSingle(o => o.Action == PlanAction.Delete && o.ObjectId == "p-iso");
        plan.Notes.Should().Contain(n => n.Contains("deleted") && n.Contains("by default"));
    }

    [Fact]
    public void Rehome_LeavesClientPolicies_WithANote()
    {
        var live = WithSubnetsAndGateway();
        live.Collections["firewall_policy"].Add(Rule("p-cl", "[TV] Commands", "ALLOW", Side("z-int", "CLIENT"), Side("z-int", "NETWORK", ["n-iot"])));

        var plan = PlanFor(live);

        Named(plan.Desired, "[TV] Commands")!["destination"]!["zone_id"]!.ToString().Should().Be("z-int");
        plan.Notes.Should().Contain(n => n.Contains("[TV] Commands") && n.Contains("matches clients"));
    }

    [Fact]
    public void ZoneWideBlocksOut_AreCopiedToEachNewZone_ButNotThreatIntelligence()
    {
        var live = WithSubnetsAndGateway();
        live.Collections["firewall_policy"].Add(Rule("p-dot", "Block External DNS over TLS / QUIC", "BLOCK", Side("z-int"), Side("z-ext", port: "853"), protocol: "tcp_udp"));
        live.Collections["firewall_policy"].Add(Rule("p-no", "Net. Opt. Public Lists (Outbound)", "BLOCK", Side("z-int"), Side("z-ext", "IP", ips: ["198.51.100.0/24"])));
        live.Collections["firewall_policy"].Add(Rule("p-tts", "[Security] Allow TTS HTTPS", "ALLOW", Side("z-int"), Side("z-int", "IP", ips: ["10.0.1.5"], port: "443"), protocol: "tcp"));

        var plan = PlanFor(live);

        Named(plan.Desired, "Block External DNS over TLS / QUIC (IoT)")!["source"]!["zone_id"]!.ToString().Should().Be("new-zone-iot");
        Named(plan.Desired, "Block External DNS over TLS / QUIC (Cameras)").Should().NotBeNull();
        plan.Desired.Get("firewall_policy").Count(p => p["name"]!.ToString().StartsWith("Net. Opt.")).Should().Be(1);
        plan.Notes.Should().Contain(n => n.Contains("Threat Intelligence"));
        plan.Notes.Should().Contain(n => n.Contains("[Security] Allow TTS HTTPS") && n.Contains("not carried"));
    }

    [Fact]
    public void NameFor_SwapsTheZoneName_WhenTheTemplateHasIt() =>
        ZoneSetup.NameFor("[DNS] Block DoQ / DoT from Internal", "Internal", "IoT").Should().Be("[DNS] Block DoQ / DoT from IoT");

    [Fact]
    public void MovedNetworks_KeepReachingALanResolver()
    {
        var live = WithSubnetsAndGateway();
        live.Collections["networkconf"].Add(new JsonObject { ["_id"] = "n-dns", ["name"] = "DNS Servers", ["vlan"] = 53, ["ip_subnet"] = "10.0.53.1/24" });
        live.Collections["firewall_zone"].Add(Zone("z-dns", "DNS Servers", null, "n-dns"));
        live.Collections["firewall_policy"].Add(Predefined("z-int", "z-dns", "BLOCK"));
        live.Collections["firewall_policy"].Add(Rule("p-pihole", "[DNS] VLANs to Pi-hole", "ALLOW", Side("z-int"), Side("z-dns", port: "53"), protocol: "tcp_udp"));
        foreach (var n in live.Get("networkconf").Where(n => n["_id"]!.ToString() is not "n-dns"))
        {
            n["dhcpd_dns_enabled"] = true;
            n["dhcpd_dns_1"] = "10.0.53.10";
        }

        var plan = PlanFor(live);

        Named(plan.Desired, "[DNS] VLANs to Pi-hole (IoT)")!["source"]!["zone_id"]!.ToString().Should().Be("new-zone-iot");
        Members(plan.Desired, "DNS Servers").Should().Equal(["n-dns"], "a network in the operator's own zone stays there");
        ZonePaths.Allows(plan.Desired, "n-iot", "10.0.53.10", "udp", 53).Should().BeTrue();
        ZonePaths.Allows(plan.Desired, "n-cam", "10.0.53.10", "udp", 53).Should().BeTrue();
    }

    [Fact]
    public void MovedNetworks_GetAnAllowToTheirResolver_WhenNoOperatorPolicyCarriesIt()
    {
        var live = WithSubnetsAndGateway();
        live.Collections["firewall_policy"].Add(Predefined("z-int", "z-int", "ALLOW"));
        foreach (var n in live.Get("networkconf"))
        {
            n["dhcpd_dns_enabled"] = true;
            n["dhcpd_dns_1"] = "10.0.1.53";
        }

        var plan = PlanFor(live);

        Named(plan.Desired, "Allow IoT to DNS (10.0.1.53)")!["destination"]!["port"]!.ToString().Should().Be("53");
        ZonePaths.Allows(plan.Desired, "n-iot", "10.0.1.53", "udp", 53).Should().BeTrue();
    }

    [Fact]
    public void MovedDeviceNetworks_KeepReachingTheInformHost()
    {
        var live = WithSubnetsAndGateway();
        live.Collections["firewall_policy"].Add(Predefined("z-int", "z-int", "ALLOW"));
        live.Collections["device"] = [new JsonObject { ["_id"] = "d1", ["mac"] = "00:11:22:33:44:55", ["mgmt_network_id"] = "n-cam" }];

        var plan = PlanFor(live, inform: ["10.0.1.200"]);

        Named(plan.Desired, "Allow Cameras to UniFi Console (10.0.1.200)")!["destination"]!["port"]!.ToString().Should().Be("8080,3478");
    }

    [Fact]
    public void Servers_ReachedThroughAPortGroup_TheOperatorCanExtend()
    {
        var live = WithSubnetsAndGateway();
        var withServers = Networks.Append(Net("n-srv", "Servers", 60, NetworkPurpose.Server)).ToList();
        live.Collections["networkconf"].Add(new JsonObject { ["_id"] = "n-srv", ["name"] = "Servers", ["vlan"] = 60, ["ip_subnet"] = "10.0.60.1/24" });
        live.Collections["firewall_zone"].First(z => z["_id"]!.ToString() == "z-int")["network_ids"]!.AsArray().Add("n-srv");

        var desired = ZoneSetup.Desired(live, ZoneSetup.Build(live, withServers, ZoneSetup.TrustMode));

        var allow = Policy(desired, "Internal", "Servers")!;
        allow["destination"]!["port_matching_type"]!.ToString().Should().Be("OBJECT");
        var group = desired.Get("firewallgroup").Single(g => g["_id"]!.ToString() == allow["destination"]!["port_group_id"]!.ToString());
        group["name"]!.ToString().Should().Be(ZoneSetup.ServerServicesGroup);
        group["group_members"]!.AsArray().Select(m => m!.ToString()).Should().Contain(["443", "22", "32400"]);

        // A re-run keeps the operator's edited group as it is.
        var edited = desired.Clone();
        edited.Collections["firewallgroup"] = [new JsonObject { ["_id"] = "g-srv", ["name"] = ZoneSetup.ServerServicesGroup, ["group_type"] = "port-group", ["group_members"] = new JsonArray("443", "9000") }];
        var again = ZoneSetup.Desired(edited, ZoneSetup.Build(edited, withServers, ZoneSetup.TrustMode));
        again.Get("firewallgroup").Single()["group_members"]!.AsArray().Select(m => m!.ToString()).Should().Equal("443", "9000");
        Policy(again, "Internal", "Servers")!["destination"]!["port_group_id"]!.ToString().Should().Be("g-srv");
    }

    [Fact]
    public void Cameras_WithInternetAccessOff_GetNoInternetBlock()
    {
        var live = WithSubnetsAndGateway();
        live.Get("networkconf").First(n => n["_id"]!.ToString() == "n-cam")["internet_access_enabled"] = false;

        var plan = PlanFor(live);

        Policy(plan.Desired, "Cameras", "External").Should().BeNull();
        plan.Notes.Should().Contain(n => n.Contains("Internet Access off"));
    }

    [Fact]
    public void GatewayBlock_NotDuplicated_WhenAnOperatorBlockCoversIt()
    {
        var live = WithSubnetsAndGateway();
        live.Collections["firewall_policy"].Add(Rule("p-gw", "Block UniFi Access Isolated VLANs", "BLOCK",
            Side("z-int", "NETWORK", ["n-iot", "n-media"]), Side("z-gw2", port: "22,80,443"), protocol: "tcp"));

        var plan = PlanFor(live);

        Named(plan.Desired, "Block IoT to Gateway Management").Should().BeNull();
        Named(plan.Desired, "Block UniFi Access Isolated VLANs")!["source"]!["zone_id"]!.ToString().Should().Be("new-zone-iot");
        Named(plan.Desired, "Block Cameras to Gateway Management").Should().NotBeNull("Cameras is not covered by it");
    }

    [Fact]
    public void ZonePlan_WritesRehomedPoliciesAfterTheZones_AndVerifiesEnabled()
    {
        var live = WithSubnetsAndGateway();
        live.Collections["firewall_policy"].Add(Rule("p-phone", "[IoT] Allow Phone Inbound Access", "ALLOW", Side("z-int", "IP", ips: ["10.0.1.20"]), Side("z-int", "NETWORK", ["n-iot"])));
        var plan = PlanFor(live);
        var doc = PlanBuilder.Build(plan.Desired, live, ZoneSetup.Options());

        ChangeControlService.AfterZoneWrites(doc, plan.Rehomed);

        var update = doc.Operations.Single(o => o.ObjectId == "p-phone");
        update.Action.Should().Be(PlanAction.Update);
        update.WriteFields.Should().Contain(["enabled", "source", "destination"]);
        update.Body!["enabled"]!.GetValue<bool>().Should().BeTrue("UniFi pauses it on the zone write");
        var lastZone = doc.Operations.FindLastIndex(o => o.Collection == "firewall_zone");
        doc.Operations.IndexOf(update).Should().BeGreaterThan(lastZone);
    }

    [Fact]
    public void ExistingCustomZones_KeepTheirNetworks()
    {
        var live = WithSubnetsAndGateway();
        live.Collections["firewall_zone"].First(z => z["_id"]!.ToString() == "z-int")["network_ids"]!.AsArray().RemoveAt(2);
        live.Collections["firewall_zone"].Add(Zone("z-untr", "Untrusted", null, "n-iot"));

        var board = ZoneSetup.Build(live, Networks, ZoneSetup.TrustMode);

        board.Zones.Single(z => z.ExistingId == "z-untr").Should().Match<ZoneDraft>(z => z.Kept && z.NetworkIds.Contains("n-iot"));
        var desired = ZoneSetup.Desired(live, board);
        Members(desired, "Untrusted").Should().Equal("n-iot");
        desired.Get("firewall_policy").Should().NotContain(p => p["source"]!["zone_id"]!.ToString() == "z-untr", "its policies are the operator's");
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
