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
/// DoH blocking per network, as DoT and DoQ are: a DoH block on one zone no longer counts for every network,
/// and a network with Internet Access off counts as covered.
/// </summary>
[Collection("ThirdPartyDns")]
public class DnsDohCoverageTests : IDisposable
{
    private const string External = "zone-external";
    private static readonly string[] DohDomains = ["dns.google", "cloudflare-dns.com", "dns.quad9.net", "doh.opendns.com"];
    private static readonly string[] DohIpv4 = ["1.1.1.1", "1.0.0.1", "8.8.8.8", "8.8.4.4", "9.9.9.9", "208.67.222.222"];

    private readonly DnsSecurityAnalyzer _analyzer;

    public DnsDohCoverageTests()
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

    [Fact]
    public async Task ADohBlockOnOneZone_LeavesTheOtherZonesNetworksUncovered()
    {
        var result = await Analyze([DomainBlock("Block DoH IoT", "zone-iot")], Home(), IoT());

        result.HasDohBlockRule.Should().BeTrue();
        result.DohProvidesFullCoverage.Should().BeFalse();
        result.DohCoveredNetworks.Should().Equal("IoT");
        result.DohUncoveredNetworks.Should().Equal("Home");
        var issue = result.Issues.Where(i => i.Type == IssueTypes.DnsNoDohBlock).Should().ContainSingle().Subject;
        issue.Message.Should().Be("Public DoH provider blocking has partial coverage. Uncovered networks: Home");
        issue.ScoreImpact.Should().Be(3);
        var summary = _analyzer.GetSummary(result);
        summary.DohBypassBlocked.Should().BeTrue();
        summary.DohProvidesFullCoverage.Should().BeFalse();
        summary.FullyProtected.Should().BeFalse();
    }

    [Fact]
    public async Task ADohBlockOnEveryZone_IsFullCoverage()
    {
        var result = await Analyze([DomainBlock("Block DoH Home", "zone-internal"), DomainBlock("Block DoH IoT", "zone-iot")], Home(), IoT());

        result.DohProvidesFullCoverage.Should().BeTrue();
        result.DohUncoveredNetworks.Should().BeEmpty();
        result.DohRuleCoverage.Keys.Should().BeEquivalentTo(["Block DoH Home", "Block DoH IoT"]);
        result.Issues.Should().NotContain(i => i.Type == IssueTypes.DnsNoDohBlock);
        _analyzer.GetSummary(result).DohProvidesFullCoverage.Should().BeTrue();
    }

    [Fact]
    public async Task ANetworkWithInternetAccessOff_CountsAsCoveredForDoh()
    {
        var security = new NetworkInfo
        {
            Id = "net-security", Name = "Security", VlanId = 40, Subnet = "198.51.100.0/24", Gateway = "198.51.100.1",
            FirewallZoneId = "zone-security", InternetAccessEnabled = false
        };

        var result = await Analyze([DomainBlock("Block DoH Home", "zone-internal")], Home(), security);

        result.DohProvidesFullCoverage.Should().BeTrue();
        result.DohRuleCoverage["Internet Access disabled"].Should().Equal("Security");
        result.Issues.Should().NotContain(i => i.Type == IssueTypes.DnsNoDohBlock);
    }

    [Fact]
    public async Task NoDohBlock_WithEveryNetworkOffline_IsNoFinding()
    {
        var result = await Analyze([], Home(internet: false));

        result.HasDohBlockRule.Should().BeTrue("a network that cannot reach the Internet cannot reach a DoH provider");
        result.Issues.Should().NotContain(i => i.Type == IssueTypes.DnsNoDohBlock);
    }

    [Fact]
    public async Task NoDohBlockAnywhere_StillReportsNoBlock()
    {
        var result = await Analyze([], Home(), IoT());

        result.HasDohBlockRule.Should().BeFalse();
        result.Issues.Where(i => i.Type == IssueTypes.DnsNoDohBlock).Should().ContainSingle()
            .Which.Message.Should().StartWith("No firewall rule blocks public DoH providers.");
    }

    [Fact]
    public async Task AnIpBasedDohBlock_CoversOnlyItsZone()
    {
        var rule = new FirewallRule
        {
            Id = "doh-ips", Name = "Block DoH IPs", Action = "BLOCK", Enabled = true, Index = 2, Protocol = "tcp", DestinationPort = "443",
            SourceZoneId = "zone-iot", SourceMatchingTarget = "ANY",
            DestinationZoneId = External, DestinationMatchingTarget = "IP", DestinationIps = [.. DohIpv4]
        };

        var result = await Analyze([rule], Home(), IoT());

        result.DohCoveredNetworks.Should().Equal("IoT");
        result.DohUncoveredNetworks.Should().Equal("Home");
    }

    [Fact]
    public async Task ADohBlockOverIpv4OnlyForOneZone_IsAPartialGapOverIpv6()
    {
        // Home's block covers both families; IoT's covers IPv4 only. Some IPv6 DoH block exists, so the gap is named per network.
        var result = await Analyze([DomainBlock("Block DoH Home", "zone-internal"), DomainBlock("Block DoH IoT", "zone-iot", "IPV4")],
            Home(ipv6: "2001:db8:10::/64"), IoT(ipv6: "2001:db8:20::/64"));

        result.Ipv6DohUnblocked.Should().BeFalse();
        result.Ipv6DohUncoveredNetworks.Should().Equal("IoT");
        var issue = result.Issues.Where(i => i.Type == IssueTypes.DnsNoDohBlock).Should().ContainSingle().Subject;
        issue.Message.Should().Be("Public DoH provider blocking has partial coverage over IPv6. Uncovered networks: IoT");
        IpFamilyText.IsIpv6Only(issue.Metadata).Should().BeTrue();
        _analyzer.GetSummary(result).DohProvidesFullCoverage.Should().BeFalse();
    }

    private Task<DnsSecurityResult> Analyze(List<FirewallRule> rules, params NetworkInfo[] networks)
    {
        var settings = JsonDocument.Parse("""[{ "key": "doh", "state": "auto", "server_names": ["cloudflare"] }]""").RootElement;
        return _analyzer.AnalyzeAsync(settings, rules, null, [.. networks], null, null, null, null, External);
    }

    private static NetworkInfo Home(bool internet = true, string? ipv6 = null) => new()
    {
        Id = "net-home", Name = "Home", VlanId = 10, Subnet = "192.0.2.0/24", Gateway = "192.0.2.1", FirewallZoneId = "zone-internal",
        InternetAccessEnabled = internet, HasIpv6 = ipv6 != null, Ipv6Subnets = ipv6 == null ? null : [ipv6]
    };

    private static NetworkInfo IoT(string? ipv6 = null) => new()
    {
        Id = "net-iot", Name = "IoT", VlanId = 20, Subnet = "203.0.113.0/24", Gateway = "203.0.113.1", FirewallZoneId = "zone-iot",
        InternetAccessEnabled = true, HasIpv6 = ipv6 != null, Ipv6Subnets = ipv6 == null ? null : [ipv6]
    };

    private static FirewallRule DomainBlock(string name, string sourceZone, string? ipVersion = null) => new()
    {
        Id = name, Name = name, Action = "BLOCK", Enabled = true, Index = 2, IpVersion = ipVersion, Protocol = "tcp", DestinationPort = "443",
        SourceZoneId = sourceZone, SourceMatchingTarget = "ANY",
        DestinationZoneId = External, DestinationMatchingTarget = "WEB", WebDomains = [.. DohDomains]
    };
}
