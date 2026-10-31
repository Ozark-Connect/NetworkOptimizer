using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using NetworkOptimizer.Audit.Dns;
using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.Audit.Services;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.UniFi.Models;
using Xunit;

namespace NetworkOptimizer.Audit.Tests.Dns;

/// <summary>
/// Guest and Hotspot networks in DNS coverage: checked like any other network by default, never covered by UniFi's own
/// Hotspot authorization blocks, and left out only when Allow Guest / Hotspot Public DNS is on.
/// </summary>
[Collection("ThirdPartyDns")]
public class DnsGuestCoverageTests : IDisposable
{
    private const string External = "zone-external";
    private const string Hotspot = "zone-hotspot";
    private static readonly string[] DohDomains = ["dns.google", "cloudflare-dns.com", "dns.quad9.net", "doh.opendns.com"];

    private readonly DnsSecurityAnalyzer _analyzer;
    private readonly FirewallZoneLookup _zones = new(
    [
        new UniFiFirewallZone { Id = "zone-internal", Name = "Internal", ZoneKey = "internal" },
        new UniFiFirewallZone { Id = Hotspot, Name = "Hotspot", ZoneKey = "hotspot" },
        new UniFiFirewallZone { Id = External, Name = "External", ZoneKey = "external" },
    ]);

    public DnsGuestCoverageTests()
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
    public async Task UniFisHotspotAuthorizationBlocks_DoNotCoverGuest()
    {
        // As UniFi lists the Hotspot to External pair: Allow Public DNS, Allow Hotspot Portal, then two blocks that hold
        // only until a guest is authorized. Read as blocks, they covered Guest for port 53, DoT, and DoQ but not DoH.
        var result = await Analyze(HomeBlocks().Concat(HotspotBuiltIns()).ToList(), allowGuestPublicDns: false);

        result.DotUncoveredNetworks.Should().Equal("Guest");
        result.DoqUncoveredNetworks.Should().Equal("Guest");
        result.DohUncoveredNetworks.Should().Equal("Guest");
        result.DotRuleCoverage.Keys.Should().NotContain(["Post-Authorization Restrictions", "Block Unauthorized Traffic", "Internet access blocked by firewall"]);
        result.Issues.Should().Contain(i => i.Type == IssueTypes.DnsNoDotBlock && i.Message.Contains("Uncovered networks: Guest"));
        result.Issues.Should().Contain(i => i.Type == IssueTypes.DnsNoDohBlock && i.Message.Contains("Uncovered networks: Guest"));
    }

    [Fact]
    public async Task AllowGuestPublicDns_LeavesGuestOutOfEveryDnsCoverageCheck()
    {
        var result = await Analyze(HomeBlocks().Concat(HotspotBuiltIns()).ToList(), allowGuestPublicDns: true);

        result.DotProvidesFullCoverage.Should().BeTrue();
        result.DoqProvidesFullCoverage.Should().BeTrue();
        result.DohProvidesFullCoverage.Should().BeTrue();
        result.DotUncoveredNetworks.Should().BeEmpty();
        result.ExcludedFromCoverageNetworks.Should().Equal("Guest");
        result.Issues.Should().NotContain(i => i.Type == IssueTypes.DnsNoDotBlock || i.Type == IssueTypes.DnsNoDoqBlock || i.Type == IssueTypes.DnsNoDohBlock);
    }

    [Fact]
    public async Task AllowGuestPublicDns_AlsoLeavesOutAGuestPurposeNetworkOutsideTheHotspotZone()
    {
        var guest = new NetworkInfo
        {
            Id = "net-visitors", Name = "Visitors", VlanId = 80, Subnet = "198.51.100.0/24", Gateway = "198.51.100.1",
            FirewallZoneId = "zone-internal", Purpose = NetworkPurpose.Guest, InternetAccessEnabled = true
        };

        var result = await _analyzer.AnalyzeAsync(Settings(), HomeBlocks(), null, [Home(), guest], null, null, null, null, External, _zones,
            allowGuestPublicDns: true);

        result.ExcludedFromCoverageNetworks.Should().Equal("Visitors");
        result.DotUncoveredNetworks.Should().BeEmpty("Visitors is in Internal, so Home's block covers it anyway; it is excluded either way");
    }

    [Fact]
    public async Task AFindingNamingAGuestNetwork_PointsAtTheGuestSetting()
    {
        var result = await Analyze(HomeBlocks().Concat(HotspotBuiltIns()).ToList(), allowGuestPublicDns: false);

        foreach (var type in new[] { IssueTypes.DnsNoDotBlock, IssueTypes.DnsNoDoqBlock, IssueTypes.DnsNoDohBlock })
            result.Issues.Single(i => i.Type == type).Metadata!["configurable_setting"].Should().Be(DnsSecurityAnalyzer.GuestSettingHint);
    }

