using FluentAssertions;
using NetworkOptimizer.Web.Services.ChangeControl;
using Xunit;
using static NetworkOptimizer.Web.Tests.ChangeControl.ConfigDiffEngineTests;

namespace NetworkOptimizer.Web.Tests.ChangeControl;

public class ReaddressingTests
{
    private static ConfigState Site() => State(
        ("networkconf", """{"_id":"n1","name":"Default","purpose":"corporate","ip_subnet":"192.168.1.1/24","dhcpd_enabled":true,"dhcpd_start":"192.168.1.100","dhcpd_stop":"192.168.1.200","dhcpd_dns_1":"192.168.1.5"}"""),
        ("networkconf", """{"_id":"n2","name":"IoT","purpose":"corporate","vlan_enabled":true,"vlan":30,"ip_subnet":"192.168.30.1/24"}"""),
        ("networkconf", """{"_id":"w1","name":"Internet 1","purpose":"wan","wan_ip":"203.0.113.10","wan_netmask":"255.255.255.0"}"""),
        ("user", """{"_id":"u1","name":"NAS","mac":"aa:bb:cc:dd:ee:01","use_fixedip":true,"fixed_ip":"192.168.1.5","last_ip":"192.168.1.5"}"""),
        ("static_dns", """{"_id":"d1","key":"nas.lan","record_type":"A","value":"192.168.1.5"}"""),
        ("portforward", """{"_id":"p1","name":"Web","fwd":"192.168.1.5","src":"any"}"""),
        ("firewallgroup", """{"_id":"g1","name":"Cameras","group_members":["192.168.30.10-192.168.30.20","192.168.30.0/24","198.51.100.7"]}"""),
        ("setting", """{"_id":"s1","key":"super_mgmt","override_inform_host":true,"override_inform_host_location":"192.168.1.5","note":"console at 192.168.1.5 port 8443"}"""));

    private static List<SubnetMove> Pattern(ConfigState s) => Readdressing.Proposal(s, "10.42.{vlan}.0/24");

    [Fact]
    public void NatUpdate_CarriesTheLiveRuleIndex()
    {
        var live = Site();
        live.Collections["nat"] = [System.Text.Json.Nodes.JsonNode.Parse(
            """{"_id":"nat1","description":"DNS DNAT","type":"DNAT","enabled":true,"ip_address":"192.168.1.5","rule_index":2,"in_interface":"n1"}""")!.AsObject()];
        var desired = Readdressing.Apply(live, Pattern(live)).Desired;

        var doc = PlanBuilder.Build(desired, live, new PlanOptions { DeleteMissing = false });

        var body = doc.Operations.Single(o => o.Collection == "nat").Body!;
        body["ip_address"]!.ToString().Should().Be("10.42.1.5");
        body["rule_index"]!.GetValue<int>().Should().Be(2);
    }

    [Fact]
    public void BuiltInRadiusProfile_IsLeftAlone()
    {
        var live = Site();
        live.Collections["radiusprofile"] = [
            System.Text.Json.Nodes.JsonNode.Parse("""{"_id":"r1","name":"Default","attr_hidden_id":"Default","use_usg_auth_server":true,"auth_servers":[{"ip":"192.168.1.1","port":1812}]}""")!.AsObject(),
            System.Text.Json.Nodes.JsonNode.Parse("""{"_id":"r2","name":"Corp","auth_servers":[{"ip":"192.168.1.5","port":1812}]}""")!.AsObject()];

        var desired = Readdressing.Apply(live, Pattern(live)).Desired;

        desired.Get("radiusprofile").Single(r => ConfigState.IdOf(r) == "r1")["auth_servers"]![0]!["ip"]!.ToString().Should().Be("192.168.1.1");
        desired.Get("radiusprofile").Single(r => ConfigState.IdOf(r) == "r2")["auth_servers"]![0]!["ip"]!.ToString().Should().Be("10.42.1.5");
    }

