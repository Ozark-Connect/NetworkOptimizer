using FluentAssertions;
using NetworkOptimizer.Web.Components.Shared.ChangeControl;
using Xunit;

namespace NetworkOptimizer.Web.Tests.ChangeControl;

/// <summary>The Edit tab's Collections search count.</summary>
public class CcFindTests
{
    private const string Yaml = "wlans:\n- name: IoT\n  x_passphrase: <secret:wlanconf/w1/x_passphrase>\n  networkconf_id: IoT Network\n";

    [Fact]
    public void Count_MatchesFieldNamesAndValues_IgnoringCase()
    {
        CcFind.Count(Yaml, "iot").Should().Be(2);
        CcFind.Count(Yaml, "NETWORKCONF").Should().Be(1);
    }

    [Fact]
    public void Count_SkipsMaskedSecretTokens()
    {
        // "x_passphrase" is the field once; the token's path holds it again and is not counted.
        CcFind.Count(Yaml, "x_passphrase").Should().Be(1);
        CcFind.Count(Yaml, "secret").Should().Be(0);
    }

    [Fact]
    public void Count_TreatsQueryAsLiteralText()
    {
        CcFind.Count("a: 10.0.0.1\nb: 1000\n", "0.0").Should().Be(1);
        CcFind.Count("a: [x]\n", "[x]").Should().Be(1);
    }

    [Fact]
    public void Count_IsNonOverlapping()
    {
        CcFind.Count("aaaa", "aa").Should().Be(2);
    }

    [Fact]
    public void Count_AliasFindsTheYamlName()
    {
        const string yaml = "networks:\n- name: IoT\n  vlan: 20\nclients:\n- mac: aa:bb:cc:dd:ee:ff\ndevices:\n- name: Switch\n";
        CcFind.Count(yaml, "vlan").Should().Be(2);
        CcFind.Count(yaml, "VLANs").Should().Be(2);
        CcFind.Count(yaml, "client devices").Should().Be(1);
        CcFind.Count(yaml, "UniFi Devices").Should().Be(1);
    }

    [Fact]
    public void Count_AliasIsOneWay()
    {
        CcFind.Count("networks:\n  vlan: 20\n", "network").Should().Be(1);
    }

    [Fact]
    public void Count_SpaceMatchesUnderscoreHyphenOrNothing()
    {
        const string yaml = "port_forwards:\nuser_groups:\n- usergroup_id: x\n  wi-fi: on\n";
        CcFind.Count(yaml, "port forward").Should().Be(1);
        CcFind.Count(yaml, "user group").Should().Be(2);
        CcFind.Count(yaml, "wi fi").Should().Be(1);
    }

    [Theory]
    [InlineData("policy-based route")]
    [InlineData("Policy Based Routes")]
    [InlineData("policy_based_routing")]
    [InlineData("selective routing")]
    [InlineData("device-based routing")]
    [InlineData("PBR")]
    public void Count_RoutingNamesFindTrafficRoutes(string query)
    {
        CcFind.Count("traffic_routes:\n- name: VPN\n", query).Should().Be(1);
    }

    [Fact]
    public void Count_LoadBalancingFindsWanAndRadioKeys()
    {
        const string yaml = "networks:\n- wan_load_balance_type: failover-only\n  wan_load_balance_weight: 50\n  wan_failover_priority: 1\ndevices:\n- loadbalance_enabled: false\n";
        CcFind.Count(yaml, "load balancing").Should().Be(3);
        CcFind.Count(yaml, "WAN load balancing").Should().Be(2);
        CcFind.Count(yaml, "failover").Should().Be(2);
    }

    [Fact]
    public void Count_HyphenatedAliasKey_FindsItsAlias()
    {
        CcFind.Count("wlans:\n", "Wi-Fi").Should().Be(1);
        CcFind.Count("wlans:\n", "wifi").Should().Be(1);
    }

    [Fact]
    public void Terms_AreLongestFirst()
    {
        CcFind.Terms("vlan").Should().Equal("network", "vlan");
        CcFind.Terms("  wireless  ").Should().Equal("wireless", "wlan");
    }

    [Fact]
    public void Count_EmptyQuery_IsZero()
    {
        CcFind.Count(Yaml, "").Should().Be(0);
    }
}
