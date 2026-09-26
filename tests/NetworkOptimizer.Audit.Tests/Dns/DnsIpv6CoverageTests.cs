using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using NetworkOptimizer.Audit.Dns;
using NetworkOptimizer.Audit.Models;
using Xunit;

namespace NetworkOptimizer.Audit.Tests.Dns;

/// <summary>
/// DNS leak protection per address family (#1231). The IPv4 findings describe IPv4 traffic only;
/// IPv6-only gaps are reported separately, and only for networks that carry IPv6.
/// </summary>
[Collection("ThirdPartyDns")]
public class DnsIpv6CoverageTests : IDisposable
{
    private static readonly string[] DohDomains = ["dns.google", "cloudflare-dns.com", "dns.quad9.net", "doh.opendns.com"];
    private static readonly string[] DohIpv4 = ["1.1.1.1", "1.0.0.1", "8.8.8.8", "8.8.4.4", "9.9.9.9", "208.67.222.222"];
    private static readonly string[] DohIpv6 = ["2606:4700:4700::1111", "2001:4860:4860::8888", "2620:fe::fe", "2620:119:35::35"];

    private readonly DnsSecurityAnalyzer _analyzer;

    public DnsIpv6CoverageTests()
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

    #region DoT

    [Fact]
    public async Task DotBlockIpv4Only_DualStackNetwork_ReportsDotGapOverIpv6()
    {
        var result = await Analyze([PortBlock("dot", "tcp", "853", "IPV4")], DualStack());

        result.Ipv6DotUncoveredNetworks.Should().Equal("Home");
        var issue = result.Issues.Where(i => i.Type == IssueTypes.DnsNoDotBlock).Should().ContainSingle().Subject;
        issue.Message.Should().Be("DNS-over-TLS (port 853) blocking has partial coverage over IPv6. Uncovered networks: Home");
        IpFamilyText.IsIpv6Only(issue.Metadata).Should().BeTrue();
        _analyzer.GetSummary(result).DotProvidesFullCoverage.Should().BeFalse();
    }

    [Fact]
    public async Task DotBlockIpv4Only_NoIpv6_ReportsNothing()
    {
        var result = await Analyze([PortBlock("dot", "tcp", "853", "IPV4")], Ipv4Only());

        result.Ipv6DotUncoveredNetworks.Should().BeEmpty();
        result.Issues.Should().NotContain(i => i.Type == IssueTypes.DnsNoDotBlock);
        _analyzer.GetSummary(result).DotProvidesFullCoverage.Should().BeTrue();
    }

    [Theory]
    [InlineData("BOTH")]
    [InlineData(null)]
    public async Task DotBlockCoveringBothFamilies_DualStackNetwork_ReportsNothing(string? ipVersion)
    {
        var result = await Analyze([PortBlock("dot", "tcp", "853", ipVersion)], DualStack());

        result.Ipv6DotUncoveredNetworks.Should().BeEmpty();
        result.Issues.Should().NotContain(i => i.Type == IssueTypes.DnsNoDotBlock);
    }

    [Fact]
    public async Task DotBlockIpv6Only_NoIpv6_ReportsTheIpv4Finding()
    {
        // An IPv6-only DoT block used to count as blocking IPv4 DoT too
        var result = await Analyze([PortBlock("dot", "tcp", "853", "IPV6")], Ipv4Only());

        result.HasDotBlockRule.Should().BeFalse();
        var issue = result.Issues.Where(i => i.Type == IssueTypes.DnsNoDotBlock).Should().ContainSingle().Subject;
        issue.Message.Should().StartWith("No firewall rule blocks DNS-over-TLS (port 853).");
    }

    [Fact]
    public async Task DotBlockIpv4Only_Ipv6WithoutKnownPrefix_ReportsNothing()
    {
        // A delegated prefix read only from networkconf is unknown, so IPv6 coverage cannot be judged
        var networks = Ipv4Only();
        networks[0] = new NetworkInfo
        {
            Id = networks[0].Id, Name = networks[0].Name, VlanId = networks[0].VlanId,
            Subnet = networks[0].Subnet, HasIpv6 = true
        };

        var result = await Analyze([PortBlock("dot", "tcp", "853", "IPV4")], networks);

        result.Ipv6DotUncoveredNetworks.Should().BeEmpty();
    }

