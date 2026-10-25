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
