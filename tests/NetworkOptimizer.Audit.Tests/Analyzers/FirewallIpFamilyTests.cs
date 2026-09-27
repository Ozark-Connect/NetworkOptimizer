using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using NetworkOptimizer.Audit.Analyzers;
using NetworkOptimizer.Audit.Dns;
using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.UniFi.Models;
using Xunit;

namespace NetworkOptimizer.Audit.Tests.Analyzers;

/// <summary>
/// Per-address-family evaluation of firewall claims (#1231). Every IPv6 case is paired with an
/// IPv4 or no-IPv6 control that must behave exactly as before.
/// </summary>
public class FirewallIpFamilyTests
{
    private const string ExternalZone = "external-zone";
    private const string InternalZone = "internal-zone";

    private readonly FirewallRuleParser _parser = new(Mock.Of<ILogger<FirewallRuleParser>>());
    private readonly FirewallRuleAnalyzer _firewallAnalyzer;
    private readonly VlanAnalyzer _vlanAnalyzer = new(Mock.Of<ILogger<VlanAnalyzer>>());

    public FirewallIpFamilyTests()
    {
        _firewallAnalyzer = new FirewallRuleAnalyzer(Mock.Of<ILogger<FirewallRuleAnalyzer>>(), _parser);
    }

    #region FirewallRule.MatchesIpFamily

    [Theory]
    [InlineData("IPV4", IpFamily.IPv4, true)]
    [InlineData("IPV4", IpFamily.IPv6, false)]
    [InlineData("IPV6", IpFamily.IPv4, false)]
    [InlineData("IPV6", IpFamily.IPv6, true)]
    [InlineData("ipv6", IpFamily.IPv6, true)]
    [InlineData("BOTH", IpFamily.IPv4, true)]
    [InlineData("BOTH", IpFamily.IPv6, true)]
    [InlineData(null, IpFamily.IPv4, true)]
    [InlineData(null, IpFamily.IPv6, true)]
    [InlineData("SOMETHING_NEW", IpFamily.IPv6, true)]
    public void MatchesIpFamily_FollowsIpVersion(string? ipVersion, IpFamily family, bool expected)
    {
        Rule("r", "BLOCK", 1, ipVersion).MatchesIpFamily(family).Should().Be(expected);
    }

    #endregion

    #region FirewallRule.AppliesToSourceNetwork (family overload)

    [Fact]
    public void AppliesToSourceNetwork_Ipv6OnlyRule_DoesNotApplyToIpv4Traffic()
    {
        var network = Net("n1", "Home", NetworkPurpose.Home, 10, "192.0.2.0/24", "2001:db8:10::/64");
        var rule = Rule("r", "BLOCK", 1, "IPV6");

        rule.AppliesToSourceNetwork(network, IpFamily.IPv4).Should().BeFalse();
        rule.AppliesToSourceNetwork(network, IpFamily.IPv6).Should().BeTrue();
    }

    [Fact]
    public void AppliesToSourceNetwork_FamilyAgnosticOverload_IsUnchanged()
    {
        // Rule-level checks keep using the single-argument overload, which ignores ip_version
        var network = Net("n1", "Home", NetworkPurpose.Home, 10, "192.0.2.0/24");
        Rule("r", "BLOCK", 1, "IPV6").AppliesToSourceNetwork(network).Should().BeTrue();
    }

    [Fact]
    public void AppliesToSourceNetwork_IpSource_MatchesAgainstTheFamilysSubnet()
    {
        var network = Net("n1", "Home", NetworkPurpose.Home, 10, "192.0.2.0/24", "2001:db8:10::/64");
        var v4Source = Rule("r4", "BLOCK", 1, "BOTH", srcTarget: "IP", srcIps: ["192.0.2.0/24"]);
        var v6Source = Rule("r6", "BLOCK", 1, "BOTH", srcTarget: "IP", srcIps: ["2001:db8:10::/64"]);

        v4Source.AppliesToSourceNetwork(network, IpFamily.IPv4).Should().BeTrue();
        v4Source.AppliesToSourceNetwork(network, IpFamily.IPv6).Should().BeFalse();
        v6Source.AppliesToSourceNetwork(network, IpFamily.IPv4).Should().BeFalse();
        v6Source.AppliesToSourceNetwork(network, IpFamily.IPv6).Should().BeTrue();
    }

    [Fact]
    public void AppliesToSourceNetwork_MatchOppositeIpv6Source_ExcludesOnlyTheListedPrefix()
    {
        var listed = Net("n1", "Listed", NetworkPurpose.Home, 10, "192.0.2.0/24", "2001:db8:10::/64");
        var other = Net("n2", "Other", NetworkPurpose.Home, 20, "198.51.100.0/24", "2001:db8:20::/64");
        var rule = Rule("r", "BLOCK", 1, "IPV6", srcTarget: "IP", srcIps: ["2001:db8:10::/64"], srcOpposite: true);

        rule.AppliesToSourceNetwork(listed, IpFamily.IPv6).Should().BeFalse();
        rule.AppliesToSourceNetwork(other, IpFamily.IPv6).Should().BeTrue();
    }

    [Fact]
    public void AppliesToSourceNetwork_NoIpv6Prefix_IpSourceNeverMatchesOverIpv6()
    {
        var network = Net("n1", "Home", NetworkPurpose.Home, 10, "192.0.2.0/24");
        Rule("r", "BLOCK", 1, "BOTH", srcTarget: "IP", srcIps: ["2001:db8::/32"])
            .AppliesToSourceNetwork(network, IpFamily.IPv6).Should().BeFalse();
    }

    #endregion

    #region Parser: ip_version, PD isolation rules, legacy rulesets