    #endregion

    #region DNS port 53

    [Fact]
    public async Task Dns53BlockIpv4Only_DualStackNetwork_ReportsLeakOverIpv6()
    {
        var result = await Analyze([PortBlock("dns53", "udp", "53", "IPV4")], DualStack());

        result.Ipv6Dns53UncoveredNetworks.Should().Equal("Home");
        var issue = result.Issues.Where(i => i.Type == IssueTypes.DnsNo53Block).Should().ContainSingle().Subject;
        issue.Message.Should().StartWith("Networks with no DNS leak protection over IPv6: Home.");
        IpFamilyText.IsIpv6Only(issue.Metadata).Should().BeTrue();
        _analyzer.GetSummary(result).DnsLeakProtection.Should().BeFalse();
    }

    [Fact]
    public async Task Dns53BlockIpv4Only_NoIpv6_ReportsNothing()
    {
        var result = await Analyze([PortBlock("dns53", "udp", "53", "IPV4")], Ipv4Only());

        result.Issues.Should().NotContain(i => i.Type == IssueTypes.DnsNo53Block);
        _analyzer.GetSummary(result).DnsLeakProtection.Should().BeTrue();
    }

    [Fact]
    public async Task Dns53BlockPerFamily_DualStackNetwork_ReportsNothing()
    {
        var result = await Analyze([PortBlock("dns53-v4", "udp", "53", "IPV4"), PortBlock("dns53-v6", "udp", "53", "IPV6")], DualStack());

        result.Ipv6Dns53UncoveredNetworks.Should().BeEmpty();
        result.Issues.Should().NotContain(i => i.Type == IssueTypes.DnsNo53Block);
    }

    #endregion

    #region DoH

    [Fact]
    public async Task DohDomainBlockBoth_DualStack_ReportsNothing()
    {
        var result = await Analyze([DohDomainBlock("BOTH")], DualStack());

        result.Ipv6DohUnblocked.Should().BeFalse();
        result.Issues.Should().NotContain(i => i.Type == IssueTypes.DnsNoDohBlock);
    }

    [Fact]
    public async Task DohDomainBlockIpv4Only_DualStack_ReportsDohOverIpv6()
    {
        var result = await Analyze([DohDomainBlock("IPV4")], DualStack());

        result.Ipv6DohUnblocked.Should().BeTrue();
        var issue = result.Issues.Where(i => i.Type == IssueTypes.DnsNoDohBlock).Should().ContainSingle().Subject;
        issue.Message.Should().StartWith("No firewall rule blocks public DoH providers over IPv6.");
        IpFamilyText.IsIpv6Only(issue.Metadata).Should().BeTrue();
    }

    [Fact]
    public async Task DohIpBlockWithIpv4AddressesOnly_DualStack_ReportsDohOverIpv6()
    {
        // A BOTH rule listing only IPv4 resolver addresses leaves IPv6 DoH open
        var result = await Analyze([DohIpBlock("BOTH", DohIpv4)], DualStack());

        result.HasDohBlockRule.Should().BeTrue();
        result.Ipv6DohUnblocked.Should().BeTrue();
    }

    [Fact]
    public async Task DohIpBlockWithBothFamiliesAddresses_DualStack_ReportsNothing()
    {
        var result = await Analyze([DohIpBlock("BOTH", [.. DohIpv4, .. DohIpv6])], DualStack());

        result.HasDohBlockRule.Should().BeTrue();
        result.Ipv6DohUnblocked.Should().BeFalse();
    }

    [Fact]
    public async Task DohIpBlockWithIpv4AddressesOnly_NoIpv6_ReportsNothing()
    {
        var result = await Analyze([DohIpBlock("BOTH", DohIpv4)], Ipv4Only());

        result.HasDohBlockRule.Should().BeTrue();
        result.Ipv6DohUnblocked.Should().BeFalse();
        result.Issues.Should().NotContain(i => i.Type == IssueTypes.DnsNoDohBlock);
    }

    [Fact]
    public async Task DohIpBlockWithIpv6AddressesOnly_NoIpv6_ReportsTheIpv4Finding()
    {
        var result = await Analyze([DohIpBlock("BOTH", DohIpv6)], Ipv4Only());

        result.HasDohBlockRule.Should().BeFalse();
        result.Issues.Should().Contain(i => i.Type == IssueTypes.DnsNoDohBlock && !IpFamilyText.IsIpv6Only(i.Metadata));
    }

