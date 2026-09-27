using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using NetworkOptimizer.Audit.Dns;
using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.UniFi.Models;
using Xunit;

namespace NetworkOptimizer.Audit.Tests.Dns;

/// <summary>
/// IPv6 resolver checks (#180): IPv6 DNAT redirect targets, third-party LAN DNS bypassed over IPv6,
/// and WAN IPv6 DNS. Each IPv6 finding is tagged IPv6-only, and IPv4-only configurations produce none.
/// </summary>
[Collection("ThirdPartyDns")]
public class DnsIpv6ResolverTests : IDisposable
{
    private readonly DnsSecurityAnalyzer _analyzer;

    public DnsIpv6ResolverTests()
    {
        DohProviderRegistry.DnsResolver = _ => Task.FromResult<string?>(null);

        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage { StatusCode = HttpStatusCode.NotFound });
        var detector = new ThirdPartyDnsDetector(Mock.Of<ILogger<ThirdPartyDnsDetector>>(), new HttpClient(handler.Object));
        _analyzer = new DnsSecurityAnalyzer(Mock.Of<ILogger<DnsSecurityAnalyzer>>(), detector);
    }

    public void Dispose() => DohProviderRegistry.ResetDnsResolver();

    #region DNAT redirect targets over IPv6

    [Theory]
    [InlineData("2001:db8:10::1")]
    [InlineData("2001:db8:10:0:0:0:0:1")]          // Expanded form of the gateway address
    [InlineData("fe80::1e0b:8bff:fe00:1")]          // The gateway's link-local address
    public async Task Ipv6DnatToGateway_WithDoh_ReportsNothing(string target)
    {
        var result = await Analyze([DualStack()], nat: Dnat("IPV6", target));

        result.Ipv6InvalidDnatRules.Should().BeEmpty();
        result.Issues.Should().NotContain(i => i.Type == IssueTypes.DnsDnatWrongDestination);
    }

    [Fact]
    public async Task Ipv6DnatToWrongAddress_ReportsWrongDestinationOverIpv6()
    {
        var result = await Analyze([DualStack()], nat: Dnat("IPV6", "2001:db8:99::53"));

        result.Ipv6InvalidDnatRules.Should().ContainSingle().Which.Should().Contain("redirects to 2001:db8:99::53");
        var issue = result.Issues.Where(i => i.Type == IssueTypes.DnsDnatWrongDestination).Should().ContainSingle().Subject;
        issue.Message.Should().StartWith("DNAT DNS rules have incorrect translated IP address over IPv6.");
        IpFamilyText.IsIpv6Only(issue.Metadata).Should().BeTrue();
        result.DnatRedirectTargetIsValid.Should().BeTrue(); // The IPv4 verdict is untouched
        _analyzer.GetSummary(result).FullyProtected.Should().BeFalse();
    }

    [Fact]
    public async Task Ipv6DnatWithCustomIpv6Dns_MustTargetThatServer()
    {
        var network = DualStack(ipv6Dns: ["2001:db8:10::53"]);

        (await Analyze([network], nat: Dnat("IPV6", "2001:db8:10::53"))).Ipv6InvalidDnatRules.Should().BeEmpty();
        (await Analyze([network], nat: Dnat("IPV6", "2001:db8:10::1"))).Ipv6InvalidDnatRules.Should().ContainSingle();
    }

    [Fact]
    public async Task Ipv6DnatWithNoKnownTarget_IsNotJudged()
    {
        // DoH off and the network hands out the gateway: no valid target can be proven
        var result = await Analyze([DualStack()], nat: Dnat("IPV6", "2001:db8:99::53"), doh: false);

        result.Ipv6InvalidDnatRules.Should().BeEmpty();
    }

    [Fact]
    public async Task Ipv6DnatToTrustedTarget_ReportsNothing()
    {
        var result = await Analyze([DualStack()], nat: Dnat("IPV6", "2001:db8:99::53"), trusted: ["2001:db8:99::53"]);

        result.Ipv6InvalidDnatRules.Should().BeEmpty();
    }

    [Fact]
    public async Task Ipv4DnatToWrongAddress_ReportsOnlyTheIpv4Finding()
    {
        var result = await Analyze([DualStack()], nat: Dnat("IPV4", "192.0.2.99"));

        result.Ipv6InvalidDnatRules.Should().BeEmpty();
        var issue = result.Issues.Where(i => i.Type == IssueTypes.DnsDnatWrongDestination).Should().ContainSingle().Subject;
        IpFamilyText.IsIpv6Only(issue.Metadata).Should().BeFalse();
    }

    #endregion

    #region Third-party LAN DNS over IPv6

    [Fact]
    public async Task ThirdPartyOverIpv4_GatewayOverIpv6_ReportsBypassOverIpv6()
    {
        var result = await Analyze([DualStack(ipv4Dns: ["192.168.10.53"])]);

        result.Ipv6ThirdPartyBypassNetworks.Should().Equal("Home");
        var issue = result.Issues.Where(i => i.Type == IssueTypes.DnsInconsistentConfig).Should().ContainSingle().Subject;
        issue.Message.Should().Be("Third-Party LAN DNS is bypassed over IPv6 on: Home. These networks give devices Third-Party LAN DNS as their DNS server over IPv4, but the gateway over IPv6. Devices with IPv6 usually ask the IPv6 server first, so their lookups skip Third-Party LAN DNS.");
        IpFamilyText.IsIpv6Only(issue.Metadata).Should().BeTrue();
        _analyzer.GetSummary(result).FullyProtected.Should().BeFalse();
    }

    [Fact]
    public async Task ThirdPartyOverIpv4_Ipv6DnsIsTheGatewayAddress_ReportsBypass()
    {
        var result = await Analyze([DualStack(ipv4Dns: ["192.168.10.53"], ipv6Dns: ["2001:db8:10::1"])]);

        result.Ipv6ThirdPartyBypassNetworks.Should().Equal("Home");
    }

    [Theory]
    [InlineData("fd00:10::53")]        // ULA resolver
    [InlineData("2001:db8:10::53")]    // Inside the network's prefix
    public async Task ThirdPartyOverBothFamilies_ReportsNoBypass(string ipv6Resolver)
    {
        var result = await Analyze([DualStack(ipv4Dns: ["192.168.10.53"], ipv6Dns: [ipv6Resolver])]);

        result.Ipv6ThirdPartyBypassNetworks.Should().BeEmpty();
        result.Ipv6ThirdPartyDnsServers.Should().ContainSingle().Which.DnsServerIp.Should().Be(ipv6Resolver);
        result.ThirdPartyDnsServers.Should().OnlyContain(t => t.DnsServerIp == "192.168.10.53"); // IPv4 list untouched
    }

    [Fact]
    public async Task ThirdPartyOverIpv4_NetworkWithoutIpv6_ReportsNothingOverIpv6()
    {
        var ipv4Only = new NetworkInfo
        {
            Id = "home", Name = "Home", VlanId = 10, Subnet = "192.168.10.0/24", Gateway = "192.168.10.1",
            DhcpEnabled = true, DnsServers = ["192.168.10.53"]
        };

        var result = await Analyze([ipv4Only]);

        result.HasThirdPartyDns.Should().BeTrue();
        result.Ipv6ThirdPartyBypassNetworks.Should().BeEmpty();
        result.Ipv6ThirdPartyDnsServers.Should().BeEmpty();
        result.Issues.Should().NotContain(i => IpFamilyText.IsIpv6Only(i.Metadata));
    }

    [Fact]
    public async Task ThirdPartyOverIpv4_Ipv6WithoutKnownPrefix_StillReportsBypass()
    {
        // The gateway is handed out by configuration (Auto DNS Server), which holds without a known prefix
        var pd = new NetworkInfo
        {
            Id = "home", Name = "Home", VlanId = 10, Subnet = "192.168.10.0/24", Gateway = "192.168.10.1",
            DhcpEnabled = true, DnsServers = ["192.168.10.53"], HasIpv6 = true
        };

        var result = await Analyze([pd]);

        result.Ipv6ThirdPartyBypassNetworks.Should().Equal("Home");
    }

    #endregion

    #region WAN DNS over IPv6

    [Fact]
    public async Task Ipv6Wan_IspDnsWithDoh_ReportsNoStaticDnsOverIpv6()
    {
        var result = await Analyze([DualStack()], wans: [Wan("WAN2", "dhcpv6", "auto")]);

        result.Ipv6WanNoStaticDns.Should().Equal("wan2");
        var issue = result.Issues.Where(i => i.Type == IssueTypes.DnsWanNoStatic && IpFamilyText.IsIpv6Only(i.Metadata))
            .Should().ContainSingle().Subject;
        issue.Message.Should().EndWith("has no static DNS configured over IPv6. If DoH fails, DNS queries will leak to your ISP's DNS servers.");
        issue.RecommendedAction.Should().Contain("Cloudflare");
    }

    [Fact]
    public async Task Ipv6Wan_StaticDnsMatchingDoh_ReportsNothing()
    {
        var result = await Analyze([DualStack()],
            wans: [Wan("WAN2", "dhcpv6", "manual", "2606:4700:4700::1111", "2606:4700:4700::1001")]);

        result.Ipv6WanNoStaticDns.Should().BeEmpty();
        result.Ipv6WanDnsMismatches.Should().BeEmpty();
    }

    [Fact]
    public async Task Ipv6Wan_StaticDnsOfAnotherProvider_ReportsMismatchOverIpv6()
    {
        var result = await Analyze([DualStack()],
            wans: [Wan("WAN2", "dhcpv6", "manual", "2001:4860:4860::8888")]);

        var issue = result.Issues.Where(i => i.Type == IssueTypes.DnsWanMismatch && IpFamilyText.IsIpv6Only(i.Metadata))
            .Should().ContainSingle().Subject;
        issue.Message.Should().Contain("2001:4860:4860::8888 (Google) instead of Cloudflare over IPv6");
        issue.RecommendedAction.Should().Be("Set IPv6 DNS to Cloudflare servers: 2606:4700:4700::1111, 2606:4700:4700::1001");
    }

    [Fact]
    public async Task Ipv6Wan_StaticDnsIsTheIpv6ThirdPartyResolver_ReportsNothing()
    {
        var network = DualStack(ipv4Dns: ["192.168.10.53"], ipv6Dns: ["fd00:10::53"]);

        var result = await Analyze([network], wans: [Wan("WAN2", "dhcpv6", "manual", "fd00:10::53")]);

        result.Ipv6WanDnsMismatches.Should().BeEmpty();
    }

    [Theory]
    [InlineData("2a07:a8c1::1", "2a07:a8c0::1", true)]   // dns2 entered first
    [InlineData("2a07:a8c0::1", "2a07:a8c1::1", false)]  // dns1 first
    public async Task Ipv6Wan_NextDnsOrder_ReportsWrongOrderOverIpv6(string first, string second, bool expectIssue)
    {
        DohProviderRegistry.DnsResolver = ip => Task.FromResult<string?>(
            ip.ToString().StartsWith("2a07:a8c0") ? "dns1.nextdns.io" : "dns2.nextdns.io");

        var result = await Analyze([DualStack()], wans: [Wan("WAN2", "dhcpv6", "manual", first, second)], dohServer: "NextDNS-abc123");

        result.Ipv6WanDnsMismatches.Should().BeEmpty();
        var orderIssues = result.Issues.Where(i => i.Type == IssueTypes.DnsWanOrder && IpFamilyText.IsIpv6Only(i.Metadata)).ToList();
        if (expectIssue)
            orderIssues.Should().ContainSingle().Which.Message.Should().EndWith($"DNS in wrong order over IPv6: {first}, {second}. Should be {second}, {first}");
        else
            orderIssues.Should().BeEmpty();
    }

    [Fact]
    public async Task Wan_Ipv6Disabled_ReportsNothingOverIpv6()
    {
        var result = await Analyze([DualStack()], wans: [Wan("WAN2", "disabled", "manual", "2001:4860:4860::8888")]);

        result.WanInterfaces.Should().ContainSingle().Which.HasIpv6.Should().BeFalse();
        result.Ipv6WanNoStaticDns.Should().BeEmpty();
        result.Ipv6WanDnsMismatches.Should().BeEmpty();
    }

    [Fact]
    public async Task Wan_Ipv6DnsNeverEntersTheIpv4DnsList()
    {
        var result = await Analyze([DualStack()],
            wans: [Wan("WAN2", "dhcpv6", "manual", "2606:4700:4700::1111", ipv4Dns1: "1.1.1.1", ipv4Dns2: "1.0.0.1")]);

        var wan = result.WanInterfaces.Should().ContainSingle().Subject;
        wan.DnsServers.Should().Equal("1.1.1.1", "1.0.0.1");
        wan.Ipv6DnsServers.Should().Equal("2606:4700:4700::1111");
        result.WanDnsServers.Should().Equal("1.1.1.1", "1.0.0.1");
        result.WanDnsMatchesDoH.Should().BeTrue();
    }

    #endregion

    #region Helpers

    private Task<DnsSecurityResult> Analyze(
        List<NetworkInfo> networks, JsonElement? nat = null, bool doh = true, List<string>? trusted = null,
        List<UniFiNetworkConfig>? wans = null, string dohServer = "cloudflare")
    {
        var settings = JsonDocument.Parse(doh
            ? $$"""[{ "key": "doh", "state": "auto", "server_names": ["{{dohServer}}"] }]"""
            : """[{ "key": "doh", "state": "disabled" }]""").RootElement;

        JsonElement? devices = null;
        if (wans != null)
        {
            var ports = string.Join(",", wans.Select(w =>
                $$"""{ "name": "{{w.WanNetworkgroup}}", "network_name": "{{w.WanNetworkgroup!.ToLowerInvariant()}}", "up": true }"""));
            devices = JsonDocument.Parse($$"""[{ "type": "uxg", "name": "Gateway", "port_table": [{{ports}}] }]""").RootElement;
        }

        return _analyzer.AnalyzeAsync(settings, [], null, networks, devices, null, nat,
            networkConfigs: wans, trustedDnsRedirectTargets: trusted);
    }

    private static NetworkInfo DualStack(List<string>? ipv4Dns = null, List<string>? ipv6Dns = null) => new()
    {
        Id = "home",
        Name = "Home",
        VlanId = 10,
        Subnet = "192.168.10.0/24",
        Gateway = "192.168.10.1",
        DhcpEnabled = true,
        DnsServers = ipv4Dns,
        HasIpv6 = true,
        Ipv6Subnets = ["2001:db8:10::/64"],
        Ipv6GatewayAddresses = ["fe80::1e0b:8bff:fe00:1", "2001:db8:10::1"],
        Ipv6DnsServers = ipv6Dns
    };

    private static JsonElement Dnat(string ipVersion, string target) => JsonDocument.Parse($$"""
        [{
            "_id": "1", "description": "Redirect DNS", "type": "DNAT", "enabled": true, "protocol": "udp",
            "ip_version": "{{ipVersion}}", "ip_address": "{{target}}",
            "destination_filter": { "filter_type": "ADDRESS_AND_PORT", "port": "53" },
            "source_filter": { "filter_type": "NETWORK_CONF", "network_conf_id": "home" }
        }]
        """).RootElement;

    private static UniFiNetworkConfig Wan(string group, string typeV6, string ipv6DnsPreference,
        string? ipv6Dns1 = null, string? ipv6Dns2 = null, string? ipv4Dns1 = null, string? ipv4Dns2 = null) => new()
    {
        Id = group,
        Name = group,
        Purpose = "wan",
        WanNetworkgroup = group,
        WanDns1 = ipv4Dns1,
        WanDns2 = ipv4Dns2,
        WanTypeV6 = typeV6,
        WanIpv6DnsPreference = ipv6DnsPreference,
        WanIpv6Dns1 = ipv6Dns1 ?? "",
        WanIpv6Dns2 = ipv6Dns2 ?? ""
    };

    #endregion
}