    [Fact]
    public void Honeypots_WriteAfterEveryNetwork_ConsolePathNetworkLast()
    {
        var live = Site();
        live.Collections["setting"].Add(System.Text.Json.Nodes.JsonNode.Parse(
            """{"_id":"s2","key":"ips","honeypot_enabled":true,"honeypot":[{"ip_address":"192.168.30.250","network_id":"n2"}]}""")!.AsObject());
        live.Collections["device"] = [
            System.Text.Json.Nodes.JsonNode.Parse("""{"_id":"gw","mac":"aa:bb:cc:dd:ee:10","name":"Gateway","config_network":{"type":"static","ip":"192.168.1.1"}}""")!.AsObject(),
            System.Text.Json.Nodes.JsonNode.Parse("""{"_id":"ap","mac":"aa:bb:cc:dd:ee:11","name":"AP","config_network":{"type":"static","ip":"192.168.1.20"}}""")!.AsObject()];
        var desired = Readdressing.Apply(live, Pattern(live)).Desired;
        var doc = PlanBuilder.Build(desired, live, new PlanOptions { DeleteMissing = false });

        ChangeControlService.OrderReaddressWrites(doc, new HashSet<string> { "n1" }, new HashSet<string> { "gw" });

        var keys = doc.Operations.Select(o => o.Key).ToList();
        int At(string id) => keys.FindIndex(k => doc.Operations[keys.IndexOf(k)].ObjectId == id && !k.EndsWith(":honeypots-off"));
        At("n1").Should().BeGreaterThan(At("n2"));
        At("gw").Should().BeGreaterThan(At("ap"));
        At("n1").Should().BeGreaterThan(At("gw"));
        At("s2").Should().BeGreaterThan(At("n1"));
        At("u1").Should().BeGreaterThan(At("n1"));
        At("s2").Should().BeGreaterThan(At("u1"));
        doc.Context[PlanCoordinator.ReaddressPathContext].Split(',').Should().HaveCount(3);

        // Honeypots go off before any network moves, and the restore turns them back on.
        var off = doc.Operations.Single(o => o.Key.EndsWith(":honeypots-off"));
        keys.IndexOf(off.Key).Should().BeLessThan(At("n2"));
        off.Body!["honeypot"]!.AsArray().Should().BeEmpty();
        off.Body!["honeypot_enabled"]!.GetValue<bool>().Should().BeFalse();
        var restore = doc.Operations[At("s2")];
        restore.Body!["honeypot"]![0]!["ip_address"]!.ToString().Should().Be("10.42.30.250");
        restore.Body!["honeypot_enabled"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public void RollBack_OfAReaddress_TakesTheSameWriteOrder()
    {
        var live = Site();
        live.Collections["setting"].Add(System.Text.Json.Nodes.JsonNode.Parse(
            """{"_id":"s2","key":"ips","honeypot_enabled":true,"honeypot":[{"ip_address":"192.168.30.250","network_id":"n2"}]}""")!.AsObject());
        var moves = Pattern(live);
        var after = Readdressing.Apply(live, moves).Desired;
        var forward = PlanBuilder.Build(after, live, new PlanOptions { DeleteMissing = false });
        ChangeControlService.OrderReaddressWrites(forward, new HashSet<string> { "n1" });
        forward.Context[PlanCoordinator.ReaddressContext] = System.Text.Json.JsonSerializer.Serialize(moves.Where(m => m.OldCidr != m.NewCidr).ToList());

        // The revert writes the pre-apply snapshot back over the moved site.
        var revert = PlanBuilder.Build(live, after, new PlanOptions { DeleteMissing = false });
        PlanCoordinator.ReaddressRevertOrder(forward, live)!(revert);

        var keys = revert.Operations.Select(o => o.Key).ToList();
        int At(string id) => revert.Operations.FindIndex(o => o.ObjectId == id && !o.Key.EndsWith(":honeypots-off"));
        keys.FindIndex(k => k.EndsWith(":honeypots-off")).Should().BeLessThan(At("n2"));
        At("n1").Should().BeGreaterThan(At("n2"));
        At("u1").Should().BeGreaterThan(At("n1"));
        At("s2").Should().BeGreaterThan(At("n1"));
        revert.Operations[At("u1")].Body!["fixed_ip"]!.ToString().Should().Be("192.168.1.5");
        revert.Operations[At("u1")].Body!["use_fixedip"]!.GetValue<bool>().Should().BeTrue();
        revert.Context[PlanCoordinator.ReaddressContext].Should().Contain("\"OldCidr\":\"10.42.");
        PlanCoordinator.ReaddressRevertOrder(new PlanDocument(), live).Should().BeNull();
    }

    [Fact]
    public void ClientWrites_CarryTheReservationAndLocalDnsFlags()
    {
        var live = Site();
        live.Collections["user"][0]["local_dns_record"] = "nas.lan";
        live.Collections["user"][0]["local_dns_record_enabled"] = true;
        var desired = Readdressing.Apply(live, Pattern(live)).Desired;
        var doc = PlanBuilder.Build(desired, live, new PlanOptions { DeleteMissing = false });

        ChangeControlService.CarryReservations(doc, desired);

        var body = doc.Operations.Single(o => o.Collection == "user").Body!;
        body["fixed_ip"]!.ToString().Should().Be("10.42.1.5");
        body["use_fixedip"]!.GetValue<bool>().Should().BeTrue();
        body["local_dns_record_enabled"]!.GetValue<bool>().Should().BeTrue();
        body["local_dns_record"]!.ToString().Should().Be("nas.lan");
        // Verify checks the carried fields too, so a reservation the console dropped shows.
        doc.Operations.Single(o => o.Collection == "user").WriteFields.Should()
            .Contain(["use_fixedip", "fixed_ip", "local_dns_record_enabled", "local_dns_record"]).And.OnlyHaveUniqueItems();
    }

    [Fact]
    public void NetworkHoldingAStaticDevice_MovesAfterTheDevice()
    {
        var live = Site();
        live.Collections["device"] = [
            System.Text.Json.Nodes.JsonNode.Parse("""{"_id":"gw","mac":"aa:bb:cc:dd:ee:10","name":"Gateway","config_network":{"type":"dhcp"}}""")!.AsObject(),
            System.Text.Json.Nodes.JsonNode.Parse("""{"_id":"cam","mac":"aa:bb:cc:dd:ee:12","name":"Switch","config_network":{"type":"static","ip":"192.168.30.20"}}""")!.AsObject()];
        var moves = Pattern(live).Where(m => m.OldCidr != m.NewCidr).ToList();
        var desired = Readdressing.Apply(live, moves).Desired;
        var doc = PlanBuilder.Build(desired, live, new PlanOptions { DeleteMissing = false });

        ChangeControlService.OrderReaddressWrites(doc, new HashSet<string> { "n1" }, new HashSet<string> { "gw" }, moves);

        int At(string id) => doc.Operations.FindIndex(o => o.ObjectId == id && !o.Key.EndsWith(ChangeControlService.HoneypotsOffSuffix));
        At("n2").Should().BeGreaterThan(At("cam"));
        At("n1").Should().BeGreaterThan(At("n2"));
        // Not the console's path: its lost reply is not an expected end.
        doc.Context[PlanCoordinator.ReaddressPathContext].Should().NotContain(doc.Operations[At("n2")].Key);
    }

    [Fact]
    public void DeviceWrites_GoLeavesFirst_GatewayLast()
    {
        var live = Site();
        static System.Text.Json.Nodes.JsonObject Device(string id, string mac, string type, string? uplink, string ip)
        {
            var meta = new System.Text.Json.Nodes.JsonObject { ["type"] = type };
            if (uplink != null) meta["uplink"] = new System.Text.Json.Nodes.JsonObject { ["uplink_mac"] = uplink };
            return new System.Text.Json.Nodes.JsonObject
            {
                ["_id"] = id, ["mac"] = mac, ["name"] = id,
                ["config_network"] = new System.Text.Json.Nodes.JsonObject { ["type"] = "static", ["ip"] = ip },
                [ConfigCollections.DeviceMetaField] = meta,
            };
        }
        // Names sort against the topology, so only depth can put them in this order.
        live.Collections["device"] = [
            Device("gw", "aa:bb:cc:dd:ee:10", "ugw", null, "192.168.1.1"),
            Device("a-core", "aa:bb:cc:dd:ee:11", "usw", "aa:bb:cc:dd:ee:10", "192.168.1.2"),
            Device("b-edge", "aa:bb:cc:dd:ee:12", "usw", "aa:bb:cc:dd:ee:11", "192.168.1.3"),
            Device("c-ap", "aa:bb:cc:dd:ee:13", "uap", "aa:bb:cc:dd:ee:12", "192.168.1.4"),
            Device("d-lost", "aa:bb:cc:dd:ee:14", "uap", null, "192.168.1.5")];
        var desired = Readdressing.Apply(live, Pattern(live)).Desired;
        var doc = PlanBuilder.Build(desired, live, new PlanOptions { DeleteMissing = false });

        ChangeControlService.OrderReaddressWrites(doc, new HashSet<string> { "n1" }, ChangeControlService.GatewayIds(live),
            null, ChangeControlService.DeviceDepths(live));

        doc.Operations.Where(o => o.Collection == "device").Select(o => o.ObjectId)
            .Should().ContainInOrder("d-lost", "c-ap", "b-edge", "a-core", "gw");
        doc.Operations.FindIndex(o => o.ObjectId == "n1").Should().BeGreaterThan(doc.Operations.FindIndex(o => o.ObjectId == "gw"));
    }

    [Fact]
    public void LeavingOutTheHoneypotRestore_LeavesOutHoneypotsOff()
    {
        var live = Site();
        live.Collections["setting"].Add(System.Text.Json.Nodes.JsonNode.Parse(
            """{"_id":"s2","key":"ips","honeypot_enabled":true,"honeypot":[{"ip_address":"192.168.30.250","network_id":"n2"}]}""")!.AsObject());
        var doc = PlanBuilder.Build(Readdressing.Apply(live, Pattern(live)).Desired, live, new PlanOptions { DeleteMissing = false });
        ChangeControlService.OrderReaddressWrites(doc, new HashSet<string> { "n1" });
        var restore = doc.Operations.Single(o => o.ObjectId == "s2" && !o.Key.EndsWith(ChangeControlService.HoneypotsOffSuffix)).Key;

        PlanReadiness.Exclude(doc, new HashSet<string> { restore });

        doc.Operations.Should().NotContain(o => o.ObjectId == "s2");
        doc.Operations.Should().Contain(o => o.ObjectId == "n2");
    }

    [Theory]
    [InlineData("nas.lan", true)]
    [InlineData("console.lan", true)]
    [InlineData("loop.lan", false)]
    [InlineData("other.lan", false)]
    [InlineData("server.example.lan", true)]
    [InlineData("tv.example.lan", false)]
    public void MovedByLocalDns_FollowsCnamesToAMovedRecord(string host, bool moved)
    {
        var s = State(
            ("networkconf", """{"_id":"n1","name":"Default","purpose":"corporate","ip_subnet":"192.168.1.1/24"}"""),
            ("user", """{"_id":"u1","mac":"aa:bb:cc:dd:ee:01","use_fixedip":true,"fixed_ip":"192.168.1.9","local_dns_record_enabled":true,"local_dns_record":"host1"}"""),
            ("user", """{"_id":"u2","mac":"aa:bb:cc:dd:ee:02","use_fixedip":false,"fixed_ip":"192.168.1.10","local_dns_record_enabled":true,"local_dns_record":"tv1"}"""),
            ("static_dns", """{"_id":"d6","key":"server.example.lan","record_type":"CNAME","value":"host1.example.lan"}"""),
            ("static_dns", """{"_id":"d7","key":"tv.example.lan","record_type":"CNAME","value":"tv1.example.lan"}"""),
            ("static_dns", """{"_id":"d1","key":"nas.lan","record_type":"A","value":"192.168.1.5"}"""),
            ("static_dns", """{"_id":"d2","key":"console.lan","record_type":"CNAME","value":"nas.lan"}"""),
            ("static_dns", """{"_id":"d3","key":"loop.lan","record_type":"CNAME","value":"loop2.lan"}"""),
            ("static_dns", """{"_id":"d4","key":"loop2.lan","record_type":"CNAME","value":"loop.lan"}"""),
            ("static_dns", """{"_id":"d5","key":"other.lan","record_type":"A","value":"198.51.100.7"}"""));
        ChangeControlService.MovedByLocalDns(s, host, Readdressing.Map(Pattern(s))).Should().Be(moved);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("\"true\"", true)]
    [InlineData("\"True\"", true)]
    [InlineData("\"false\"", false)]
    [InlineData("\"yes\"", false)]
    [InlineData("1", false)]
    [InlineData("null", false)]
    public void IsTrue_TakesAJsonBoolOrTheStringForm(string json, bool expected) =>
        Readdressing.IsTrue(System.Text.Json.Nodes.JsonNode.Parse(json)).Should().Be(expected);

    [Fact]
    public void Proposal_VlanEnabledAsAString_StillReadsTheVlan()
    {
        var s = State(("networkconf", """{"_id":"n2","name":"IoT","purpose":"corporate","vlan_enabled":"true","vlan":30,"ip_subnet":"192.168.30.1/24"}"""));
        Pattern(s).Single().NewCidr.Should().Be("10.42.30.0/24");
    }

    [Fact]
    public void Proposal_LanNetworksOnly_FromThePattern()
    {
        var moves = Pattern(Site());
        moves.Select(m => (m.NetworkName, m.OldCidr, m.NewCidr)).Should().Equal(
            ("Default", "192.168.1.0/24", "10.42.1.0/24"),
            ("IoT", "192.168.30.0/24", "10.42.30.0/24"));
    }

    [Theory]
    [InlineData("10.42.{vlan}.0/24")]
    [InlineData("10.42.{VLAN}.0/24")]
    [InlineData("10.42.x.x")]
    [InlineData("10.42.x.0")]
    [InlineData("10.42.*.0")]
    [InlineData("10.42.*.*")]
    [InlineData("10.42.X.0/24")]
    public void Pattern_AcceptsTokensAndWildcards(string pattern)
    {
        var old = Readdressing.ParseSubnet("192.168.30.1/24")!.Value;
        Readdressing.ApplyPattern(pattern, 30, old).Should().Be("10.42.30.0/24");
    }

    [Fact]
    public void Apply_RewritesEveryAddress_KeepingTheHostPart()
    {
        var result = Readdressing.Apply(Site(), Pattern(Site()));
        var d = result.Desired;
        d.Get("networkconf")[0]["ip_subnet"]!.ToString().Should().Be("10.42.1.1/24");
        d.Get("networkconf")[0]["dhcpd_start"]!.ToString().Should().Be("10.42.1.100");
        d.Get("networkconf")[0]["dhcpd_dns_1"]!.ToString().Should().Be("10.42.1.5");
        d.Get("user")[0]["fixed_ip"]!.ToString().Should().Be("10.42.1.5");
        d.Get("user")[0]["last_ip"]!.ToString().Should().Be("192.168.1.5", "live state is not config");
        d.Get("static_dns")[0]["value"]!.ToString().Should().Be("10.42.1.5");
        d.Get("portforward")[0]["fwd"]!.ToString().Should().Be("10.42.1.5");
        d.Get("firewallgroup")[0]["group_members"]!.AsArray().Select(e => e!.ToString())
            .Should().Equal("10.42.30.10-10.42.30.20", "10.42.30.0/24", "198.51.100.7");
        d.Get("setting")[0]["override_inform_host_location"]!.ToString().Should().Be("10.42.1.5");
        d.Get("networkconf")[2]["wan_ip"]!.ToString().Should().Be("203.0.113.10");

        result.Mentions.Should().ContainSingle(m => m.Path == "note");
        result.Collections.Should().BeEquivalentTo(["networkconf", "user", "static_dns", "portforward", "firewallgroup", "setting"]);
    }

    [Fact]
    public void Apply_SkipsPoliciesUniFiGenerates()
    {
        var site = State(
            ("networkconf", """{"_id":"n1","name":"Cameras","purpose":"corporate","vlan_enabled":true,"vlan":42,"ip_subnet":"192.168.42.1/24"}"""),
            ("firewall_policy", """{"_id":"p1","name":"Block 192.168.42.0/24 Internet Access","origin_type":"network","origin_id":"n1","source":{"ips":["192.168.42.0/24"]}}"""),
            ("firewall_policy", """{"_id":"p2","name":"Allow 192.168.42.7","predefined":true,"source":{"ips":["192.168.42.7"]}}"""),
            ("firewall_policy", """{"_id":"p3","name":"Block 192.168.42.9","source":{"ips":["192.168.42.9"]}}"""));
        var result = Readdressing.Apply(site, Readdressing.Proposal(site, "10.42.{vlan}.0/24"));

        result.Desired.Get("firewall_policy")[0]["source"]!["ips"]![0]!.ToString().Should().Be("192.168.42.0/24");
        result.Desired.Get("firewall_policy")[1]["source"]!["ips"]![0]!.ToString().Should().Be("192.168.42.7");
        result.Desired.Get("firewall_policy")[2]["source"]!["ips"]![0]!.ToString().Should().Be("10.42.42.9");
        result.Rewrites.Where(r => r.Collection == "firewall_policy").Should().ContainSingle(r => r.New == "10.42.42.9");
        result.Mentions.Should().ContainSingle(m => m.Value == "Block 192.168.42.9");
    }

    [Fact]
    public void Rewrite_LeavesAddressesOutsideTheMovedSubnets()
    {
        var map = Readdressing.Map(Pattern(Site()));
        Readdressing.Rewrite("192.168.2.5", map).Should().BeNull();
        Readdressing.Rewrite("10.0.0.0/8", map).Should().BeNull("wider than the moved subnet");
        Readdressing.Rewrite("192.168.1.64/26", map).Should().Be("10.42.1.64/26");
    }

    [Fact]
    public void Validate_RefusesOverlapsShrinksAndClashes()
    {
        var site = Site();
        var moves = Pattern(site);
        Readdressing.Validate(site, moves).Should().BeEmpty();

        var overlap = moves.Select(m => m with { NewCidr = "10.42.0.0/16" }).ToList();
        Readdressing.Validate(site, overlap).Should().Contain(e => e.Contains("would overlap"));

        var shrink = moves.Select(m => m.NetworkName == "IoT" ? m with { NewCidr = "10.42.30.0/25" } : m).ToList();
        Readdressing.Validate(site, shrink).Should().Contain(e => e.Contains("smaller"));

        var clash = moves.Select(m => m.NetworkName == "IoT" ? m with { NewCidr = "203.0.113.0/24" } : m).ToList();
        Readdressing.Validate(site, clash).Should().Contain(e => e.Contains("Internet 1"));
    }

    // Shapes from a real site: a WAN whose static /30 sits inside a LAN range, a WireGuard client, and
    // a static route, traffic route, and External-zone policy that reach the VPN's far side.
    private static ConfigState WithRemote() => State(
        ("networkconf", """{"_id":"lan","name":"Management","purpose":"corporate","vlan_enabled":true,"vlan":99,"ip_subnet":"192.168.99.1/24"}"""),
        ("networkconf", """{"_id":"wan5g","name":"5G","purpose":"wan","ifname":"gre1","lan_tunnel_wan_device":"aa:bb:cc:dd:ee:05","ip_subnet":"192.168.99.5/30","wan_ip":"100.64.10.9","wan_gateway":"100.64.10.8","wan_netmask":"255.255.255.254"}"""),
        ("networkconf", """{"_id":"wanc","name":"Cable","purpose":"wan","wan_type":"static","wan_ip":"192.168.99.150","wan_gateway":"192.168.99.149","wan_netmask":"255.255.255.252"}"""),
        ("networkconf", """{"_id":"wg","name":"Far Site","purpose":"vpn-client","vpn_type":"wireguard-client","ip_subnet":"192.168.99.200/32"}"""),
        ("firewall_zone", """{"_id":"zi","name":"Internal","zone_key":"internal","network_ids":["lan"]}"""),
        ("firewall_zone", """{"_id":"ze","name":"External","zone_key":"external","network_ids":["wan5g","wg"]}"""),
        ("routing", """{"_id":"r1","name":"Far gateway","static-route_interface":"wg","static-route_network":"192.168.99.240/28"}"""),
        ("trafficroute", """{"_id":"t1","description":"Far site","network_id":"wg","ip_addresses":[{"ip_or_subnet":"192.168.99.128/26"}],"ip_ranges":[]}"""),
        ("firewall_policy", """{"_id":"p1","name":"Block far side","source":{"zone_id":"zi","ips":["192.168.99.10"]},"destination":{"zone_id":"ze","ips":["192.168.99.130"]}}"""),
        ("user", """{"_id":"u1","name":"Switch","mac":"aa:bb:cc:dd:ee:02","use_fixedip":true,"fixed_ip":"192.168.99.10"}"""),
        ("user", """{"_id":"u2","name":"Cable Modem","mac":"aa:bb:cc:dd:ee:03","use_fixedip":true,"fixed_ip":"192.168.99.149"}"""),
        ("device", """{"_id":"d5","name":"5G Modem","mac":"aa:bb:cc:dd:ee:05","config_network":{"type":"static","ip":"192.168.99.5","netmask":"255.255.255.0","gateway":"192.168.99.1","dns1":"192.168.99.1"}}"""));

    [Fact]
    public void Apply_LeavesWanAndVpnAddressesAlone_EvenInsideAMovedSubnet()
    {
        var site = WithRemote();
        var result = Readdressing.Apply(site, Readdressing.Proposal(site, "10.42.{vlan}.0/24"));
        var d = result.Desired;

        d.Get("networkconf")[0]["ip_subnet"]!.ToString().Should().Be("10.42.99.1/24");
        // A GRE WAN to a modem on the LAN: its endpoint is the modem's LAN address and moves with it.
        d.Get("networkconf")[1]["ip_subnet"]!.ToString().Should().Be("10.42.99.5/30");
        d.Get("networkconf")[1]["wan_ip"]!.ToString().Should().Be("100.64.10.9");
        d.Get("device")[0]["config_network"]!["ip"]!.ToString().Should().Be("10.42.99.5");
        d.Get("device")[0]["config_network"]!["gateway"]!.ToString().Should().Be("10.42.99.1");
        // A static WAN's carrier addresses are the ISP's, even inside a LAN range.
        d.Get("networkconf")[2]["wan_ip"]!.ToString().Should().Be("192.168.99.150");
        d.Get("networkconf")[3]["ip_subnet"]!.ToString().Should().Be("192.168.99.200/32", "the VPN server sets the tunnel address");
        d.Get("routing")[0]["static-route_network"]!.ToString().Should().Be("192.168.99.240/28");
        d.Get("trafficroute")[0]["ip_addresses"]![0]!["ip_or_subnet"]!.ToString().Should().Be("192.168.99.128/26");
        d.Get("firewall_policy")[0]["source"]!["ips"]![0]!.ToString().Should().Be("10.42.99.10");
        d.Get("firewall_policy")[0]["destination"]!["ips"]![0]!.ToString().Should().Be("192.168.99.130");
        d.Get("user")[0]["fixed_ip"]!.ToString().Should().Be("10.42.99.10");
        d.Get("user")[1]["fixed_ip"]!.ToString().Should().Be("192.168.99.149", "the static WAN's gateway");

        result.Remote.Should().HaveCountGreaterThanOrEqualTo(6);
    }

    [Fact]
    public void Validate_RefusesANewSubnetOverAVpnFarSide()
    {
        var site = WithRemote();
        var moves = Readdressing.Proposal(site, null).Select(m => m with { NewCidr = "192.168.99.0/24" == m.OldCidr ? "192.168.98.0/23" : m.NewCidr }).ToList();
        Readdressing.Validate(site, moves).Should().Contain(e => e.Contains("reached over"));
    }

    [Fact]
    public void Reverse_PutsEveryAddressBack()
    {
        var site = Site();
        var moved = Readdressing.Apply(site, Pattern(site)).Desired;
        var back = Readdressing.Apply(moved, Readdressing.Reverse(Pattern(site))).Desired;
        back.Get("firewallgroup")[0]["group_members"]!.ToJsonString().Should().Be(site.Get("firewallgroup")[0]["group_members"]!.ToJsonString());
        back.Get("networkconf")[0]["ip_subnet"]!.ToString().Should().Be("192.168.1.1/24");
    }
}