    [Fact]
    public async Task AFindingNamingOnlyOtherNetworks_HasNoGuestHint()
    {
        // Guest has its own DoT/DoQ block; Home has none, so the DoT finding names Home only.
        var guestDot = new FirewallRule
        {
            Id = "dot-guest", Name = "Block Guest DoT", Enabled = true, Action = "BLOCK", Index = 1, Protocol = "tcp_udp", DestinationPort = "853",
            SourceZoneId = Hotspot, SourceMatchingTarget = "ANY", DestinationZoneId = External, DestinationMatchingTarget = "ANY"
        };

        var result = await Analyze([guestDot], allowGuestPublicDns: false);

        var dot = result.Issues.Single(i => i.Type == IssueTypes.DnsNoDotBlock);
        dot.Message.Should().EndWith("Uncovered networks: Home");
        (dot.Metadata?.ContainsKey("configurable_setting") ?? false).Should().BeFalse();
    }

    [Fact]
    public async Task NoDohRuleAnywhere_AppliesToGuestsToo()
    {
        var result = await Analyze([], allowGuestPublicDns: false);

        result.Issues.Single(i => i.Type == IssueTypes.DnsNoDohBlock).Metadata!["configurable_setting"].Should().Be(DnsSecurityAnalyzer.GuestSettingHint);
    }

    [Fact]
    public async Task WithGuestsAllowedPublicDns_NoFindingCarriesTheGuestHint()
    {
        var result = await Analyze([], allowGuestPublicDns: true);

        result.Issues.Should().NotContain(i => i.Metadata != null && i.Metadata.ContainsKey("configurable_setting")
            && i.Metadata["configurable_setting"].ToString()!.Contains("Allow Guest / Hotspot Public DNS"));
    }

    [Fact]
    public async Task WithoutAZoneLookup_TheRulesAreReadAsGiven()
    {
        var rules = HotspotBuiltIns();

        DnsSecurityAnalyzer.WithoutHotspotAuthorizationBlocks(rules, null).Should().BeSameAs(rules);
        DnsSecurityAnalyzer.WithoutHotspotAuthorizationBlocks(rules, _zones)!.Select(r => r.Name)
            .Should().Equal("Allow Public DNS", "Allow Hotspot Portal", "Allow All Traffic");
    }

    private Task<DnsSecurityResult> Analyze(List<FirewallRule> rules, bool allowGuestPublicDns) =>
        _analyzer.AnalyzeAsync(Settings(), rules, null, [Home(), Guest()], null, null, null, null, External, _zones,
            allowGuestPublicDns: allowGuestPublicDns);

    private static JsonElement Settings() =>
        JsonDocument.Parse("""[{ "key": "doh", "state": "auto", "server_names": ["cloudflare"] }]""").RootElement;

    private static NetworkInfo Home() => new()
    {
        Id = "net-home", Name = "Home", VlanId = 10, Subnet = "192.0.2.0/24", Gateway = "192.0.2.1",
        FirewallZoneId = "zone-internal", InternetAccessEnabled = true
    };

    private static NetworkInfo Guest() => new()
    {
        Id = "net-guest", Name = "Guest", VlanId = 70, Subnet = "203.0.113.0/24", Gateway = "203.0.113.1",
        FirewallZoneId = Hotspot, Purpose = NetworkPurpose.Guest, IsUniFiGuestNetwork = true, InternetAccessEnabled = true
    };

    /// <summary>Home's port 53, DoT/DoQ, and DoH blocks; none for Guest.</summary>
    private static List<FirewallRule> HomeBlocks() =>
    [
        new()
        {
            Id = "dns-home", Name = "Block External DNS", Enabled = true, Action = "BLOCK", Index = 1, Protocol = "tcp_udp", DestinationPort = "53,853",
            SourceZoneId = "zone-internal", SourceMatchingTarget = "ANY", DestinationZoneId = External, DestinationMatchingTarget = "ANY"
        },
        new()
        {
            Id = "doh-home", Name = "Block External DoH", Enabled = true, Action = "BLOCK", Index = 2, Protocol = "tcp", DestinationPort = "443",
            SourceZoneId = "zone-internal", SourceMatchingTarget = "ANY", DestinationZoneId = External, DestinationMatchingTarget = "WEB",
            WebDomains = [.. DohDomains]
        },
    ];

    /// <summary>UniFi's predefined Hotspot to External policies, in its order.</summary>
    private static List<FirewallRule> HotspotBuiltIns() =>
    [
        BuiltIn("Allow Public DNS", "ALLOW", 30000, "tcp_udp", "53"),
        BuiltIn("Allow Hotspot Portal", "ALLOW", 30001, "tcp", "443"),
        BuiltIn("Post-Authorization Restrictions", "BLOCK", 30002, "all", null),
        BuiltIn("Block Unauthorized Traffic", "BLOCK", 30003, "all", null),
        BuiltIn("Allow All Traffic", "ALLOW", 2147483647, "all", null),
    ];

    private static FirewallRule BuiltIn(string name, string action, int index, string protocol, string? port) => new()
    {
        Id = $"hotspot-{index}", Name = name, Enabled = true, Action = action, Index = index, Protocol = protocol, DestinationPort = port,
        IpVersion = name == "Allow All Traffic" ? "BOTH" : "IPV4", Predefined = true,
        SourceZoneId = Hotspot, SourceMatchingTarget = "ANY", DestinationZoneId = External, DestinationMatchingTarget = "ANY"
    };
}