    [Fact]
    public void ParseFirewallPolicy_PdIsolationRule_IsRescopedToTheNamedVlan()
    {
        var rule = _parser.ParseFirewallPolicy(PdIsolationPolicy("Isolate IPv6 traffic from PD interface br69 to any local subnet"))!;

        rule.PdIsolationVlanId.Should().Be(69);
        rule.SourceMatchingTarget.Should().Be("NETWORK");
        rule.IsAnySource().Should().BeFalse();

        var pd = Net("pd", "Gaming", NetworkPurpose.Gaming, 69, "192.0.2.0/24", "2001:db8:69::/64");
        var other = Net("other", "Home", NetworkPurpose.Home, 10, "198.51.100.0/24", "2001:db8:10::/64");
        rule.AppliesToSourceNetwork(pd, IpFamily.IPv6).Should().BeTrue();
        rule.AppliesToSourceNetwork(other, IpFamily.IPv6).Should().BeFalse();
    }

    [Fact]
    public void ParseFirewallPolicy_PdIsolationRuleOnBr0_MapsToDefaultNetwork()
    {
        _parser.ParseFirewallPolicy(PdIsolationPolicy("Isolate IPv6 traffic from PD interface br0 to any local subnet"))!
            .PdIsolationVlanId.Should().Be(1);
    }

    [Fact]
    public void ParseFirewallPolicy_PdIsolationRuleWithUnparseableName_MatchesNoNetwork()
    {
        // Taken literally the rule isolates every Internal network, so an unknown name drops it
        var rule = _parser.ParseFirewallPolicy(PdIsolationPolicy("Renamed by a future firmware"))!;
        var network = Net("n1", "Home", NetworkPurpose.Home, 10, "192.0.2.0/24", "2001:db8:10::/64");

        rule.AppliesToSourceNetwork(network, IpFamily.IPv6).Should().BeFalse();
    }

