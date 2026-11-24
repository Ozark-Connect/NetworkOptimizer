using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using NetworkOptimizer.Audit.Dns;
using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.Audit.Services;
using NetworkOptimizer.UniFi.Models;
using Xunit;

namespace NetworkOptimizer.Audit.Tests.Dns;

/// <summary>
/// A port 53 coverage finding that names the network holding a LAN DNS server points at the two ways to leave that
/// server's own lookups alone. The network is never left out on its own.
/// </summary>
[Collection("ThirdPartyDns")]
public class DnsResolverNetworkHintTests : IDisposable
{
    private const string External = "zone-external";
    private static readonly string[] Port53Findings = [IssueTypes.DnsNo53Block, IssueTypes.Dns53PartialCoverage, IssueTypes.DnsDnatPartialCoverage];

    private readonly DnsSecurityAnalyzer _analyzer;
    private readonly FirewallZoneLookup _zones = new(
    [
        new UniFiFirewallZone { Id = "zone-internal", Name = "Internal", ZoneKey = "internal" },
        new UniFiFirewallZone { Id = External, Name = "External", ZoneKey = "external" },
    ]);

    public DnsResolverNetworkHintTests()
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
    public async Task UncoveredNetworkHoldingTheDnsServer_GetsTheHint()
    {
        var result = await Analyze([Home("198.51.100.53"), DnsNet()], excludedVlans: null);

        var finding = result.Issues.First(i => Port53Findings.Contains(i.Type));
        finding.Metadata!["configurable_setting"].ToString().Should()
            .Contain("DNS (198.51.100.53) holds your DNS server")
            .And.Contain("exclude it from the source of your port 53 DNAT or block rule")
            .And.Contain("add VLAN 50 to DNAT DNS Coverage: Excluded VLANs");
    }

    [Fact]
    public async Task ResolverNetworkAlreadyExcluded_GetsNoHint()
    {
        var result = await Analyze([Home("198.51.100.53"), DnsNet()], excludedVlans: [50]);

        result.Issues.Should().NotContain(i => HasResolverHint(i));
    }

    [Fact]
    public async Task DnsServerOutsideEveryNetwork_GetsNoHint()
    {
        var result = await Analyze([Home("203.0.113.53")], excludedVlans: null);

        result.Issues.Should().NotContain(i => HasResolverHint(i));
    }

    [Fact]
    public async Task GatewayAsDnsServer_GetsNoHint()
    {
        var result = await Analyze([Home("192.0.2.1"), DnsNet()], excludedVlans: null);

        result.Issues.Should().NotContain(i => HasResolverHint(i));
    }

    private static bool HasResolverHint(AuditIssue issue) =>
        issue.Metadata?.TryGetValue("configurable_setting", out var hint) == true && hint.ToString()!.Contains("holds your DNS server");

    private Task<DnsSecurityResult> Analyze(List<NetworkInfo> networks, List<int>? excludedVlans) =>
        _analyzer.AnalyzeAsync(Settings(), [], null, networks, null, null, null, excludedVlans, External, _zones);

    private static JsonElement Settings() =>
        JsonDocument.Parse("""[{ "key": "doh", "state": "auto", "server_names": ["cloudflare"] }]""").RootElement;

    private static NetworkInfo Home(string dnsServer) => new()
    {
        Id = "net-home", Name = "Home", VlanId = 10, Subnet = "192.0.2.0/24", Gateway = "192.0.2.1",
        FirewallZoneId = "zone-internal", InternetAccessEnabled = true, DnsServers = [dnsServer]
    };

    private static NetworkInfo DnsNet() => new()
    {
        Id = "net-dns", Name = "DNS", VlanId = 50, Subnet = "198.51.100.0/24", Gateway = "198.51.100.1",
        FirewallZoneId = "zone-internal", InternetAccessEnabled = true
    };
}
