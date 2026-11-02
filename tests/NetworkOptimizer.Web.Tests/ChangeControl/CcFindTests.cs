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
    public void Count_DeviceKindsFindDevicesAndTheirLists()
    {
        const string yaml = "devices:\n- name: Office\n  port_overrides:\n  - port_idx: 1\n- name: Kitchen\n  radio_table:\n  - radio: ng\n";
        CcFind.Count(yaml, "Switches").Should().Be(2);
        CcFind.Count(yaml, "APs").Should().Be(2);
        CcFind.Count(yaml, "access points").Should().Be(2);
        CcFind.Count(yaml, "gateways").Should().Be(1);
        CcFind.Count(yaml, "Routers").Should().Be(1);
    }

    [Fact]
    public void Terms_ShortSingularIsNotAdded()
    {
        CcFind.Terms("APs").Should().NotContain("ap");
        CcFind.Terms("Switches").Should().Contain("switch");
    }

    [Fact]
    public void Count_DnsServerFindsNumberedServerFields()
    {
        const string yaml = "networks:\n- dhcpd_dns_1: 192.0.2.1\n  dhcpdv6_dns_2: ''\n  wan_dns1: 192.0.2.2\n  wan_ipv6_dns2: ''\n  primary_dns_server: 192.0.2.3\n  dhcpd_dns_enabled: true\n";
        CcFind.Count(yaml, "DNS servers").Should().Be(5);
    }

    [Theory]
    [InlineData("reservations")]
    [InlineData("DHCP reservation")]
    [InlineData("static IPs")]
    [InlineData("fixed ip")]
    public void Count_ReservationNamesFindFixedIp(string query)
    {
        CcFind.Count("clients:\n- mac: aa:bb:cc:dd:ee:ff\n  use_fixedip: true\n  fixed_ip: 192.0.2.10\n", query).Should().Be(2);
    }

    [Theory]
    [InlineData("sqm")]
    [InlineData("AQM")]
    [InlineData("fq_codel")]
    [InlineData("smart queues")]
    [InlineData("SmartQueues")]
    public void Count_QueueingNamesFindSmartQueues(string query)
    {
        CcFind.Count("networks:\n- wan_smartq_enabled: true\n  wan_smartq_down_rate: 900000\n", query).Should().Be(2);
    }

    [Fact]
    public void Count_DerivedAliases()
    {
        const string yaml = "networks:\n- dhcpd_enabled: true\n  dhcpd_start: 192.0.2.10\n  wan_ip: 198.51.100.2\n  wan_ip_aliases: []\ndevices:\n- radio_table:\n  - radio: na\n    channel: 36\nsettings:\n- upnp_nat_pmp_enabled: false\n";
        CcFind.Count(yaml, "DHCP server").Should().Be(2);
        CcFind.Count(yaml, "WAN IP").Should().Be(2);
        CcFind.Count(yaml, "public IP").Should().Be(2);
        CcFind.Count(yaml, "5 GHz").Should().Be(1);
        CcFind.Count(yaml, "nat-pmp").Should().Be(1);
    }

    [Fact]
    public void Count_BandwidthFindsRateLimitsAndSmartQueueRates()
    {
        const string yaml = "user_groups:\n- qos_rate_max_down: -1\n  qos_rate_max_up: -1\nnetworks:\n- wan_smartq_down_rate: 900000\n  wan_smartq_up_rate: 900000\nchannel_plans:\n- channel_width: 80\n  ht_modes_na: []\n";
        CcFind.Count(yaml, "bandwidth").Should().Be(6);
    }

    [Theory]
    [InlineData("bw160", 2)]
    [InlineData("BW 160", 2)]
    [InlineData("160 MHz", 2)]
    [InlineData("160mhz", 2)]
    [InlineData("bw80", 1)]
    public void Count_ChannelWidthQueryFindsThatWidth(string query, int expected)
    {
        const string yaml = "channel_plans:\n- channel_width: 160\ndevices:\n- radio_table:\n  - radio: 6e\n    ht: 160\n  - radio: na\n    ht: 80\n";
        CcFind.Count(yaml, query).Should().Be(expected);
    }

    private const string DnsYaml = "local_dns:\n- key: host\nnetworks:\n- dhcpd_dns_enabled: true\n  dhcpd_dns_1: 192.0.2.1\n  dhcpdv6_dns_1: ''\nclients:\n- local_dns_record: host.lan\n";

    [Theory]
    [InlineData("dns1")]
    [InlineData("dns_1")]
    [InlineData("DNS-1")]
    public void Count_LetterDigitBoundaryMatchesWithOrWithoutSeparator(string query)
    {
        const string yaml = "networks:\n- dhcpd_dns_1: 192.0.2.1\n  dhcpdv6_dns_1: ''\n  wan_dns1: 192.0.2.2\ndevices:\n- config_network:\n    dns1: 192.0.2.3\n";
        CcFind.Count(yaml, query).Should().Be(4);
    }

    [Fact]
    public void Count_DeviceDnsFindsManagementDns()
    {
        CcFind.Count("devices:\n- config_network:\n    dns1: 192.0.2.3\n    dns2: ''\n    dnssuffix: lan\n", "device DNS").Should().Be(3);
    }

    [Fact]
    public void Count_DnsRecordsFindsCollectionAndClientRecords()
    {
        CcFind.Count(DnsYaml, "DNS records").Should().Be(2);
    }

    [Theory]
    [InlineData("local DNS", 5)]
    [InlineData("LAN DNS servers", 3)]
    [InlineData("local DNS server", 3)]
    public void Count_LanDnsFindsTheLanDnsServers(string query, int expected)
    {
        // "local DNS" also finds the Local DNS collection and the client record; the server forms only the LAN's servers.
        CcFind.Count(DnsYaml, query).Should().Be(expected);
    }

    [Fact]
    public void Count_BwAloneIsBandwidth()
    {
        CcFind.Count("networks:\n- wan_smartq_down_rate: 900000\n", "bw").Should().Be(1);
    }

    [Theory]
    [InlineData("_")]
    [InlineData(" - ")]
    public void Count_SeparatorOnlyQuery_IsZero(string query)
    {
        CcFind.Count("a_b-c d\n", query).Should().Be(0);
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