    [Theory]
    [InlineData(false, "IPV6", "network_config")] // user rule
    [InlineData(true, "BOTH", "network_config")]  // not single-family
    [InlineData(true, "IPV6", null)]              // e.g. Allow Neighbor Solicitations
    public void ParseFirewallPolicy_OtherAnySourceRules_AreNotRescoped(bool predefined, string ipVersion, string? originType)
    {
        var origin = originType == null ? "" : $@"""origin_type"": ""{originType}"",";
        var rule = _parser.ParseFirewallPolicy(JsonDocument.Parse($@"{{
            ""_id"": ""p1"", ""name"": ""from PD interface br69"", ""action"": ""BLOCK"", ""enabled"": true,
            ""index"": 30001, ""predefined"": {predefined.ToString().ToLowerInvariant()}, ""ip_version"": ""{ipVersion}"", {origin}
            ""protocol"": ""all"",
            ""source"": {{ ""matching_target"": ""ANY"", ""zone_id"": ""{InternalZone}"" }},
            ""destination"": {{ ""matching_target"": ""ANY"", ""zone_id"": ""{InternalZone}"" }}
        }}").RootElement)!;

        rule.PdIsolationVlanId.Should().BeNull();
        rule.SourceMatchingTarget.Should().Be("ANY");
    }

    [Theory]
    [InlineData("LAN_IN", "IPV4")]
    [InlineData("WAN_OUT", "IPV4")]
    [InlineData("GUEST_LOCAL", "IPV4")]
    [InlineData("LANv6_IN", "IPV6")]
    [InlineData("WANv6_OUT", "IPV6")]
    [InlineData("GUESTv6_LOCAL", "IPV6")]
    [InlineData("SOMETHING_ELSE", null)]
    [InlineData(null, null)]
    public void IpVersionForLegacyRuleset_FollowsTheRuleset(string? ruleset, string? expected)
    {
        FirewallRuleParser.IpVersionForLegacyRuleset(ruleset).Should().Be(expected);
    }

    [Fact]
    public void MapRulesetToZones_V6Ruleset_SharesItsV4TwinsZones()
    {
        FirewallRuleParser.MapRulesetToZones("LANv6_IN").Should().Be(FirewallRuleParser.MapRulesetToZones("LAN_IN"));
        FirewallRuleParser.MapRulesetToZones("WANv6_OUT").Should().Be(FirewallRuleParser.MapRulesetToZones("WAN_OUT"));
    }

    [Fact]
    public void ParseFirewallRule_LegacyV6Ruleset_IsIpv6Only()
    {
        var rule = _parser.ParseFirewallRule(JsonDocument.Parse(@"{
            ""_id"": ""legacy1"", ""name"": ""Block v6"", ""action"": ""drop"", ""enabled"": true,
            ""rule_index"": 2000, ""ruleset"": ""LANv6_IN"", ""protocol"": ""all""
        }").RootElement)!;

        rule.IpVersion.Should().Be("IPV6");
        rule.MatchesIpFamily(IpFamily.IPv4).Should().BeFalse();
    }

    #endregion

    #region Network parsing

    [Fact]
    public void ExtractNetworks_ReadsStaticAndDelegatedPrefixes()
    {
        var devices = JsonDocument.Parse(@"[{
            ""type"": ""udm"",
            ""network_table"": [
                { ""_id"": ""static"", ""name"": ""Security"", ""vlan"": 42, ""ip_subnet"": ""192.0.2.1/24"",
                  ""ipv6_interface_type"": ""static"", ""ipv6_subnet"": ""2001:db8:42::1/64"" },
                { ""_id"": ""pd"", ""name"": ""Gaming"", ""vlan"": 69, ""ip_subnet"": ""198.51.100.1/24"",
                  ""ipv6_interface_type"": ""pd"", ""ipv6_subnets"": [""2001:db8:69::1/64""] },
                { ""_id"": ""none"", ""name"": ""IoT"", ""vlan"": 64, ""ip_subnet"": ""203.0.113.1/24"",
                  ""ipv6_interface_type"": ""none"", ""ipv6_ra_enabled"": true },
                { ""_id"": ""legacy"", ""name"": ""Home"", ""vlan"": 10, ""ip_subnet"": ""192.0.2.129/25"" }
            ]
        }]").RootElement;

        var networks = _vlanAnalyzer.ExtractNetworks(devices).ToDictionary(n => n.Id);

        networks["static"].HasIpv6.Should().BeTrue();
        networks["static"].Ipv6Subnets.Should().Equal("2001:db8:42::/64");
        networks["pd"].HasIpv6.Should().BeTrue();
        networks["pd"].Ipv6Subnets.Should().Equal("2001:db8:69::/64");
        networks["none"].HasIpv6.Should().BeFalse();
        networks["none"].IsIpv6Evaluable.Should().BeFalse();
        networks["legacy"].HasIpv6.Should().BeFalse();
        networks["legacy"].Ipv6Subnets.Should().BeNull();
    }

    [Fact]
    public void NetworkInfoFromConfig_DelegatedPrefixWithoutSubnet_IsNotEvaluable()
    {
        var info = _vlanAnalyzer.NetworkInfoFromConfig(new UniFiNetworkConfig
        {
            Id = "pd", Name = "Gaming", Vlan = 69, IpSubnet = "198.51.100.1/24", Ipv6InterfaceType = "pd"
        });

        info.HasIpv6.Should().BeTrue();
        info.IsIpv6Evaluable.Should().BeFalse();
        info.EvaluableFamilies.Should().Equal(IpFamily.IPv4);
    }

    [Fact]
    public void WithPurpose_KeepsIpv6Fields()
    {
        var network = Net("n1", "Home", NetworkPurpose.Home, 10, "192.0.2.0/24", "2001:db8:10::/64");
        var copy = network.WithPurpose(NetworkPurpose.IoT, hasPurposeOverride: true);

        copy.HasIpv6.Should().BeTrue();
        copy.Ipv6Subnets.Should().Equal("2001:db8:10::/64");
        copy.Purpose.Should().Be(NetworkPurpose.IoT);
    }

    [Fact]
    public void WithPurpose_KeepsIpv6DnsServers()
    {
        var network = new NetworkInfo
        {
            Id = "n1", Name = "Home", VlanId = 10, HasIpv6 = true,
            Ipv6Subnets = ["2001:db8:10::/64"], Ipv6GatewayAddresses = ["2001:db8:10::1"],
            Ipv6DnsServers = ["2001:db8:10::53"]
        };

        var copy = network.WithPurpose(NetworkPurpose.IoT, hasPurposeOverride: true);

        copy.Ipv6DnsServers.Should().Equal("2001:db8:10::53");
        copy.Ipv6GatewayAddresses.Should().Equal("2001:db8:10::1");
    }

    [Fact]
    public void ExtractNetworks_ReadsCustomIpv6DnsOnlyWhenAutoIsOff()
    {
        var devices = JsonDocument.Parse(@"[{
            ""type"": ""udm"",
            ""network_table"": [
                { ""_id"": ""custom"", ""name"": ""Home"", ""vlan"": 10, ""ip_subnet"": ""192.0.2.1/24"",
                  ""dhcpd_dns_enabled"": true, ""dhcpd_dns_1"": ""192.0.2.53"",
                  ""ipv6_interface_type"": ""static"", ""ipv6_subnet"": ""2001:db8:10::1/64"",
                  ""dhcpdv6_dns_auto"": false, ""dhcpdv6_dns_1"": ""2001:4860:4860::8888"", ""dhcpdv6_dns_2"": """" },
                { ""_id"": ""auto"", ""name"": ""IoT"", ""vlan"": 20, ""ip_subnet"": ""198.51.100.1/24"",
                  ""ipv6_interface_type"": ""static"", ""ipv6_subnet"": ""2001:db8:20::1/64"",
                  ""dhcpdv6_dns_auto"": true, ""dhcpdv6_dns_1"": ""2001:4860:4860::8888"" },
                { ""_id"": ""absent"", ""name"": ""Guest"", ""vlan"": 30, ""ip_subnet"": ""203.0.113.1/24"",
                  ""ipv6_interface_type"": ""static"", ""ipv6_subnet"": ""2001:db8:30::1/64"",
                  ""dhcpdv6_dns_1"": ""2001:4860:4860::8888"" },
                { ""_id"": ""v6off"", ""name"": ""Lab"", ""vlan"": 40, ""ip_subnet"": ""192.0.2.129/25"",
                  ""ipv6_interface_type"": ""none"",
                  ""dhcpdv6_dns_auto"": false, ""dhcpdv6_dns_1"": ""2001:4860:4860::8888"" }
            ]
        }]").RootElement;

        var networks = _vlanAnalyzer.ExtractNetworks(devices).ToDictionary(n => n.Id);

        networks["custom"].Ipv6DnsServers.Should().Equal("2001:4860:4860::8888");
        networks["custom"].DnsServers.Should().Equal("192.0.2.53"); // IPv4 DNS unchanged
        networks["auto"].Ipv6DnsServers.Should().BeNull();
        networks["absent"].Ipv6DnsServers.Should().BeNull();
        networks["v6off"].Ipv6DnsServers.Should().BeNull();
    }

    [Fact]
    public void NetworkInfoFromConfig_ReadsCustomIpv6Dns()
    {
        var info = _vlanAnalyzer.NetworkInfoFromConfig(new UniFiNetworkConfig
        {
            Id = "n1", Name = "Home", Vlan = 10, IpSubnet = "192.0.2.1/24",
            DhcpdDnsEnabled = true, DhcpdDns1 = "192.0.2.53",
            Ipv6InterfaceType = "static", Ipv6Subnet = "2001:db8:10::1/64",
            Dhcpdv6DnsAuto = false, Dhcpdv6Dns1 = "2001:db8:10::53", Dhcpdv6Dns2 = "2620:fe::fe"
        });

        info.Ipv6DnsServers.Should().Equal("2001:db8:10::53", "2620:fe::fe");
        info.DnsServers.Should().Equal("192.0.2.53");
    }

    [Theory]
    [InlineData(true, false, new[] { "2001:db8::53" }, new[] { "2001:db8::53" })]
    [InlineData(true, false, new[] { " 2001:db8::53 ", "2001:db8::53" }, new[] { "2001:db8::53" })] // Trimmed, deduplicated
    [InlineData(true, false, new[] { "192.0.2.53", "not-an-ip", "" }, null)]                           // IPv4 and junk never enter the IPv6 list
    [InlineData(true, true, new[] { "2001:db8::53" }, null)]                                            // Auto hands out the gateway
    [InlineData(false, false, new[] { "2001:db8::53" }, null)]                                          // IPv6 off
    public void SelectIpv6DnsServers_ReturnsExpected(bool hasIpv6, bool dnsAuto, string[] servers, string[]? expected)
    {
        var result = VlanAnalyzer.SelectIpv6DnsServers(hasIpv6, dnsAuto, servers);

        if (expected == null)
            result.Should().BeNull();
        else
            result.Should().Equal(expected);
    }

    [Fact]
    public void ExtractNetworks_AdvancedBackToAuto_IgnoresTheStoredServer()
    {
        // Live console shape: switching IPv6 Advanced back to Auto sets dhcpdv6_dns_auto true
        // but leaves the old server stored in dhcpdv6_dns_1
        var devices = JsonDocument.Parse(@"[{
            ""type"": ""uxg"",
            ""network_table"": [
                { ""_id"": ""pd"", ""name"": ""Gaming"", ""vlan"": 69, ""ip_subnet"": ""198.51.100.1/24"",
                  ""ipv6_interface_type"": ""pd"", ""ipv6_subnets"": [""2001:db8:69::1/64""],
                  ""ipv6_setting_preference"": ""auto"", ""dhcpdv6_dns_auto"": true,
                  ""dhcpdv6_dns_1"": ""2606:4700:4700::1111"", ""dhcpdv6_dns_2"": """" }
            ]
        }]").RootElement;

        _vlanAnalyzer.ExtractNetworks(devices).Single().Ipv6DnsServers.Should().BeNull();
    }

    [Fact]
    public void ExtractNetworks_ReadsTheGatewaysIpv6Addresses()
    {
        var devices = JsonDocument.Parse(@"[{
            ""type"": ""uxg"",
            ""network_table"": [
                { ""_id"": ""pd"", ""name"": ""Gaming"", ""vlan"": 69, ""ip_subnet"": ""198.51.100.1/24"",
                  ""ipv6_interface_type"": ""pd"", ""ipv6_subnets"": [""2001:db8:69::1/64""],
                  ""ipv6_link_local_address"": ""fe80::1e0b:8bff:fe00:1"" }
            ]
        }]").RootElement;

        var network = _vlanAnalyzer.ExtractNetworks(devices).Single();

        network.Ipv6GatewayAddresses.Should().Equal("fe80::1e0b:8bff:fe00:1", "2001:db8:69::1");
        network.Ipv6Subnets.Should().Equal("2001:db8:69::/64");
    }

    [Theory]
    [InlineData("2001:db8:1::1/64", "2001:db8:1::1")]
    [InlineData("2001:db8:1:0:0:0:0:1/64", "2001:db8:1::1")] // Canonical form
    [InlineData("2001:db8:1::/64", null)]                    // Network form names no gateway
    [InlineData("192.0.2.1/24", null)]                       // IPv4 is not an IPv6 gateway
    [InlineData("garbage", null)]
    public void ExtractIpv6GatewayAddresses_ReturnsExpected(string prefix, string? expected)
    {
        var result = VlanAnalyzer.ExtractIpv6GatewayAddresses([prefix]);

        if (expected == null)
            result.Should().BeNull();
        else
            result.Should().Equal(expected);
    }

    [Fact]
    public void ExtractIpv6GatewayAddresses_IgnoresANonLinkLocalLinkLocalField()
    {
        VlanAnalyzer.ExtractIpv6GatewayAddresses([], "2001:db8::1").Should().BeNull();
    }

    [Theory]
    [InlineData(null, true)]                          // Auto DNS Server
    [InlineData(new[] { "2001:db8:10::1" }, true)]    // The gateway's own address
    [InlineData(new[] { "fd00:10::53" }, false)]      // A LAN resolver
    public void HandsOutGatewayOverIpv6_ReturnsExpected(string[]? ipv6Dns, bool expected)
    {
        var network = new NetworkInfo
        {
            Id = "n1", Name = "Home", VlanId = 10, HasIpv6 = true,
            Ipv6GatewayAddresses = ["2001:db8:10::1"], Ipv6DnsServers = ipv6Dns?.ToList()
        };

        ThirdPartyDnsDetector.HandsOutGatewayOverIpv6(network).Should().Be(expected);
    }

    [Fact]
    public void HandsOutGatewayOverIpv6_NetworkWithoutIpv6_IsFalse()
    {
        ThirdPartyDnsDetector.HandsOutGatewayOverIpv6(new NetworkInfo { Id = "n1", Name = "Home", VlanId = 10 })
            .Should().BeFalse();
    }

    [Fact]
    public void SelectIpv6DnsServers_AbsentAutoFlag_MeansAuto()
    {
        VlanAnalyzer.SelectIpv6DnsServers(true, null, ["2001:db8::53"]).Should().BeNull();
    }

    [Fact]
    public void UniFiNetworkConfig_UnreadIpv6DnsFields_AreNotSerialized()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new UniFiNetworkConfig { Id = "n1", Name = "Home" });

        json.Should().NotContain("dhcpdv6_dns");
    }

    #endregion

    #region FirewallRuleEvaluator

    [Fact]
    public void Evaluate_WithFamily_IgnoresRulesOfTheOtherFamily()
    {
        var rules = new List<FirewallRule>
        {
            Rule("allow-v6", "ALLOW", 1, "IPV6"),
            Rule("block-all", "BLOCK", 2, "BOTH")
        };

        FirewallRuleEvaluator.Evaluate(rules, _ => true, family: IpFamily.IPv4).EffectiveRule!.Id.Should().Be("block-all");
        FirewallRuleEvaluator.Evaluate(rules, _ => true, family: IpFamily.IPv6).EffectiveRule!.Id.Should().Be("allow-v6");
        FirewallRuleEvaluator.Evaluate(rules, _ => true).EffectiveRule!.Id.Should().Be("allow-v6");
    }

    #endregion

    #region VlanAnalyzer.AnalyzeNetworkIsolation

    [Fact]
    public void AnalyzeNetworkIsolation_Ipv4OnlyBlock_NetworkWithoutIpv6_IsIsolated()
    {
        var networks = new List<NetworkInfo>
        {
            Net("sec", "Security", NetworkPurpose.Security, 42, "192.0.2.0/24"),
            Net("home", "Home", NetworkPurpose.Home, 10, "198.51.100.0/24")
        };
        var rules = new List<FirewallRule> { BroadBlockFrom("sec", "IPV4") };

        _vlanAnalyzer.AnalyzeNetworkIsolation(networks, firewallRules: rules).Should().BeEmpty();
    }

    [Fact]
    public void AnalyzeNetworkIsolation_Ipv4OnlyBlock_DualStackPair_ReportsNotIsolatedOverIpv6()
    {
        var networks = DualStackSecurityAndHome();
        var rules = new List<FirewallRule> { BroadBlockFrom("sec", "IPV4") };

        var issue = _vlanAnalyzer.AnalyzeNetworkIsolation(networks, firewallRules: rules).Should().ContainSingle().Subject;
        issue.Type.Should().Be(IssueTypes.SecurityNetworkNotIsolated);
        issue.Message.Should().Be("Security/Camera VLAN 'Security' is not isolated over IPv6");
        IpFamilyText.IsIpv6Only(issue.Metadata).Should().BeTrue();
    }

    [Theory]
    [InlineData("BOTH")]
    [InlineData(null)]
    public void AnalyzeNetworkIsolation_BlockCoveringBothFamilies_DualStackPair_IsIsolated(string? ipVersion)
    {
        var rules = new List<FirewallRule> { BroadBlockFrom("sec", ipVersion) };
        _vlanAnalyzer.AnalyzeNetworkIsolation(DualStackSecurityAndHome(), firewallRules: rules).Should().BeEmpty();
    }

    [Fact]
    public void AnalyzeNetworkIsolation_SeparateIpv4AndIpv6Blocks_DualStackPair_IsIsolated()
    {
        var rules = new List<FirewallRule> { BroadBlockFrom("sec", "IPV4", index: 1), BroadBlockFrom("sec", "IPV6", index: 2) };
        _vlanAnalyzer.AnalyzeNetworkIsolation(DualStackSecurityAndHome(), firewallRules: rules).Should().BeEmpty();
    }

    [Fact]
    public void AnalyzeNetworkIsolation_Ipv6OnlyBlock_ReportsTheIpv4Finding()
    {
        // An IPv6-only block used to count as isolation for IPv4 traffic too
        var networks = new List<NetworkInfo>
        {
            Net("sec", "Security", NetworkPurpose.Security, 42, "192.0.2.0/24"),
            Net("home", "Home", NetworkPurpose.Home, 10, "198.51.100.0/24")
        };
        var rules = new List<FirewallRule> { BroadBlockFrom("sec", "IPV6") };

        var issue = _vlanAnalyzer.AnalyzeNetworkIsolation(networks, firewallRules: rules).Should().ContainSingle().Subject;
        issue.Message.Should().Be("Security/Camera VLAN 'Security' is not isolated");
        IpFamilyText.IsIpv6Only(issue.Metadata).Should().BeFalse();
    }

    [Fact]
    public void AnalyzeNetworkIsolation_Ipv6AllowAheadOfBlock_ReportsOverIpv6Only()
    {
        var rules = new List<FirewallRule>
        {
            Rule("allow-v6", "ALLOW", 1, "IPV6", srcTarget: "NETWORK", srcNets: ["sec"]),
            BroadBlockFrom("sec", "BOTH", index: 2)
        };

        var issue = _vlanAnalyzer.AnalyzeNetworkIsolation(DualStackSecurityAndHome(), firewallRules: rules).Should().ContainSingle().Subject;
        issue.Message.Should().EndWith("over IPv6");
    }

    [Fact]
    public void AnalyzeNetworkIsolation_OnlyNetworkWithIpv6_HasNoIpv6PeerToReach()
    {
        var networks = new List<NetworkInfo>
        {
            Net("sec", "Security", NetworkPurpose.Security, 42, "192.0.2.0/24", "2001:db8:42::/64"),
            Net("home", "Home", NetworkPurpose.Home, 10, "198.51.100.0/24")
        };
        var rules = new List<FirewallRule> { BroadBlockFrom("sec", "IPV4") };

        _vlanAnalyzer.AnalyzeNetworkIsolation(networks, firewallRules: rules).Should().BeEmpty();
    }

    [Fact]
    public void AnalyzeNetworkIsolation_IsolationSettingOn_TrustsItForIpv6()
    {
        var networks = new List<NetworkInfo>
        {
            Net("sec", "Security", NetworkPurpose.Security, 42, "192.0.2.0/24", "2001:db8:42::/64", isolation: true),
            Net("home", "Home", NetworkPurpose.Home, 10, "198.51.100.0/24", "2001:db8:10::/64")
        };

        _vlanAnalyzer.AnalyzeNetworkIsolation(networks, firewallRules: []).Should().BeEmpty();
    }

    [Fact]
    public void AnalyzeNetworkIsolation_PdIsolationRuleForAnotherNetwork_DoesNotIsolateThisOne()
    {
        // The API reports the PD rule as Internal ANY -> Internal ANY; read literally it would
        // isolate Security over IPv6 and hide the gap
        var pdRule = _parser.ParseFirewallPolicy(PdIsolationPolicy("Isolate IPv6 traffic from PD interface br69 to any local subnet"))!;
        var networks = DualStackSecurityAndHome();
        networks.Add(Net("pd", "Gaming", NetworkPurpose.Gaming, 69, "203.0.113.0/24", "2001:db8:69::/64"));
        var rules = new List<FirewallRule> { BroadBlockFrom("sec", "IPV4"), pdRule };

        var issue = _vlanAnalyzer.AnalyzeNetworkIsolation(networks, firewallRules: rules).Should().ContainSingle().Subject;
        issue.CurrentNetwork.Should().Be("Security");
        issue.Message.Should().EndWith("over IPv6");
    }

    #endregion

    #region VlanAnalyzer.AnalyzeInternetAccess

    [Fact]
    public void AnalyzeInternetAccess_Ipv4OnlyInternetBlock_DualStack_ReportsInternetOverIpv6()
    {
        var mgmt = Net("mgmt", "Management", NetworkPurpose.Management, 99, "192.0.2.0/24", "2001:db8:99::/64");
        var rules = new List<FirewallRule> { InternetBlockFrom("mgmt", "IPV4") };

        var issue = _vlanAnalyzer.AnalyzeInternetAccess([mgmt], firewallRules: rules, externalZoneId: ExternalZone, firewallAnalyzer: _firewallAnalyzer)
            .Should().ContainSingle().Subject;
        issue.Type.Should().Be(IssueTypes.MgmtNetworkHasInternet);
        issue.Message.Should().Be("Management VLAN 'Management' has internet access enabled over IPv6");
        IpFamilyText.IsIpv6Only(issue.Metadata).Should().BeTrue();
    }

    [Fact]
    public void AnalyzeInternetAccess_Ipv4OnlyInternetBlock_NoIpv6_ReportsNothing()
    {
        var mgmt = Net("mgmt", "Management", NetworkPurpose.Management, 99, "192.0.2.0/24");
        var rules = new List<FirewallRule> { InternetBlockFrom("mgmt", "IPV4") };

        _vlanAnalyzer.AnalyzeInternetAccess([mgmt], firewallRules: rules, externalZoneId: ExternalZone, firewallAnalyzer: _firewallAnalyzer)
            .Should().BeEmpty();
    }

    [Fact]
    public void AnalyzeInternetAccess_PerFamilyInternetBlocks_DualStack_ReportsNothing()
    {
        // The shape UniFi generates when internet access is disabled on a static-IPv6 network
        var mgmt = Net("mgmt", "Management", NetworkPurpose.Management, 99, "192.0.2.0/24", "2001:db8:99::/64");
        var rules = new List<FirewallRule>
        {
            Rule("v4", "BLOCK", 30002, "IPV4", srcTarget: "IP", srcIps: ["192.0.2.0/24"], dstZone: ExternalZone),
            Rule("v6", "BLOCK", 30004, "IPV6", srcTarget: "IP", srcIps: ["2001:db8:99::/64"], dstZone: ExternalZone)
        };

        _vlanAnalyzer.AnalyzeInternetAccess([mgmt], firewallRules: rules, externalZoneId: ExternalZone, firewallAnalyzer: _firewallAnalyzer)
            .Should().BeEmpty();
    }

    [Fact]
    public void AnalyzeInternetAccess_Ipv6OnlyInternetBlock_ReportsTheIpv4Finding()
    {
        var mgmt = Net("mgmt", "Management", NetworkPurpose.Management, 99, "192.0.2.0/24");
        var rules = new List<FirewallRule> { InternetBlockFrom("mgmt", "IPV6") };

        var issue = _vlanAnalyzer.AnalyzeInternetAccess([mgmt], firewallRules: rules, externalZoneId: ExternalZone, firewallAnalyzer: _firewallAnalyzer)
            .Should().ContainSingle().Subject;
        issue.Message.Should().Be("Management VLAN 'Management' has internet access enabled");
    }

    #endregion

    #region FirewallRuleAnalyzer.CheckInterVlanIsolation

    [Fact]
    public void CheckInterVlanIsolation_Ipv4OnlyBlock_DualStackPair_ReportsMissingIsolationOverIpv6()
    {
        var networks = DualStackSecurityAndHome();
        var rules = new List<FirewallRule> { BlockToNetwork("home", "sec", "IPV4") };

        var issue = _firewallAnalyzer.CheckInterVlanIsolation(rules, networks)
            .Where(i => i.Type == IssueTypes.MissingIsolation).Should().ContainSingle().Subject;
        issue.Message.Should().Be("No rule blocking Home (Home) from reaching Security (Security) over IPv6");
        IpFamilyText.IsIpv6Only(issue.Metadata).Should().BeTrue();
    }

    [Fact]
    public void CheckInterVlanIsolation_Ipv4OnlyBlock_NoIpv6_ReportsNothing()
    {
        var networks = new List<NetworkInfo>
        {
            Net("sec", "Security", NetworkPurpose.Security, 42, "192.0.2.0/24"),
            Net("home", "Home", NetworkPurpose.Home, 10, "198.51.100.0/24")
        };
        var rules = new List<FirewallRule> { BlockToNetwork("home", "sec", "IPV4") };

        _firewallAnalyzer.CheckInterVlanIsolation(rules, networks).Should().NotContain(i => i.Type == IssueTypes.MissingIsolation);
    }

    [Fact]
    public void CheckInterVlanIsolation_Ipv6OnlyBlock_ReportsTheIpv4Finding()
    {
        var networks = new List<NetworkInfo>
        {
            Net("sec", "Security", NetworkPurpose.Security, 42, "192.0.2.0/24"),
            Net("home", "Home", NetworkPurpose.Home, 10, "198.51.100.0/24")
        };
        var rules = new List<FirewallRule> { BlockToNetwork("home", "sec", "IPV6") };

        var issue = _firewallAnalyzer.CheckInterVlanIsolation(rules, networks)
            .Where(i => i.Type == IssueTypes.MissingIsolation).Should().ContainSingle().Subject;
        issue.Message.Should().NotContain("IPv6");
    }

    [Fact]
    public void CheckInterVlanIsolation_BothFamiliesBlocked_DualStackPair_ReportsNothing()
    {
        var rules = new List<FirewallRule> { BlockToNetwork("home", "sec", "BOTH") };
        _firewallAnalyzer.CheckInterVlanIsolation(rules, DualStackSecurityAndHome())
            .Should().NotContain(i => i.Type == IssueTypes.MissingIsolation);
    }

    [Fact]
    public void CheckInterVlanIsolation_Ipv6OnlyAllowAheadOfBlock_ReportsBypassOverIpv6()
    {
        var rules = new List<FirewallRule>
        {
            Rule("allow-v6", "ALLOW", 1, "IPV6", srcTarget: "NETWORK", srcNets: ["home"], dstTarget: "NETWORK", dstNets: ["sec"]),
            BlockToNetwork("home", "sec", "BOTH", index: 2)
        };

        var issues = _firewallAnalyzer.CheckInterVlanIsolation(rules, DualStackSecurityAndHome());

        var bypass = issues.Where(i => i.Type == IssueTypes.IsolationBypassed).Should().ContainSingle().Subject;
        bypass.Message.Should().Contain("to Security (Security) over IPv6 which should be isolated");
        IpFamilyText.IsIpv6Only(bypass.Metadata).Should().BeTrue();
        issues.Should().NotContain(i => i.Type == IssueTypes.MissingIsolation);
    }

    [Fact]
    public void CheckInterVlanIsolation_Ipv6OnlyAllow_WithoutIpv6_IsNotABypass()
    {
        // An IPv6-only allow used to eclipse the IPv4 block and read as a bypass
        var networks = new List<NetworkInfo>
        {
            Net("sec", "Security", NetworkPurpose.Security, 42, "192.0.2.0/24"),
            Net("home", "Home", NetworkPurpose.Home, 10, "198.51.100.0/24")
        };
        var rules = new List<FirewallRule>
        {
            Rule("allow-v6", "ALLOW", 1, "IPV6", srcTarget: "NETWORK", srcNets: ["home"], dstTarget: "NETWORK", dstNets: ["sec"]),
            BlockToNetwork("home", "sec", "BOTH", index: 2)
        };

        _firewallAnalyzer.CheckInterVlanIsolation(rules, networks).Should().BeEmpty();
    }

    [Fact]
    public void CheckInterVlanIsolation_BothFamilyAllow_DualStackPair_ReportsOneBypass()
    {
        var rules = new List<FirewallRule>
        {
            Rule("allow", "ALLOW", 1, "BOTH", srcTarget: "NETWORK", srcNets: ["home"], dstTarget: "NETWORK", dstNets: ["sec"]),
            BlockToNetwork("home", "sec", "BOTH", index: 2)
        };

        var bypass = _firewallAnalyzer.CheckInterVlanIsolation(rules, DualStackSecurityAndHome())
            .Where(i => i.Type == IssueTypes.IsolationBypassed).Should().ContainSingle().Subject;
        bypass.Message.Should().NotContain("IPv6");
    }

    #endregion

    #region FirewallRuleAnalyzer.CheckInternetDisabledBroadAllow

    [Fact]
    public void CheckInternetDisabledBroadAllow_Ipv6OnlyAllow_DualStack_ReportsOverIpv6()
    {
        var mgmt = Net("mgmt", "Management", NetworkPurpose.Management, 99, "192.0.2.0/24", "2001:db8:99::/64", internet: false);
        var rules = new List<FirewallRule> { InternetAllowFrom("mgmt", "IPV6") };

        var issue = _firewallAnalyzer.CheckInternetDisabledBroadAllow(rules, [mgmt], ExternalZone).Should().ContainSingle().Subject;
        issue.Message.Should().StartWith("Network 'Management' has internet disabled over IPv6 but rule 'allow-internet' allows");
        IpFamilyText.IsIpv6Only(issue.Metadata).Should().BeTrue();
    }

    [Fact]
    public void CheckInternetDisabledBroadAllow_Ipv6OnlyAllow_NoIpv6_ReportsNothing()
    {
        var mgmt = Net("mgmt", "Management", NetworkPurpose.Management, 99, "192.0.2.0/24", internet: false);
        var rules = new List<FirewallRule> { InternetAllowFrom("mgmt", "IPV6") };

        _firewallAnalyzer.CheckInternetDisabledBroadAllow(rules, [mgmt], ExternalZone).Should().BeEmpty();
    }

    [Fact]
    public void CheckInternetDisabledBroadAllow_BothFamilyAllow_DualStack_ReportsOnceOverIpv4()
    {
        var mgmt = Net("mgmt", "Management", NetworkPurpose.Management, 99, "192.0.2.0/24", "2001:db8:99::/64", internet: false);
        var rules = new List<FirewallRule> { InternetAllowFrom("mgmt", "BOTH") };

        var issue = _firewallAnalyzer.CheckInternetDisabledBroadAllow(rules, [mgmt], ExternalZone).Should().ContainSingle().Subject;
        issue.Message.Should().StartWith("Network 'Management' has internet disabled but rule");
        IpFamilyText.IsIpv6Only(issue.Metadata).Should().BeFalse();
    }

    [Fact]
    public void CheckInternetDisabledBroadAllow_Ipv6OnlyBlockAheadOfAllow_DoesNotEclipseIpv4()
    {
        var mgmt = Net("mgmt", "Management", NetworkPurpose.Management, 99, "192.0.2.0/24", internet: false);
        var rules = new List<FirewallRule> { InternetBlockFrom("mgmt", "IPV6", index: 1), InternetAllowFrom("mgmt", "BOTH", index: 2) };

        _firewallAnalyzer.CheckInternetDisabledBroadAllow(rules, [mgmt], ExternalZone).Should().ContainSingle();
    }

    #endregion

    #region FirewallRuleAnalyzer.AnalyzeManagementNetworkFirewallAccess (IPv4 only)

    [Fact]
    public void AnalyzeManagementNetworkFirewallAccess_Ipv6OnlyUniFiAllow_StillMissingAccess()
    {
        var mgmt = Net("mgmt", "Management", NetworkPurpose.Management, 99, "192.0.2.0/24", "2001:db8:99::/64", internet: false);
        var rules = new List<FirewallRule> { UniFiCloudAllow("mgmt", "IPV6") };

        _firewallAnalyzer.AnalyzeManagementNetworkFirewallAccess(rules, [mgmt], externalZoneId: ExternalZone)
            .Should().Contain(i => i.Type == IssueTypes.MgmtMissingUnifiAccess);
    }

    [Theory]
    [InlineData("IPV4")]
    [InlineData("BOTH")]
    public void AnalyzeManagementNetworkFirewallAccess_Ipv4CapableUniFiAllow_HasAccess(string ipVersion)
    {
        var mgmt = Net("mgmt", "Management", NetworkPurpose.Management, 99, "192.0.2.0/24", "2001:db8:99::/64", internet: false);
        var rules = new List<FirewallRule> { UniFiCloudAllow("mgmt", ipVersion) };

        _firewallAnalyzer.AnalyzeManagementNetworkFirewallAccess(rules, [mgmt], externalZoneId: ExternalZone)
            .Should().NotContain(i => i.Type == IssueTypes.MgmtMissingUnifiAccess);
    }

    [Fact]
    public void AnalyzeManagementNetworkFirewallAccess_Ipv6OnlyBlockAheadOfAllow_DoesNotEclipseIt()
    {
        var mgmt = Net("mgmt", "Management", NetworkPurpose.Management, 99, "192.0.2.0/24", internet: false);
        var rules = new List<FirewallRule>
        {
            InternetBlockFrom("mgmt", "IPV6", index: 1),
            UniFiCloudAllow("mgmt", "BOTH", index: 2)
        };

        _firewallAnalyzer.AnalyzeManagementNetworkFirewallAccess(rules, [mgmt], externalZoneId: ExternalZone)
            .Should().NotContain(i => i.Type == IssueTypes.MgmtMissingUnifiAccess);
    }

    [Fact]
    public void AnalyzeManagementNetworkFirewallAccess_Ipv4BlockAheadOfAllow_StillEclipsesIt()
    {
        var mgmt = Net("mgmt", "Management", NetworkPurpose.Management, 99, "192.0.2.0/24", internet: false);
        var rules = new List<FirewallRule>
        {
            InternetBlockFrom("mgmt", "IPV4", index: 1),
            UniFiCloudAllow("mgmt", "BOTH", index: 2)
        };

        _firewallAnalyzer.AnalyzeManagementNetworkFirewallAccess(rules, [mgmt], externalZoneId: ExternalZone)
            .Should().Contain(i => i.Type == IssueTypes.MgmtMissingUnifiAccess);
    }

    #endregion

    #region Helpers

    private static List<NetworkInfo> DualStackSecurityAndHome() =>
    [
        Net("sec", "Security", NetworkPurpose.Security, 42, "192.0.2.0/24", "2001:db8:42::/64"),
        Net("home", "Home", NetworkPurpose.Home, 10, "198.51.100.0/24", "2001:db8:10::/64")
    ];

    private static NetworkInfo Net(string id, string name, NetworkPurpose purpose, int vlan, string subnet,
        string? ipv6Prefix = null, bool isolation = false, bool internet = true) => new()
    {
        Id = id,
        Name = name,
        VlanId = vlan,
        Purpose = purpose,
        Subnet = subnet,
        NetworkIsolationEnabled = isolation,
        InternetAccessEnabled = internet,
        FirewallZoneId = InternalZone,
        HasIpv6 = ipv6Prefix != null,
        Ipv6Subnets = ipv6Prefix == null ? null : [ipv6Prefix]
    };

    private static FirewallRule Rule(string id, string action, int index, string? ipVersion,
        string srcTarget = "ANY", List<string>? srcNets = null, List<string>? srcIps = null, bool srcOpposite = false,
        string dstTarget = "ANY", List<string>? dstNets = null, string dstZone = InternalZone,
        string protocol = "all", List<string>? webDomains = null) => new()
    {
        Id = id,
        Name = id,
        Action = action,
        Enabled = true,
        Index = index,
        IpVersion = ipVersion,
        Protocol = protocol,
        SourceMatchingTarget = srcTarget,
        SourceNetworkIds = srcNets,
        SourceIps = srcIps,
        SourceMatchOppositeIps = srcOpposite,
        SourceZoneId = InternalZone,
        DestinationMatchingTarget = dstTarget,
        DestinationNetworkIds = dstNets,
        DestinationZoneId = dstZone,
        WebDomains = webDomains
    };

    private static FirewallRule BroadBlockFrom(string networkId, string? ipVersion, int index = 1) =>
        Rule($"block-{networkId}-{ipVersion ?? "none"}", "BLOCK", index, ipVersion, srcTarget: "NETWORK", srcNets: [networkId]);

    private static FirewallRule BlockToNetwork(string sourceId, string destId, string ipVersion, int index = 1) =>
        Rule($"block-{sourceId}-{destId}-{ipVersion}", "BLOCK", index, ipVersion,
            srcTarget: "NETWORK", srcNets: [sourceId], dstTarget: "NETWORK", dstNets: [destId]);

    private static FirewallRule InternetBlockFrom(string networkId, string ipVersion, int index = 1) =>
        Rule($"block-internet-{ipVersion}", "BLOCK", index, ipVersion, srcTarget: "NETWORK", srcNets: [networkId], dstZone: ExternalZone);

    private static FirewallRule InternetAllowFrom(string networkId, string ipVersion, int index = 1) =>
        Rule("allow-internet", "ALLOW", index, ipVersion, srcTarget: "NETWORK", srcNets: [networkId], dstZone: ExternalZone);

    private static FirewallRule UniFiCloudAllow(string networkId, string ipVersion, int index = 1) =>
        Rule("allow-unifi", "ALLOW", index, ipVersion, srcTarget: "NETWORK", srcNets: [networkId],
            dstTarget: "WEB", dstZone: ExternalZone, protocol: "tcp", webDomains: ["ui.com"]);

    private static JsonElement PdIsolationPolicy(string name) => JsonDocument.Parse($@"{{
        ""_id"": ""pd-isolation"",
        ""name"": ""{name}"",
        ""action"": ""BLOCK"",
        ""enabled"": true,
        ""index"": 30015,
        ""predefined"": true,
        ""ip_version"": ""IPV6"",
        ""origin_type"": ""network_config"",
        ""protocol"": ""all"",
        ""connection_state_type"": ""ALL"",
        ""source"": {{ ""matching_target"": ""ANY"", ""zone_id"": ""{InternalZone}"" }},
        ""destination"": {{ ""matching_target"": ""ANY"", ""zone_id"": ""{InternalZone}"" }}
    }}").RootElement;

    #endregion
}