    #endregion

    #region DNAT

    [Fact]
    public void DnatAnalyzer_Ipv4Rule_CoversOnlyIpv4Traffic()
    {
        var networks = DualStack();
        var natRules = JsonDocument.Parse("""
        [{
            "_id": "1", "description": "Redirect DNS", "type": "DNAT", "enabled": true, "protocol": "udp",
            "ip_version": "IPV4", "ip_address": "192.0.2.1",
            "destination_filter": { "filter_type": "ADDRESS_AND_PORT", "port": "53" },
            "source_filter": { "filter_type": "NETWORK_CONF", "network_conf_id": "home" }
        }]
        """).RootElement;

        var analyzer = new DnatDnsAnalyzer();
        analyzer.Analyze(natRules, networks).CoveredNetworkIds.Should().Contain("home");
        analyzer.Analyze(natRules, networks, family: IpFamily.IPv6).CoveredNetworkIds.Should().BeEmpty();
    }

    [Fact]
    public void DnatAnalyzer_SubnetSource_MatchesTheFamilysPrefix()
    {
        var networks = DualStack();
        var natRules = JsonDocument.Parse("""
        [{
            "_id": "1", "description": "Redirect DNS v6", "type": "DNAT", "enabled": true, "protocol": "udp",
            "ip_version": "IPV6", "ip_address": "2001:db8:10::1",
            "destination_filter": { "filter_type": "ADDRESS_AND_PORT", "port": "53" },
            "source_filter": { "filter_type": "ADDRESS_AND_PORT", "address": "2001:db8:10::/64" }
        }]
        """).RootElement;

        var analyzer = new DnatDnsAnalyzer();
        analyzer.Analyze(natRules, networks, family: IpFamily.IPv6).CoveredNetworkIds.Should().Contain("home");
        analyzer.Analyze(natRules, networks).CoveredNetworkIds.Should().BeEmpty();
    }

    #endregion

    #region Helpers

    private Task<DnsSecurityResult> Analyze(List<FirewallRule> rules, List<NetworkInfo> networks)
    {
        var settings = JsonDocument.Parse("""[{ "key": "doh", "state": "auto", "server_names": ["cloudflare"] }]""").RootElement;
        return _analyzer.AnalyzeAsync(settings, rules, null, networks);
    }

    private static List<NetworkInfo> DualStack() =>
    [
        new() { Id = "home", Name = "Home", VlanId = 10, Subnet = "192.0.2.0/24", HasIpv6 = true, Ipv6Subnets = ["2001:db8:10::/64"] }
    ];

    private static List<NetworkInfo> Ipv4Only() =>
    [
        new() { Id = "home", Name = "Home", VlanId = 10, Subnet = "192.0.2.0/24" }
    ];

    private static FirewallRule PortBlock(string id, string protocol, string port, string? ipVersion) => new()
    {
        Id = id,
        Name = id,
        Action = "BLOCK",
        Enabled = true,
        Index = 1,
        IpVersion = ipVersion,
        Protocol = protocol,
        DestinationPort = port,
        SourceMatchingTarget = "ANY",
        DestinationMatchingTarget = "ANY"
    };

    private static FirewallRule DohDomainBlock(string ipVersion) => new()
    {
        Id = "doh-domains",
        Name = "Block DoH",
        Action = "BLOCK",
        Enabled = true,
        Index = 2,
        IpVersion = ipVersion,
        Protocol = "tcp",
        DestinationPort = "443",
        SourceMatchingTarget = "ANY",
        DestinationMatchingTarget = "WEB",
        WebDomains = [.. DohDomains]
    };

    private static FirewallRule DohIpBlock(string ipVersion, string[] ips) => new()
    {
        Id = "doh-ips",
        Name = "Block DoH IPs",
        Action = "BLOCK",
        Enabled = true,
        Index = 2,
        IpVersion = ipVersion,
        Protocol = "tcp",
        DestinationPort = "443",
        SourceMatchingTarget = "ANY",
        DestinationMatchingTarget = "IP",
        DestinationIps = [.. ips]
    };

    #endregion
}
