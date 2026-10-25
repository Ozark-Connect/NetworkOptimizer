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
