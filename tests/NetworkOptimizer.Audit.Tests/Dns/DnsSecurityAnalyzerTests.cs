using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using NetworkOptimizer.Audit.Analyzers;
using NetworkOptimizer.Audit.Dns;
using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.Audit.Services;
using NetworkOptimizer.UniFi.Models;
using Xunit;

namespace NetworkOptimizer.Audit.Tests.Dns;

public class DnsSecurityAnalyzerTests : IDisposable
{
    private readonly DnsSecurityAnalyzer _analyzer;
    private readonly Mock<ILogger<DnsSecurityAnalyzer>> _loggerMock;
    private readonly ThirdPartyDnsDetector _thirdPartyDetector;
    private readonly FirewallRuleParser _firewallParser;

    public DnsSecurityAnalyzerTests()
    {
        // Mock DNS resolver to avoid real network calls and timeouts
        DohProviderRegistry.DnsResolver = _ => Task.FromResult<string?>(null);

        _loggerMock = new Mock<ILogger<DnsSecurityAnalyzer>>();
        var detectorLoggerMock = new Mock<ILogger<ThirdPartyDnsDetector>>();
        var parserLoggerMock = new Mock<ILogger<FirewallRuleParser>>();

        // Use mock HttpClient that returns 404 immediately (no Pi-hole detected)
        var httpClient = CreateMockHttpClient(HttpStatusCode.NotFound);
        _thirdPartyDetector = new ThirdPartyDnsDetector(detectorLoggerMock.Object, httpClient);
        _analyzer = new DnsSecurityAnalyzer(_loggerMock.Object, _thirdPartyDetector);
        _firewallParser = new FirewallRuleParser(parserLoggerMock.Object);
    }

    private static HttpClient CreateMockHttpClient(HttpStatusCode statusCode, string content = "")
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = statusCode,
                Content = new StringContent(content)
            });
        return new HttpClient(handlerMock.Object) { Timeout = TimeSpan.FromSeconds(1) };
    }

    /// <summary>
    /// Parse JSON firewall policies into FirewallRule list for testing.
    /// This preserves existing test data format while using the new API.
    /// </summary>
    private List<FirewallRule> ParseFirewallRules(JsonElement json, List<UniFiFirewallGroup>? groups = null)
    {
        _firewallParser.SetFirewallGroups(groups);
        return _firewallParser.ExtractFirewallPolicies(json);
    }

    public void Dispose()
    {
        DohProviderRegistry.ResetDnsResolver();
    }

    #region Constructor Tests

    [Fact]
    public void Constructor_WithLogger_CreatesInstance()
    {
        var detectorLoggerMock = new Mock<ILogger<ThirdPartyDnsDetector>>();
        var thirdPartyDetector = new ThirdPartyDnsDetector(detectorLoggerMock.Object, CreateMockHttpClient(HttpStatusCode.NotFound));
        var analyzer = new DnsSecurityAnalyzer(_loggerMock.Object, thirdPartyDetector);
        analyzer.Should().NotBeNull();
    }

    #endregion

    #region Analyze Basic Tests

    [Fact]
    public async Task Analyze_NullSettingsAndFirewall_ReturnsDefaultResult()
    {
        var result = await _analyzer.AnalyzeAsync(null, null);

        result.Should().NotBeNull();
        result.DohConfigured.Should().BeFalse();
        result.HasDns53BlockRule.Should().BeFalse();
        result.HasDotBlockRule.Should().BeFalse();
        result.HasDohBlockRule.Should().BeFalse();
    }

    [Fact]
    public async Task Analyze_EmptySettingsArray_ReturnsDefaultResult()
    {
        var settings = JsonDocument.Parse("[]").RootElement;
        var result = await _analyzer.AnalyzeAsync(settings, null);

        result.Should().NotBeNull();
        result.DohConfigured.Should().BeFalse();
    }

    [Fact]
    public async Task Analyze_EmptyDataWrapper_ReturnsDefaultResult()
    {
        var settings = JsonDocument.Parse("{\"data\": []}").RootElement;
        var result = await _analyzer.AnalyzeAsync(settings, null);

        result.Should().NotBeNull();
        result.DohConfigured.Should().BeFalse();
    }

    #endregion

    #region DoH Configuration Tests

    [Fact]
    public async Task Analyze_WithDohDisabled_SetsStateCorrectly()
    {
        var settings = JsonDocument.Parse(@"[
            { ""key"": ""doh"", ""state"": ""disabled"" }
        ]").RootElement;

        var result = await _analyzer.AnalyzeAsync(settings, null);

        result.DohState.Should().Be("disabled");
        result.DohConfigured.Should().BeFalse();
    }

    [Fact]
    public async Task Analyze_WithDohOff_SetsStateCorrectly()
    {
        // UniFi API also uses "off" as a disabled state
        var settings = JsonDocument.Parse(@"[
            { ""key"": ""doh"", ""state"": ""off"" }
        ]").RootElement;

        var result = await _analyzer.AnalyzeAsync(settings, null);

        result.DohState.Should().Be("off");
        result.DohConfigured.Should().BeFalse();
    }

    [Fact]
    public async Task Analyze_WithDohOff_WithServers_StillNotConfigured()
    {
        // Even with server entries, "off" state means DoH is not configured
        var settings = JsonDocument.Parse(@"[
            {
                ""key"": ""doh"",
                ""state"": ""off"",
                ""server_names"": [""cloudflare""]
            }
        ]").RootElement;

        var result = await _analyzer.AnalyzeAsync(settings, null);

        result.DohState.Should().Be("off");
        result.DohConfigured.Should().BeFalse("DoH should not be configured when state is 'off'");
        result.ConfiguredServers.Should().NotBeEmpty("servers are still parsed for reference");
    }

    [Fact]
    public async Task Analyze_WithDohAuto_SetsStateCorrectly()
    {
        var settings = JsonDocument.Parse(@"[
            { ""key"": ""doh"", ""state"": ""auto"" }
        ]").RootElement;

        var result = await _analyzer.AnalyzeAsync(settings, null);

        result.DohState.Should().Be("auto");
    }

    [Fact]
    public async Task Analyze_WithDohCustom_SetsStateCorrectly()
    {
        var settings = JsonDocument.Parse(@"[
            { ""key"": ""doh"", ""state"": ""custom"" }
        ]").RootElement;

        var result = await _analyzer.AnalyzeAsync(settings, null);

        result.DohState.Should().Be("custom");
    }

    [Fact]
    public async Task Analyze_WithDohServerNames_ParsesBuiltInServers()
    {
        var settings = JsonDocument.Parse(@"[
            {
                ""key"": ""doh"",
                ""state"": ""auto"",
                ""server_names"": [""cloudflare"", ""google""]
            }
        ]").RootElement;

        var result = await _analyzer.AnalyzeAsync(settings, null);

        result.ConfiguredServers.Should().HaveCount(2);
        result.DohConfigured.Should().BeTrue();
    }

    [Fact]
    public async Task Analyze_WithCustomSdnsStamp_ParsesCustomServer()
    {
        // NextDNS SDNS stamp
        var sdnsStamp = "sdns://AgcAAAAAAAAAAAAOZG5zLm5leHRkbnMuaW8HL2FiY2RlZg";
        var settings = JsonDocument.Parse($@"[
            {{
                ""key"": ""doh"",
                ""state"": ""custom"",
                ""custom_servers"": [
                    {{ ""server_name"": ""NextDNS"", ""sdns_stamp"": ""{sdnsStamp}"", ""enabled"": true }}
                ]
            }}
        ]").RootElement;

        var result = await _analyzer.AnalyzeAsync(settings, null);

        result.ConfiguredServers.Should().HaveCountGreaterThanOrEqualTo(1);
        result.DohConfigured.Should().BeTrue();
    }

    [Fact]
    public async Task Analyze_WithCustomStateAndStaleServerNames_IgnoresStaleEntries()
    {
        // When state is "custom", only custom_servers are active.
        // server_names may contain stale built-in entries from a previous auto/manual config.
        var sdnsStamp = "sdns://AgcAAAAAAAAAAAAOZG5zLm5leHRkbnMuaW8HL2FiY2RlZg";
        var settings = JsonDocument.Parse($@"[
            {{
                ""key"": ""doh"",
                ""state"": ""custom"",
                ""custom_servers"": [
                    {{ ""server_name"": ""NextDNS"", ""sdns_stamp"": ""{sdnsStamp}"", ""enabled"": true }}
                ],
                ""server_names"": [""cloudflare"", ""google""]
            }}
        ]").RootElement;

        var result = await _analyzer.AnalyzeAsync(settings, null);

        result.DohConfigured.Should().BeTrue();
        // Only the custom server should be enabled
        var enabledServers = result.ConfiguredServers.Where(s => s.Enabled).ToList();
        enabledServers.Should().HaveCount(1);
        enabledServers[0].ServerName.Should().Be("NextDNS");
        // Stale server_names are parsed but marked disabled
        var disabledServers = result.ConfiguredServers.Where(s => !s.Enabled).ToList();
        disabledServers.Should().HaveCount(2);
    }

    [Fact]
    public async Task Analyze_WithAutoStateAndServerNames_ServerNamesAreActive()
    {
        // When state is "auto" or "manual", server_names are the active providers
        var settings = JsonDocument.Parse(@"[
            {
                ""key"": ""doh"",
                ""state"": ""auto"",
                ""server_names"": [""cloudflare"", ""google""]
            }
        ]").RootElement;

        var result = await _analyzer.AnalyzeAsync(settings, null);

        result.DohConfigured.Should().BeTrue();
        result.ConfiguredServers.Should().HaveCount(2);
        result.ConfiguredServers.Should().OnlyContain(s => s.Enabled);
    }

    [Fact]
    public async Task Analyze_WithDisabledCustomServer_DoesNotCountAsConfigured()
    {
        var sdnsStamp = "sdns://AgcAAAAAAAAAAAAOZG5zLm5leHRkbnMuaW8HL2FiY2RlZg";
        var settings = JsonDocument.Parse($@"[
            {{
                ""key"": ""doh"",
                ""state"": ""custom"",
                ""custom_servers"": [
                    {{ ""server_name"": ""NextDNS"", ""sdns_stamp"": ""{sdnsStamp}"", ""enabled"": false }}
                ]
            }}
        ]").RootElement;

        var result = await _analyzer.AnalyzeAsync(settings, null);

        result.DohConfigured.Should().BeFalse();
    }

    [Fact]
    public async Task Analyze_WithInvalidSdnsStamp_SkipsServer()
    {
        var settings = JsonDocument.Parse(@"[
            {
                ""key"": ""doh"",
                ""state"": ""custom"",
                ""custom_servers"": [
                    { ""server_name"": ""Invalid"", ""sdns_stamp"": ""invalid_stamp"", ""enabled"": true }
                ]
            }
        ]").RootElement;

        var result = await _analyzer.AnalyzeAsync(settings, null);

        result.ConfiguredServers.Should().BeEmpty();
    }

    #endregion

    #region WAN DNS Settings Tests

    [Fact]
    public async Task Analyze_WithWanDnsServers_ParsesServers()
    {
        var settings = JsonDocument.Parse(@"[
            {
                ""key"": ""dns"",
                ""dns_servers"": [""8.8.8.8"", ""8.8.4.4""]
            }
        ]").RootElement;

        var result = await _analyzer.AnalyzeAsync(settings, null);

        result.WanDnsServers.Should().Contain("8.8.8.8");
        result.WanDnsServers.Should().Contain("8.8.4.4");
    }

    [Fact]
    public async Task Analyze_WithWanDnsKey_ParsesServers()
    {
        var settings = JsonDocument.Parse(@"[
            {
                ""key"": ""wan_dns"",
                ""dns_servers"": [""1.1.1.1""]
            }
        ]").RootElement;

        var result = await _analyzer.AnalyzeAsync(settings, null);

        result.WanDnsServers.Should().Contain("1.1.1.1");
    }

    [Fact]
    public async Task Analyze_WithAutoMode_SetsIspDnsFlag()
    {
        var settings = JsonDocument.Parse(@"[
            {
                ""key"": ""dns"",
                ""mode"": ""auto""
            }
        ]").RootElement;

        var result = await _analyzer.AnalyzeAsync(settings, null);

        result.UsingIspDns.Should().BeTrue();
    }

    [Fact]
    public async Task Analyze_WithDhcpMode_SetsIspDnsFlag()
    {
        var settings = JsonDocument.Parse(@"[
            {
                ""key"": ""dns"",
                ""mode"": ""dhcp""
            }
        ]").RootElement;

        var result = await _analyzer.AnalyzeAsync(settings, null);

        result.UsingIspDns.Should().BeTrue();
    }

    #endregion

    #region Firewall Rules Tests

    [Fact]
    public async Task Analyze_WithDns53BlockRule_DetectsRule()
    {
        var firewall = JsonDocument.Parse(@"[
            {
                ""name"": ""Block DNS"",
                ""enabled"": true,
                ""action"": ""drop"",
                ""destination"": { ""port"": ""53"" }
            }
        ]").RootElement;

        var result = await _analyzer.AnalyzeAsync(null, ParseFirewallRules(firewall));

        result.HasDns53BlockRule.Should().BeTrue();
        result.Dns53RuleName.Should().Be("Block DNS");
    }

    [Fact]
    public async Task Analyze_WithDotBlockRule_DetectsRule()
    {
        var firewall = JsonDocument.Parse(@"[
            {
                ""name"": ""Block DoT"",
                ""enabled"": true,
                ""action"": ""reject"",
                ""destination"": { ""port"": ""853"" }
            }
        ]").RootElement;

        var result = await _analyzer.AnalyzeAsync(null, ParseFirewallRules(firewall));

        result.HasDotBlockRule.Should().BeTrue();
        result.DotRuleName.Should().Be("Block DoT");
    }

    [Fact]
    public async Task Analyze_WithDohBlockRule_DetectsRule()
    {
        var firewall = JsonDocument.Parse(@"[
            {
                ""name"": ""Block DoH Bypass"",
                ""enabled"": true,
                ""action"": ""drop"",
                ""destination"": {
                    ""port"": ""443"",
                    ""matching_target"": ""WEB"",
                    ""web_domains"": [""dns.google"", ""cloudflare-dns.com""]
                }
            }
        ]").RootElement;

        var result = await _analyzer.AnalyzeAsync(null, ParseFirewallRules(firewall));

        result.HasDohBlockRule.Should().BeTrue();
        result.DohBlockedDomains.Should().Contain("dns.google");
        result.DohBlockedDomains.Should().Contain("cloudflare-dns.com");
    }

    [Fact]
    public async Task Analyze_WithDoqBlockRule_DetectsRule()
    {
        // DoQ (DNS over QUIC) uses UDP 853 per RFC 9250
        var firewall = JsonDocument.Parse(@"[
            {
                ""name"": ""Block DoQ"",
                ""enabled"": true,
                ""action"": ""drop"",
                ""protocol"": ""udp"",
                ""destination"": { ""port"": ""853"" }
            }
        ]").RootElement;

        var result = await _analyzer.AnalyzeAsync(null, ParseFirewallRules(firewall));

        result.HasDoqBlockRule.Should().BeTrue();
    }

    [Fact]
    public async Task Analyze_WithCombinedDotDoqBlockRule_DetectsBoth()
    {
        // A single rule with tcp_udp protocol on port 853 blocks both DoT (TCP) and DoQ (UDP)
        var firewall = JsonDocument.Parse(@"[
            {
                ""name"": ""Block DoT and DoQ"",
                ""enabled"": true,
                ""action"": ""drop"",
                ""protocol"": ""tcp_udp"",
                ""destination"": { ""port"": ""853"" }
            }
        ]").RootElement;

        var result = await _analyzer.AnalyzeAsync(null, ParseFirewallRules(firewall));

        result.HasDotBlockRule.Should().BeTrue();
        result.HasDoqBlockRule.Should().BeTrue();
    }

    [Fact]
    public async Task Analyze_WithDns53TcpOnlyProtocol_DoesNotDetect()
    {
        // DNS 53 blocking requires UDP protocol - TCP-only rules should NOT be detected
        var firewall = JsonDocument.Parse(@"[
            {
                ""name"": ""Block DNS TCP Only"",
                ""enabled"": true,
                ""action"": ""drop"",
                ""protocol"": ""tcp"",
                ""destination"": { ""port"": ""53"" }
            }
        ]").RootElement;

        var result = await _analyzer.AnalyzeAsync(null, ParseFirewallRules(firewall));

        result.HasDns53BlockRule.Should().BeFalse();
    }

    [Fact]
    public async Task Analyze_WithPort853UdpOnly_DetectsDoqNotDot()
    {
        // UDP 853 is DoQ (DNS over QUIC), not DoT (which requires TCP)
        var firewall = JsonDocument.Parse(@"[
            {
                ""name"": ""Block DoQ UDP Only"",
                ""enabled"": true,
                ""action"": ""drop"",
                ""protocol"": ""udp"",
                ""destination"": { ""port"": ""853"" }
            }
        ]").RootElement;

        var result = await _analyzer.AnalyzeAsync(null, ParseFirewallRules(firewall));

        result.HasDotBlockRule.Should().BeFalse();
        result.HasDoqBlockRule.Should().BeTrue();
    }

    [Fact]
    public async Task Analyze_WithUdp443AndDomains_DetectsDoH3NotDoH()
    {
        // UDP 443 with web domains is DoH3 (HTTP/3 over QUIC), not DoH (which requires TCP)
        var firewall = JsonDocument.Parse(@"[
            {
                ""name"": ""Block DoH3 Only"",
                ""enabled"": true,
                ""action"": ""drop"",
                ""protocol"": ""udp"",
                ""destination"": {
                    ""port"": ""443"",
                    ""matching_target"": ""WEB"",
                    ""web_domains"": [""dns.google""]
                }
            }
        ]").RootElement;

        var result = await _analyzer.AnalyzeAsync(null, ParseFirewallRules(firewall));

        result.HasDohBlockRule.Should().BeFalse();
        result.HasDoh3BlockRule.Should().BeTrue();
    }

    [Fact]
    public async Task Analyze_WithDohTcpOnlyProtocol_DetectsOnlyDoh()
    {
        // TCP-only 443 rule with web domains should detect DoH but NOT DoH3 (which requires UDP)
        var firewall = JsonDocument.Parse(@"[
            {
                ""name"": ""Block DoH Only"",
                ""enabled"": true,
                ""action"": ""drop"",
                ""protocol"": ""tcp"",
                ""destination"": {
                    ""port"": ""443"",
                    ""matching_target"": ""WEB"",
                    ""web_domains"": [""dns.google""]
                }
            }
        ]").RootElement;

        var result = await _analyzer.AnalyzeAsync(null, ParseFirewallRules(firewall));

        result.HasDohBlockRule.Should().BeTrue();
        result.HasDoh3BlockRule.Should().BeFalse();
    }

    [Fact]
    public async Task Analyze_WithCombinedDns53AndDotBlockRule_DetectsBoth()
    {
        // A single rule with tcp_udp protocol and ports "53,853" blocks both DNS (UDP 53) and DoT (TCP 853)
        var firewall = JsonDocument.Parse(@"[
            {
                ""name"": ""Block DNS and DoT"",
                ""enabled"": true,
                ""action"": ""drop"",
                ""protocol"": ""tcp_udp"",
                ""destination"": { ""port"": ""53,853"" }
            }
        ]").RootElement;

        var result = await _analyzer.AnalyzeAsync(null, ParseFirewallRules(firewall));

        result.HasDns53BlockRule.Should().BeTrue();
        result.HasDotBlockRule.Should().BeTrue();
        result.Dns53RuleName.Should().Be("Block DNS and DoT");
        result.DotRuleName.Should().Be("Block DNS and DoT");
    }

    [Fact]
    public async Task Analyze_WithDisabledFirewallRule_IgnoresRule()
    {
        var firewall = JsonDocument.Parse(@"[
            {
                ""name"": ""Block DNS (Disabled)"",
                ""enabled"": false,
                ""action"": ""drop"",
                ""destination"": { ""port"": ""53"" }
            }
        ]").RootElement;

        var result = await _analyzer.AnalyzeAsync(null, ParseFirewallRules(firewall));

        result.HasDns53BlockRule.Should().BeFalse();
    }

    [Fact]
    public async Task Analyze_WithNonBlockAction_IgnoresRule()
    {
        var firewall = JsonDocument.Parse(@"[
            {
                ""name"": ""Allow DNS"",
                ""enabled"": true,
                ""action"": ""accept"",
                ""destination"": { ""port"": ""53"" }
            }
        ]").RootElement;

        var result = await _analyzer.AnalyzeAsync(null, ParseFirewallRules(firewall));

        result.HasDns53BlockRule.Should().BeFalse();
    }

    [Fact]
    public async Task Analyze_WithBlockAction_DetectsRule()
    {
        var firewall = JsonDocument.Parse(@"[
            {
                ""name"": ""Block DNS"",
                ""enabled"": true,
                ""action"": ""block"",
                ""destination"": { ""port"": ""53"" }
            }
        ]").RootElement;

        var result = await _analyzer.AnalyzeAsync(null, ParseFirewallRules(firewall));

        result.HasDns53BlockRule.Should().BeTrue();
    }

    [Fact]
    public async Task Analyze_BroadBlockAllRule_PortMatchingTypeAny_DetectsDns53()
    {
        // A broad "block all" rule with port_matching_type=ANY blocks all ports including DNS 53
        var firewall = JsonDocument.Parse(@"[
            {
                ""name"": ""Block All Traffic"",
                ""enabled"": true,
                ""action"": ""drop"",
                ""destination"": {
                    ""port_matching_type"": ""ANY"",
                    ""matching_target"": ""ANY""
                }
            }
        ]").RootElement;

        var result = await _analyzer.AnalyzeAsync(null, ParseFirewallRules(firewall));

        result.HasDns53BlockRule.Should().BeTrue("a block-all rule with port_matching_type=ANY blocks all ports including 53");
        result.Dns53RuleName.Should().Be("Block All Traffic");
    }

    [Fact]
    public async Task Analyze_BroadBlockAllRule_PortMatchingTypeAny_DetectsDoTAndDoQ()
    {
        // A broad "block all" rule should also be detected as blocking DoT (TCP 853) and DoQ (UDP 853)
        var firewall = JsonDocument.Parse(@"[
            {
                ""name"": ""Block All Traffic"",
                ""enabled"": true,
                ""action"": ""drop"",
                ""destination"": {
                    ""port_matching_type"": ""ANY"",
                    ""matching_target"": ""ANY""
                }
            }
        ]").RootElement;

        var result = await _analyzer.AnalyzeAsync(null, ParseFirewallRules(firewall));

        result.HasDotBlockRule.Should().BeTrue("a block-all rule blocks all ports including 853/TCP");
        result.HasDoqBlockRule.Should().BeTrue("a block-all rule blocks all ports including 853/UDP");
    }

    [Fact]
    public async Task Analyze_BroadBlockAllRule_PortMatchingTypeAny_DoesNotDetectDoH()
    {
        // A broad "block all" rule should NOT be detected as a DoH block rule
        // because DoH detection requires matching_target=WEB and web_domains
        var firewall = JsonDocument.Parse(@"[
            {
                ""name"": ""Block All Traffic"",
                ""enabled"": true,
                ""action"": ""drop"",
                ""destination"": {
                    ""port_matching_type"": ""ANY"",
                    ""matching_target"": ""ANY""
                }
            }
        ]").RootElement;

        var result = await _analyzer.AnalyzeAsync(null, ParseFirewallRules(firewall));

        result.HasDohBlockRule.Should().BeFalse("DoH detection requires web domain matching, not just port blocking");
    }

    [Fact]
    public async Task Analyze_BroadBlockAllRule_UdpOnly_PortMatchingTypeAny_DetectsDns53()
    {
        // A "block all UDP" rule with port_matching_type=ANY blocks DNS (UDP 53)
        var firewall = JsonDocument.Parse(@"[
            {
                ""name"": ""Block All UDP"",
                ""enabled"": true,
                ""action"": ""drop"",
                ""protocol"": ""udp"",
                ""destination"": {
                    ""port_matching_type"": ""ANY"",
                    ""matching_target"": ""ANY""
                }
            }
        ]").RootElement;

        var result = await _analyzer.AnalyzeAsync(null, ParseFirewallRules(firewall));

        result.HasDns53BlockRule.Should().BeTrue("blocking all UDP blocks DNS port 53");
        result.HasDotBlockRule.Should().BeFalse("DoT is TCP, not blocked by UDP-only rule");
        result.HasDoqBlockRule.Should().BeTrue("DoQ is UDP 853, blocked by all-UDP rule");
    }

    [Fact]
    public async Task Analyze_BroadBlockAllRule_TcpOnly_PortMatchingTypeAny_DoesNotDetectDns53()
    {
        // A "block all TCP" rule with port_matching_type=ANY does NOT block DNS (UDP 53)
        var firewall = JsonDocument.Parse(@"[
            {
                ""name"": ""Block All TCP"",
                ""enabled"": true,
                ""action"": ""drop"",
                ""protocol"": ""tcp"",
                ""destination"": {
                    ""port_matching_type"": ""ANY"",
                    ""matching_target"": ""ANY""
                }
            }
        ]").RootElement;

        var result = await _analyzer.AnalyzeAsync(null, ParseFirewallRules(firewall));

        result.HasDns53BlockRule.Should().BeFalse("DNS uses UDP, not blocked by TCP-only rule");
        result.HasDotBlockRule.Should().BeTrue("DoT is TCP 853, blocked by all-TCP rule");
        result.HasDoqBlockRule.Should().BeFalse("DoQ is UDP, not blocked by TCP-only rule");
    }

    [Fact]
    public async Task Analyze_BlockInvalidTraffic_DoesNotDetectAsDnsBlock()
    {
        // "Block Invalid Traffic" only blocks INVALID connection states, not NEW connections.
        // It should NOT be detected as a DNS block rule even though it has no port filter.
        var firewall = JsonDocument.Parse(@"[
            {
                ""name"": ""Block Invalid Traffic"",
                ""enabled"": true,
                ""action"": ""drop"",
                ""connection_state_type"": ""CUSTOM"",
                ""connection_states"": [""INVALID""],
                ""destination"": {
                    ""port_matching_type"": ""ANY"",
                    ""matching_target"": ""ANY""
                }
            }
        ]").RootElement;

        var result = await _analyzer.AnalyzeAsync(null, ParseFirewallRules(firewall));

        result.HasDns53BlockRule.Should().BeFalse("Block Invalid Traffic doesn't block NEW DNS connections");
        result.HasDotBlockRule.Should().BeFalse("Block Invalid Traffic doesn't block NEW DoT connections");
        result.HasDoqBlockRule.Should().BeFalse("Block Invalid Traffic doesn't block NEW DoQ connections");
    }

    [Fact]
    public async Task Analyze_BlockInvalidAndEstablished_DoesNotDetectAsDnsBlock()
    {
        // Rules blocking INVALID+ESTABLISHED but not NEW don't prevent DNS queries
        var firewall = JsonDocument.Parse(@"[
            {
                ""name"": ""Block Unauthorized Traffic"",
                ""enabled"": true,
                ""action"": ""drop"",
                ""connection_state_type"": ""CUSTOM"",
                ""connection_states"": [""INVALID"", ""ESTABLISHED""],
                ""destination"": {
                    ""port_matching_type"": ""ANY"",
                    ""matching_target"": ""ANY""
                }
            }
        ]").RootElement;

        var result = await _analyzer.AnalyzeAsync(null, ParseFirewallRules(firewall));

        result.HasDns53BlockRule.Should().BeFalse("rule without NEW state doesn't block DNS queries");
        result.HasDotBlockRule.Should().BeFalse("rule without NEW state doesn't block DoT queries");
        result.HasDoqBlockRule.Should().BeFalse("rule without NEW state doesn't block DoQ queries");
    }

    [Fact]
    public async Task Analyze_BlockAllWithNewState_DetectsAsDnsBlock()
    {
        // Rules that block NEW connections DO prevent DNS queries
        var firewall = JsonDocument.Parse(@"[
            {
                ""name"": ""Block All New Traffic"",
                ""enabled"": true,
                ""action"": ""drop"",
                ""connection_state_type"": ""CUSTOM"",
                ""connection_states"": [""NEW"", ""INVALID""],
                ""destination"": {
                    ""port_matching_type"": ""ANY"",
                    ""matching_target"": ""ANY""
                }
            }
        ]").RootElement;

        var result = await _analyzer.AnalyzeAsync(null, ParseFirewallRules(firewall));

        result.HasDns53BlockRule.Should().BeTrue("rule blocking NEW connections prevents DNS queries");
    }

    [Fact]
    public async Task Analyze_NarrowRulesWithWebDomainsOrAppCategories_DoNotDetectAsDnsBlock()
    {
        // Rules with web domains or app categories operate at the application layer,
        // not at the network port level. They should NOT be detected as DNS port-based block rules.
        var firewall = JsonDocument.Parse(@"[
            {
                ""name"": ""Block Scam Domains"",
                ""enabled"": true,
                ""action"": ""drop"",
                ""destination"": {
                    ""matching_target"": ""WEB"",
                    ""zone_id"": ""external-zone"",
                    ""web_domains"": [""scam-site.com"", ""phishing.net""]
                }
            },
            {
                ""name"": ""Block Torrent Trackers"",
                ""enabled"": true,
                ""action"": ""drop"",
                ""destination"": {
                    ""matching_target"": ""APP"",
                    ""zone_id"": ""external-zone"",
                    ""app_category_ids"": [5, 18]
                }
            }
        ]").RootElement;

        var result = await _analyzer.AnalyzeAsync(null, ParseFirewallRules(firewall));

        result.HasDns53BlockRule.Should().BeFalse("web domain rules aren't DNS port-based blocks");
        result.HasDotBlockRule.Should().BeFalse("app category rules aren't DoT port-based blocks");
        result.HasDoqBlockRule.Should().BeFalse("narrow rules aren't DoQ port-based blocks");
    }

    [Fact]
    public async Task Analyze_PredefinedCatchAllRule_DetectsAsDnsBlock()
    {
        // Predefined catch-all rules that block NEW connections DO block DNS traffic.
        // Users on default-block-all posture rely on these rules for DNS blocking.
        // We evaluate by content, not by predefined status.
        var firewall = JsonDocument.Parse(@"[
            {
                ""name"": ""Block Unauthorized Traffic"",
                ""enabled"": true,
                ""action"": ""drop"",
                ""predefined"": true,
                ""destination"": {
                    ""matching_target"": ""ANY"",
                    ""zone_id"": ""external-zone""
                }
            }
        ]").RootElement;

        var result = await _analyzer.AnalyzeAsync(null, ParseFirewallRules(firewall));

        result.HasDns53BlockRule.Should().BeTrue("predefined catch-all rules genuinely block DNS traffic");
        result.HasDotBlockRule.Should().BeTrue("predefined catch-all rules block DoT traffic");
        result.HasDoqBlockRule.Should().BeTrue("predefined catch-all rules block DoQ traffic");
    }

    [Fact]
    public async Task Analyze_PredefinedRuleWithSpecificPort_DetectsAsDnsBlock()
    {
        // Predefined rules with specific port restrictions ARE detected - they intentionally target ports.
        var firewall = JsonDocument.Parse(@"[
            {
                ""name"": ""Block External DNS"",
                ""enabled"": true,
                ""action"": ""drop"",
                ""predefined"": true,
                ""destination"": {
                    ""port"": ""53"",
                    ""matching_target"": ""ANY"",
                    ""zone_id"": ""external-zone""
                }
            }
        ]").RootElement;

        var result = await _analyzer.AnalyzeAsync(null, ParseFirewallRules(firewall));

        result.HasDns53BlockRule.Should().BeTrue("predefined rule with port 53 IS a DNS block rule");
    }

    [Fact]
    public async Task Analyze_BlockAllWithPortMatchingTypeAny_DetectsAsDnsBlock()
    {
        // A rule with source=ANY and no port restriction blocks all traffic including DNS.
        // This IS a DNS block rule (general block-all to external).
        var firewall = JsonDocument.Parse(@"[
            {
                ""name"": ""Block All External Traffic"",
                ""enabled"": true,
                ""action"": ""drop"",
                ""source"": {
                    ""matching_target"": ""ANY""
                },
                ""destination"": {
                    ""port_matching_type"": ""ANY"",
                    ""matching_target"": ""ANY"",
                    ""zone_id"": ""external-zone""
                }
            }
        ]").RootElement;

        var result = await _analyzer.AnalyzeAsync(null, ParseFirewallRules(firewall));

        result.HasDns53BlockRule.Should().BeTrue("block-all to external with ANY source blocks DNS");
        result.HasDotBlockRule.Should().BeTrue("block-all to external blocks DoT");
        result.HasDoqBlockRule.Should().BeTrue("block-all to external blocks DoQ");
    }

    [Fact]
    public async Task Analyze_SourceSpecificBlockAll_DetectsAsDnsBlock()
    {
        // Source-specific block-all rules DO block DNS for those networks.
        // Coverage tracking handles per-network accounting.
        var firewall = JsonDocument.Parse(@"[
            {
                ""name"": ""Block Network Internet Access"",
                ""enabled"": true,
                ""action"": ""drop"",
                ""source"": {
                    ""matching_target"": ""NETWORK"",
                    ""network_ids"": [""net-123""]
                },
                ""destination"": {
                    ""matching_target"": ""ANY"",
                    ""zone_id"": ""external-zone""
                }
            }
        ]").RootElement;

        var result = await _analyzer.AnalyzeAsync(null, ParseFirewallRules(firewall));

        result.HasDns53BlockRule.Should().BeTrue("source-specific block-all rules block DNS for those networks");
    }

    [Fact]
    public async Task Analyze_WithDns53BlockRuleUsingPortGroup_DetectsRule()
    {
        // Arrange - Firewall rule using port group reference instead of direct port
        var firewall = JsonDocument.Parse(@"[
            {
                ""name"": ""Block External DNS"",
                ""enabled"": true,
                ""action"": ""drop"",
                ""destination"": {
                    ""port_matching_type"": ""OBJECT"",
                    ""port_group_id"": ""67890abc""
                }
            }
        ]").RootElement;

        var firewallGroups = new List<UniFiFirewallGroup>
        {
            new UniFiFirewallGroup
            {
                Id = "67890abc",
                Name = "DNS Ports",
                GroupType = "port-group",
                GroupMembers = new List<string> { "53" }
            }
        };

        // Act
        var result = await _analyzer.AnalyzeAsync(null, ParseFirewallRules(firewall, firewallGroups), null, null, null, null, null);

        // Assert
        result.HasDns53BlockRule.Should().BeTrue();
        result.Dns53RuleName.Should().Be("Block External DNS");
    }

    [Fact]
    public async Task Analyze_WithDoTBlockRuleUsingPortGroup_DetectsRule()
    {
        // Arrange - Firewall rule using port group reference for DoT (port 853)
        var firewall = JsonDocument.Parse(@"[
            {
                ""name"": ""Block DoT via Group"",
                ""enabled"": true,
                ""action"": ""drop"",
                ""protocol"": ""tcp"",
                ""destination"": {
                    ""port_matching_type"": ""OBJECT"",
                    ""port_group_id"": ""dot-group-id""
                }
            }
        ]").RootElement;

        var firewallGroups = new List<UniFiFirewallGroup>
        {
            new UniFiFirewallGroup
            {
                Id = "dot-group-id",
                Name = "DoT Port",
                GroupType = "port-group",
                GroupMembers = new List<string> { "853" }
            }
        };

        // Act
        var result = await _analyzer.AnalyzeAsync(null, ParseFirewallRules(firewall, firewallGroups), null, null, null, null, null);

        // Assert
        result.HasDotBlockRule.Should().BeTrue();
        result.DotRuleName.Should().Be("Block DoT via Group");
    }

    [Fact]
    public async Task Analyze_WithCombinedDnsAndDoTBlockRuleUsingPortGroup_DetectsBoth()
    {
        // Arrange - Firewall rule using port group with both DNS and DoT ports
        var firewall = JsonDocument.Parse(@"[
            {
                ""name"": ""Block DNS and DoT"",
                ""enabled"": true,
                ""action"": ""drop"",
                ""protocol"": ""tcp_udp"",
                ""destination"": {
                    ""port_matching_type"": ""OBJECT"",
                    ""port_group_id"": ""dns-dot-group""
                }
            }
        ]").RootElement;

        var firewallGroups = new List<UniFiFirewallGroup>
        {
            new UniFiFirewallGroup
            {
                Id = "dns-dot-group",
                Name = "DNS and DoT Ports",
                GroupType = "port-group",
                GroupMembers = new List<string> { "53", "853" }
            }
        };

        // Act
        var result = await _analyzer.AnalyzeAsync(null, ParseFirewallRules(firewall, firewallGroups), null, null, null, null, null);

        // Assert
        result.HasDns53BlockRule.Should().BeTrue();
        result.HasDotBlockRule.Should().BeTrue();
        result.Dns53RuleName.Should().Be("Block DNS and DoT");
        result.DotRuleName.Should().Be("Block DNS and DoT");
    }

    [Fact]
    public async Task Analyze_WithPortGroupContainingPortRange_DetectsIncludedPorts()
    {
        // Arrange - Port group with a range that includes DNS port
        var firewall = JsonDocument.Parse(@"[
            {
                ""name"": ""Block Low Ports"",
                ""enabled"": true,
                ""action"": ""drop"",
                ""destination"": {
                    ""port_matching_type"": ""OBJECT"",
                    ""port_group_id"": ""low-ports-group""
                }
            }
        ]").RootElement;

        var firewallGroups = new List<UniFiFirewallGroup>
        {
            new UniFiFirewallGroup
            {
                Id = "low-ports-group",
                Name = "Low Ports",
                GroupType = "port-group",
                GroupMembers = new List<string> { "50-100" } // Range includes port 53
            }
        };

        // Act
        var result = await _analyzer.AnalyzeAsync(null, ParseFirewallRules(firewall, firewallGroups), null, null, null, null, null);

        // Assert
        result.HasDns53BlockRule.Should().BeTrue();
    }

    [Fact]
    public async Task Analyze_WithMissingPortGroupId_DoesNotDetectAsDnsBlock()
    {
        // Arrange - Firewall rule references non-existent port group.
        // The rule intended to block specific ports but the group couldn't be resolved.
        // The parser marks this with HasUnresolvedDestinationPortGroup, so it's skipped.
        var firewall = JsonDocument.Parse(@"[
            {
                ""name"": ""Block DNS (Broken)"",
                ""enabled"": true,
                ""action"": ""drop"",
                ""destination"": {
                    ""port_matching_type"": ""OBJECT"",
                    ""port_group_id"": ""nonexistent-group""
                }
            }
        ]").RootElement;

        var firewallGroups = new List<UniFiFirewallGroup>
        {
            new UniFiFirewallGroup
            {
                Id = "different-group",
                Name = "Other Ports",
                GroupType = "port-group",
                GroupMembers = new List<string> { "53" }
            }
        };

        // Act
        var result = await _analyzer.AnalyzeAsync(null, ParseFirewallRules(firewall, firewallGroups), null, null, null, null, null);

        // Assert - Unresolved port group = rule skipped for port-based detection
        result.HasDns53BlockRule.Should().BeFalse();
    }

    [Fact]
    public async Task Analyze_WithMatchOppositePorts_DoesNotDetectAsBlockRule()
    {
        // Arrange - Rule with match_opposite_ports=true means "block everything EXCEPT port 53"
        // This should NOT be detected as a DNS block rule
        var firewall = JsonDocument.Parse(@"[
            {
                ""name"": ""Block All Except DNS"",
                ""enabled"": true,
                ""action"": ""drop"",
                ""destination"": {
                    ""port"": ""53"",
                    ""match_opposite_ports"": true
                }
            }
        ]").RootElement;

        // Act
        var result = await _analyzer.AnalyzeAsync(null, ParseFirewallRules(firewall));

        // Assert - Should NOT detect as DNS block rule (ports are inverted)
        result.HasDns53BlockRule.Should().BeFalse();
    }

    [Fact]
    public async Task Analyze_WithMatchOppositeProtocolUdp_DoesNotBlockDns()
    {
        // Arrange - Rule with match_opposite_protocol=true and protocol=udp
        // Means "block everything EXCEPT UDP" - so UDP traffic (DNS) is NOT blocked
        var firewall = JsonDocument.Parse(@"[
            {
                ""name"": ""Block Non-UDP"",
                ""enabled"": true,
                ""action"": ""drop"",
                ""protocol"": ""udp"",
                ""match_opposite_protocol"": true,
                ""destination"": {
                    ""port"": ""53""
                }
            }
        ]").RootElement;

        // Act
        var result = await _analyzer.AnalyzeAsync(null, ParseFirewallRules(firewall));

        // Assert - UDP is excluded, so DNS (UDP 53) is NOT blocked
        result.HasDns53BlockRule.Should().BeFalse();
    }

    [Fact]
    public async Task Analyze_WithMatchOppositeProtocolIcmp_DoesBlockDns()
    {
        // Arrange - Rule with match_opposite_protocol=true and protocol=icmp
        // Means "block everything EXCEPT ICMP" - so UDP/TCP traffic IS blocked
        var firewall = JsonDocument.Parse(@"[
            {
                ""name"": ""Block All Except ICMP"",
                ""enabled"": true,
                ""action"": ""drop"",
                ""protocol"": ""icmp"",
                ""match_opposite_protocol"": true,
                ""destination"": {
                    ""port"": ""53""
                }
            }
        ]").RootElement;

        // Act
        var result = await _analyzer.AnalyzeAsync(null, ParseFirewallRules(firewall));

        // Assert - ICMP is excluded, but UDP is still blocked, so DNS IS blocked
        result.HasDns53BlockRule.Should().BeTrue();
        result.Dns53RuleName.Should().Be("Block All Except ICMP");
    }

    [Fact]
    public async Task Analyze_WithMatchOppositeProtocolTcp_DoesBlockDnsButNotDoT()
    {
        // Arrange - Rule with match_opposite_protocol=true and protocol=tcp
        // Means "block everything EXCEPT TCP" - so UDP is blocked but TCP is not
        var firewall = JsonDocument.Parse(@"[
            {
                ""name"": ""Block Non-TCP"",
                ""enabled"": true,
                ""action"": ""drop"",
                ""protocol"": ""tcp"",
                ""match_opposite_protocol"": true,
                ""destination"": {
                    ""port"": ""53,853""
                }
            }
        ]").RootElement;

        // Act
        var result = await _analyzer.AnalyzeAsync(null, ParseFirewallRules(firewall));

        // Assert - TCP is excluded, so DoT (TCP 853) is NOT blocked
        // But UDP is blocked, so DNS53 (UDP 53) IS blocked
        result.HasDns53BlockRule.Should().BeTrue();
        result.HasDotBlockRule.Should().BeFalse();
    }

    [Fact]
    public async Task Analyze_WithMatchOppositeProtocolTcp_DoesBlockDoQ()
    {
        // Arrange - Rule with match_opposite_protocol=true and protocol=tcp for port 853
        // Means "block everything EXCEPT TCP" - so DoQ (UDP 853) IS blocked
        var firewall = JsonDocument.Parse(@"[
            {
                ""name"": ""Block Non-TCP on 853"",
                ""enabled"": true,
                ""action"": ""drop"",
                ""protocol"": ""tcp"",
                ""match_opposite_protocol"": true,
                ""destination"": {
                    ""port"": ""853""
                }
            }
        ]").RootElement;

        // Act
        var result = await _analyzer.AnalyzeAsync(null, ParseFirewallRules(firewall));

        // Assert - TCP is excluded, so DoT is NOT blocked
        // But UDP is blocked, so DoQ IS blocked
        result.HasDotBlockRule.Should().BeFalse();
        result.HasDoqBlockRule.Should().BeTrue();
    }

    [Fact]
    public async Task Analyze_NormalProtocolAndPorts_WorksWithoutInversion()
    {
        // Arrange - Normal rule without any match_opposite flags
        var firewall = JsonDocument.Parse(@"[
            {
                ""name"": ""Block DNS Normal"",
                ""enabled"": true,
                ""action"": ""drop"",
                ""protocol"": ""udp"",
                ""match_opposite_protocol"": false,
                ""destination"": {
                    ""port"": ""53"",
                    ""match_opposite_ports"": false
                }
            }
        ]").RootElement;

        // Act
        var result = await _analyzer.AnalyzeAsync(null, ParseFirewallRules(firewall));

        // Assert - Normal blocking works
        result.HasDns53BlockRule.Should().BeTrue();
    }

    #region App-Based Detection Tests

    [Fact]
    public async Task Analyze_AppBasedDnsBlock_DetectsDns53()
    {
        // App-based rule using DNS app ID (589885) should detect DNS53 blocking
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "app-dns-rule",
                Name = "Block DNS App",
                Enabled = true,
                Action = "drop",
                Protocol = "tcp_udp",
                AppIds = new List<int> { DnsAppIds.Dns },
                DestinationMatchingTarget = "APP",
                DestinationZoneId = ExternalZoneId
            }
        };

        var result = await _analyzer.AnalyzeAsync(null, rules, null, null, null, null, null, null, ExternalZoneId);

        result.HasDns53BlockRule.Should().BeTrue();
        result.Dns53RuleName.Should().Be("Block DNS App");
    }

    [Fact]
    public async Task Analyze_AppBasedPort853Block_DetectsDotAndDoq()
    {
        // App-based rule using DoT app ID (1310917) with tcp_udp protocol should detect both DoT and DoQ
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "app-dot-rule",
                Name = "Block DoT App",
                Enabled = true,
                Action = "drop",
                Protocol = "tcp_udp",
                AppIds = new List<int> { DnsAppIds.DnsOverTls },
                DestinationMatchingTarget = "APP",
                DestinationZoneId = ExternalZoneId
            }
        };

        var result = await _analyzer.AnalyzeAsync(null, rules, null, null, null, null, null, null, ExternalZoneId);

        result.HasDotBlockRule.Should().BeTrue();
        result.HasDoqBlockRule.Should().BeTrue();
        result.DotRuleName.Should().Be("Block DoT App");
        result.DoqRuleName.Should().Be("Block DoT App");
    }

    [Fact]
    public async Task Analyze_AppBasedPort443Block_DetectsDohAndDoh3()
    {
        // App-based rule using DoH app ID (1310919) with tcp_udp protocol should detect both DoH and DoH3
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "app-doh-rule",
                Name = "Block DoH App",
                Enabled = true,
                Action = "drop",
                Protocol = "tcp_udp",
                AppIds = new List<int> { DnsAppIds.DnsOverHttps },
                DestinationMatchingTarget = "APP",
                DestinationZoneId = ExternalZoneId
            }
        };

        var result = await _analyzer.AnalyzeAsync(null, rules, null, null, null, null, null, null, ExternalZoneId);

        result.HasDohBlockRule.Should().BeTrue();
        result.HasDoh3BlockRule.Should().BeTrue();
        result.DohRuleName.Should().Be("Block DoH App");
        result.Doh3RuleName.Should().Be("Block DoH App");
    }

    [Fact]
    public async Task Analyze_AppBasedAllApps_DetectsFullCoverage()
    {
        // App-based rule with all DNS app IDs should detect all DNS protocols
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "app-all-dns-rule",
                Name = "Block All DNS Apps",
                Enabled = true,
                Action = "drop",
                Protocol = "tcp_udp",
                AppIds = new List<int> { DnsAppIds.Dns, DnsAppIds.DnsOverTls, DnsAppIds.DnsOverHttps },
                DestinationMatchingTarget = "APP",
                DestinationZoneId = ExternalZoneId
            }
        };

        var result = await _analyzer.AnalyzeAsync(null, rules, null, null, null, null, null, null, ExternalZoneId);

        result.HasDns53BlockRule.Should().BeTrue();
        result.HasDotBlockRule.Should().BeTrue();
        result.HasDoqBlockRule.Should().BeTrue();
        result.HasDohBlockRule.Should().BeTrue();
        result.HasDoh3BlockRule.Should().BeTrue();
    }

    [Fact]
    public async Task Analyze_AppBasedWithTcpOnly_SkipsUdpProtocols()
    {
        // App-based rule with TCP-only protocol should only detect TCP-based DNS (DoT, DoH)
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "app-tcp-only",
                Name = "Block DNS Apps TCP Only",
                Enabled = true,
                Action = "drop",
                Protocol = "tcp",
                AppIds = new List<int> { DnsAppIds.Dns, DnsAppIds.DnsOverTls, DnsAppIds.DnsOverHttps },
                DestinationMatchingTarget = "APP",
                DestinationZoneId = ExternalZoneId
            }
        };

        var result = await _analyzer.AnalyzeAsync(null, rules, null, null, null, null, null, null, ExternalZoneId);

        // TCP-only should detect DoT and DoH but NOT DNS53, DoQ, or DoH3 (which require UDP)
        result.HasDns53BlockRule.Should().BeFalse();
        result.HasDotBlockRule.Should().BeTrue();
        result.HasDoqBlockRule.Should().BeFalse();
        result.HasDohBlockRule.Should().BeTrue();
        result.HasDoh3BlockRule.Should().BeFalse();
    }

    [Fact]
    public async Task Analyze_LegacyAppRule_AssumesAllProtocols()
    {
        // Legacy app-based rule with protocol="all" (no protocol field) should assume all protocols blocked
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "legacy-app-rule",
                Name = "Legacy Block DNS Apps",
                Enabled = true,
                Action = "block",
                Protocol = "all", // Legacy rules have no protocol - we default to "all"
                AppIds = new List<int> { DnsAppIds.Dns, DnsAppIds.DnsOverTls, DnsAppIds.DnsOverHttps },
                DestinationMatchingTarget = "APP",
                DestinationZoneId = ExternalZoneId
            }
        };

        var result = await _analyzer.AnalyzeAsync(null, rules, null, null, null, null, null, null, ExternalZoneId);

        // With protocol="all", all DNS protocols should be detected as blocked
        result.HasDns53BlockRule.Should().BeTrue();
        result.HasDotBlockRule.Should().BeTrue();
        result.HasDoqBlockRule.Should().BeTrue();
        result.HasDohBlockRule.Should().BeTrue();
        result.HasDoh3BlockRule.Should().BeTrue();
    }

    [Fact]
    public async Task Analyze_AppBasedWithUdpOnly_SkipsTcpProtocols()
    {
        // App-based rule with UDP-only protocol should only detect UDP-based DNS (DNS53, DoQ, DoH3)
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "app-udp-only",
                Name = "Block DNS Apps UDP Only",
                Enabled = true,
                Action = "drop",
                Protocol = "udp",
                AppIds = new List<int> { DnsAppIds.Dns, DnsAppIds.DnsOverTls, DnsAppIds.DnsOverHttps },
                DestinationMatchingTarget = "APP",
                DestinationZoneId = ExternalZoneId
            }
        };

        var result = await _analyzer.AnalyzeAsync(null, rules, null, null, null, null, null, null, ExternalZoneId);

        // UDP-only should detect DNS53, DoQ, and DoH3 but NOT DoT or DoH (which require TCP)
        result.HasDns53BlockRule.Should().BeTrue();
        result.HasDotBlockRule.Should().BeFalse();
        result.HasDoqBlockRule.Should().BeTrue();
        result.HasDohBlockRule.Should().BeFalse();
        result.HasDoh3BlockRule.Should().BeTrue();
    }

    [Fact]
    public async Task Analyze_AppBasedNonBlockAction_IgnoresRule()
    {
        // App-based rule with accept action should NOT be detected as block rule
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "app-allow-rule",
                Name = "Allow DNS Apps",
                Enabled = true,
                Action = "accept",
                Protocol = "tcp_udp",
                AppIds = new List<int> { DnsAppIds.Dns, DnsAppIds.DnsOverTls, DnsAppIds.DnsOverHttps },
                DestinationMatchingTarget = "APP",
                DestinationZoneId = ExternalZoneId
            }
        };

        var result = await _analyzer.AnalyzeAsync(null, rules, null, null, null, null, null, null, ExternalZoneId);

        result.HasDns53BlockRule.Should().BeFalse();
        result.HasDotBlockRule.Should().BeFalse();
        result.HasDohBlockRule.Should().BeFalse();
    }

    [Fact]
    public async Task Analyze_AppBasedDisabledRule_IgnoresRule()
    {
        // Disabled app-based rule should NOT be detected
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "app-disabled-rule",
                Name = "Disabled DNS Apps",
                Enabled = false,
                Action = "drop",
                Protocol = "tcp_udp",
                AppIds = new List<int> { DnsAppIds.Dns, DnsAppIds.DnsOverTls, DnsAppIds.DnsOverHttps },
                DestinationMatchingTarget = "APP",
                DestinationZoneId = ExternalZoneId
            }
        };

        var result = await _analyzer.AnalyzeAsync(null, rules, null, null, null, null, null, null, ExternalZoneId);

        result.HasDns53BlockRule.Should().BeFalse();
        result.HasDotBlockRule.Should().BeFalse();
        result.HasDohBlockRule.Should().BeFalse();
    }

    [Fact]
    public async Task Analyze_AppBasedWrongZone_IgnoresRule()
    {
        // App-based rule targeting wrong zone should NOT be detected
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "app-wrong-zone",
                Name = "Block DNS Apps LAN",
                Enabled = true,
                Action = "drop",
                Protocol = "tcp_udp",
                AppIds = new List<int> { DnsAppIds.Dns },
                DestinationMatchingTarget = "APP",
                DestinationZoneId = LanZoneId // Wrong zone
            }
        };

        var result = await _analyzer.AnalyzeAsync(null, rules, null, null, null, null, null, null, ExternalZoneId);

        result.HasDns53BlockRule.Should().BeFalse();
    }

    #endregion

    #region External Zone ID Tests

    private const string ExternalZoneId = "external-zone-123";
    private const string LanZoneId = "lan-zone-456";

    [Fact]
    public async Task Analyze_WithDns53BlockRule_TargetingExternalZone_DetectsRule()
    {
        // Arrange - Rule explicitly targets the external zone
        var firewall = JsonDocument.Parse($@"[
            {{
                ""name"": ""Block External DNS"",
                ""enabled"": true,
                ""action"": ""drop"",
                ""destination"": {{
                    ""port"": ""53"",
                    ""zone_id"": ""{ExternalZoneId}""
                }}
            }}
        ]").RootElement;

        // Act - Pass the matching external zone ID
        var result = await _analyzer.AnalyzeAsync(null, ParseFirewallRules(firewall), null, null, null, null, null, null, ExternalZoneId);

        // Assert - Rule is detected because it targets the external zone
        result.HasDns53BlockRule.Should().BeTrue();
        result.Dns53RuleName.Should().Be("Block External DNS");
    }

    [Fact]
    public async Task Analyze_WithDns53BlockRule_TargetingLanZone_DoesNotDetectRule()
    {
        // Arrange - Rule targets the LAN zone, not external
        var firewall = JsonDocument.Parse($@"[
            {{
                ""name"": ""Block LAN DNS"",
                ""enabled"": true,
                ""action"": ""drop"",
                ""destination"": {{
                    ""port"": ""53"",
                    ""zone_id"": ""{LanZoneId}""
                }}
            }}
        ]").RootElement;

        // Act - Pass the external zone ID (different from rule's destination)
        var result = await _analyzer.AnalyzeAsync(null, ParseFirewallRules(firewall), null, null, null, null, null, null, ExternalZoneId);

        // Assert - Rule is NOT detected because it doesn't target the external zone
        result.HasDns53BlockRule.Should().BeFalse();
    }

    [Fact]
    public async Task Analyze_WithDns53BlockRule_NoZoneIdProvided_FallsBackToDetecting()
    {
        // Arrange - Rule has no zone_id specified
        var firewall = JsonDocument.Parse(@"[
            {
                ""name"": ""Block DNS"",
                ""enabled"": true,
                ""action"": ""drop"",
                ""destination"": { ""port"": ""53"" }
            }
        ]").RootElement;

        // Act - Pass external zone ID, but rule doesn't have zone_id (matches any zone)
        var result = await _analyzer.AnalyzeAsync(null, ParseFirewallRules(firewall), null, null, null, null, null, null, ExternalZoneId);

        // Assert - Rule is detected (no zone_id means it applies to all zones)
        result.HasDns53BlockRule.Should().BeTrue();
    }

    [Fact]
    public async Task Analyze_WithDns53BlockRule_NoExternalZoneIdProvided_DetectsAnyRule()
    {
        // Arrange - Rule with zone_id, but we don't know the external zone
        var firewall = JsonDocument.Parse($@"[
            {{
                ""name"": ""Block Some Zone DNS"",
                ""enabled"": true,
                ""action"": ""drop"",
                ""destination"": {{
                    ""port"": ""53"",
                    ""zone_id"": ""{LanZoneId}""
                }}
            }}
        ]").RootElement;

        // Act - Don't pass external zone ID (null)
        var result = await _analyzer.AnalyzeAsync(null, ParseFirewallRules(firewall), null, null, null, null, null, null, null);

        // Assert - Rule is detected because we can't validate zone (fallback behavior)
        result.HasDns53BlockRule.Should().BeTrue();
    }

    [Fact]
    public async Task Analyze_WithDotBlockRule_TargetingWrongZone_DoesNotDetect()
    {
        // Arrange - DoT rule targets wrong zone
        var firewall = JsonDocument.Parse($@"[
            {{
                ""name"": ""Block LAN DoT"",
                ""enabled"": true,
                ""action"": ""drop"",
                ""destination"": {{
                    ""port"": ""853"",
                    ""zone_id"": ""{LanZoneId}""
                }}
            }}
        ]").RootElement;

        // Act
        var result = await _analyzer.AnalyzeAsync(null, ParseFirewallRules(firewall), null, null, null, null, null, null, ExternalZoneId);

        // Assert - Not detected because it targets the wrong zone
        result.HasDotBlockRule.Should().BeFalse();
        result.HasDoqBlockRule.Should().BeFalse();
    }

    #endregion

    #endregion

    #region WAN DNS Extraction Tests

    [Fact]
    public async Task Analyze_WithGatewayDeviceData_ExtractsWanDns()
    {
        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""ugw"",
                ""port_table"": [
                    {
                        ""network_name"": ""wan"",
                        ""name"": ""WAN"",
                        ""up"": true,
                        ""ip"": ""192.0.2.100"",
                        ""dns"": [""8.8.8.8"", ""8.8.4.4""]
                    }
                ]
            }
        ]").RootElement;

        var result = await _analyzer.AnalyzeAsync(null, null, null, null, deviceData);

        result.WanDnsServers.Should().Contain("8.8.8.8");
        result.WanDnsServers.Should().Contain("8.8.4.4");
        result.WanInterfaces.Should().HaveCount(1);
    }

    [Fact]
    public async Task Analyze_WithUdmDevice_ExtractsWanDns()
    {
        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""udm"",
                ""port_table"": [
                    {
                        ""network_name"": ""wan"",
                        ""name"": ""WAN1"",
                        ""up"": true,
                        ""dns"": [""1.1.1.1""]
                    }
                ]
            }
        ]").RootElement;

        var result = await _analyzer.AnalyzeAsync(null, null, null, null, deviceData);

        result.WanDnsServers.Should().Contain("1.1.1.1");
    }

    [Fact]
    public async Task Analyze_WithMultipleWanInterfaces_ExtractsAll()
    {
        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""udm"",
                ""port_table"": [
                    {
                        ""network_name"": ""wan"",
                        ""name"": ""WAN1"",
                        ""up"": true,
                        ""dns"": [""8.8.8.8""]
                    },
                    {
                        ""network_name"": ""wan2"",
                        ""name"": ""WAN2"",
                        ""up"": true,
                        ""dns"": [""1.1.1.1""]
                    }
                ]
            }
        ]").RootElement;

        var result = await _analyzer.AnalyzeAsync(null, null, null, null, deviceData);

        result.WanInterfaces.Should().HaveCount(2);
        result.WanDnsServers.Should().Contain("8.8.8.8");
        result.WanDnsServers.Should().Contain("1.1.1.1");
    }

    [Fact]
    public async Task Analyze_WithWanInterfaceWithoutDns_SetsIspDnsFlag()
    {
        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""ugw"",
                ""port_table"": [
                    {
                        ""network_name"": ""wan"",
                        ""name"": ""WAN"",
                        ""up"": true,
                        ""ip"": ""192.0.2.100""
                    }
                ]
            }
        ]").RootElement;

        var result = await _analyzer.AnalyzeAsync(null, null, null, null, deviceData);

        result.UsingIspDns.Should().BeTrue();
    }

    [Fact]
    public async Task Analyze_WithNonGatewayDevice_SkipsDevice()
    {
        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""usw"",
                ""port_table"": [
                    {
                        ""network_name"": ""wan"",
                        ""dns"": [""8.8.8.8""]
                    }
                ]
            }
        ]").RootElement;

        var result = await _analyzer.AnalyzeAsync(null, null, null, null, deviceData);

        result.WanInterfaces.Should().BeEmpty();
    }

    #endregion

    #region GetSummary Tests

    [Fact]
    public void GetSummary_WithEmptyResult_ReturnsDefaultSummary()
    {
        var analysisResult = new DnsSecurityResult();

        var summary = _analyzer.GetSummary(analysisResult);

        summary.DohEnabled.Should().BeFalse();
        summary.DnsLeakProtection.Should().BeFalse();
        summary.FullyProtected.Should().BeFalse();
    }

    [Fact]
    public async Task GetSummary_WithDohConfigured_ReflectsInSummary()
    {
        var settings = JsonDocument.Parse(@"[
            {
                ""key"": ""doh"",
                ""state"": ""auto"",
                ""server_names"": [""cloudflare""]
            }
        ]").RootElement;

        var analysisResult = await _analyzer.AnalyzeAsync(settings, null);
        var summary = _analyzer.GetSummary(analysisResult);

        summary.DohEnabled.Should().BeTrue();
        summary.DohProviders.Should().NotBeEmpty();
    }

    [Fact]
    public void GetSummary_WithAllProtection_ShowsFullyProtected()
    {
        var analysisResult = new DnsSecurityResult
        {
            DohConfigured = true,
            HasDns53BlockRule = true,
            HasDotBlockRule = true,
            DotProvidesFullCoverage = true,
            HasDohBlockRule = true,
            HasDoqBlockRule = true,
            DoqProvidesFullCoverage = true,
            WanDnsMatchesDoH = true,
            DeviceDnsPointsToGateway = true
        };

        var summary = _analyzer.GetSummary(analysisResult);

        summary.FullyProtected.Should().BeTrue();
        summary.DoqBypassBlocked.Should().BeTrue();
    }

    [Fact]
    public void GetSummary_CountsIssues()
    {
        var analysisResult = new DnsSecurityResult();
        analysisResult.Issues.Add(new AuditIssue { Type = "TEST1", Severity = AuditSeverity.Critical, Message = "Test" });
        analysisResult.Issues.Add(new AuditIssue { Type = "TEST2", Severity = AuditSeverity.Recommended, Message = "Test" });

        var summary = _analyzer.GetSummary(analysisResult);

        summary.IssueCount.Should().Be(2);
        summary.CriticalIssueCount.Should().Be(1);
    }

    #endregion

    #region DnsSecurityResult Tests

    [Fact]
    public void DnsSecurityResult_WanDnsOrderCorrect_ReturnsTrue_WhenAllInterfacesCorrect()
    {
        var result = new DnsSecurityResult();
        result.WanInterfaces.Add(new WanInterfaceDns { InterfaceName = "wan", OrderCorrect = true });
        result.WanInterfaces.Add(new WanInterfaceDns { InterfaceName = "wan2", OrderCorrect = true });

        result.WanDnsOrderCorrect.Should().BeTrue();
    }

    [Fact]
    public void DnsSecurityResult_WanDnsOrderCorrect_ReturnsFalse_WhenAnyInterfaceIncorrect()
    {
        var result = new DnsSecurityResult();
        result.WanInterfaces.Add(new WanInterfaceDns { InterfaceName = "wan", OrderCorrect = true });
        result.WanInterfaces.Add(new WanInterfaceDns { InterfaceName = "wan2", OrderCorrect = false });

        result.WanDnsOrderCorrect.Should().BeFalse();
    }

    [Fact]
    public void DnsSecurityResult_WanDnsPtrResults_AggregatesFromAllInterfaces()
    {
        var result = new DnsSecurityResult();
        result.WanInterfaces.Add(new WanInterfaceDns
        {
            InterfaceName = "wan",
            ReverseDnsResults = new List<string?> { "dns1.example.com", "dns2.example.com" }
        });
        result.WanInterfaces.Add(new WanInterfaceDns
        {
            InterfaceName = "wan2",
            ReverseDnsResults = new List<string?> { "dns3.example.com" }
        });

        result.WanDnsPtrResults.Should().HaveCount(3);
    }

    #endregion

    #region Third-Party DNS Detection Properties Tests

    [Fact]
    public void DnsSecurityResult_IsPiholeDetected_ReturnsTrue_WhenPiholeInThirdPartyServers()
    {
        var result = new DnsSecurityResult();
        result.ThirdPartyDnsServers.Add(new ThirdPartyDnsDetector.ThirdPartyDnsInfo
        {
            DnsServerIp = "192.168.1.5",
            NetworkName = "Corporate",
            IsPihole = true,
            DnsProviderName = "Pi-hole"
        });

        result.IsPiholeDetected.Should().BeTrue();
        result.IsAdGuardHomeDetected.Should().BeFalse();
    }

    [Fact]
    public void DnsSecurityResult_IsAdGuardHomeDetected_ReturnsTrue_WhenAdGuardHomeInThirdPartyServers()
    {
        var result = new DnsSecurityResult();
        result.ThirdPartyDnsServers.Add(new ThirdPartyDnsDetector.ThirdPartyDnsInfo
        {
            DnsServerIp = "192.168.1.5",
            NetworkName = "Corporate",
            IsAdGuardHome = true,
            DnsProviderName = "AdGuard Home"
        });

        result.IsPiholeDetected.Should().BeFalse();
        result.IsAdGuardHomeDetected.Should().BeTrue();
    }

    [Fact]
    public void DnsSecurityResult_BothPiholeAndAdGuardHome_WhenBothDetected()
    {
        var result = new DnsSecurityResult();
        result.ThirdPartyDnsServers.Add(new ThirdPartyDnsDetector.ThirdPartyDnsInfo
        {
            DnsServerIp = "192.168.1.5",
            NetworkName = "Corporate",
            IsPihole = true,
            DnsProviderName = "Pi-hole"
        });
        result.ThirdPartyDnsServers.Add(new ThirdPartyDnsDetector.ThirdPartyDnsInfo
        {
            DnsServerIp = "192.168.1.6",
            NetworkName = "IoT",
            IsAdGuardHome = true,
            DnsProviderName = "AdGuard Home"
        });

        result.IsPiholeDetected.Should().BeTrue();
        result.IsAdGuardHomeDetected.Should().BeTrue();
    }

    [Fact]
    public void DnsSecurityResult_NeitherDetected_WhenUnknownProvider()
    {
        var result = new DnsSecurityResult();
        result.ThirdPartyDnsServers.Add(new ThirdPartyDnsDetector.ThirdPartyDnsInfo
        {
            DnsServerIp = "192.168.1.5",
            NetworkName = "Corporate",
            IsPihole = false,
            IsAdGuardHome = false,
            DnsProviderName = "Third-Party LAN DNS"
        });

        result.IsPiholeDetected.Should().BeFalse();
        result.IsAdGuardHomeDetected.Should().BeFalse();
    }

    [Fact]
    public void DnsSecurityResult_NeitherDetected_WhenNoThirdPartyServers()
    {
        var result = new DnsSecurityResult();

        result.IsPiholeDetected.Should().BeFalse();
        result.IsAdGuardHomeDetected.Should().BeFalse();
    }

    [Fact]
    public void DnsSecurityResult_PiholeDetected_WhenMixedServersIncludePihole()
    {
        var result = new DnsSecurityResult();
        // First server is unknown
        result.ThirdPartyDnsServers.Add(new ThirdPartyDnsDetector.ThirdPartyDnsInfo
        {
            DnsServerIp = "192.168.1.5",
            NetworkName = "Network1",
            IsPihole = false,
            IsAdGuardHome = false,
            DnsProviderName = "Third-Party LAN DNS"
        });
        // Second server is Pi-hole
        result.ThirdPartyDnsServers.Add(new ThirdPartyDnsDetector.ThirdPartyDnsInfo
        {
            DnsServerIp = "192.168.1.10",
            NetworkName = "Network2",
            IsPihole = true,
            DnsProviderName = "Pi-hole"
        });

        result.IsPiholeDetected.Should().BeTrue();
        result.IsAdGuardHomeDetected.Should().BeFalse();
    }

    [Fact]
    public void DnsSecurityResult_AdGuardHomeDetected_WhenMixedServersIncludeAdGuardHome()
    {
        var result = new DnsSecurityResult();
        // First server is unknown
        result.ThirdPartyDnsServers.Add(new ThirdPartyDnsDetector.ThirdPartyDnsInfo
        {
            DnsServerIp = "192.168.1.5",
            NetworkName = "Network1",
            IsPihole = false,
            IsAdGuardHome = false,
            DnsProviderName = "Third-Party LAN DNS"
        });
        // Second server is AdGuard Home
        result.ThirdPartyDnsServers.Add(new ThirdPartyDnsDetector.ThirdPartyDnsInfo
        {
            DnsServerIp = "192.168.1.10",
            NetworkName = "Network2",
            IsAdGuardHome = true,
            DnsProviderName = "AdGuard Home"
        });

        result.IsPiholeDetected.Should().BeFalse();
        result.IsAdGuardHomeDetected.Should().BeTrue();
    }

    #endregion

    #region WanInterfaceDns Tests

    [Fact]
    public void WanInterfaceDns_HasStaticDns_ReturnsTrue_WhenDnsServersExist()
    {
        var wanInterface = new WanInterfaceDns
        {
            InterfaceName = "wan",
            DnsServers = new List<string> { "8.8.8.8" }
        };

        wanInterface.HasStaticDns.Should().BeTrue();
    }

    [Fact]
    public void WanInterfaceDns_HasStaticDns_ReturnsFalse_WhenDnsServersEmpty()
    {
        var wanInterface = new WanInterfaceDns
        {
            InterfaceName = "wan",
            DnsServers = new List<string>()
        };

        wanInterface.HasStaticDns.Should().BeFalse();
    }

    #endregion

    #region Device DNS Configuration Tests (from switches)

    [Fact]
    public async Task Analyze_WithDevicesHavingStaticDns_ChecksDnsConfiguration()
    {
        var switches = new List<SwitchInfo>
        {
            new SwitchInfo
            {
                Name = "Gateway",
                IsGateway = true,
                Model = "UDM-PRO",
                IpAddress = "192.168.1.1",
                Capabilities = new SwitchCapabilities()
            },
            new SwitchInfo
            {
                Name = "Switch1",
                IsGateway = false,
                Model = "USW-24",
                IpAddress = "192.168.1.10",
                ConfiguredDns1 = "192.168.1.1",
                NetworkConfigType = "static",
                Capabilities = new SwitchCapabilities()
            }
        };

        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "Management",
                VlanId = 1,
                Gateway = "192.168.1.1",
                Purpose = NetworkPurpose.Management
            }
        };

        var result = await _analyzer.AnalyzeAsync(null, null, switches, networks);

        result.TotalDevicesChecked.Should().Be(1);
        result.DevicesWithCorrectDns.Should().Be(1);
        result.DeviceDnsPointsToGateway.Should().BeTrue();
    }

    [Fact]
    public async Task Analyze_WithMisconfiguredDeviceDns_GeneratesIssue()
    {
        var switches = new List<SwitchInfo>
        {
            new SwitchInfo
            {
                Name = "Gateway",
                IsGateway = true,
                Model = "UDM-PRO",
                IpAddress = "192.168.1.1",
                Capabilities = new SwitchCapabilities()
            },
            new SwitchInfo
            {
                Name = "Switch1",
                IsGateway = false,
                Model = "USW-24",
                IpAddress = "192.168.1.10",
                ConfiguredDns1 = "8.8.8.8", // Wrong - should point to gateway
                NetworkConfigType = "static",
                Capabilities = new SwitchCapabilities()
            }
        };

        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "Management",
                VlanId = 1,
                Gateway = "192.168.1.1",
                Purpose = NetworkPurpose.Management
            }
        };

        var result = await _analyzer.AnalyzeAsync(null, null, switches, networks);

        result.DeviceDnsPointsToGateway.Should().BeFalse();
        result.Issues.Should().Contain(i => i.Type == "DNS_DEVICE_MISCONFIGURED");
    }

    [Fact]
    public async Task Analyze_WithDhcpDevices_CountsAsDhcp()
    {
        var switches = new List<SwitchInfo>
        {
            new SwitchInfo
            {
                Name = "Gateway",
                IsGateway = true,
                Model = "UDM-PRO",
                IpAddress = "192.168.1.1",
                Capabilities = new SwitchCapabilities()
            },
            new SwitchInfo
            {
                Name = "Switch1",
                IsGateway = false,
                Model = "USW-24",
                IpAddress = "192.168.1.10",
                NetworkConfigType = "dhcp",
                Capabilities = new SwitchCapabilities()
            }
        };

        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "Management",
                VlanId = 1,
                Gateway = "192.168.1.1",
                Purpose = NetworkPurpose.Management
            }
        };

        var result = await _analyzer.AnalyzeAsync(null, null, switches, networks);

        result.DhcpDeviceCount.Should().Be(1);
    }

    #endregion

    #region Device DNS from Raw Device Data Tests

    [Fact]
    public async Task Analyze_WithRawDeviceData_ChecksAllDevices()
    {
        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""udm"",
                ""name"": ""Gateway"",
                ""ip"": ""192.168.1.1""
            },
            {
                ""type"": ""usw"",
                ""name"": ""Switch1"",
                ""ip"": ""192.168.1.10"",
                ""config_network"": {
                    ""type"": ""static"",
                    ""dns1"": ""192.168.1.1""
                }
            },
            {
                ""type"": ""uap"",
                ""name"": ""AP1"",
                ""ip"": ""192.168.1.20"",
                ""config_network"": {
                    ""type"": ""dhcp""
                }
            }
        ]").RootElement;

        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "Management",
                VlanId = 1,
                Gateway = "192.168.1.1",
                Purpose = NetworkPurpose.Management
            }
        };

        var result = await _analyzer.AnalyzeAsync(null, null, null, networks, deviceData);

        result.TotalDevicesChecked.Should().Be(1); // Switch with static DNS
        result.DhcpDeviceCount.Should().Be(1); // AP with DHCP
        result.DevicesWithCorrectDns.Should().Be(1);
    }

    [Fact]
    public async Task Analyze_WithMisconfiguredApDns_GeneratesIssue()
    {
        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""udm"",
                ""name"": ""Gateway"",
                ""ip"": ""192.168.1.1""
            },
            {
                ""type"": ""uap"",
                ""name"": ""AP1"",
                ""ip"": ""192.168.1.20"",
                ""config_network"": {
                    ""type"": ""static"",
                    ""dns1"": ""8.8.8.8""
                }
            }
        ]").RootElement;

        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "Management",
                VlanId = 1,
                Gateway = "192.168.1.1",
                Purpose = NetworkPurpose.Management
            }
        };

        var result = await _analyzer.AnalyzeAsync(null, null, null, networks, deviceData);

        result.DeviceDnsPointsToGateway.Should().BeFalse();
        result.Issues.Should().Contain(i => i.Type == "DNS_DEVICE_MISCONFIGURED");
    }

    #endregion

    #region Hardening Notes Tests

    [Fact]
    public async Task Analyze_WithDohConfigured_AddsHardeningNote()
    {
        var settings = JsonDocument.Parse(@"[
            {
                ""key"": ""doh"",
                ""state"": ""auto"",
                ""server_names"": [""cloudflare""]
            }
        ]").RootElement;

        var result = await _analyzer.AnalyzeAsync(settings, null);

        result.HardeningNotes.Should().Contain(n => n.Contains("DoH"));
    }

    [Fact]
    public async Task Analyze_WithFullProtection_AddsFullProtectionNote()
    {
        var settings = JsonDocument.Parse(@"[
            {
                ""key"": ""doh"",
                ""state"": ""auto"",
                ""server_names"": [""cloudflare""]
            }
        ]").RootElement;

        var firewall = JsonDocument.Parse(@"[
            { ""name"": ""Block DNS"", ""enabled"": true, ""action"": ""drop"", ""destination"": { ""port"": ""53"" } },
            { ""name"": ""Block DoT"", ""enabled"": true, ""action"": ""drop"", ""destination"": { ""port"": ""853"" } },
            { ""name"": ""Block DoH"", ""enabled"": true, ""action"": ""drop"", ""destination"": { ""port"": ""443"", ""matching_target"": ""WEB"", ""web_domains"": [""dns.google""] } }
        ]").RootElement;

        var result = await _analyzer.AnalyzeAsync(settings, ParseFirewallRules(firewall));

        result.HardeningNotes.Should().Contain(n => n.Contains("fully configured"));
    }

    [Fact]
    public async Task Analyze_FullProtectionWithNetworks_EachProtocolCoverageEvaluatedIndependently()
    {
        // Arrange - DNS53 covers all networks, DoT only covers some
        // Hardening note should NOT say "fully configured" because DoT is partial
        var networks = CreateDhcpNetworks(
            ("net1", "LAN", "192.168.1.0/24"),
            ("net2", "IoT", "192.168.2.0/24"));

        var settings = JsonDocument.Parse(@"[
            {
                ""key"": ""doh"",
                ""state"": ""auto"",
                ""server_names"": [""cloudflare""]
            }
        ]").RootElement;

        var firewall = JsonDocument.Parse(@"[
            {
                ""name"": ""Block DNS"",
                ""enabled"": true,
                ""action"": ""drop"",
                ""source"": { ""matching_target"": ""ANY"" },
                ""destination"": { ""port"": ""53"" }
            },
            {
                ""name"": ""Block DoT (LAN only)"",
                ""enabled"": true,
                ""action"": ""drop"",
                ""source"": {
                    ""matching_target"": ""NETWORK"",
                    ""network_ids"": [""net1""]
                },
                ""destination"": { ""port"": ""853"", ""protocol"": ""tcp"" }
            },
            {
                ""name"": ""Block DoH"",
                ""enabled"": true,
                ""action"": ""drop"",
                ""source"": { ""matching_target"": ""ANY"" },
                ""destination"": { ""port"": ""443"", ""matching_target"": ""WEB"", ""web_domains"": [""dns.google""] }
            }
        ]").RootElement;

        var result = await _analyzer.AnalyzeAsync(settings, ParseFirewallRules(firewall), null, networks);

        // DNS53 covers all networks, DoT only covers LAN (not IoT)
        result.Dns53ProvidesFullCoverage.Should().BeTrue();
        result.DotProvidesFullCoverage.Should().BeFalse();
        // Not "fully configured" because DoT is partial
        result.HardeningNotes.Should().NotContain(n => n.Contains("fully configured"));
    }

    #endregion

    #region Additional Issue Generation Tests

    [Fact]
    public async Task Analyze_WithDohAutoMode_GeneratesAutoModeIssue()
    {
        var settings = JsonDocument.Parse(@"[
            {
                ""key"": ""doh"",
                ""state"": ""auto"",
                ""server_names"": [""cloudflare""]
            }
        ]").RootElement;

        var result = await _analyzer.AnalyzeAsync(settings, null);

        result.Issues.Should().Contain(i => i.Type == "DNS_DOH_AUTO");
    }

    [Fact]
    public async Task Analyze_UsingIspDnsWithoutDoh_GeneratesIspDnsIssue()
    {
        var settings = JsonDocument.Parse(@"[
            {
                ""key"": ""dns"",
                ""mode"": ""auto""
            }
        ]").RootElement;

        var result = await _analyzer.AnalyzeAsync(settings, null);

        result.Issues.Should().Contain(i => i.Type == "DNS_ISP");
    }

    [Fact]
    public async Task Analyze_WithDohButNoDohBlock_GeneratesDohBypassIssue()
    {
        var settings = JsonDocument.Parse(@"[
            {
                ""key"": ""doh"",
                ""state"": ""auto"",
                ""server_names"": [""cloudflare""]
            }
        ]").RootElement;

        var result = await _analyzer.AnalyzeAsync(settings, null);

        result.Issues.Should().Contain(i => i.Type == "DNS_NO_DOH_BLOCK");
    }

    [Fact]
    public async Task Analyze_WithDohButNoDoqBlock_GeneratesDoqBypassIssue()
    {
        var settings = JsonDocument.Parse(@"[
            {
                ""key"": ""doh"",
                ""state"": ""auto"",
                ""server_names"": [""cloudflare""]
            }
        ]").RootElement;

        var result = await _analyzer.AnalyzeAsync(settings, null);

        result.Issues.Should().Contain(i => i.Type == "DNS_NO_DOQ_BLOCK");
    }

    [Fact]
    public async Task Analyze_WithDoqBlockRule_DoesNotGenerateDoqBypassIssue()
    {
        // DoH configured + DoQ block rule (UDP 853) = no DoQ bypass issue
        var settings = JsonDocument.Parse(@"[
            {
                ""key"": ""doh"",
                ""state"": ""auto"",
                ""server_names"": [""cloudflare""]
            }
        ]").RootElement;

        var firewall = JsonDocument.Parse(@"[
            {
                ""name"": ""Block DoQ"",
                ""enabled"": true,
                ""action"": ""drop"",
                ""protocol"": ""udp"",
                ""destination"": { ""port"": ""853"" }
            }
        ]").RootElement;

        var result = await _analyzer.AnalyzeAsync(settings, ParseFirewallRules(firewall));

        result.Issues.Should().NotContain(i => i.Type == "DNS_NO_DOQ_BLOCK");
    }

    #endregion

    #region DeviceName on Issues Tests

    [Fact]
    public async Task Analyze_DnsIssues_HaveGatewayDeviceName()
    {
        // Arrange
        var switches = new List<SwitchInfo>
        {
            new SwitchInfo
            {
                Name = "Dream Machine Pro",
                IsGateway = true,
                Model = "UDM-Pro"
            }
        };

        // Act - analyze with no settings/firewall data to trigger DNS issues
        var result = await _analyzer.AnalyzeAsync(
            settingsData: null,
            firewallRules: null,
            switches: switches,
            networks: null);

        // Assert - all issues should have DeviceName set to gateway
        result.Issues.Should().NotBeEmpty("DNS issues should be generated when no DoH/firewall config");

        foreach (var issue in result.Issues)
        {
            issue.DeviceName.Should().Be("Dream Machine Pro",
                $"Issue type '{issue.Type}' should have DeviceName set to gateway");
        }
    }

    [Fact]
    public async Task Analyze_NoGateway_IssuesHaveNullDeviceName()
    {
        // Arrange - no switches provided
        var result = await _analyzer.AnalyzeAsync(
            settingsData: null,
            firewallRules: null,
            switches: null,
            networks: null);

        // Assert - issues should still be generated, but DeviceName will be null
        result.Issues.Should().NotBeEmpty();

        // When no gateway is available, DeviceName should be null (not crash)
        foreach (var issue in result.Issues)
        {
            issue.DeviceName.Should().BeNull(
                $"Issue type '{issue.Type}' should have null DeviceName when no gateway available");
        }
    }

    [Fact]
    public async Task Analyze_MultipleDevices_UsesGatewayName()
    {
        // Arrange - multiple devices, only one is gateway
        var switches = new List<SwitchInfo>
        {
            new SwitchInfo
            {
                Name = "Office Switch",
                IsGateway = false,
                Model = "USW-24"
            },
            new SwitchInfo
            {
                Name = "Cloud Gateway Ultra",
                IsGateway = true,
                Model = "UCG-Ultra"
            },
            new SwitchInfo
            {
                Name = "Garage Switch",
                IsGateway = false,
                Model = "USW-Lite-8"
            }
        };

        // Act
        var result = await _analyzer.AnalyzeAsync(
            settingsData: null,
            firewallRules: null,
            switches: switches,
            networks: null);

        // Assert - should use the gateway's name, not other switches
        result.Issues.Should().NotBeEmpty();

        foreach (var issue in result.Issues)
        {
            issue.DeviceName.Should().Be("Cloud Gateway Ultra");
        }
    }

    #endregion

    #region Issue Generation Tests

    [Fact]
    public async Task Analyze_NoDoHConfigured_GeneratesCriticalIssue()
    {
        // Arrange
        var switches = new List<SwitchInfo>
        {
            new SwitchInfo { Name = "Gateway", IsGateway = true }
        };

        // Act
        var result = await _analyzer.AnalyzeAsync(
            settingsData: null,
            firewallRules: null,
            switches: switches,
            networks: null);

        // Assert - DoH not configured is Critical severity
        result.Issues.Should().Contain(i =>
            i.Type == "DNS_NO_DOH" &&
            i.Severity == AuditSeverity.Critical);
    }

    [Fact]
    public async Task Analyze_NoPort53Block_GeneratesCriticalIssue()
    {
        // Arrange
        var switches = new List<SwitchInfo>
        {
            new SwitchInfo { Name = "Gateway", IsGateway = true }
        };

        // Act
        var result = await _analyzer.AnalyzeAsync(
            settingsData: null,
            firewallRules: null,
            switches: switches,
            networks: null);

        // Assert
        result.Issues.Should().Contain(i =>
            i.Type == "DNS_NO_53_BLOCK" &&
            i.Severity == AuditSeverity.Critical &&
            i.DeviceName == "Gateway");
    }

    #endregion

    #region Third-Party DNS Detection Tests

    [Fact]
    public async Task Analyze_WithThirdPartyLanDns_SetsHasThirdPartyDns()
    {
        // Arrange
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "Corporate",
                VlanId = 10,
                DhcpEnabled = true,
                Gateway = "192.168.1.1",
                DnsServers = new List<string> { "192.168.1.5" }
            }
        };
        var switches = new List<SwitchInfo>
        {
            new SwitchInfo { Name = "Gateway", IsGateway = true }
        };

        // Act
        var result = await _analyzer.AnalyzeAsync(
            settingsData: null,
            firewallRules: null,
            switches: switches,
            networks: networks);

        // Assert
        result.HasThirdPartyDns.Should().BeTrue();
        result.ThirdPartyDnsServers.Should().NotBeEmpty();
        result.ThirdPartyDnsServers.Should().Contain(t => t.DnsServerIp == "192.168.1.5");
    }

    [Fact]
    public async Task Analyze_WithThirdPartyDns_GeneratesIssue()
    {
        // Arrange - Unknown third-party DNS (not Pi-hole or AdGuard)
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "Corporate",
                VlanId = 10,
                DhcpEnabled = true,
                Gateway = "192.168.1.1",
                DnsServers = new List<string> { "192.168.1.5" }
            }
        };
        var switches = new List<SwitchInfo>
        {
            new SwitchInfo { Name = "Gateway", IsGateway = true }
        };

        // Act
        var result = await _analyzer.AnalyzeAsync(
            settingsData: null,
            firewallRules: null,
            switches: switches,
            networks: networks);

        // Assert - Unknown providers get Recommended severity (minor penalty)
        result.Issues.Should().Contain(i =>
            i.Type == IssueTypes.DnsThirdPartyDetected &&
            i.Severity == AuditSeverity.Recommended);
    }

    [Fact]
    public async Task Analyze_WithThirdPartyDns_DoesNotGenerateDnsNoDohIssue()
    {
        // Arrange
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "Corporate",
                VlanId = 10,
                DhcpEnabled = true,
                Gateway = "192.168.1.1",
                DnsServers = new List<string> { "192.168.1.5" }
            }
        };
        var switches = new List<SwitchInfo>
        {
            new SwitchInfo { Name = "Gateway", IsGateway = true }
        };

        // Act
        var result = await _analyzer.AnalyzeAsync(
            settingsData: null,
            firewallRules: null,
            switches: switches,
            networks: networks);

        // Assert - Should NOT have DNS_NO_DOH issue when third-party DNS is detected
        result.Issues.Should().NotContain(i => i.Type == IssueTypes.DnsNoDoh);
    }

    [Fact]
    public async Task Analyze_WithoutDoHOrThirdParty_GeneratesUnknownConfigIssue()
    {
        // Arrange
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "Corporate",
                VlanId = 10,
                DhcpEnabled = true,
                Gateway = "192.168.1.1",
                DnsServers = new List<string> { "192.168.1.1" } // DNS matches gateway
            }
        };
        var switches = new List<SwitchInfo>
        {
            new SwitchInfo { Name = "Gateway", IsGateway = true }
        };

        // Act
        var result = await _analyzer.AnalyzeAsync(
            settingsData: null,
            firewallRules: null,
            switches: switches,
            networks: networks);

        // Assert
        result.HasThirdPartyDns.Should().BeFalse();
        result.Issues.Should().Contain(i => i.Type == IssueTypes.DnsUnknownConfig);
        result.Issues.Should().Contain(i => i.Type == IssueTypes.DnsNoDoh);
    }

    [Fact]
    public async Task Analyze_WithPublicDns_DoesNotDetectAsThirdParty()
    {
        // Arrange
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "Corporate",
                VlanId = 10,
                DhcpEnabled = true,
                Gateway = "192.168.1.1",
                DnsServers = new List<string> { "8.8.8.8", "1.1.1.1" }
            }
        };
        var switches = new List<SwitchInfo>
        {
            new SwitchInfo { Name = "Gateway", IsGateway = true }
        };

        // Act
        var result = await _analyzer.AnalyzeAsync(
            settingsData: null,
            firewallRules: null,
            switches: switches,
            networks: networks);

        // Assert
        result.HasThirdPartyDns.Should().BeFalse();
        result.ThirdPartyDnsServers.Should().BeEmpty();
    }

    [Fact]
    public async Task Analyze_ThirdPartyDnsWithMultipleNetworks_SetsProviderName()
    {
        // Arrange
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "Corporate",
                VlanId = 10,
                DhcpEnabled = true,
                Gateway = "192.168.1.1",
                DnsServers = new List<string> { "192.168.1.5" }
            },
            new NetworkInfo
            {
                Id = "net2",
                Name = "IoT",
                VlanId = 20,
                DhcpEnabled = true,
                Gateway = "192.168.2.1",
                DnsServers = new List<string> { "192.168.1.5" }
            }
        };
        var switches = new List<SwitchInfo>
        {
            new SwitchInfo { Name = "Gateway", IsGateway = true }
        };

        // Act
        var result = await _analyzer.AnalyzeAsync(
            settingsData: null,
            firewallRules: null,
            switches: switches,
            networks: networks);

        // Assert
        result.HasThirdPartyDns.Should().BeTrue();
        result.ThirdPartyDnsProviderName.Should().Be("Third-Party LAN DNS");
        result.ThirdPartyDnsServers.Should().HaveCount(2);
    }

    [Fact]
    public async Task Analyze_UnknownThirdPartyDnsIssue_HasMinorScoreImpact()
    {
        // Arrange - Unknown third-party DNS (not Pi-hole or AdGuard)
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "Corporate",
                VlanId = 10,
                DhcpEnabled = true,
                Gateway = "192.168.1.1",
                DnsServers = new List<string> { "192.168.1.5" }
            }
        };
        var switches = new List<SwitchInfo>
        {
            new SwitchInfo { Name = "Gateway", IsGateway = true }
        };

        // Act
        var result = await _analyzer.AnalyzeAsync(
            settingsData: null,
            firewallRules: null,
            switches: switches,
            networks: networks);

        // Assert - Unknown third-party DNS has minor score impact (not zero)
        var thirdPartyIssue = result.Issues.FirstOrDefault(i => i.Type == IssueTypes.DnsThirdPartyDetected);
        thirdPartyIssue.Should().NotBeNull();
        thirdPartyIssue!.ScoreImpact.Should().Be(3);
    }

    [Fact]
    public async Task Analyze_UnknownThirdPartyDns_NoHardeningNote()
    {
        // Arrange - Unknown third-party DNS (not Pi-hole or AdGuard) should NOT get hardening note
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "Corporate",
                VlanId = 10,
                DhcpEnabled = true,
                Gateway = "192.168.1.1",
                DnsServers = new List<string> { "192.168.1.5" }
            }
        };
        var switches = new List<SwitchInfo>
        {
            new SwitchInfo { Name = "Gateway", IsGateway = true }
        };

        // Act
        var result = await _analyzer.AnalyzeAsync(
            settingsData: null,
            firewallRules: null,
            switches: switches,
            networks: networks);

        // Assert - Unknown providers don't get hardening notes (only known like Pi-hole)
        result.HardeningNotes.Should().NotContain(n => n.Contains("Third-Party LAN DNS"));
    }

    #endregion

    #region WAN DNS Validation Tests

    [Fact]
    public async Task Analyze_WithDohAndMatchingWanDns_SetsWanDnsMatchesDoH()
    {
        // Arrange - DoH configured with Cloudflare, WAN DNS also Cloudflare
        var settings = JsonDocument.Parse(@"[
            {
                ""key"": ""doh"",
                ""state"": ""auto"",
                ""server_names"": [""cloudflare""]
            }
        ]").RootElement;

        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""udm"",
                ""port_table"": [
                    {
                        ""network_name"": ""wan"",
                        ""name"": ""WAN"",
                        ""up"": true,
                        ""dns"": [""1.1.1.1"", ""1.0.0.1""]
                    }
                ]
            }
        ]").RootElement;

        // Act
        var result = await _analyzer.AnalyzeAsync(settings, null, null, null, deviceData);

        // Assert
        result.DohConfigured.Should().BeTrue();
        result.WanDnsServers.Should().Contain("1.1.1.1");
        result.ExpectedDnsProvider.Should().Be("Cloudflare");
    }

    [Fact]
    public async Task Analyze_WithDohAndMismatchedWanDns_GeneratesIssue()
    {
        // Arrange - DoH configured with Cloudflare, but WAN DNS is Google
        var settings = JsonDocument.Parse(@"[
            {
                ""key"": ""doh"",
                ""state"": ""auto"",
                ""server_names"": [""cloudflare""]
            }
        ]").RootElement;

        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""udm"",
                ""port_table"": [
                    {
                        ""network_name"": ""wan"",
                        ""name"": ""WAN"",
                        ""up"": true,
                        ""dns"": [""8.8.8.8"", ""8.8.4.4""]
                    }
                ]
            }
        ]").RootElement;

        // Act
        var result = await _analyzer.AnalyzeAsync(settings, null, null, null, deviceData);

        // Assert
        result.DohConfigured.Should().BeTrue();
        result.WanDnsMatchesDoH.Should().BeFalse();
        result.Issues.Should().Contain(i => i.Type == "DNS_WAN_MISMATCH");
    }

    [Fact]
    public async Task Analyze_WithDohButNoWanDns_SkipsValidation()
    {
        // Arrange - DoH configured but no WAN DNS info
        var settings = JsonDocument.Parse(@"[
            {
                ""key"": ""doh"",
                ""state"": ""auto"",
                ""server_names"": [""cloudflare""]
            }
        ]").RootElement;

        // Act
        var result = await _analyzer.AnalyzeAsync(settings, null, null, null, null);

        // Assert - Should not crash, validation skipped
        result.DohConfigured.Should().BeTrue();
        result.WanDnsServers.Should().BeEmpty();
    }

    [Fact]
    public async Task Analyze_WithGoogleDohAndGoogleWanDns_Matches()
    {
        // Arrange - Google DoH with Google WAN DNS
        var settings = JsonDocument.Parse(@"[
            {
                ""key"": ""doh"",
                ""state"": ""auto"",
                ""server_names"": [""google""]
            }
        ]").RootElement;

        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""udm"",
                ""port_table"": [
                    {
                        ""network_name"": ""wan"",
                        ""name"": ""WAN"",
                        ""up"": true,
                        ""dns"": [""8.8.8.8"", ""8.8.4.4""]
                    }
                ]
            }
        ]").RootElement;

        // Act
        var result = await _analyzer.AnalyzeAsync(settings, null, null, null, deviceData);

        // Assert
        result.ExpectedDnsProvider.Should().Be("Google");
        result.WanDnsProvider.Should().Be("Google");
    }

    [Fact]
    public async Task Analyze_WithQuad9DohAndQuad9WanDns_Matches()
    {
        // Arrange - Quad9 DoH with Quad9 WAN DNS
        var settings = JsonDocument.Parse(@"[
            {
                ""key"": ""doh"",
                ""state"": ""auto"",
                ""server_names"": [""quad9""]
            }
        ]").RootElement;

        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""udm"",
                ""port_table"": [
                    {
                        ""network_name"": ""wan"",
                        ""name"": ""WAN"",
                        ""up"": true,
                        ""dns"": [""9.9.9.9"", ""149.112.112.112""]
                    }
                ]
            }
        ]").RootElement;

        // Act
        var result = await _analyzer.AnalyzeAsync(settings, null, null, null, deviceData);

        // Assert
        result.ExpectedDnsProvider.Should().Be("Quad9");
    }

    [Fact]
    public async Task Analyze_MultipleWanInterfaces_ChecksEach()
    {
        // Arrange - DoH with dual WAN, one matching, one not
        var settings = JsonDocument.Parse(@"[
            {
                ""key"": ""doh"",
                ""state"": ""auto"",
                ""server_names"": [""cloudflare""]
            }
        ]").RootElement;

        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""udm"",
                ""port_table"": [
                    {
                        ""network_name"": ""wan"",
                        ""name"": ""WAN1"",
                        ""up"": true,
                        ""dns"": [""1.1.1.1""]
                    },
                    {
                        ""network_name"": ""wan2"",
                        ""name"": ""WAN2"",
                        ""up"": true,
                        ""dns"": [""8.8.8.8""]
                    }
                ]
            }
        ]").RootElement;

        // Act
        var result = await _analyzer.AnalyzeAsync(settings, null, null, null, deviceData);

        // Assert - WAN2 has mismatched DNS
        result.WanInterfaces.Should().HaveCount(2);
        result.WanDnsMatchesDoH.Should().BeFalse();
    }

    [Fact]
    public async Task Analyze_WanInterfaceWithoutDns_CountsAsNoDns()
    {
        // Arrange - WAN interface with no static DNS
        var settings = JsonDocument.Parse(@"[
            {
                ""key"": ""doh"",
                ""state"": ""auto"",
                ""server_names"": [""cloudflare""]
            }
        ]").RootElement;

        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""udm"",
                ""port_table"": [
                    {
                        ""network_name"": ""wan"",
                        ""name"": ""WAN"",
                        ""up"": true
                    }
                ]
            }
        ]").RootElement;

        // Act
        var result = await _analyzer.AnalyzeAsync(settings, null, null, null, deviceData);

        // Assert
        result.WanInterfaces.Should().HaveCount(1);
        result.WanInterfaces[0].HasStaticDns.Should().BeFalse();
    }

    [Fact]
    public async Task Analyze_WithOpenDnsDohAndOpenDnsWanDns_Matches()
    {
        // Arrange - OpenDNS DoH with OpenDNS WAN DNS
        var settings = JsonDocument.Parse(@"[
            {
                ""key"": ""doh"",
                ""state"": ""auto"",
                ""server_names"": [""opendns""]
            }
        ]").RootElement;

        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""udm"",
                ""port_table"": [
                    {
                        ""network_name"": ""wan"",
                        ""name"": ""WAN"",
                        ""up"": true,
                        ""dns"": [""208.67.222.222"", ""208.67.220.220""]
                    }
                ]
            }
        ]").RootElement;

        // Act
        var result = await _analyzer.AnalyzeAsync(settings, null, null, null, deviceData);

        // Assert
        result.ExpectedDnsProvider.Should().Be("OpenDNS");
    }

    [Fact]
    public async Task Analyze_WithAdGuardDohAndAdGuardWanDns_Matches()
    {
        // Arrange - AdGuard DoH with AdGuard WAN DNS
        var settings = JsonDocument.Parse(@"[
            {
                ""key"": ""doh"",
                ""state"": ""auto"",
                ""server_names"": [""adguard""]
            }
        ]").RootElement;

        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""udm"",
                ""port_table"": [
                    {
                        ""network_name"": ""wan"",
                        ""name"": ""WAN"",
                        ""up"": true,
                        ""dns"": [""94.140.14.14"", ""94.140.15.15""]
                    }
                ]
            }
        ]").RootElement;

        // Act
        var result = await _analyzer.AnalyzeAsync(settings, null, null, null, deviceData);

        // Assert
        result.ExpectedDnsProvider.Should().Be("AdGuard");
    }

    [Fact]
    public async Task Analyze_MatchingWanDns_AddsHardeningNote()
    {
        // Arrange
        var settings = JsonDocument.Parse(@"[
            {
                ""key"": ""doh"",
                ""state"": ""auto"",
                ""server_names"": [""cloudflare""]
            }
        ]").RootElement;

        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""udm"",
                ""port_table"": [
                    {
                        ""network_name"": ""wan"",
                        ""name"": ""WAN"",
                        ""up"": true,
                        ""dns"": [""1.1.1.1"", ""1.0.0.1""]
                    }
                ]
            }
        ]").RootElement;

        // Act
        var result = await _analyzer.AnalyzeAsync(settings, null, null, null, deviceData);

        // Assert
        if (result.WanDnsMatchesDoH)
        {
            result.HardeningNotes.Should().Contain(n => n.Contains("WAN DNS correctly configured"));
        }
    }

    #endregion

    #region DNS Order Issues Tests

    [Fact]
    public async Task Analyze_WithWrongDnsOrder_GeneratesOrderIssue()
    {
        // Arrange - Cloudflare DoH with Google DNS first (wrong order)
        var settings = JsonDocument.Parse(@"[
            {
                ""key"": ""doh"",
                ""state"": ""auto"",
                ""server_names"": [""cloudflare""]
            }
        ]").RootElement;

        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""udm"",
                ""name"": ""Gateway"",
                ""port_table"": [
                    {
                        ""network_name"": ""wan"",
                        ""name"": ""WAN"",
                        ""up"": true,
                        ""dns"": [""8.8.8.8"", ""1.1.1.1""]
                    }
                ]
            }
        ]").RootElement;

        // Act
        var result = await _analyzer.AnalyzeAsync(settings, null, null, null, deviceData);

        // Assert - DNS order is wrong (Google before Cloudflare when DoH is Cloudflare)
        result.WanDnsMatchesDoH.Should().BeFalse();
    }

    [Fact]
    public async Task Analyze_WithCorrectDnsOrder_NoOrderIssue()
    {
        // Arrange - Cloudflare DoH with Cloudflare DNS first
        var settings = JsonDocument.Parse(@"[
            {
                ""key"": ""doh"",
                ""state"": ""auto"",
                ""server_names"": [""cloudflare""]
            }
        ]").RootElement;

        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""udm"",
                ""name"": ""Gateway"",
                ""port_table"": [
                    {
                        ""network_name"": ""wan"",
                        ""name"": ""WAN"",
                        ""up"": true,
                        ""dns"": [""1.1.1.1"", ""1.0.0.1""]
                    }
                ]
            }
        ]").RootElement;

        // Act
        var result = await _analyzer.AnalyzeAsync(settings, null, null, null, deviceData);

        // Assert - Should not have order issue
        result.Issues.Should().NotContain(i => i.Type == "DNS_WAN_ORDER");
    }

    #endregion

    #region No Static DNS Issues Tests

    [Fact]
    public async Task Analyze_WithNoStaticDns_GeneratesNoStaticDnsIssue()
    {
        // Arrange - DoH configured but WAN has no DNS
        var settings = JsonDocument.Parse(@"[
            {
                ""key"": ""doh"",
                ""state"": ""auto"",
                ""server_names"": [""cloudflare""]
            }
        ]").RootElement;

        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""udm"",
                ""name"": ""Gateway"",
                ""port_table"": [
                    {
                        ""network_name"": ""wan"",
                        ""name"": ""WAN"",
                        ""up"": true
                    }
                ]
            }
        ]").RootElement;

        // Act
        var result = await _analyzer.AnalyzeAsync(settings, null, null, null, deviceData);

        // Assert
        result.WanInterfaces.Should().HaveCount(1);
        result.WanInterfaces[0].HasStaticDns.Should().BeFalse();
    }

    [Fact]
    public async Task Analyze_DualWan_OneWithoutDns_DetectsMissing()
    {
        // Arrange - Dual WAN, one has DNS, one doesn't
        var settings = JsonDocument.Parse(@"[
            {
                ""key"": ""doh"",
                ""state"": ""auto"",
                ""server_names"": [""cloudflare""]
            }
        ]").RootElement;

        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""udm"",
                ""name"": ""Gateway"",
                ""port_table"": [
                    {
                        ""network_name"": ""wan"",
                        ""name"": ""WAN1"",
                        ""up"": true,
                        ""dns"": [""1.1.1.1""]
                    },
                    {
                        ""network_name"": ""wan2"",
                        ""name"": ""WAN2"",
                        ""up"": true
                    }
                ]
            }
        ]").RootElement;

        // Act
        var result = await _analyzer.AnalyzeAsync(settings, null, null, null, deviceData);

        // Assert
        result.WanInterfaces.Should().HaveCount(2);
        result.WanInterfaces[0].HasStaticDns.Should().BeTrue();
        result.WanInterfaces[1].HasStaticDns.Should().BeFalse();
    }

    #endregion

    #region NextDNS Ordering Tests

    [Fact]
    public async Task Analyze_WithNextDnsStamp_IdentifiesProvider()
    {
        // Arrange - NextDNS custom SDNS stamp
        var sdnsStamp = "sdns://AgcAAAAAAAAAAAAOZG5zLm5leHRkbnMuaW8HL2FiY2RlZg";
        var settings = JsonDocument.Parse($@"[
            {{
                ""key"": ""doh"",
                ""state"": ""custom"",
                ""custom_servers"": [
                    {{ ""server_name"": ""NextDNS"", ""sdns_stamp"": ""{sdnsStamp}"", ""enabled"": true }}
                ]
            }}
        ]").RootElement;

        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""udm"",
                ""name"": ""Gateway"",
                ""port_table"": [
                    {
                        ""network_name"": ""wan"",
                        ""name"": ""WAN"",
                        ""up"": true,
                        ""dns"": [""45.90.28.0"", ""45.90.30.0""]
                    }
                ]
            }
        ]").RootElement;

        // Act
        var result = await _analyzer.AnalyzeAsync(settings, null, null, null, deviceData);

        // Assert
        result.DohConfigured.Should().BeTrue();
        result.ConfiguredServers.Should().NotBeEmpty();
    }

    #endregion

    #region Provider Identification Tests

    [Fact]
    public async Task Analyze_WithCloudflareFamily_IdentifiesProvider()
    {
        // Arrange - Cloudflare for Families
        var settings = JsonDocument.Parse(@"[
            {
                ""key"": ""doh"",
                ""state"": ""auto"",
                ""server_names"": [""cloudflare-family""]
            }
        ]").RootElement;

        // Act
        var result = await _analyzer.AnalyzeAsync(settings, null, null, null, null);

        // Assert
        result.DohConfigured.Should().BeTrue();
        result.ConfiguredServers.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Analyze_WithMultipleDoHServers_CountsAll()
    {
        // Arrange - Multiple DoH servers enabled
        var settings = JsonDocument.Parse(@"[
            {
                ""key"": ""doh"",
                ""state"": ""auto"",
                ""server_names"": [""cloudflare"", ""google"", ""quad9""]
            }
        ]").RootElement;

        // Act
        var result = await _analyzer.AnalyzeAsync(settings, null, null, null, null);

        // Assert
        result.DohConfigured.Should().BeTrue();
        result.ConfiguredServers.Should().HaveCount(3);
    }

    #endregion

    #region DNS Consistency Check Tests

    [Fact]
    public async Task Analyze_ThirdPartyDnsOnSomeNetworksNotAll_GeneratesRecommendedIssue()
    {
        // Arrange - Third-party DNS on one non-Corporate network but not all DHCP networks
        // The network WITH third-party DNS must be non-Corporate to trigger consistency check
        // (If only Corporate networks have third-party DNS, it's considered specialized setup)
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "Home",
                VlanId = 10,
                Purpose = NetworkPurpose.Home, // Non-Corporate - will trigger consistency check
                DhcpEnabled = true,
                Gateway = "192.168.1.1",
                DnsServers = new List<string> { "192.168.1.5" } // Third-party DNS
            },
            new NetworkInfo
            {
                Id = "net2",
                Name = "IoT",
                VlanId = 20,
                Purpose = NetworkPurpose.IoT, // Non-Corporate - should be flagged for not using third-party DNS
                DhcpEnabled = true,
                Gateway = "192.168.2.1",
                DnsServers = new List<string> { "192.168.2.1" } // Gateway DNS (no third-party)
            }
        };
        var switches = new List<SwitchInfo>
        {
            new SwitchInfo { Name = "Gateway", IsGateway = true }
        };

        // Act
        var result = await _analyzer.AnalyzeAsync(
            settingsData: null,
            firewallRules: null,
            switches: switches,
            networks: networks);

        // Assert - Inconsistent DNS config is Recommended (may be intentional)
        result.HasThirdPartyDns.Should().BeTrue();
        result.Issues.Should().Contain(i =>
            i.Type == IssueTypes.DnsInconsistentConfig &&
            i.Severity == AuditSeverity.Recommended);
    }

    [Fact]
    public async Task Analyze_ThirdPartyDnsOnAllDhcpNetworks_NoCriticalIssue()
    {
        // Arrange - Third-party DNS on ALL DHCP networks
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "Corporate",
                VlanId = 10,
                DhcpEnabled = true,
                Gateway = "192.168.1.1",
                DnsServers = new List<string> { "192.168.1.5" }
            },
            new NetworkInfo
            {
                Id = "net2",
                Name = "IoT",
                VlanId = 20,
                DhcpEnabled = true,
                Gateway = "192.168.2.1",
                DnsServers = new List<string> { "192.168.1.5" }
            }
        };
        var switches = new List<SwitchInfo>
        {
            new SwitchInfo { Name = "Gateway", IsGateway = true }
        };

        // Act
        var result = await _analyzer.AnalyzeAsync(
            settingsData: null,
            firewallRules: null,
            switches: switches,
            networks: networks);

        // Assert
        result.HasThirdPartyDns.Should().BeTrue();
        result.Issues.Should().NotContain(i => i.Type == IssueTypes.DnsInconsistentConfig);
    }

    [Fact]
    public async Task Analyze_ThirdPartyDnsInconsistent_IssueHasModerateScoreImpact()
    {
        // Arrange
        // The network WITH third-party DNS must be non-Corporate to trigger consistency check
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "Home",
                VlanId = 1,
                Purpose = NetworkPurpose.Home, // Non-Corporate - will trigger consistency check
                DhcpEnabled = true,
                Gateway = "192.168.1.1",
                DnsServers = new List<string> { "192.168.1.5" }
            },
            new NetworkInfo
            {
                Id = "net2",
                Name = "IoT",
                VlanId = 50,
                Purpose = NetworkPurpose.IoT, // Non-Corporate - should be flagged for not using third-party DNS
                DhcpEnabled = true,
                Gateway = "192.168.50.1",
                DnsServers = new List<string> { "192.168.50.1" } // Missing third-party DNS
            }
        };
        var switches = new List<SwitchInfo>
        {
            new SwitchInfo { Name = "Gateway", IsGateway = true }
        };

        // Act
        var result = await _analyzer.AnalyzeAsync(
            settingsData: null,
            firewallRules: null,
            switches: switches,
            networks: networks);

        // Assert - Moderate score impact (5) since inconsistency may be intentional
        // Note: Guest networks are now exempt and get informational issue instead
        var inconsistentIssue = result.Issues.FirstOrDefault(i => i.Type == IssueTypes.DnsInconsistentConfig);
        inconsistentIssue.Should().NotBeNull();
        inconsistentIssue!.ScoreImpact.Should().Be(5);
    }

    [Fact]
    public async Task Analyze_NonDhcpNetworkWithoutThirdPartyDns_NotFlagged()
    {
        // Arrange - Third-party DNS on DHCP network, non-DHCP network without it
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "Corporate",
                VlanId = 10,
                DhcpEnabled = true,
                Gateway = "192.168.1.1",
                DnsServers = new List<string> { "192.168.1.5" }
            },
            new NetworkInfo
            {
                Id = "net2",
                Name = "StaticNetwork",
                VlanId = 99,
                DhcpEnabled = false, // No DHCP - should not be checked
                Gateway = "192.168.99.1",
                DnsServers = new List<string> { "192.168.99.1" }
            }
        };
        var switches = new List<SwitchInfo>
        {
            new SwitchInfo { Name = "Gateway", IsGateway = true }
        };

        // Act
        var result = await _analyzer.AnalyzeAsync(
            settingsData: null,
            firewallRules: null,
            switches: switches,
            networks: networks);

        // Assert - Non-DHCP networks should not trigger consistency issue
        result.Issues.Should().NotContain(i => i.Type == IssueTypes.DnsInconsistentConfig);
    }

    [Fact]
    public async Task Analyze_CorporateNetworkWithoutThirdPartyDns_NotFlagged()
    {
        // Arrange - Third-party DNS on IoT network, Corporate network uses different DNS
        // Corporate networks are exempt from DNS consistency checks as they may use internal DNS
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "IoT",
                VlanId = 10,
                Purpose = NetworkPurpose.IoT,
                DhcpEnabled = true,
                Gateway = "192.168.10.1",
                DnsServers = new List<string> { "192.168.10.5" } // Pi-hole
            },
            new NetworkInfo
            {
                Id = "net2",
                Name = "Corporate",
                VlanId = 20,
                Purpose = NetworkPurpose.Corporate,
                DhcpEnabled = true,
                Gateway = "192.168.20.1",
                DnsServers = new List<string> { "192.168.20.1" } // Uses gateway DNS (internal corporate DNS)
            }
        };
        var switches = new List<SwitchInfo>
        {
            new SwitchInfo { Name = "Gateway", IsGateway = true }
        };

        // Act
        var result = await _analyzer.AnalyzeAsync(
            settingsData: null,
            firewallRules: null,
            switches: switches,
            networks: networks);

        // Assert - Corporate network should be exempt from DNS consistency check
        result.HasThirdPartyDns.Should().BeTrue();
        result.Issues.Should().NotContain(i => i.Type == IssueTypes.DnsInconsistentConfig);
    }

    [Fact]
    public async Task Analyze_NonCorporateNetworkWithoutThirdPartyDns_IsFlagged()
    {
        // Arrange - Third-party DNS on one network, Home network uses different DNS
        // Non-corporate networks should still be flagged for inconsistent DNS
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "IoT",
                VlanId = 10,
                Purpose = NetworkPurpose.IoT,
                DhcpEnabled = true,
                Gateway = "192.168.10.1",
                DnsServers = new List<string> { "192.168.10.5" } // Pi-hole
            },
            new NetworkInfo
            {
                Id = "net2",
                Name = "Home",
                VlanId = 20,
                Purpose = NetworkPurpose.Home,
                DhcpEnabled = true,
                Gateway = "192.168.20.1",
                DnsServers = new List<string> { "192.168.20.1" } // Not using Pi-hole
            }
        };
        var switches = new List<SwitchInfo>
        {
            new SwitchInfo { Name = "Gateway", IsGateway = true }
        };

        // Act
        var result = await _analyzer.AnalyzeAsync(
            settingsData: null,
            firewallRules: null,
            switches: switches,
            networks: networks);

        // Assert - Home network should be flagged for not using Pi-hole
        result.HasThirdPartyDns.Should().BeTrue();
        var inconsistentIssue = result.Issues.FirstOrDefault(i => i.Type == IssueTypes.DnsInconsistentConfig);
        inconsistentIssue.Should().NotBeNull();
        inconsistentIssue!.Message.Should().Contain("Home");
    }

    [Fact]
    public async Task Analyze_ThirdPartyDnsOnlyCorporateNetworks_NoInconsistentIssue()
    {
        // Arrange - Third-party DNS ONLY on Corporate networks
        // This is considered a specialized setup (internal corporate DNS), not network-wide DNS filtering
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "Corporate",
                VlanId = 10,
                Purpose = NetworkPurpose.Corporate, // Corporate with third-party DNS
                DhcpEnabled = true,
                Gateway = "192.168.10.1",
                DnsServers = new List<string> { "192.168.10.5" } // Internal corporate DNS
            },
            new NetworkInfo
            {
                Id = "net2",
                Name = "Home",
                VlanId = 20,
                Purpose = NetworkPurpose.Home,
                DhcpEnabled = true,
                Gateway = "192.168.20.1",
                DnsServers = new List<string> { "192.168.20.1" } // Uses gateway DNS
            },
            new NetworkInfo
            {
                Id = "net3",
                Name = "IoT",
                VlanId = 30,
                Purpose = NetworkPurpose.IoT,
                DhcpEnabled = true,
                Gateway = "192.168.30.1",
                DnsServers = new List<string> { "192.168.30.1" } // Uses gateway DNS
            }
        };
        var switches = new List<SwitchInfo>
        {
            new SwitchInfo { Name = "Gateway", IsGateway = true }
        };

        // Act
        var result = await _analyzer.AnalyzeAsync(
            settingsData: null,
            firewallRules: null,
            switches: switches,
            networks: networks);

        // Assert - No consistency issue because third-party DNS is only on Corporate
        result.HasThirdPartyDns.Should().BeTrue();
        result.Issues.Should().NotContain(i => i.Type == IssueTypes.DnsInconsistentConfig);
    }

    #endregion

    #region Unknown vs Known Provider Rating Tests

    [Fact]
    public async Task Analyze_UnknownThirdPartyDns_HasScoreImpact()
    {
        // Arrange - Unknown third-party DNS (not Pi-hole or AdGuard)
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "Corporate",
                VlanId = 10,
                DhcpEnabled = true,
                Gateway = "192.168.1.1",
                DnsServers = new List<string> { "192.168.1.5" }
            }
        };
        var switches = new List<SwitchInfo>
        {
            new SwitchInfo { Name = "Gateway", IsGateway = true }
        };

        // Act
        var result = await _analyzer.AnalyzeAsync(
            settingsData: null,
            firewallRules: null,
            switches: switches,
            networks: networks);

        // Assert - Unknown provider should have score impact
        var thirdPartyIssue = result.Issues.FirstOrDefault(i => i.Type == IssueTypes.DnsThirdPartyDetected);
        thirdPartyIssue.Should().NotBeNull();
        thirdPartyIssue!.ScoreImpact.Should().BeGreaterThan(0);
        thirdPartyIssue.Severity.Should().Be(AuditSeverity.Recommended);
    }

    [Fact]
    public async Task Analyze_UnknownThirdPartyDns_MetadataIncludesIsKnownProvider()
    {
        // Arrange
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "Corporate",
                VlanId = 10,
                DhcpEnabled = true,
                Gateway = "192.168.1.1",
                DnsServers = new List<string> { "192.168.1.5" }
            }
        };
        var switches = new List<SwitchInfo>
        {
            new SwitchInfo { Name = "Gateway", IsGateway = true }
        };

        // Act
        var result = await _analyzer.AnalyzeAsync(
            settingsData: null,
            firewallRules: null,
            switches: switches,
            networks: networks);

        // Assert
        var thirdPartyIssue = result.Issues.FirstOrDefault(i => i.Type == IssueTypes.DnsThirdPartyDetected);
        thirdPartyIssue.Should().NotBeNull();
        thirdPartyIssue!.Metadata.Should().ContainKey("is_known_provider");
        thirdPartyIssue.Metadata!["is_known_provider"].Should().Be(false);
    }

    #endregion

    #region Custom Pi-hole Port Tests

    [Fact]
    public async Task Analyze_WithCustomPiholePort_PassesToDetector()
    {
        // Arrange - Network with third-party DNS but custom port won't find Pi-hole
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "Corporate",
                VlanId = 10,
                DhcpEnabled = true,
                Gateway = "192.168.1.1",
                DnsServers = new List<string> { "192.168.1.5" }
            }
        };
        var switches = new List<SwitchInfo>
        {
            new SwitchInfo { Name = "Gateway", IsGateway = true }
        };

        // Act - With custom port (won't actually probe in tests)
        var result = await _analyzer.AnalyzeAsync(
            settingsData: null,
            firewallRules: null,
            switches: switches,
            networks: networks,
            deviceData: null,
            customDnsManagementPort: 8080);

        // Assert - Should still detect third-party DNS even if Pi-hole probe fails
        result.HasThirdPartyDns.Should().BeTrue();
        result.ThirdPartyDnsServers.Should().NotBeEmpty();
    }

    #endregion

    #region DoH Configuration Tests (CyberSecure)

    [Fact]
    public async Task Analyze_WithNextDnsDoH_IdentifiesProvider()
    {
        // Arrange - NextDNS DoH
        var settings = JsonDocument.Parse(@"[
            {
                ""key"": ""doh"",
                ""state"": ""auto"",
                ""server_names"": [""nextdns""]
            }
        ]").RootElement;

        // Act
        var result = await _analyzer.AnalyzeAsync(settings, null);

        // Assert
        result.DohConfigured.Should().BeTrue();
        result.ConfiguredServers.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Analyze_WithDoHAndAllFirewallRules_FullyProtected()
    {
        // Arrange - Complete DNS security setup
        var settings = JsonDocument.Parse(@"[
            {
                ""key"": ""doh"",
                ""state"": ""auto"",
                ""server_names"": [""cloudflare""]
            }
        ]").RootElement;

        var firewall = JsonDocument.Parse(@"[
            { ""name"": ""Block DNS"", ""enabled"": true, ""action"": ""drop"", ""protocol"": ""udp"", ""destination"": { ""port"": ""53"" } },
            { ""name"": ""Block DoT/DoQ"", ""enabled"": true, ""action"": ""drop"", ""protocol"": ""tcp_udp"", ""destination"": { ""port"": ""853"" } },
            { ""name"": ""Block DoH"", ""enabled"": true, ""action"": ""drop"", ""protocol"": ""tcp"", ""destination"": { ""port"": ""443"", ""matching_target"": ""WEB"", ""web_domains"": [""dns.google""] } }
        ]").RootElement;

        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""udm"",
                ""port_table"": [
                    {
                        ""network_name"": ""wan"",
                        ""name"": ""WAN"",
                        ""up"": true,
                        ""dns"": [""1.1.1.1"", ""1.0.0.1""]
                    }
                ]
            }
        ]").RootElement;

        // Act
        var result = await _analyzer.AnalyzeAsync(settings, ParseFirewallRules(firewall), null, null, deviceData);

        // Assert
        result.DohConfigured.Should().BeTrue();
        result.HasDns53BlockRule.Should().BeTrue();
        result.HasDotBlockRule.Should().BeTrue();
        result.HasDohBlockRule.Should().BeTrue();
        result.HasDoqBlockRule.Should().BeTrue();
    }

    [Fact]
    public async Task Analyze_WithDoHDisabled_GeneratesRecommendation()
    {
        // Arrange
        var settings = JsonDocument.Parse(@"[
            {
                ""key"": ""doh"",
                ""state"": ""disabled""
            }
        ]").RootElement;

        // Act
        var result = await _analyzer.AnalyzeAsync(settings, null);

        // Assert
        result.DohConfigured.Should().BeFalse();
        result.DohState.Should().Be("disabled");
    }

    #endregion

    #region DNAT DNS Integration Tests

    private static List<NetworkInfo> CreateDhcpNetworks(params (string id, string name, string subnet)[] networks)
    {
        return networks.Select(n => new NetworkInfo
        {
            Id = n.id,
            Name = n.name,
            VlanId = 1,
            Subnet = n.subnet,
            Gateway = DeriveGatewayFromSubnet(n.subnet),
            DhcpEnabled = true
        }).ToList();
    }

    private static string? DeriveGatewayFromSubnet(string subnet)
    {
        // Convert 192.168.1.0/24 -> 192.168.1.1
        if (string.IsNullOrEmpty(subnet)) return null;
        var parts = subnet.Split('/')[0].Split('.');
        if (parts.Length != 4) return null;
        parts[3] = "1";
        return string.Join(".", parts);
    }

    private static JsonElement CreateDnatNatRules(params (string networkConfId, string redirectIp)[] rules)
    {
        var ruleJsons = rules.Select((r, i) => $$"""
            {
                "_id": "rule{{i}}",
                "type": "DNAT",
                "enabled": true,
                "protocol": "udp",
                "ip_address": "{{r.redirectIp}}",
                "destination_filter": { "filter_type": "ADDRESS_AND_PORT", "port": "53" },
                "source_filter": { "filter_type": "NETWORK_CONF", "network_conf_id": "{{r.networkConfId}}" }
            }
            """);
        return JsonDocument.Parse($"[{string.Join(",", ruleJsons)}]").RootElement;
    }

    private static JsonElement CreateSubnetDnatNatRules(params (string subnet, string redirectIp)[] rules)
    {
        var ruleJsons = rules.Select((r, i) => $$"""
            {
                "_id": "rule{{i}}",
                "type": "DNAT",
                "enabled": true,
                "protocol": "udp",
                "ip_address": "{{r.redirectIp}}",
                "destination_filter": { "filter_type": "ADDRESS_AND_PORT", "port": "53" },
                "source_filter": { "filter_type": "ADDRESS_AND_PORT", "address": "{{r.subnet}}" }
            }
            """);
        return JsonDocument.Parse($"[{string.Join(",", ruleJsons)}]").RootElement;
    }

    [Fact]
    public async Task Analyze_WithDnatFullCoverageAndDoH_SuppressesDnsNo53BlockIssue()
    {
        // Arrange - DoH configured + DNAT full coverage
        var settings = JsonDocument.Parse(@"[
            {
                ""key"": ""doh"",
                ""state"": ""auto"",
                ""server_names"": [""NextDNS-test""]
            }
        ]").RootElement;
        var networks = CreateDhcpNetworks(("net1", "LAN", "192.168.1.0/24"));
        var natRules = CreateDnatNatRules(("net1", "192.168.1.1"));

        // Act
        var result = await _analyzer.AnalyzeAsync(settings, null, null, networks, null, null, natRules);

        // Assert - Should NOT have DNS_NO_53_BLOCK issue
        result.HasDnatDnsRules.Should().BeTrue();
        result.DnatProvidesFullCoverage.Should().BeTrue();
        result.Issues.Should().NotContain(i => i.Type == IssueTypes.DnsNo53Block);
    }

    [Fact]
    public async Task Analyze_WithDnatPartialCoverage_GeneratesBothIssues()
    {
        // Arrange - DNAT only covers one of two networks
        var networks = CreateDhcpNetworks(
            ("net1", "LAN", "192.168.1.0/24"),
            ("net2", "IoT", "192.168.2.0/24"));
        var natRules = CreateDnatNatRules(("net1", "192.168.1.1")); // Only covers net1

        // Act
        var result = await _analyzer.AnalyzeAsync(null, null, null, networks, null, null, natRules);

        // Assert - Should have both DNS_NO_53_BLOCK and partial coverage issue
        result.HasDnatDnsRules.Should().BeTrue();
        result.DnatProvidesFullCoverage.Should().BeFalse();
        result.Issues.Should().Contain(i => i.Type == IssueTypes.DnsNo53Block);
        result.Issues.Should().Contain(i => i.Type == IssueTypes.DnsDnatPartialCoverage);
    }

    [Fact]
    public async Task Analyze_WithDnatSingleIpRule_GeneratesInformationalIssue()
    {
        // Arrange - Single IP DNAT (abnormal configuration)
        var networks = CreateDhcpNetworks(("net1", "LAN", "192.168.1.0/24"));
        var singleIpRule = JsonDocument.Parse(@"[
            {
                ""_id"": ""rule1"",
                ""type"": ""DNAT"",
                ""enabled"": true,
                ""protocol"": ""udp"",
                ""ip_address"": ""192.168.1.1"",
                ""destination_filter"": { ""filter_type"": ""ADDRESS_AND_PORT"", ""port"": ""53"" },
                ""source_filter"": { ""filter_type"": ""ADDRESS_AND_PORT"", ""address"": ""192.168.1.100"" }
            }
        ]").RootElement;

        // Act
        var result = await _analyzer.AnalyzeAsync(null, null, null, networks, null, null, singleIpRule);

        // Assert
        result.HasDnatDnsRules.Should().BeTrue();
        result.DnatSingleIpRules.Should().Contain("192.168.1.100");
        result.Issues.Should().Contain(i => i.Type == IssueTypes.DnsDnatSingleIp);
    }

    [Fact]
    public async Task Analyze_WithBothFirewallBlockAndDnat_NoIssues()
    {
        // Arrange - Both firewall block AND DNAT (redundant but valid)
        var firewall = JsonDocument.Parse(@"[
            {
                ""_id"": ""rule1"",
                ""name"": ""Block DNS"",
                ""enabled"": true,
                ""action"": ""DROP"",
                ""destination"": { ""port_matching_type"": ""SPECIFIC"", ""port"": ""53"" },
                ""protocol"": ""udp""
            }
        ]").RootElement;
        var networks = CreateDhcpNetworks(("net1", "LAN", "192.168.1.0/24"));
        var natRules = CreateDnatNatRules(("net1", "192.168.1.1"));

        // Act
        var result = await _analyzer.AnalyzeAsync(null, ParseFirewallRules(firewall), null, networks, null, null, natRules);

        // Assert - Should NOT have DNS_NO_53_BLOCK (firewall handles it)
        result.HasDns53BlockRule.Should().BeTrue();
        result.HasDnatDnsRules.Should().BeTrue();
        result.Issues.Should().NotContain(i => i.Type == IssueTypes.DnsNo53Block);
    }

    [Fact]
    public async Task Analyze_WithDnatFullCoverageButNoDoH_StillGeneratesDoHIssue()
    {
        // Arrange - DNAT full coverage but no DoH
        var networks = CreateDhcpNetworks(("net1", "LAN", "192.168.1.0/24"));
        var natRules = CreateDnatNatRules(("net1", "192.168.1.1"));

        // Act
        var result = await _analyzer.AnalyzeAsync(null, null, null, networks, null, null, natRules);

        // Assert - Should still have DNS_NO_DOH issue (DNAT doesn't replace DoH)
        result.DnatProvidesFullCoverage.Should().BeTrue();
        result.Issues.Should().Contain(i => i.Type == IssueTypes.DnsNoDoh);
        // But should suppress DNS_NO_53_BLOCK since no DNS control solution
        result.Issues.Should().Contain(i => i.Type == IssueTypes.DnsNo53Block);
    }

    [Fact]
    public async Task Analyze_WithSubnetDnatCoveringAllNetworks_ProvidesFullCoverage()
    {
        // Arrange - Single /16 DNAT covers multiple /24 networks
        var networks = CreateDhcpNetworks(
            ("net1", "LAN", "192.168.1.0/24"),
            ("net2", "IoT", "192.168.2.0/24"));
        var natRules = CreateSubnetDnatNatRules(("192.168.0.0/16", "192.168.1.1"));

        // Act
        var result = await _analyzer.AnalyzeAsync(null, null, null, networks, null, null, natRules);

        // Assert
        result.HasDnatDnsRules.Should().BeTrue();
        result.DnatProvidesFullCoverage.Should().BeTrue();
        result.DnatCoveredNetworks.Should().Contain("LAN");
        result.DnatCoveredNetworks.Should().Contain("IoT");
    }

    [Fact]
    public async Task Analyze_DnatResultPropertiesPopulatedCorrectly()
    {
        // Arrange
        var networks = CreateDhcpNetworks(
            ("net1", "LAN", "192.168.1.0/24"),
            ("net2", "IoT", "192.168.2.0/24"));
        var natRules = CreateDnatNatRules(("net1", "10.0.0.1"));

        // Act
        var result = await _analyzer.AnalyzeAsync(null, null, null, networks, null, null, natRules);

        // Assert
        result.HasDnatDnsRules.Should().BeTrue();
        result.DnatRedirectTarget.Should().Be("10.0.0.1");
        result.DnatCoveredNetworks.Should().Contain("LAN");
        result.DnatUncoveredNetworks.Should().Contain("IoT");
    }

    [Fact]
    public async Task Analyze_WithThirdPartyDnsAndDnatFullCoverage_SuppressesDnsNo53BlockIssue()
    {
        // Arrange - Third-party DNS (Pi-hole style) + DNAT full coverage, no firewall block
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "LAN",
                VlanId = 1,
                Subnet = "192.168.1.0/24",
                DhcpEnabled = true,
                Gateway = "192.168.1.1",
                DnsServers = new List<string> { "192.168.1.5" } // Third-party DNS
            }
        };
        var switches = new List<SwitchInfo>
        {
            new SwitchInfo { Name = "Gateway", IsGateway = true }
        };
        var natRules = CreateDnatNatRules(("net1", "192.168.1.5"));

        // Act - No firewall data (port 53 open)
        var result = await _analyzer.AnalyzeAsync(
            settingsData: null,
            firewallRules: null,
            switches: switches,
            networks: networks,
            deviceData: null,
            customDnsManagementPort: null,
            natRulesData: natRules);

        // Assert - Third-party DNS + DNAT should suppress DNS_NO_53_BLOCK
        result.HasThirdPartyDns.Should().BeTrue();
        result.HasDnatDnsRules.Should().BeTrue();
        result.DnatProvidesFullCoverage.Should().BeTrue();
        result.Issues.Should().NotContain(i => i.Type == IssueTypes.DnsNo53Block);
    }

    [Fact]
    public async Task Analyze_WithThirdPartyDnsAndNoDnatAndNoFirewallBlock_GeneratesDnsNo53BlockIssue()
    {
        // Arrange - Third-party DNS but no DNAT and no firewall block = DNS leak risk
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "LAN",
                VlanId = 1,
                Subnet = "192.168.1.0/24",
                DhcpEnabled = true,
                Gateway = "192.168.1.1",
                DnsServers = new List<string> { "192.168.1.5" } // Third-party DNS
            }
        };
        var switches = new List<SwitchInfo>
        {
            new SwitchInfo { Name = "Gateway", IsGateway = true }
        };

        // Act - No firewall block, no DNAT
        var result = await _analyzer.AnalyzeAsync(
            settingsData: null,
            firewallRules: null,
            switches: switches,
            networks: networks,
            deviceData: null,
            customDnsManagementPort: null,
            natRulesData: null);

        // Assert - Should raise DNS_NO_53_BLOCK even with third-party DNS
        result.HasThirdPartyDns.Should().BeTrue();
        result.HasDnatDnsRules.Should().BeFalse();
        result.Issues.Should().Contain(i => i.Type == IssueTypes.DnsNo53Block);
    }

    [Fact]
    public async Task Analyze_WithThirdPartyDnsAndDnatPartialCoverage_OnlyPartialCoverageIssue()
    {
        // Arrange - Third-party DNS + DNAT only covers one of two networks
        // With valid partial DNAT coverage, DNS_NO_53_BLOCK is suppressed (partial coverage issue is more actionable)
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "LAN",
                VlanId = 1,
                Subnet = "192.168.1.0/24",
                DhcpEnabled = true,
                Gateway = "192.168.1.1",
                DnsServers = new List<string> { "192.168.1.5" }
            },
            new NetworkInfo
            {
                Id = "net2",
                Name = "IoT",
                VlanId = 20,
                Subnet = "192.168.20.0/24",
                DhcpEnabled = true,
                Gateway = "192.168.20.1",
                DnsServers = new List<string> { "192.168.1.5" }
            }
        };
        var switches = new List<SwitchInfo>
        {
            new SwitchInfo { Name = "Gateway", IsGateway = true }
        };
        // DNAT only covers net1, not net2
        var natRules = CreateDnatNatRules(("net1", "192.168.1.5"));

        // Act
        var result = await _analyzer.AnalyzeAsync(
            settingsData: null,
            firewallRules: null,
            switches: switches,
            networks: networks,
            deviceData: null,
            customDnsManagementPort: null,
            natRulesData: natRules);

        // Assert - Only partial coverage issue (DNS_NO_53_BLOCK suppressed for valid partial DNAT)
        result.HasThirdPartyDns.Should().BeTrue();
        result.DnatProvidesFullCoverage.Should().BeFalse();
        result.Issues.Should().NotContain(i => i.Type == IssueTypes.DnsNo53Block);
        result.Issues.Should().Contain(i => i.Type == IssueTypes.DnsDnatPartialCoverage);
    }

    [Fact]
    public async Task Analyze_WithThirdPartyDnsAndFirewallBlock_NoDnsNo53BlockIssue()
    {
        // Arrange - Third-party DNS (Pi-hole) + firewall blocks port 53 (ideal config)
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "LAN",
                VlanId = 1,
                Subnet = "192.168.1.0/24",
                DhcpEnabled = true,
                Gateway = "192.168.1.1",
                DnsServers = new List<string> { "192.168.1.5" } // Pi-hole
            }
        };
        var switches = new List<SwitchInfo>
        {
            new SwitchInfo { Name = "Gateway", IsGateway = true }
        };
        // Firewall rule blocking port 53
        var firewall = JsonDocument.Parse(@"[
            {
                ""name"": ""Block DNS"",
                ""enabled"": true,
                ""action"": ""drop"",
                ""protocol"": ""udp"",
                ""destination"": { ""port"": ""53"" }
            }
        ]");

        // Act - No DNAT, but firewall blocks port 53
        var result = await _analyzer.AnalyzeAsync(
            settingsData: null,
            firewallRules: ParseFirewallRules(firewall.RootElement),
            switches: switches,
            networks: networks,
            deviceData: null,
            customDnsManagementPort: null,
            natRulesData: null);

        // Assert - Firewall block should be sufficient, no DNS_NO_53_BLOCK issue
        result.HasThirdPartyDns.Should().BeTrue();
        result.HasDns53BlockRule.Should().BeTrue();
        result.Issues.Should().NotContain(i => i.Type == IssueTypes.DnsNo53Block);
    }

    [Fact]
    public async Task Analyze_WithThirdPartyDnsAndFirewallBlockAndDnat_NoDnsNo53BlockIssue()
    {
        // Arrange - Third-party DNS + firewall block + DNAT (redundant but valid)
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "LAN",
                VlanId = 1,
                Subnet = "192.168.1.0/24",
                DhcpEnabled = true,
                Gateway = "192.168.1.1",
                DnsServers = new List<string> { "192.168.1.5" }
            }
        };
        var switches = new List<SwitchInfo>
        {
            new SwitchInfo { Name = "Gateway", IsGateway = true }
        };
        var firewall = JsonDocument.Parse(@"[
            {
                ""name"": ""Block DNS"",
                ""enabled"": true,
                ""action"": ""drop"",
                ""protocol"": ""udp"",
                ""destination"": { ""port"": ""53"" }
            }
        ]");
        var natRules = CreateDnatNatRules(("net1", "192.168.1.5"));

        // Act
        var result = await _analyzer.AnalyzeAsync(
            settingsData: null,
            firewallRules: ParseFirewallRules(firewall.RootElement),
            switches: switches,
            networks: networks,
            deviceData: null,
            customDnsManagementPort: null,
            natRulesData: natRules);

        // Assert - Both protections in place, no issues
        result.HasThirdPartyDns.Should().BeTrue();
        result.HasDns53BlockRule.Should().BeTrue();
        result.HasDnatDnsRules.Should().BeTrue();
        result.Issues.Should().NotContain(i => i.Type == IssueTypes.DnsNo53Block);
    }

    #endregion

    #region Real-World Multi-VLAN Scenario Tests

    [Fact]
    public async Task Analyze_RealWorldScenario_MultipleVlansWithPiholeAndFullDnatCoverage()
    {
        // Arrange - Typical home/SMB setup:
        // - LAN (VLAN 1): Main network with DHCP, Pi-hole DNS
        // - IoT (VLAN 20): IoT devices with DHCP, Pi-hole DNS
        // - Guest (VLAN 50): Guest network with DHCP, Pi-hole DNS
        // - Management (VLAN 99): Static IPs only (no DHCP), Pi-hole DNS
        // - DNAT rules redirect all DNS to Pi-hole
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1", Name = "LAN", VlanId = 1,
                Subnet = "192.168.1.0/24", DhcpEnabled = true,
                Gateway = "192.168.1.1",
                DnsServers = new List<string> { "192.168.1.5" } // Pi-hole
            },
            new NetworkInfo
            {
                Id = "net2", Name = "IoT", VlanId = 20,
                Subnet = "192.168.20.0/24", DhcpEnabled = true,
                Gateway = "192.168.20.1",
                DnsServers = new List<string> { "192.168.1.5" }
            },
            new NetworkInfo
            {
                Id = "net3", Name = "Guest", VlanId = 50,
                Subnet = "192.168.50.0/24", DhcpEnabled = true,
                Gateway = "192.168.50.1",
                DnsServers = new List<string> { "192.168.1.5" }
            },
            new NetworkInfo
            {
                Id = "net4", Name = "Management", VlanId = 99,
                Subnet = "192.168.99.0/24", DhcpEnabled = false, // Static IPs only
                Gateway = "192.168.99.1",
                DnsServers = new List<string> { "192.168.1.5" }
            }
        };
        var switches = new List<SwitchInfo>
        {
            new SwitchInfo { Name = "Gateway", IsGateway = true }
        };
        // Single /16 DNAT rule covers all /24 networks
        var natRules = CreateSubnetDnatNatRules(("192.168.0.0/16", "192.168.1.5"));

        // Act
        var result = await _analyzer.AnalyzeAsync(
            settingsData: null,
            firewallRules: null,
            switches: switches,
            networks: networks,
            deviceData: null,
            customDnsManagementPort: null,
            natRulesData: natRules);

        // Assert - Full coverage including non-DHCP Management network
        result.HasThirdPartyDns.Should().BeTrue();
        result.HasDnatDnsRules.Should().BeTrue();
        result.DnatProvidesFullCoverage.Should().BeTrue();
        result.DnatCoveredNetworks.Should().HaveCount(4);
        result.DnatCoveredNetworks.Should().Contain("LAN");
        result.DnatCoveredNetworks.Should().Contain("IoT");
        result.DnatCoveredNetworks.Should().Contain("Guest");
        result.DnatCoveredNetworks.Should().Contain("Management");
        result.DnatUncoveredNetworks.Should().BeEmpty();
        result.Issues.Should().NotContain(i => i.Type == IssueTypes.DnsNo53Block);
    }

    [Fact]
    public async Task Analyze_RealWorldScenario_MultipleVlansWithPartialDnatCoverage()
    {
        // Arrange - Setup where DNAT only covers some networks
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1", Name = "LAN", VlanId = 1,
                Subnet = "192.168.1.0/24", DhcpEnabled = true,
                Gateway = "192.168.1.1",
                DnsServers = new List<string> { "192.168.1.5" }
            },
            new NetworkInfo
            {
                Id = "net2", Name = "IoT", VlanId = 20,
                Subnet = "192.168.20.0/24", DhcpEnabled = true,
                Gateway = "192.168.20.1",
                DnsServers = new List<string> { "192.168.1.5" }
            },
            new NetworkInfo
            {
                Id = "net3", Name = "Guest", VlanId = 50,
                Subnet = "10.10.50.0/24", DhcpEnabled = true, // Different subnet range!
                Gateway = "10.10.50.1",
                DnsServers = new List<string> { "192.168.1.5" }
            }
        };
        var switches = new List<SwitchInfo>
        {
            new SwitchInfo { Name = "Gateway", IsGateway = true }
        };
        // /16 DNAT only covers 192.168.x.x networks, not 10.10.x.x
        var natRules = CreateSubnetDnatNatRules(("192.168.0.0/16", "192.168.1.5"));

        // Act
        var result = await _analyzer.AnalyzeAsync(
            settingsData: null,
            firewallRules: null,
            switches: switches,
            networks: networks,
            deviceData: null,
            customDnsManagementPort: null,
            natRulesData: natRules);

        // Assert - Partial coverage, Guest network not covered
        // DNS_NO_53_BLOCK is suppressed for valid partial DNAT (partial coverage issue is more actionable)
        result.HasThirdPartyDns.Should().BeTrue();
        result.DnatProvidesFullCoverage.Should().BeFalse();
        result.DnatCoveredNetworks.Should().Contain("LAN");
        result.DnatCoveredNetworks.Should().Contain("IoT");
        result.DnatUncoveredNetworks.Should().Contain("Guest");
        result.Issues.Should().NotContain(i => i.Type == IssueTypes.DnsNo53Block);
        result.Issues.Should().Contain(i => i.Type == IssueTypes.DnsDnatPartialCoverage);
    }

    [Fact]
    public async Task Analyze_RealWorldScenario_PerNetworkDnatRules()
    {
        // Arrange - Individual DNAT rules per network (common UniFi setup)
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1", Name = "LAN", VlanId = 1,
                Subnet = "192.168.1.0/24", DhcpEnabled = true,
                Gateway = "192.168.1.1",
                DnsServers = new List<string> { "192.168.1.5" }
            },
            new NetworkInfo
            {
                Id = "net2", Name = "IoT", VlanId = 20,
                Subnet = "192.168.20.0/24", DhcpEnabled = true,
                Gateway = "192.168.20.1",
                DnsServers = new List<string> { "192.168.1.5" }
            },
            new NetworkInfo
            {
                Id = "net3", Name = "Guest", VlanId = 50,
                Subnet = "192.168.50.0/24", DhcpEnabled = true,
                Gateway = "192.168.50.1",
                DnsServers = new List<string> { "192.168.1.5" }
            }
        };
        var switches = new List<SwitchInfo>
        {
            new SwitchInfo { Name = "Gateway", IsGateway = true }
        };
        // Individual network-ref DNAT rules for each network
        var natRules = CreateDnatNatRules(
            ("net1", "192.168.1.5"),
            ("net2", "192.168.1.5"),
            ("net3", "192.168.1.5"));

        // Act
        var result = await _analyzer.AnalyzeAsync(
            settingsData: null,
            firewallRules: null,
            switches: switches,
            networks: networks,
            deviceData: null,
            customDnsManagementPort: null,
            natRulesData: natRules);

        // Assert - Full coverage via individual rules
        result.HasThirdPartyDns.Should().BeTrue();
        result.DnatProvidesFullCoverage.Should().BeTrue();
        result.DnatCoveredNetworks.Should().HaveCount(3);
        result.Issues.Should().NotContain(i => i.Type == IssueTypes.DnsNo53Block);
    }

    [Fact]
    public async Task Analyze_RealWorldScenario_MixedDhcpAndStaticNetworksAllNeedCoverage()
    {
        // Arrange - Mix of DHCP and static-only networks
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1", Name = "LAN", VlanId = 1,
                Subnet = "192.168.1.0/24", DhcpEnabled = true,
                Gateway = "192.168.1.1",
                DnsServers = new List<string> { "192.168.1.5" }
            },
            new NetworkInfo
            {
                Id = "net2", Name = "Servers", VlanId = 10,
                Subnet = "192.168.10.0/24", DhcpEnabled = false, // Static only
                Gateway = "192.168.10.1",
                DnsServers = new List<string> { "192.168.1.5" }
            }
        };
        var switches = new List<SwitchInfo>
        {
            new SwitchInfo { Name = "Gateway", IsGateway = true }
        };
        // DNAT only covers LAN, not Servers
        var natRules = CreateDnatNatRules(("net1", "192.168.1.5"));

        // Act
        var result = await _analyzer.AnalyzeAsync(
            settingsData: null,
            firewallRules: null,
            switches: switches,
            networks: networks,
            deviceData: null,
            customDnsManagementPort: null,
            natRulesData: natRules);

        // Assert - Servers network (non-DHCP) still needs coverage
        result.DnatProvidesFullCoverage.Should().BeFalse();
        result.DnatCoveredNetworks.Should().Contain("LAN");
        result.DnatUncoveredNetworks.Should().Contain("Servers");
        result.Issues.Should().Contain(i => i.Type == IssueTypes.DnsDnatPartialCoverage);
    }

    #endregion

    #region DNAT Redirect Destination Validation Tests

    [Fact]
    public async Task Analyze_DnatWithPihole_RedirectsToPiholeIp_NoIssue()
    {
        // Arrange - Pi-hole configured, DNAT correctly points to Pi-hole
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1", Name = "LAN", VlanId = 1,
                Subnet = "192.168.1.0/24", DhcpEnabled = true,
                Gateway = "192.168.1.1",
                DnsServers = new List<string> { "192.168.1.5" } // Pi-hole
            }
        };
        var switches = new List<SwitchInfo>();
        var natRules = CreateDnatNatRules(("net1", "192.168.1.5")); // Points to Pi-hole

        // Act
        var result = await _analyzer.AnalyzeAsync(null, null, switches, networks, null, null, natRules);

        // Assert - No wrong destination issue
        result.DnatRedirectTargetIsValid.Should().BeTrue();
        result.InvalidDnatRules.Should().BeEmpty();
        result.Issues.Should().NotContain(i => i.Type == IssueTypes.DnsDnatWrongDestination);
    }

    [Fact]
    public async Task Analyze_DnatWithPihole_RedirectsToGateway_RaisesIssue()
    {
        // Arrange - Pi-hole configured on non-Corporate network (site-wide), but DNAT incorrectly points to gateway
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1", Name = "LAN", VlanId = 1,
                Purpose = NetworkPurpose.Home, // Non-Corporate so third-party DNS is site-wide
                Subnet = "192.168.1.0/24", DhcpEnabled = true,
                Gateway = "192.168.1.1",
                DnsServers = new List<string> { "192.168.1.5" } // Pi-hole
            }
        };
        var switches = new List<SwitchInfo>();
        var natRules = CreateDnatNatRules(("net1", "192.168.1.1")); // Wrong - points to gateway

        // Act
        var result = await _analyzer.AnalyzeAsync(null, null, switches, networks, null, null, natRules);

        // Assert - Should raise wrong destination issue
        result.IsSiteWideThirdPartyDns.Should().BeTrue();
        result.DnatRedirectTargetIsValid.Should().BeFalse();
        result.InvalidDnatRules.Should().NotBeEmpty();
        result.Issues.Should().Contain(i => i.Type == IssueTypes.DnsDnatWrongDestination);
    }

    [Fact]
    public async Task Analyze_DnatWithDoH_RedirectsToGateway_NoIssue()
    {
        // Arrange - DoH configured, DNAT correctly points to gateway
        var settings = JsonDocument.Parse(@"[
            {
                ""key"": ""doh"",
                ""state"": ""auto"",
                ""server_names"": [""NextDNS-test""]
            }
        ]").RootElement;
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1", Name = "LAN", VlanId = 1,
                Subnet = "192.168.1.0/24", DhcpEnabled = true,
                Gateway = "192.168.1.1"
            }
        };
        var natRules = CreateDnatNatRules(("net1", "192.168.1.1")); // Points to gateway

        // Act
        var result = await _analyzer.AnalyzeAsync(settings, null, null, networks, null, null, natRules);

        // Assert - No wrong destination issue
        result.DnatRedirectTargetIsValid.Should().BeTrue();
        result.Issues.Should().NotContain(i => i.Type == IssueTypes.DnsDnatWrongDestination);
    }

    [Fact]
    public async Task Analyze_DnatWithDoH_RedirectsToRandomIp_RaisesIssue()
    {
        // Arrange - DoH configured, but DNAT points to non-gateway IP
        var settings = JsonDocument.Parse(@"[
            {
                ""key"": ""doh"",
                ""state"": ""auto"",
                ""server_names"": [""NextDNS-test""]
            }
        ]").RootElement;
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1", Name = "LAN", VlanId = 1,
                Subnet = "192.168.1.0/24", DhcpEnabled = true,
                Gateway = "192.168.1.1"
            }
        };
        var natRules = CreateDnatNatRules(("net1", "10.99.99.99")); // Wrong - random IP

        // Act
        var result = await _analyzer.AnalyzeAsync(settings, null, null, networks, null, null, natRules);

        // Assert - Should raise wrong destination issue
        result.DnatRedirectTargetIsValid.Should().BeFalse();
        result.Issues.Should().Contain(i => i.Type == IssueTypes.DnsDnatWrongDestination);
    }

    [Fact]
    public async Task Analyze_DnatWithDoH_RedirectsToVlanGateway_NoIssue()
    {
        // Arrange - DoH configured, DNAT points to VLAN-specific gateway
        var settings = JsonDocument.Parse(@"[
            {
                ""key"": ""doh"",
                ""state"": ""auto"",
                ""server_names"": [""NextDNS-test""]
            }
        ]").RootElement;
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1", Name = "LAN", VlanId = 1,
                Subnet = "192.168.1.0/24", DhcpEnabled = true,
                Gateway = "192.168.1.1"
            },
            new NetworkInfo
            {
                Id = "net2", Name = "IoT", VlanId = 20,
                Subnet = "192.168.20.0/24", DhcpEnabled = true,
                Gateway = "192.168.20.1"
            }
        };
        var natRules = JsonDocument.Parse(@"[
            {
                ""_id"": ""rule1"",
                ""type"": ""DNAT"",
                ""enabled"": true,
                ""protocol"": ""udp"",
                ""ip_address"": ""192.168.1.1"",
                ""destination_filter"": { ""filter_type"": ""ADDRESS_AND_PORT"", ""port"": ""53"" },
                ""source_filter"": { ""filter_type"": ""NETWORK_CONF"", ""network_conf_id"": ""net1"" }
            },
            {
                ""_id"": ""rule2"",
                ""type"": ""DNAT"",
                ""enabled"": true,
                ""protocol"": ""udp"",
                ""ip_address"": ""192.168.20.1"",
                ""destination_filter"": { ""filter_type"": ""ADDRESS_AND_PORT"", ""port"": ""53"" },
                ""source_filter"": { ""filter_type"": ""NETWORK_CONF"", ""network_conf_id"": ""net2"" }
            }
        ]").RootElement;

        // Act
        var result = await _analyzer.AnalyzeAsync(settings, null, null, networks, null, null, natRules);

        // Assert - Both rules point to valid gateways
        result.DnatRedirectTargetIsValid.Should().BeTrue();
        result.Issues.Should().NotContain(i => i.Type == IssueTypes.DnsDnatWrongDestination);
    }

    [Fact]
    public async Task Analyze_DnatWithDoH_VlanRulePointsToNativeGateway_NoIssue()
    {
        // Arrange - DoH configured, non-native VLAN rule points to native (VLAN 1) gateway
        // This is valid - all rules can point to the native VLAN gateway
        var settings = JsonDocument.Parse(@"[
            {
                ""key"": ""doh"",
                ""state"": ""auto"",
                ""server_names"": [""NextDNS-test""]
            }
        ]").RootElement;
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1", Name = "LAN", VlanId = 1, // VlanId = 1 makes IsNative = true
                Subnet = "192.168.1.0/24", DhcpEnabled = true,
                Gateway = "192.168.1.1"
            },
            new NetworkInfo
            {
                Id = "net2", Name = "IoT", VlanId = 20,
                Subnet = "192.168.20.0/24", DhcpEnabled = true,
                Gateway = "192.168.20.1"
            }
        };
        var natRules = JsonDocument.Parse(@"[
            {
                ""_id"": ""rule1"",
                ""description"": ""IoT DNS to native gateway"",
                ""type"": ""DNAT"",
                ""enabled"": true,
                ""protocol"": ""udp"",
                ""ip_address"": ""192.168.1.1"",
                ""destination_filter"": { ""filter_type"": ""ADDRESS_AND_PORT"", ""port"": ""53"" },
                ""source_filter"": { ""filter_type"": ""NETWORK_CONF"", ""network_conf_id"": ""net2"" }
            }
        ]").RootElement;

        // Act
        var result = await _analyzer.AnalyzeAsync(settings, null, null, networks, null, null, natRules);

        // Assert - Pointing to native gateway is always valid
        result.DnatRedirectTargetIsValid.Should().BeTrue();
        result.Issues.Should().NotContain(i => i.Type == IssueTypes.DnsDnatWrongDestination);
    }

    [Fact]
    public async Task Analyze_DnatWithDoH_VlanRulePointsToDifferentVlanGateway_RaisesIssue()
    {
        // Arrange - DoH configured, one VLAN rule points to a DIFFERENT non-native VLAN's gateway
        // This is INVALID - rules must point to native gateway OR their own VLAN's gateway
        var settings = JsonDocument.Parse(@"[
            {
                ""key"": ""doh"",
                ""state"": ""auto"",
                ""server_names"": [""NextDNS-test""]
            }
        ]").RootElement;
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1", Name = "LAN", VlanId = 1, // VlanId = 1 makes IsNative = true
                Subnet = "192.168.1.0/24", DhcpEnabled = true,
                Gateway = "192.168.1.1"
            },
            new NetworkInfo
            {
                Id = "net2", Name = "IoT", VlanId = 20,
                Subnet = "192.168.20.0/24", DhcpEnabled = true,
                Gateway = "192.168.20.1"
            },
            new NetworkInfo
            {
                Id = "net3", Name = "Guest", VlanId = 30,
                Subnet = "192.168.30.0/24", DhcpEnabled = true,
                Gateway = "192.168.30.1"
            }
        };
        var natRules = JsonDocument.Parse(@"[
            {
                ""_id"": ""rule1"",
                ""description"": ""IoT DNS - wrong VLAN"",
                ""type"": ""DNAT"",
                ""enabled"": true,
                ""protocol"": ""udp"",
                ""ip_address"": ""192.168.30.1"",
                ""destination_filter"": { ""filter_type"": ""ADDRESS_AND_PORT"", ""port"": ""53"" },
                ""source_filter"": { ""filter_type"": ""NETWORK_CONF"", ""network_conf_id"": ""net2"" }
            }
        ]").RootElement;

        // Act
        var result = await _analyzer.AnalyzeAsync(settings, null, null, networks, null, null, natRules);

        // Assert - Pointing to a different VLAN's gateway (not native) is invalid
        result.DnatRedirectTargetIsValid.Should().BeFalse();
        result.InvalidDnatRules.Should().ContainSingle();
        result.InvalidDnatRules[0].Should().Contain("IoT DNS - wrong VLAN");
        result.InvalidDnatRules[0].Should().Contain("192.168.30.1"); // Wrong destination
        result.Issues.Should().Contain(i => i.Type == IssueTypes.DnsDnatWrongDestination);
    }

    [Fact]
    public async Task Analyze_DnatWithDoH_OneRuleWrongDestination_RaisesIssue()
    {
        // Arrange - DoH configured, one rule correct, one wrong
        var settings = JsonDocument.Parse(@"[
            {
                ""key"": ""doh"",
                ""state"": ""auto"",
                ""server_names"": [""NextDNS-test""]
            }
        ]").RootElement;
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1", Name = "LAN", VlanId = 1,
                Subnet = "192.168.1.0/24", DhcpEnabled = true,
                Gateway = "192.168.1.1"
            },
            new NetworkInfo
            {
                Id = "net2", Name = "IoT", VlanId = 20,
                Subnet = "192.168.20.0/24", DhcpEnabled = true,
                Gateway = "192.168.20.1"
            }
        };
        var natRules = JsonDocument.Parse(@"[
            {
                ""_id"": ""rule1"",
                ""description"": ""LAN DNS"",
                ""type"": ""DNAT"",
                ""enabled"": true,
                ""protocol"": ""udp"",
                ""ip_address"": ""192.168.1.1"",
                ""destination_filter"": { ""filter_type"": ""ADDRESS_AND_PORT"", ""port"": ""53"" },
                ""source_filter"": { ""filter_type"": ""NETWORK_CONF"", ""network_conf_id"": ""net1"" }
            },
            {
                ""_id"": ""rule2"",
                ""description"": ""IoT DNS - wrong"",
                ""type"": ""DNAT"",
                ""enabled"": true,
                ""protocol"": ""udp"",
                ""ip_address"": ""8.8.8.8"",
                ""destination_filter"": { ""filter_type"": ""ADDRESS_AND_PORT"", ""port"": ""53"" },
                ""source_filter"": { ""filter_type"": ""NETWORK_CONF"", ""network_conf_id"": ""net2"" }
            }
        ]").RootElement;

        // Act
        var result = await _analyzer.AnalyzeAsync(settings, null, null, networks, null, null, natRules);

        // Assert - One rule is invalid
        result.DnatRedirectTargetIsValid.Should().BeFalse();
        result.InvalidDnatRules.Should().ContainSingle();
        result.InvalidDnatRules[0].Should().Contain("IoT DNS - wrong");
        result.Issues.Should().Contain(i => i.Type == IssueTypes.DnsDnatWrongDestination);
    }

    [Fact]
    public async Task Analyze_DnatWithNoDnsControl_SkipsDestinationValidation()
    {
        // Arrange - No DoH, no Pi-hole - skip destination validation
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1", Name = "LAN", VlanId = 1,
                Subnet = "192.168.1.0/24", DhcpEnabled = true,
                Gateway = "192.168.1.1"
            }
        };
        var natRules = CreateDnatNatRules(("net1", "10.99.99.99")); // "Wrong" IP but no validation

        // Act
        var result = await _analyzer.AnalyzeAsync(null, null, null, networks, null, null, natRules);

        // Assert - No validation without DNS control solution
        result.DnatRedirectTargetIsValid.Should().BeTrue(); // Default true, not validated
        result.ExpectedDnatDestinations.Should().BeEmpty(); // No expected destinations
        result.Issues.Should().NotContain(i => i.Type == IssueTypes.DnsDnatWrongDestination);
    }

    [Fact]
    public async Task Analyze_DnatWithMultiplePiholes_AnyPiholeIpIsValid()
    {
        // Arrange - Multiple Pi-hole servers, DNAT points to one of them
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1", Name = "LAN", VlanId = 1,
                Subnet = "192.168.1.0/24", DhcpEnabled = true,
                Gateway = "192.168.1.1",
                DnsServers = new List<string> { "192.168.1.5", "192.168.1.6" } // Two Pi-holes
            }
        };
        var switches = new List<SwitchInfo>();
        var natRules = CreateDnatNatRules(("net1", "192.168.1.6")); // Points to secondary

        // Act
        var result = await _analyzer.AnalyzeAsync(null, null, switches, networks, null, null, natRules);

        // Assert - Secondary Pi-hole is valid
        result.DnatRedirectTargetIsValid.Should().BeTrue();
        result.Issues.Should().NotContain(i => i.Type == IssueTypes.DnsDnatWrongDestination);
    }

    [Fact]
    public async Task Analyze_DnatWrongDestination_WillNotSuppressDnsNo53Block()
    {
        // Arrange - DoH configured, DNAT has wrong destination - should NOT suppress DNS_NO_53_BLOCK
        var settings = JsonDocument.Parse(@"[
            {
                ""key"": ""doh"",
                ""state"": ""auto"",
                ""server_names"": [""NextDNS-test""]
            }
        ]").RootElement;
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1", Name = "LAN", VlanId = 1,
                Subnet = "192.168.1.0/24", DhcpEnabled = true,
                Gateway = "192.168.1.1"
            }
        };
        var natRules = CreateDnatNatRules(("net1", "8.8.8.8")); // Wrong destination

        // Act
        var result = await _analyzer.AnalyzeAsync(settings, null, null, networks, null, null, natRules);

        // Assert - DNAT is not a valid alternative due to wrong destination
        result.DnatProvidesFullCoverage.Should().BeTrue(); // Coverage is full
        result.DnatRedirectTargetIsValid.Should().BeFalse(); // But destination is wrong
        result.Issues.Should().Contain(i => i.Type == IssueTypes.DnsNo53Block); // So DNS leak issue raised
        result.Issues.Should().Contain(i => i.Type == IssueTypes.DnsDnatWrongDestination);
    }

    #endregion

    #region Source Network Match Opposite Tests

    [Fact]
    public async Task Analyze_WithDns53BlockRule_MatchOppositeNetworks_ExcludesSpecifiedNetwork()
    {
        // Arrange - DNS block rule with Match Opposite: applies to all networks EXCEPT net2
        var networks = CreateDhcpNetworks(
            ("net1", "LAN", "192.168.1.0/24"),
            ("net2", "IoT", "192.168.2.0/24"),
            ("net3", "Guest", "192.168.3.0/24"));

        var firewall = JsonDocument.Parse(@"[
            {
                ""name"": ""Block DNS (Match Opposite)"",
                ""enabled"": true,
                ""action"": ""drop"",
                ""protocol"": ""udp"",
                ""source"": {
                    ""matching_target"": ""NETWORK"",
                    ""network_ids"": [""net2""],
                    ""match_opposite_networks"": true
                },
                ""destination"": { ""port"": ""53"" }
            }
        ]").RootElement;

        // Act
        var result = await _analyzer.AnalyzeAsync(null, ParseFirewallRules(firewall), null, networks);

        // Assert - Rule covers LAN and Guest (all except IoT)
        result.HasDns53BlockRule.Should().BeTrue();
        result.Dns53ProvidesFullCoverage.Should().BeFalse();
        result.Dns53CoveredNetworks.Should().Contain("LAN");
        result.Dns53CoveredNetworks.Should().Contain("Guest");
        result.Dns53CoveredNetworks.Should().NotContain("IoT");
        result.Dns53UncoveredNetworks.Should().Contain("IoT");
        result.Issues.Should().Contain(i => i.Type == IssueTypes.Dns53PartialCoverage);
    }

    [Fact]
    public async Task Analyze_WithDns53BlockRule_SpecificNetworks_OnlyCoversListedNetworks()
    {
        // Arrange - DNS block rule applies ONLY to net1 (no Match Opposite)
        var networks = CreateDhcpNetworks(
            ("net1", "LAN", "192.168.1.0/24"),
            ("net2", "IoT", "192.168.2.0/24"));

        var firewall = JsonDocument.Parse(@"[
            {
                ""name"": ""Block DNS for LAN Only"",
                ""enabled"": true,
                ""action"": ""drop"",
                ""protocol"": ""udp"",
                ""source"": {
                    ""matching_target"": ""NETWORK"",
                    ""network_ids"": [""net1""],
                    ""match_opposite_networks"": false
                },
                ""destination"": { ""port"": ""53"" }
            }
        ]").RootElement;

        // Act
        var result = await _analyzer.AnalyzeAsync(null, ParseFirewallRules(firewall), null, networks);

        // Assert - Only LAN is covered
        result.HasDns53BlockRule.Should().BeTrue();
        result.Dns53ProvidesFullCoverage.Should().BeFalse();
        result.Dns53CoveredNetworks.Should().Contain("LAN");
        result.Dns53UncoveredNetworks.Should().Contain("IoT");
        result.Issues.Should().Contain(i => i.Type == IssueTypes.Dns53PartialCoverage);
    }

    [Fact]
    public async Task Analyze_WithDns53BlockRule_SourceAny_CoversAllNetworks()
    {
        // Arrange - DNS block rule with source ANY covers all networks
        var networks = CreateDhcpNetworks(
            ("net1", "LAN", "192.168.1.0/24"),
            ("net2", "IoT", "192.168.2.0/24"));

        var firewall = JsonDocument.Parse(@"[
            {
                ""name"": ""Block DNS for All"",
                ""enabled"": true,
                ""action"": ""drop"",
                ""protocol"": ""udp"",
                ""source"": {
                    ""matching_target"": ""ANY""
                },
                ""destination"": { ""port"": ""53"" }
            }
        ]").RootElement;

        // Act
        var result = await _analyzer.AnalyzeAsync(null, ParseFirewallRules(firewall), null, networks);

        // Assert - All networks covered
        result.HasDns53BlockRule.Should().BeTrue();
        result.Dns53ProvidesFullCoverage.Should().BeTrue();
        result.Dns53CoveredNetworks.Should().Contain("LAN");
        result.Dns53CoveredNetworks.Should().Contain("IoT");
        result.Dns53UncoveredNetworks.Should().BeEmpty();
        result.Issues.Should().NotContain(i => i.Type == IssueTypes.Dns53PartialCoverage);
    }

    [Fact]
    public async Task Analyze_WithDns53BlockRule_MultipleRulesCombineCoverage()
    {
        // Arrange - Multiple DNS block rules cover different networks
        var networks = CreateDhcpNetworks(
            ("net1", "LAN", "192.168.1.0/24"),
            ("net2", "IoT", "192.168.2.0/24"),
            ("net3", "Guest", "192.168.3.0/24"));

        var firewall = JsonDocument.Parse(@"[
            {
                ""name"": ""Block DNS for LAN"",
                ""enabled"": true,
                ""action"": ""drop"",
                ""protocol"": ""udp"",
                ""source"": {
                    ""matching_target"": ""NETWORK"",
                    ""network_ids"": [""net1""]
                },
                ""destination"": { ""port"": ""53"" }
            },
            {
                ""name"": ""Block DNS for IoT and Guest"",
                ""enabled"": true,
                ""action"": ""drop"",
                ""protocol"": ""udp"",
                ""source"": {
                    ""matching_target"": ""NETWORK"",
                    ""network_ids"": [""net2"", ""net3""]
                },
                ""destination"": { ""port"": ""53"" }
            }
        ]").RootElement;

        // Act
        var result = await _analyzer.AnalyzeAsync(null, ParseFirewallRules(firewall), null, networks);

        // Assert - All networks covered by combined rules
        result.HasDns53BlockRule.Should().BeTrue();
        result.Dns53ProvidesFullCoverage.Should().BeTrue();
        result.Dns53CoveredNetworks.Should().HaveCount(3);
        result.Issues.Should().NotContain(i => i.Type == IssueTypes.Dns53PartialCoverage);
    }

    [Fact]
    public async Task Analyze_WithDnatRule_MatchOpposite_CoversAllExceptSpecified()
    {
        // Arrange - DNAT rule with Match Opposite: applies to all networks EXCEPT net2
        var networks = CreateDhcpNetworks(
            ("net1", "LAN", "192.168.1.0/24"),
            ("net2", "IoT", "192.168.2.0/24"),
            ("net3", "Guest", "192.168.3.0/24"));

        var natRules = JsonDocument.Parse(@"[
            {
                ""_id"": ""rule1"",
                ""type"": ""DNAT"",
                ""enabled"": true,
                ""protocol"": ""udp"",
                ""ip_address"": ""192.168.1.1"",
                ""destination_filter"": { ""port"": ""53"" },
                ""source_filter"": {
                    ""filter_type"": ""NETWORK_CONF"",
                    ""network_conf_id"": ""net2"",
                    ""match_opposite"": true
                }
            }
        ]").RootElement;

        // Act
        var result = await _analyzer.AnalyzeAsync(null, null, null, networks, null, null, natRules);

        // Assert - Covers LAN and Guest (all except IoT)
        result.HasDnatDnsRules.Should().BeTrue();
        result.DnatProvidesFullCoverage.Should().BeFalse();
        result.DnatCoveredNetworks.Should().Contain("LAN");
        result.DnatCoveredNetworks.Should().Contain("Guest");
        result.DnatUncoveredNetworks.Should().Contain("IoT");
    }

    [Fact]
    public async Task Analyze_WithDnatRule_NoMatchOpposite_OnlyCoversSpecifiedNetwork()
    {
        // Arrange - DNAT rule without Match Opposite: applies only to net1
        var networks = CreateDhcpNetworks(
            ("net1", "LAN", "192.168.1.0/24"),
            ("net2", "IoT", "192.168.2.0/24"));

        var natRules = JsonDocument.Parse(@"[
            {
                ""_id"": ""rule1"",
                ""type"": ""DNAT"",
                ""enabled"": true,
                ""protocol"": ""udp"",
                ""ip_address"": ""192.168.1.1"",
                ""destination_filter"": { ""port"": ""53"" },
                ""source_filter"": {
                    ""filter_type"": ""NETWORK_CONF"",
                    ""network_conf_id"": ""net1"",
                    ""match_opposite"": false
                }
            }
        ]").RootElement;

        // Act
        var result = await _analyzer.AnalyzeAsync(null, null, null, networks, null, null, natRules);

        // Assert - Only LAN is covered
        result.HasDnatDnsRules.Should().BeTrue();
        result.DnatProvidesFullCoverage.Should().BeFalse();
        result.DnatCoveredNetworks.Should().Contain("LAN");
        result.DnatUncoveredNetworks.Should().Contain("IoT");
    }

    [Fact]
    public async Task Analyze_WithDns53PartialCoverage_DnatFullCoverage_SuppressesPartialIssue()
    {
        // Arrange - DNS53 firewall rule covers only LAN, but DNAT covers all
        var networks = CreateDhcpNetworks(
            ("net1", "LAN", "192.168.1.0/24"),
            ("net2", "IoT", "192.168.2.0/24"));

        var firewall = JsonDocument.Parse(@"[
            {
                ""name"": ""Block DNS for LAN Only"",
                ""enabled"": true,
                ""action"": ""drop"",
                ""protocol"": ""udp"",
                ""source"": {
                    ""matching_target"": ""NETWORK"",
                    ""network_ids"": [""net1""]
                },
                ""destination"": { ""port"": ""53"" }
            }
        ]").RootElement;

        var natRules = CreateDnatNatRules(("net1", "192.168.1.1"), ("net2", "192.168.1.1"));

        var settings = JsonDocument.Parse(@"[
            {
                ""key"": ""doh"",
                ""state"": ""auto"",
                ""server_names"": [""NextDNS-test""]
            }
        ]").RootElement;

        // Act
        var result = await _analyzer.AnalyzeAsync(settings, ParseFirewallRules(firewall), null, networks, null, null, natRules);

        // Assert - DNAT provides full coverage, so no partial coverage issue
        result.HasDns53BlockRule.Should().BeTrue();
        result.Dns53ProvidesFullCoverage.Should().BeFalse(); // Firewall alone is partial
        result.DnatProvidesFullCoverage.Should().BeTrue(); // But DNAT covers all
        result.Issues.Should().NotContain(i => i.Type == IssueTypes.Dns53PartialCoverage); // Suppressed by DNAT
        result.Issues.Should().NotContain(i => i.Type == IssueTypes.DnsNo53Block); // Firewall handles part of it
    }

    [Fact]
    public async Task Analyze_DnatWithIpRange_MatchesMultipleThirdPartyDnsServers()
    {
        // Arrange - Two third-party DNS servers (Pi-hole style redundancy)
        // DNAT rule uses IP range format that should match both
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "LAN",
                VlanId = 1,
                Subnet = "192.168.1.0/24",
                DhcpEnabled = true,
                Gateway = "192.168.1.1",
                DnsServers = new List<string> { "192.168.1.253", "192.168.1.254" } // Two DNS servers
            }
        };
        var switches = new List<SwitchInfo>
        {
            new SwitchInfo { Name = "Gateway", IsGateway = true }
        };
        // DNAT rule with IP range that matches both DNS servers
        var natRules = JsonDocument.Parse("""
        [
            {
                "_id": "rule1",
                "type": "DNAT",
                "enabled": true,
                "protocol": "udp",
                "ip_address": "192.168.1.253-192.168.1.254",
                "destination_filter": { "filter_type": "ADDRESS_AND_PORT", "port": "53" },
                "source_filter": { "filter_type": "NETWORK_CONF", "network_conf_id": "net1" }
            }
        ]
        """).RootElement;

        // Act
        var result = await _analyzer.AnalyzeAsync(
            settingsData: null,
            firewallRules: null,
            switches: switches,
            networks: networks,
            deviceData: null,
            customDnsManagementPort: null,
            natRulesData: natRules);

        // Assert - IP range should match both DNS servers, no invalid destination issue
        result.HasThirdPartyDns.Should().BeTrue();
        result.HasDnatDnsRules.Should().BeTrue();
        result.DnatProvidesFullCoverage.Should().BeTrue();
        result.DnatRedirectTargetIsValid.Should().BeTrue();
        result.Issues.Should().NotContain(i => i.Type == IssueTypes.DnsDnatWrongDestination);
    }

    [Fact]
    public async Task Analyze_DnatWithIpRange_OrderIndependent_DhcpOrderDiffersFromRange()
    {
        // Arrange - DHCP DNS order (254, 253) differs from DNAT range (253-254)
        // DNAT ranges must be start-end where start <= end, so order can't match DHCP
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "LAN",
                VlanId = 1,
                Purpose = NetworkPurpose.Home, // Non-Corporate so third-party DNS is site-wide
                Subnet = "192.168.1.0/24",
                DhcpEnabled = true,
                Gateway = "192.168.1.1",
                DnsServers = new List<string> { "192.168.1.254", "192.168.1.253" } // Reversed from DNAT range
            }
        };
        var switches = new List<SwitchInfo>
        {
            new SwitchInfo { Name = "Gateway", IsGateway = true }
        };
        // DNAT range is always low-high, can't match DHCP order of high-low
        var natRules = JsonDocument.Parse("""
        [
            {
                "_id": "rule1",
                "type": "DNAT",
                "enabled": true,
                "protocol": "udp",
                "ip_address": "192.168.1.253-192.168.1.254",
                "destination_filter": { "filter_type": "ADDRESS_AND_PORT", "port": "53" },
                "source_filter": { "filter_type": "NETWORK_CONF", "network_conf_id": "net1" }
            }
        ]
        """).RootElement;

        // Act
        var result = await _analyzer.AnalyzeAsync(
            settingsData: null,
            firewallRules: null,
            switches: switches,
            networks: networks,
            deviceData: null,
            customDnsManagementPort: null,
            natRulesData: natRules);

        // Assert - Order doesn't matter, only that the same IPs are present
        result.HasThirdPartyDns.Should().BeTrue();
        result.ThirdPartyDnsServers.Should().HaveCount(2); // Both DNS servers detected
        result.ThirdPartyDnsServers.Select(t => t.DnsServerIp).Should().Contain("192.168.1.254");
        result.ThirdPartyDnsServers.Select(t => t.DnsServerIp).Should().Contain("192.168.1.253");
        result.ExpectedDnatDestinations.Should().HaveCount(2); // Both in expected destinations
        result.ExpectedDnatDestinations.Should().Contain("192.168.1.254");
        result.ExpectedDnatDestinations.Should().Contain("192.168.1.253");
        result.DnatRedirectTargetIsValid.Should().BeTrue();
        result.Issues.Should().NotContain(i => i.Type == IssueTypes.DnsDnatWrongDestination);
    }

    [Fact]
    public async Task Analyze_DnatWithIpRange_GatewayAndThirdPartyDns_BothValid()
    {
        // Arrange - DHCP DNS 1 = gateway, DNS 2 = third-party (Pi-hole)
        // DNAT redirects to range that includes both - should be valid
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "Guest",
                VlanId = 210,
                Purpose = NetworkPurpose.Guest, // Non-Corporate so third-party DNS is site-wide
                Subnet = "192.168.210.0/24",
                DhcpEnabled = true,
                Gateway = "192.168.210.1",
                DnsServers = new List<string> { "192.168.210.1", "192.168.210.2" } // DNS 1 = gateway, DNS 2 = Pi-hole
            }
        };
        var switches = new List<SwitchInfo>
        {
            new SwitchInfo { Name = "Gateway", IsGateway = true }
        };
        // DNAT redirects to range including both gateway and Pi-hole
        var natRules = JsonDocument.Parse("""
        [
            {
                "_id": "rule1",
                "description": "DNS DNAT VLAN 210",
                "type": "DNAT",
                "enabled": true,
                "protocol": "udp",
                "ip_address": "192.168.210.1-192.168.210.2",
                "destination_filter": {
                    "address": "192.168.210.1-192.168.210.2",
                    "filter_type": "ADDRESS_AND_PORT",
                    "invert_address": true,
                    "port": "53"
                },
                "in_interface": "net1",
                "source_filter": { "filter_type": "NONE" }
            }
        ]
        """).RootElement;

        // Act
        var result = await _analyzer.AnalyzeAsync(
            settingsData: null,
            firewallRules: null,
            switches: switches,
            networks: networks,
            deviceData: null,
            customDnsManagementPort: null,
            natRulesData: natRules);

        // Assert - Both gateway (DNS 1) and third-party (DNS 2) should be valid destinations
        result.HasThirdPartyDns.Should().BeTrue();
        result.ThirdPartyDnsServers.Should().ContainSingle(); // Only DNS 2 is "third-party"
        result.ThirdPartyDnsServers[0].DnsServerIp.Should().Be("192.168.210.2");
        result.ExpectedDnatDestinations.Should().HaveCount(2); // Both are valid destinations
        result.ExpectedDnatDestinations.Should().Contain("192.168.210.1"); // Gateway as DNS
        result.ExpectedDnatDestinations.Should().Contain("192.168.210.2"); // Third-party
        result.DnatRedirectTargetIsValid.Should().BeTrue();
        result.Issues.Should().NotContain(i => i.Type == IssueTypes.DnsDnatWrongDestination);
    }

    [Fact]
    public async Task Analyze_DnatWithIpRange_ThirdPartyFirstGatewaySecond_BothValid()
    {
        // Arrange - DHCP DNS 1 = third-party (Pi-hole), DNS 2 = gateway (reverse order)
        // DNAT redirects to range that includes both - should be valid
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "Guest",
                VlanId = 210,
                Purpose = NetworkPurpose.Guest, // Non-Corporate so third-party DNS is site-wide
                Subnet = "192.168.210.0/24",
                DhcpEnabled = true,
                Gateway = "192.168.210.1",
                DnsServers = new List<string> { "192.168.210.2", "192.168.210.1" } // DNS 1 = Pi-hole, DNS 2 = gateway
            }
        };
        var switches = new List<SwitchInfo>
        {
            new SwitchInfo { Name = "Gateway", IsGateway = true }
        };
        // DNAT redirects to range including both gateway and Pi-hole
        var natRules = JsonDocument.Parse("""
        [
            {
                "_id": "rule1",
                "description": "DNS DNAT VLAN 210",
                "type": "DNAT",
                "enabled": true,
                "protocol": "udp",
                "ip_address": "192.168.210.1-192.168.210.2",
                "destination_filter": {
                    "address": "192.168.210.1-192.168.210.2",
                    "filter_type": "ADDRESS_AND_PORT",
                    "invert_address": true,
                    "port": "53"
                },
                "in_interface": "net1",
                "source_filter": { "filter_type": "NONE" }
            }
        ]
        """).RootElement;

        // Act
        var result = await _analyzer.AnalyzeAsync(
            settingsData: null,
            firewallRules: null,
            switches: switches,
            networks: networks,
            deviceData: null,
            customDnsManagementPort: null,
            natRulesData: natRules);

        // Assert - Both gateway (DNS 2) and third-party (DNS 1) should be valid destinations
        result.HasThirdPartyDns.Should().BeTrue();
        result.ThirdPartyDnsServers.Should().ContainSingle(); // Only 192.168.210.2 is "third-party"
        result.ThirdPartyDnsServers[0].DnsServerIp.Should().Be("192.168.210.2");
        result.ExpectedDnatDestinations.Should().HaveCount(2); // Both are valid destinations
        result.ExpectedDnatDestinations.Should().Contain("192.168.210.1"); // Gateway as DNS
        result.ExpectedDnatDestinations.Should().Contain("192.168.210.2"); // Third-party
        result.DnatRedirectTargetIsValid.Should().BeTrue();
        result.Issues.Should().NotContain(i => i.Type == IssueTypes.DnsDnatWrongDestination);
    }

    [Fact]
    public async Task Analyze_DnatWithIpRange_PartialMatch_GeneratesInvalidDestinationIssue()
    {
        // Arrange - One third-party DNS server but DNAT range includes extra IPs
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "LAN",
                VlanId = 1,
                Purpose = NetworkPurpose.Home, // Non-Corporate so third-party DNS is site-wide
                Subnet = "192.168.1.0/24",
                DhcpEnabled = true,
                Gateway = "192.168.1.1",
                DnsServers = new List<string> { "192.168.1.253" } // Only one DNS server
            }
        };
        var switches = new List<SwitchInfo>
        {
            new SwitchInfo { Name = "Gateway", IsGateway = true }
        };
        // DNAT rule with range that includes an IP not in the DNS servers list
        var natRules = JsonDocument.Parse("""
        [
            {
                "_id": "rule1",
                "description": "DNS Redirect",
                "type": "DNAT",
                "enabled": true,
                "protocol": "udp",
                "ip_address": "192.168.1.253-192.168.1.254",
                "destination_filter": { "filter_type": "ADDRESS_AND_PORT", "port": "53" },
                "source_filter": { "filter_type": "NETWORK_CONF", "network_conf_id": "net1" }
            }
        ]
        """).RootElement;

        // Act
        var result = await _analyzer.AnalyzeAsync(
            settingsData: null,
            firewallRules: null,
            switches: switches,
            networks: networks,
            deviceData: null,
            customDnsManagementPort: null,
            natRulesData: natRules);

        // Assert - Range includes 192.168.1.254 which is not a valid DNS server
        result.HasThirdPartyDns.Should().BeTrue();
        result.HasDnatDnsRules.Should().BeTrue();
        result.DnatRedirectTargetIsValid.Should().BeFalse();
        result.Issues.Should().Contain(i => i.Type == IssueTypes.DnsDnatWrongDestination);
    }

    [Fact]
    public async Task Analyze_DnatWithCorporateOnlyThirdPartyDns_ValidatesAgainstGateway()
    {
        // Arrange - Third-party DNS ONLY on Corporate network
        // DNAT should validate against gateway (not third-party DNS) since it's not site-wide
        var settings = JsonDocument.Parse(@"[
            {
                ""key"": ""doh"",
                ""state"": ""auto"",
                ""server_names"": [""NextDNS-test""]
            }
        ]").RootElement;
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "corp",
                Name = "Corporate",
                VlanId = 10,
                Purpose = NetworkPurpose.Corporate, // Corporate with third-party DNS
                DhcpEnabled = true,
                Gateway = "192.168.10.1",
                DnsServers = new List<string> { "192.168.10.5" } // Internal corporate DNS
            },
            new NetworkInfo
            {
                Id = "home",
                Name = "Home",
                VlanId = 1, // VLAN 1 is the default network in UniFi
                Purpose = NetworkPurpose.Home,
                DhcpEnabled = true,
                Gateway = "192.168.1.1"
                // No third-party DNS - uses gateway
            }
        };
        var switches = new List<SwitchInfo>
        {
            new SwitchInfo { Name = "Gateway", IsGateway = true }
        };
        // DNAT points to native gateway - should be valid since third-party DNS is not site-wide
        var natRules = JsonDocument.Parse("""
        [
            {
                "_id": "rule1",
                "type": "DNAT",
                "enabled": true,
                "protocol": "udp",
                "ip_address": "192.168.1.1",
                "destination_filter": { "filter_type": "ADDRESS_AND_PORT", "port": "53" },
                "source_filter": { "filter_type": "NETWORK_CONF", "network_conf_id": "home" }
            }
        ]
        """).RootElement;

        // Act
        var result = await _analyzer.AnalyzeAsync(
            settingsData: settings,
            firewallRules: null,
            switches: switches,
            networks: networks,
            deviceData: null,
            customDnsManagementPort: null,
            natRulesData: natRules);

        // Assert - Third-party DNS detected but NOT site-wide (only on Corporate)
        result.HasThirdPartyDns.Should().BeTrue();
        result.IsSiteWideThirdPartyDns.Should().BeFalse();
        // DNAT validates against gateway since there's no site-wide third-party DNS
        result.DnatRedirectTargetIsValid.Should().BeTrue();
        result.Issues.Should().NotContain(i => i.Type == IssueTypes.DnsDnatWrongDestination);
    }

    #endregion

    #region IP Range Parsing Tests

    [Fact]
    public void ParseIpOrRange_SingleIp_ReturnsSingleIp()
    {
        // Act
        var result = DnsSecurityAnalyzer.ParseIpOrRange("192.168.1.1");

        // Assert
        result.Should().ContainSingle().Which.Should().Be("192.168.1.1");
    }

    [Fact]
    public void ParseIpOrRange_NullOrEmpty_ReturnsEmptyList()
    {
        // Act & Assert
        DnsSecurityAnalyzer.ParseIpOrRange(null).Should().BeEmpty();
        DnsSecurityAnalyzer.ParseIpOrRange("").Should().BeEmpty();
    }

    [Fact]
    public void ParseIpOrRange_ValidRange_ReturnsAllIps()
    {
        // Act
        var result = DnsSecurityAnalyzer.ParseIpOrRange("172.16.1.253-172.16.1.254");

        // Assert
        result.Should().HaveCount(2);
        result.Should().Contain("172.16.1.253");
        result.Should().Contain("172.16.1.254");
    }

    [Fact]
    public void ParseIpOrRange_ThreeIpRange_ReturnsAllIps()
    {
        // Act
        var result = DnsSecurityAnalyzer.ParseIpOrRange("192.168.1.10-192.168.1.12");

        // Assert
        result.Should().HaveCount(3);
        result.Should().ContainInOrder("192.168.1.10", "192.168.1.11", "192.168.1.12");
    }

    [Fact]
    public void ParseIpOrRange_CrossSubnetRange_ReturnsSingleValue()
    {
        // Ranges spanning subnets are not supported, treated as single value
        // Act
        var result = DnsSecurityAnalyzer.ParseIpOrRange("192.168.1.1-192.168.2.1");

        // Assert - treated as a single non-parseable value
        result.Should().ContainSingle().Which.Should().Be("192.168.1.1-192.168.2.1");
    }

    [Fact]
    public void ParseIpOrRange_InvalidIpFormat_ReturnsSingleValue()
    {
        // Act
        var result = DnsSecurityAnalyzer.ParseIpOrRange("not-an-ip");

        // Assert - treated as a single value
        result.Should().ContainSingle().Which.Should().Be("not-an-ip");
    }

    [Fact]
    public void ParseIpOrRange_ReversedRange_ReturnsSingleValue()
    {
        // Start > End is invalid
        // Act
        var result = DnsSecurityAnalyzer.ParseIpOrRange("192.168.1.10-192.168.1.5");

        // Assert - treated as a single value
        result.Should().ContainSingle().Which.Should().Be("192.168.1.10-192.168.1.5");
    }

    [Theory]
    [InlineData("192.168.1.1", true)]  // Single IP in set
    [InlineData("192.168.1.2", true)]  // Single IP in set
    [InlineData("192.168.1.3", false)] // Single IP not in set
    [InlineData("192.168.1.1-192.168.1.2", true)]  // Range, all in set
    [InlineData("192.168.1.1-192.168.1.3", false)] // Range, some not in set
    public void IsValidRedirectTarget_VariousInputs_ReturnsExpected(string redirectIp, bool expected)
    {
        // Arrange
        var validDestinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "192.168.1.1",
            "192.168.1.2"
        };

        // Act
        var result = DnsSecurityAnalyzer.IsValidRedirectTarget(redirectIp, validDestinations);

        // Assert
        result.Should().Be(expected);
    }

    [Fact]
    public void IsValidRedirectTarget_NullOrEmpty_ReturnsTrue()
    {
        // Arrange
        var validDestinations = new HashSet<string> { "192.168.1.1" };

        // Act & Assert
        DnsSecurityAnalyzer.IsValidRedirectTarget(null, validDestinations).Should().BeTrue();
        DnsSecurityAnalyzer.IsValidRedirectTarget("", validDestinations).Should().BeTrue();
    }

    [Fact]
    public void IsValidRedirectTarget_CaseInsensitive_ReturnsTrue()
    {
        // Arrange - IP addresses aren't typically case-sensitive but the comparison should handle it
        var validDestinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "192.168.1.1" };

        // Act & Assert
        DnsSecurityAnalyzer.IsValidRedirectTarget("192.168.1.1", validDestinations).Should().BeTrue();
    }

    #endregion

    #region DNAT Destination Filter Validation Tests

    [Fact]
    public void DnatRuleInfo_HasRestrictedDestination_NoAddress_ReturnsFalse()
    {
        // Arrange - No destination address = Any
        var rule = new DnatRuleInfo
        {
            Id = "test",
            CoverageType = "network",
            DestinationAddress = null,
            InvertDestinationAddress = false
        };

        // Assert
        rule.HasRestrictedDestination.Should().BeFalse();
    }

    [Fact]
    public void DnatRuleInfo_HasRestrictedDestination_EmptyAddress_ReturnsFalse()
    {
        // Arrange
        var rule = new DnatRuleInfo
        {
            Id = "test",
            CoverageType = "network",
            DestinationAddress = "",
            InvertDestinationAddress = false
        };

        // Assert
        rule.HasRestrictedDestination.Should().BeFalse();
    }

    [Fact]
    public void DnatRuleInfo_HasRestrictedDestination_AddressWithInvert_ReturnsFalse()
    {
        // Arrange - Address with invert = matches traffic NOT going to that address
        var rule = new DnatRuleInfo
        {
            Id = "test",
            CoverageType = "network",
            DestinationAddress = "192.168.1.1",
            InvertDestinationAddress = true
        };

        // Assert - This is valid for DNS redirection
        rule.HasRestrictedDestination.Should().BeFalse();
    }

    [Fact]
    public void DnatRuleInfo_HasRestrictedDestination_AddressRangeWithInvert_ReturnsFalse()
    {
        // Arrange
        var rule = new DnatRuleInfo
        {
            Id = "test",
            CoverageType = "network",
            DestinationAddress = "192.168.1.1-192.168.1.2",
            InvertDestinationAddress = true
        };

        // Assert
        rule.HasRestrictedDestination.Should().BeFalse();
    }

    [Fact]
    public void DnatRuleInfo_HasRestrictedDestination_AddressWithoutInvert_ReturnsTrue()
    {
        // Arrange - Specific address without invert = only catches traffic to that IP
        var rule = new DnatRuleInfo
        {
            Id = "test",
            CoverageType = "network",
            DestinationAddress = "8.8.8.8",
            InvertDestinationAddress = false
        };

        // Assert - This is restricted
        rule.HasRestrictedDestination.Should().BeTrue();
    }

    [Fact]
    public void DnatRuleInfo_HasRestrictedDestination_AddressRangeWithoutInvert_ReturnsTrue()
    {
        // Arrange
        var rule = new DnatRuleInfo
        {
            Id = "test",
            CoverageType = "network",
            DestinationAddress = "8.8.8.8-8.8.4.4",
            InvertDestinationAddress = false
        };

        // Assert
        rule.HasRestrictedDestination.Should().BeTrue();
    }

    [Fact]
    public async Task Analyze_DnatWithNoDestinationAddress_NoRestrictedDestinationIssue()
    {
        // Arrange - No destination address = Any (valid)
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "LAN",
                VlanId = 1,
                Subnet = "192.168.1.0/24",
                DhcpEnabled = true,
                Gateway = "192.168.1.1",
                DnsServers = new List<string> { "192.168.1.5" }
            }
        };
        var switches = new List<SwitchInfo>
        {
            new SwitchInfo { Name = "Gateway", IsGateway = true }
        };
        var natRules = JsonDocument.Parse("""
        [
            {
                "_id": "rule1",
                "type": "DNAT",
                "enabled": true,
                "protocol": "udp",
                "ip_address": "192.168.1.5",
                "destination_filter": { "filter_type": "ADDRESS_AND_PORT", "port": "53" },
                "source_filter": { "filter_type": "NETWORK_CONF", "network_conf_id": "net1" }
            }
        ]
        """).RootElement;

        // Act
        var result = await _analyzer.AnalyzeAsync(
            settingsData: null,
            firewallRules: null,
            switches: switches,
            networks: networks,
            deviceData: null,
            customDnsManagementPort: null,
            natRulesData: natRules);

        // Assert
        result.DnatDestinationFilterIsValid.Should().BeTrue();
        result.Issues.Should().NotContain(i => i.Type == IssueTypes.DnsDnatRestrictedDestination);
    }

    [Fact]
    public async Task Analyze_DnatWithInvertedDestination_NoRestrictedDestinationIssue()
    {
        // Arrange - Destination with invert_address: true (valid)
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "LAN",
                VlanId = 1,
                Subnet = "192.168.210.0/24",
                DhcpEnabled = true,
                Gateway = "192.168.210.1",
                DnsServers = new List<string> { "192.168.210.1" }
            }
        };
        var switches = new List<SwitchInfo>
        {
            new SwitchInfo { Name = "Gateway", IsGateway = true }
        };
        // This matches the user's example - invert_address: true
        var natRules = JsonDocument.Parse("""
        [
            {
                "_id": "rule1",
                "description": "DNS DNAT VLAN 210",
                "type": "DNAT",
                "enabled": true,
                "protocol": "udp",
                "ip_address": "192.168.210.1",
                "destination_filter": {
                    "address": "192.168.210.1",
                    "filter_type": "ADDRESS_AND_PORT",
                    "invert_address": true,
                    "port": "53"
                },
                "in_interface": "net1",
                "source_filter": { "filter_type": "NONE" }
            }
        ]
        """).RootElement;

        // Act
        var result = await _analyzer.AnalyzeAsync(
            settingsData: null,
            firewallRules: null,
            switches: switches,
            networks: networks,
            deviceData: null,
            customDnsManagementPort: null,
            natRulesData: natRules);

        // Assert
        result.DnatDestinationFilterIsValid.Should().BeTrue();
        result.Issues.Should().NotContain(i => i.Type == IssueTypes.DnsDnatRestrictedDestination);
    }

    [Fact]
    public async Task Analyze_DnatWithInvertedDestinationRange_NoRestrictedDestinationIssue()
    {
        // Arrange - Destination range with invert_address: true (valid)
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "LAN",
                VlanId = 1,
                Subnet = "192.168.210.0/24",
                DhcpEnabled = true,
                Gateway = "192.168.210.1",
                DnsServers = new List<string> { "192.168.210.1", "192.168.210.2" }
            }
        };
        var switches = new List<SwitchInfo>
        {
            new SwitchInfo { Name = "Gateway", IsGateway = true }
        };
        var natRules = JsonDocument.Parse("""
        [
            {
                "_id": "rule1",
                "description": "DNS DNAT VLAN 210",
                "type": "DNAT",
                "enabled": true,
                "protocol": "udp",
                "ip_address": "192.168.210.1-192.168.210.2",
                "destination_filter": {
                    "address": "192.168.210.1-192.168.210.2",
                    "filter_type": "ADDRESS_AND_PORT",
                    "invert_address": true,
                    "port": "53"
                },
                "in_interface": "net1",
                "source_filter": { "filter_type": "NONE" }
            }
        ]
        """).RootElement;

        // Act
        var result = await _analyzer.AnalyzeAsync(
            settingsData: null,
            firewallRules: null,
            switches: switches,
            networks: networks,
            deviceData: null,
            customDnsManagementPort: null,
            natRulesData: natRules);

        // Assert
        result.DnatDestinationFilterIsValid.Should().BeTrue();
        result.Issues.Should().NotContain(i => i.Type == IssueTypes.DnsDnatRestrictedDestination);
    }

    [Fact]
    public async Task Analyze_DnatWithSpecificDestination_GeneratesRestrictedDestinationIssue()
    {
        // Arrange - Specific destination without invert (invalid - only catches traffic to 8.8.8.8)
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "LAN",
                VlanId = 1,
                Subnet = "192.168.1.0/24",
                DhcpEnabled = true,
                Gateway = "192.168.1.1",
                DnsServers = new List<string> { "192.168.1.5" }
            }
        };
        var switches = new List<SwitchInfo>
        {
            new SwitchInfo { Name = "Gateway", IsGateway = true }
        };
        var natRules = JsonDocument.Parse("""
        [
            {
                "_id": "rule1",
                "description": "DNS Redirect Google",
                "type": "DNAT",
                "enabled": true,
                "protocol": "udp",
                "ip_address": "192.168.1.5",
                "destination_filter": {
                    "address": "8.8.8.8",
                    "filter_type": "ADDRESS_AND_PORT",
                    "invert_address": false,
                    "port": "53"
                },
                "source_filter": { "filter_type": "NETWORK_CONF", "network_conf_id": "net1" }
            }
        ]
        """).RootElement;

        // Act
        var result = await _analyzer.AnalyzeAsync(
            settingsData: null,
            firewallRules: null,
            switches: switches,
            networks: networks,
            deviceData: null,
            customDnsManagementPort: null,
            natRulesData: natRules);

        // Assert
        result.DnatDestinationFilterIsValid.Should().BeFalse();
        result.RestrictedDestinationRules.Should().Contain(r => r.Contains("8.8.8.8"));
        result.Issues.Should().Contain(i => i.Type == IssueTypes.DnsDnatRestrictedDestination);
    }

    [Fact]
    public async Task Analyze_DnatWithSpecificDestinationNoInvertField_GeneratesRestrictedDestinationIssue()
    {
        // Arrange - Destination address without invert_address field (defaults to false)
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "LAN",
                VlanId = 1,
                Subnet = "192.168.1.0/24",
                DhcpEnabled = true,
                Gateway = "192.168.1.1",
                DnsServers = new List<string> { "192.168.1.5" }
            }
        };
        var switches = new List<SwitchInfo>
        {
            new SwitchInfo { Name = "Gateway", IsGateway = true }
        };
        var natRules = JsonDocument.Parse("""
        [
            {
                "_id": "rule1",
                "description": "DNS Redirect",
                "type": "DNAT",
                "enabled": true,
                "protocol": "udp",
                "ip_address": "192.168.1.5",
                "destination_filter": {
                    "address": "1.1.1.1",
                    "filter_type": "ADDRESS_AND_PORT",
                    "port": "53"
                },
                "source_filter": { "filter_type": "NETWORK_CONF", "network_conf_id": "net1" }
            }
        ]
        """).RootElement;

        // Act
        var result = await _analyzer.AnalyzeAsync(
            settingsData: null,
            firewallRules: null,
            switches: switches,
            networks: networks,
            deviceData: null,
            customDnsManagementPort: null,
            natRulesData: natRules);

        // Assert - invert_address defaults to false, so this is restricted
        result.DnatDestinationFilterIsValid.Should().BeFalse();
        result.Issues.Should().Contain(i => i.Type == IssueTypes.DnsDnatRestrictedDestination);
    }

    [Fact]
    public async Task Analyze_DnatWithSpecificDestinationRange_GeneratesRestrictedDestinationIssue()
    {
        // Arrange - Destination range without invert (invalid)
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "LAN",
                VlanId = 1,
                Subnet = "192.168.1.0/24",
                DhcpEnabled = true,
                Gateway = "192.168.1.1",
                DnsServers = new List<string> { "192.168.1.5" }
            }
        };
        var switches = new List<SwitchInfo>
        {
            new SwitchInfo { Name = "Gateway", IsGateway = true }
        };
        var natRules = JsonDocument.Parse("""
        [
            {
                "_id": "rule1",
                "description": "DNS Redirect Range",
                "type": "DNAT",
                "enabled": true,
                "protocol": "udp",
                "ip_address": "192.168.1.5",
                "destination_filter": {
                    "address": "8.8.8.8-8.8.4.4",
                    "filter_type": "ADDRESS_AND_PORT",
                    "invert_address": false,
                    "port": "53"
                },
                "source_filter": { "filter_type": "NETWORK_CONF", "network_conf_id": "net1" }
            }
        ]
        """).RootElement;

        // Act
        var result = await _analyzer.AnalyzeAsync(
            settingsData: null,
            firewallRules: null,
            switches: switches,
            networks: networks,
            deviceData: null,
            customDnsManagementPort: null,
            natRulesData: natRules);

        // Assert
        result.DnatDestinationFilterIsValid.Should().BeFalse();
        result.Issues.Should().Contain(i => i.Type == IssueTypes.DnsDnatRestrictedDestination);
    }

    #endregion

    #region DoH Custom Servers (SDNS Stamp) Tests

    [Fact]
    public async Task Analyze_WithCustomSdnsStamp_ParsesProviderInfo()
    {
        // Arrange - Custom DoH with SDNS stamp
        var settings = JsonDocument.Parse("""
        [
            {
                "key": "doh",
                "state": "custom",
                "custom_servers": [
                    {
                        "server_name": "CustomDNS",
                        "sdns_stamp": "sdns://AgcAAAAAAAAACjE5Mi4wLjIuMQAQL2Rucy1xdWVyeQ",
                        "enabled": true
                    }
                ]
            }
        ]
        """).RootElement;

        // Act
        var result = await _analyzer.AnalyzeAsync(settings, null);

        // Assert
        result.DohConfigured.Should().BeTrue();
        result.DohState.Should().Be("custom");
    }

    [Fact]
    public async Task Analyze_WithDisabledCustomServer_NotConfigured()
    {
        // Arrange - Custom DoH with disabled server only
        var settings = JsonDocument.Parse("""
        [
            {
                "key": "doh",
                "state": "custom",
                "custom_servers": [
                    {
                        "server_name": "DisabledDNS",
                        "sdns_stamp": "sdns://AgcAAAAAAAAACjE5Mi4wLjIuMQAQL2Rucy1xdWVyeQ",
                        "enabled": false
                    }
                ]
            }
        ]
        """).RootElement;

        // Act
        var result = await _analyzer.AnalyzeAsync(settings, null);

        // Assert - No enabled servers means DoH not effectively configured
        result.DohConfigured.Should().BeFalse();
        result.DohState.Should().Be("custom");
    }

    [Fact]
    public async Task Analyze_WithMultipleCustomServers_ParsesAll()
    {
        // Arrange - Multiple custom DoH servers
        var settings = JsonDocument.Parse("""
        [
            {
                "key": "doh",
                "state": "custom",
                "custom_servers": [
                    {
                        "server_name": "Primary",
                        "sdns_stamp": "sdns://AgcAAAAAAAAACjE5Mi4wLjIuMQAQL2Rucy1xdWVyeQ",
                        "enabled": true
                    },
                    {
                        "server_name": "Secondary",
                        "sdns_stamp": "sdns://AgcAAAAAAAAADDkuOS45LjkAEC9kbnMtcXVlcnk",
                        "enabled": true
                    }
                ]
            }
        ]
        """).RootElement;

        // Act
        var result = await _analyzer.AnalyzeAsync(settings, null);

        // Assert
        result.DohConfigured.Should().BeTrue();
    }

    #endregion

    #region WAN DNS Mode Tests

    [Fact]
    public async Task Analyze_WithWanDnsAutoMode_DetectsIspDns()
    {
        // Arrange - WAN DNS in auto mode (uses ISP DNS)
        var settings = JsonDocument.Parse("""
        [
            {
                "key": "dns",
                "mode": "auto"
            }
        ]
        """).RootElement;

        // Act
        var result = await _analyzer.AnalyzeAsync(settings, null);

        // Assert
        result.UsingIspDns.Should().BeTrue();
    }

    [Fact]
    public async Task Analyze_WithWanDnsDhcpMode_DetectsIspDns()
    {
        // Arrange - WAN DNS in DHCP mode (uses ISP DNS)
        var settings = JsonDocument.Parse("""
        [
            {
                "key": "dns",
                "mode": "dhcp"
            }
        ]
        """).RootElement;

        // Act
        var result = await _analyzer.AnalyzeAsync(settings, null);

        // Assert
        result.UsingIspDns.Should().BeTrue();
    }

    [Fact]
    public async Task Analyze_WithWanDnsStaticMode_NotIspDns()
    {
        // Arrange - WAN DNS in static mode
        var settings = JsonDocument.Parse("""
        [
            {
                "key": "dns",
                "mode": "static",
                "dns_servers": ["1.1.1.1", "8.8.8.8"]
            }
        ]
        """).RootElement;

        // Act
        var result = await _analyzer.AnalyzeAsync(settings, null);

        // Assert
        result.UsingIspDns.Should().BeFalse();
        result.WanDnsServers.Should().Contain("1.1.1.1");
        result.WanDnsServers.Should().Contain("8.8.8.8");
    }

    #endregion

    #region WAN DNS Extraction from Device Port Table Tests

    [Fact]
    public async Task Analyze_WithDevicePortTable_ExtractsWanDns()
    {
        // Arrange - Gateway device with WAN port DNS configuration
        var deviceData = JsonDocument.Parse("""
        [
            {
                "type": "ugw",
                "name": "UDM Pro",
                "port_table": [
                    {
                        "name": "WAN",
                        "network_name": "wan",
                        "media": "GE",
                        "up": true,
                        "ip": "203.0.113.50",
                        "dns": ["1.1.1.1", "8.8.8.8"]
                    }
                ]
            }
        ]
        """).RootElement;

        // Act
        var result = await _analyzer.AnalyzeAsync(null, null, null, null, deviceData);

        // Assert
        result.WanDnsServers.Should().Contain("1.1.1.1");
        result.WanDnsServers.Should().Contain("8.8.8.8");
        result.WanInterfaces.Should().HaveCount(1);
        result.WanInterfaces[0].InterfaceName.Should().Be("wan");
        result.WanInterfaces[0].IsUp.Should().BeTrue();
    }

    [Fact]
    public async Task Analyze_WithMultipleWanInterfaces_ExtractsAllDns()
    {
        // Arrange - Gateway with dual WAN
        var deviceData = JsonDocument.Parse("""
        [
            {
                "type": "ugw",
                "name": "UDM Pro",
                "port_table": [
                    {
                        "name": "WAN",
                        "network_name": "wan",
                        "up": true,
                        "dns": ["1.1.1.1"]
                    },
                    {
                        "name": "WAN2",
                        "network_name": "wan2",
                        "up": true,
                        "dns": ["8.8.8.8"]
                    }
                ]
            }
        ]
        """).RootElement;

        // Act
        var result = await _analyzer.AnalyzeAsync(null, null, null, null, deviceData);

        // Assert
        result.WanDnsServers.Should().Contain("1.1.1.1");
        result.WanDnsServers.Should().Contain("8.8.8.8");
        result.WanInterfaces.Should().HaveCount(2);
    }

    [Fact]
    public async Task Analyze_WithWanInterfaceNoDns_SetsIspDns()
    {
        // Arrange - WAN interface without static DNS (uses DHCP/ISP)
        var deviceData = JsonDocument.Parse("""
        [
            {
                "type": "ugw",
                "name": "UDM Pro",
                "port_table": [
                    {
                        "name": "WAN",
                        "network_name": "wan",
                        "up": true,
                        "ip": "203.0.113.50"
                    }
                ]
            }
        ]
        """).RootElement;

        // Act
        var result = await _analyzer.AnalyzeAsync(null, null, null, null, deviceData);

        // Assert
        result.UsingIspDns.Should().BeTrue();
        result.WanInterfaces.Should().HaveCount(1);
        result.WanInterfaces[0].DnsServers.Should().BeEmpty();
    }

    [Fact]
    public async Task Analyze_WithNonGatewayDevice_IgnoresPortTable()
    {
        // Arrange - Switch device (not gateway) - should not extract WAN DNS
        var deviceData = JsonDocument.Parse("""
        [
            {
                "type": "usw",
                "name": "Switch",
                "port_table": [
                    {
                        "name": "Port 1",
                        "network_name": "wan",
                        "dns": ["1.1.1.1"]
                    }
                ]
            }
        ]
        """).RootElement;

        // Act
        var result = await _analyzer.AnalyzeAsync(null, null, null, null, deviceData);

        // Assert
        result.WanDnsServers.Should().BeEmpty();
        result.WanInterfaces.Should().BeEmpty();
    }

    #endregion

    #region Device DNS Configuration Tests

    [Fact]
    public async Task Analyze_WithDeviceStaticDnsPointingToGateway_NoIssue()
    {
        // Arrange - Device with static DNS pointing to gateway
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo { Id = "net1", Name = "LAN", VlanId = 1, DhcpEnabled = true, Gateway = "192.168.1.1" }
        };
        var switches = new List<SwitchInfo>
        {
            new SwitchInfo { Name = "Gateway", IsGateway = true, IpAddress = "192.168.1.1" },
            new SwitchInfo
            {
                Name = "Switch1",
                IsGateway = false,
                IpAddress = "192.168.1.10",
                ConfiguredDns1 = "192.168.1.1",
                NetworkConfigType = "static"
            }
        };

        // Act
        var result = await _analyzer.AnalyzeAsync(null, null, switches, networks);

        // Assert
        result.DeviceDnsPointsToGateway.Should().BeTrue();
        result.Issues.Should().NotContain(i => i.Type == IssueTypes.DnsDeviceMisconfigured);
    }

    [Fact]
    public async Task Analyze_WithDeviceStaticDnsNotPointingToGateway_RaisesIssue()
    {
        // Arrange - Device with static DNS pointing elsewhere
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo { Id = "net1", Name = "LAN", VlanId = 1, DhcpEnabled = true, Gateway = "192.168.1.1" }
        };
        var switches = new List<SwitchInfo>
        {
            new SwitchInfo { Name = "Gateway", IsGateway = true, IpAddress = "192.168.1.1" },
            new SwitchInfo
            {
                Name = "MisconfiguredSwitch",
                IsGateway = false,
                IpAddress = "192.168.1.10",
                ConfiguredDns1 = "8.8.8.8",
                NetworkConfigType = "static"
            }
        };

        // Act
        var result = await _analyzer.AnalyzeAsync(null, null, switches, networks);

        // Assert
        result.DeviceDnsPointsToGateway.Should().BeFalse();
        result.Issues.Should().Contain(i => i.Type == IssueTypes.DnsDeviceMisconfigured);
    }

    [Fact]
    public async Task Analyze_WithDeviceUsingDhcp_AssumesCorrect()
    {
        // Arrange - Device using DHCP (no static DNS)
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo { Id = "net1", Name = "LAN", VlanId = 1, DhcpEnabled = true, Gateway = "192.168.1.1" }
        };
        var switches = new List<SwitchInfo>
        {
            new SwitchInfo { Name = "Gateway", IsGateway = true, IpAddress = "192.168.1.1" },
            new SwitchInfo
            {
                Name = "DhcpSwitch",
                IsGateway = false,
                IpAddress = "192.168.1.10",
                ConfiguredDns1 = null,
                NetworkConfigType = "dhcp"
            }
        };

        // Act
        var result = await _analyzer.AnalyzeAsync(null, null, switches, networks);

        // Assert
        result.DhcpDeviceCount.Should().Be(1);
        result.DeviceDnsDetails.Should().Contain(d => d.UsesDhcp && d.DeviceName == "DhcpSwitch");
    }

    [Fact]
    public async Task Analyze_WithNoGatewayIp_SkipsDeviceDnsValidation()
    {
        // Arrange - Networks without gateway IP
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo { Id = "net1", Name = "LAN", VlanId = 1, DhcpEnabled = true, Gateway = null }
        };
        var switches = new List<SwitchInfo>
        {
            new SwitchInfo { Name = "Gateway", IsGateway = true },
            new SwitchInfo { Name = "Switch1", IsGateway = false, ConfiguredDns1 = "8.8.8.8" }
        };

        // Act
        var result = await _analyzer.AnalyzeAsync(null, null, switches, networks);

        // Assert - Should skip validation, not raise issue
        result.Issues.Should().NotContain(i => i.Type == IssueTypes.DnsDeviceMisconfigured);
    }

    #endregion

    #region Third-Party DNS Provider Detection Tests

    [Fact]
    public async Task Analyze_WithAdGuardHomeOnlyNetwork_DetectsProvider()
    {
        // Arrange - AdGuard Home detected (mock HTTP response)
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1", Name = "Home", VlanId = 1,
                DhcpEnabled = true, Gateway = "192.168.1.1",
                DnsServers = new List<string> { "192.168.1.5" },
                Purpose = NetworkPurpose.Home
            }
        };

        // Create analyzer with AdGuard Home mock response
        var detectorLoggerMock = new Mock<ILogger<ThirdPartyDnsDetector>>();
        var adGuardMockClient = CreateAdGuardHomeMockClient();
        var adGuardDetector = new ThirdPartyDnsDetector(detectorLoggerMock.Object, adGuardMockClient);
        var analyzer = new DnsSecurityAnalyzer(_loggerMock.Object, adGuardDetector);

        // Act
        var result = await analyzer.AnalyzeAsync(null, null, null, networks);

        // Assert
        result.HasThirdPartyDns.Should().BeTrue();
        result.ThirdPartyDnsProviderName.Should().Be("AdGuard Home");
    }

    [Fact]
    public async Task Analyze_WithUnknownThirdPartyDns_UsesGenericName()
    {
        // Arrange - Third-party DNS that's neither Pi-hole nor AdGuard Home
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1", Name = "Home", VlanId = 1,
                DhcpEnabled = true, Gateway = "192.168.1.1",
                DnsServers = new List<string> { "192.168.1.5" },
                Purpose = NetworkPurpose.Home
            }
        };

        // Default mock returns 404 - no Pi-hole/AdGuard detected

        // Act
        var result = await _analyzer.AnalyzeAsync(null, null, null, networks);

        // Assert
        result.HasThirdPartyDns.Should().BeTrue();
        result.ThirdPartyDnsProviderName.Should().Be("Third-Party LAN DNS");
    }

    private static HttpClient CreateAdGuardHomeMockClient()
    {
        var handlerMock = new Mock<HttpMessageHandler>();

        // Mock login.html with JS reference
        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.RequestUri!.AbsolutePath.Contains("login.html")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent("<html><script src=\"login.abc123.js\"></script></html>")
            });

        // Mock JS bundle with AdGuard
        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.RequestUri!.AbsolutePath.Contains(".js")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent("/* AdGuard Home bundle */")
            });

        return new HttpClient(handlerMock.Object) { Timeout = TimeSpan.FromSeconds(1) };
    }

    #endregion

    #region DNS Consistency Edge Cases

    [Fact]
    public async Task Analyze_WithNoDhcpNetworks_SkipsConsistencyCheck()
    {
        // Arrange - Networks with DHCP disabled (manual IP assignment)
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1", Name = "Static Network", VlanId = 1,
                DhcpEnabled = false, // No DHCP
                Gateway = "192.168.1.1",
                DnsServers = new List<string> { "192.168.1.5" },
                Purpose = NetworkPurpose.Home
            }
        };

        // Act
        var result = await _analyzer.AnalyzeAsync(null, null, null, networks);

        // Assert - Should not flag DNS inconsistency for non-DHCP networks
        result.Issues.Should().NotContain(i => i.Type == IssueTypes.DnsInconsistentConfig);
    }

    [Fact]
    public async Task Analyze_WithEmptyNetworksList_HandlesGracefully()
    {
        // Arrange - Empty networks list
        var networks = new List<NetworkInfo>();

        // Act
        var result = await _analyzer.AnalyzeAsync(null, null, null, networks);

        // Assert
        result.Should().NotBeNull();
        result.HasThirdPartyDns.Should().BeFalse();
    }

    #endregion

    #region Raw Device Data DNS Analysis Tests

    [Fact]
    public async Task Analyze_WithRawDeviceData_AnalyzesApDns()
    {
        // Arrange - AP with static DNS configuration
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo { Id = "net1", Name = "LAN", VlanId = 1, DhcpEnabled = true, Gateway = "192.168.1.1" }
        };
        var deviceData = JsonDocument.Parse("""
        [
            {
                "type": "ugw",
                "name": "Gateway",
                "ip": "192.168.1.1"
            },
            {
                "type": "uap",
                "name": "AccessPoint1",
                "ip": "192.168.1.20",
                "config_network": {
                    "type": "static",
                    "dns1": "192.168.1.1"
                }
            }
        ]
        """).RootElement;

        // Act
        var result = await _analyzer.AnalyzeAsync(null, null, null, networks, deviceData);

        // Assert
        result.DeviceDnsDetails.Should().Contain(d => d.DeviceName == "AccessPoint1");
    }

    [Fact]
    public async Task Analyze_WithApDnsNotPointingToGateway_RaisesIssue()
    {
        // Arrange - AP with DNS pointing to external server
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo { Id = "net1", Name = "LAN", VlanId = 1, DhcpEnabled = true, Gateway = "192.168.1.1" }
        };
        var deviceData = JsonDocument.Parse("""
        [
            {
                "type": "ugw",
                "name": "Gateway",
                "ip": "192.168.1.1"
            },
            {
                "type": "uap",
                "name": "MisconfiguredAP",
                "ip": "192.168.1.20",
                "config_network": {
                    "type": "static",
                    "dns1": "8.8.8.8"
                }
            }
        ]
        """).RootElement;

        // Act
        var result = await _analyzer.AnalyzeAsync(null, null, null, networks, deviceData);

        // Assert
        result.Issues.Should().Contain(i => i.Type == IssueTypes.DnsDeviceMisconfigured);
    }

    [Fact]
    public async Task Analyze_WithDeviceDnsPointingToNonDefaultVlanGateway_NoIssue()
    {
        // Arrange - Management VLAN is NOT VLAN 1, devices use management VLAN gateway as DNS
        // This is the scenario from GitHub issue #389
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo { Id = "net1", Name = "Default", VlanId = 1, DhcpEnabled = true, Gateway = "192.168.1.1" },
            new NetworkInfo { Id = "net2", Name = "Management", VlanId = 9, DhcpEnabled = true, Gateway = "10.9.0.1", Purpose = NetworkPurpose.Management }
        };
        var deviceData = JsonDocument.Parse("""
        [
            {
                "type": "ugw",
                "name": "Gateway",
                "ip": "10.9.0.1"
            },
            {
                "type": "usw",
                "name": "Switch1",
                "ip": "10.9.0.10",
                "config_network": {
                    "type": "static",
                    "dns1": "10.9.0.1"
                }
            },
            {
                "type": "uap",
                "name": "AP1",
                "ip": "10.9.0.20",
                "config_network": {
                    "type": "static",
                    "dns1": "10.9.0.1"
                }
            }
        ]
        """).RootElement;

        // Act
        var result = await _analyzer.AnalyzeAsync(null, null, null, networks, deviceData);

        // Assert - devices pointing to management VLAN gateway should be correct
        result.DeviceDnsPointsToGateway.Should().BeTrue();
        result.Issues.Should().NotContain(i => i.Type == IssueTypes.DnsDeviceMisconfigured);
    }

    [Fact]
    public async Task Analyze_WithDeviceDnsPointingToNativeVlanGateway_NoIssue()
    {
        // Arrange - Device points to native/VLAN 1 gateway even though management VLAN exists
        // The native gateway is always a valid DNS target (main gateway IP)
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo { Id = "net1", Name = "Default", VlanId = 1, DhcpEnabled = true, Gateway = "192.168.1.1" },
            new NetworkInfo { Id = "net2", Name = "Management", VlanId = 9, DhcpEnabled = true, Gateway = "10.9.0.1", Purpose = NetworkPurpose.Management }
        };
        var deviceData = JsonDocument.Parse("""
        [
            {
                "type": "ugw",
                "name": "Gateway",
                "ip": "10.9.0.1"
            },
            {
                "type": "usw",
                "name": "Switch1",
                "ip": "192.168.1.10",
                "config_network": {
                    "type": "static",
                    "dns1": "192.168.1.1"
                }
            }
        ]
        """).RootElement;

        // Act
        var result = await _analyzer.AnalyzeAsync(null, null, null, networks, deviceData);

        // Assert - native VLAN gateway is a valid DNS target
        result.DeviceDnsPointsToGateway.Should().BeTrue();
        result.Issues.Should().NotContain(i => i.Type == IssueTypes.DnsDeviceMisconfigured);
    }

    [Fact]
    public async Task Analyze_WithDeviceDnsPointingToPihole_NoIssue()
    {
        // Arrange - Device DNS points to Pi-hole configured as DHCP DNS on a network
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1", Name = "Home", VlanId = 10, DhcpEnabled = true,
                Gateway = "10.10.0.1",
                DnsServers = new List<string> { "10.10.0.50" }, // Pi-hole
                Purpose = NetworkPurpose.Home
            },
            new NetworkInfo
            {
                Id = "net2", Name = "Management", VlanId = 9, DhcpEnabled = true,
                Gateway = "10.9.0.1",
                Purpose = NetworkPurpose.Management
            }
        };
        var deviceData = JsonDocument.Parse("""
        [
            {
                "type": "ugw",
                "name": "Gateway",
                "ip": "10.9.0.1"
            },
            {
                "type": "usw",
                "name": "Switch1",
                "ip": "10.9.0.10",
                "config_network": {
                    "type": "static",
                    "dns1": "10.10.0.50"
                }
            }
        ]
        """).RootElement;

        // Act
        var result = await _analyzer.AnalyzeAsync(null, null, null, networks, deviceData);

        // Assert - Pi-hole is a valid DNS target (admin configured it)
        result.DeviceDnsPointsToGateway.Should().BeTrue();
        result.Issues.Should().NotContain(i => i.Type == IssueTypes.DnsDeviceMisconfigured);
    }

    [Fact]
    public async Task Analyze_WithDeviceDnsPointingToExternalDns_StillRaisesIssue()
    {
        // Arrange - Device DNS points to public DNS (not a gateway or configured DNS)
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo { Id = "net1", Name = "Default", VlanId = 1, DhcpEnabled = true, Gateway = "192.168.1.1" },
            new NetworkInfo { Id = "net2", Name = "Management", VlanId = 9, DhcpEnabled = true, Gateway = "10.9.0.1", Purpose = NetworkPurpose.Management }
        };
        var deviceData = JsonDocument.Parse("""
        [
            {
                "type": "ugw",
                "name": "Gateway",
                "ip": "10.9.0.1"
            },
            {
                "type": "uap",
                "name": "RogueAP",
                "ip": "10.9.0.20",
                "config_network": {
                    "type": "static",
                    "dns1": "8.8.8.8"
                }
            }
        ]
        """).RootElement;

        // Act
        var result = await _analyzer.AnalyzeAsync(null, null, null, networks, deviceData);

        // Assert - external DNS is NOT valid for infrastructure devices
        result.DeviceDnsPointsToGateway.Should().BeFalse();
        result.Issues.Should().Contain(i => i.Type == IssueTypes.DnsDeviceMisconfigured);
    }

    #endregion

    #region Legacy LAN_IN DoT/DoH Detection Tests

    [Fact]
    public async Task Analyze_LegacyLanInRule_DoT853_DetectsAsBlocking()
    {
        // LAN_IN rules blocking DoT (TCP 853) should be detected as leak prevention
        // because gateway uses DoH, not DoT, for upstream queries
        var firewallRules = new List<FirewallRule>
        {
            new()
            {
                Id = "rule1",
                Name = "Block DoT on LAN",
                Enabled = true,
                Action = "drop",
                Protocol = "tcp",
                DestinationPort = "853",
                Ruleset = "LAN_IN",
                SourceZoneId = FirewallRuleParser.LegacyInternalZoneId,
                DestinationZoneId = FirewallRuleParser.LegacyInternalZoneId // LAN_IN maps to Internal
            }
        };

        var result = await _analyzer.AnalyzeAsync(
            settingsData: null,
            firewallRules: firewallRules,
            switches: null,
            networks: null,
            deviceData: null,
            customDnsManagementPort: null,
            natRulesData: null,
            dnatExcludedVlanIds: null,
            externalZoneId: FirewallRuleParser.LegacyExternalZoneId);

        result.HasDotBlockRule.Should().BeTrue();
        result.DotRuleName.Should().Be("Block DoT on LAN");
    }

    [Fact]
    public async Task Analyze_LegacyLanInRule_Dns53Udp_NotDetectedAsBlocking()
    {
        // LAN_IN rules blocking UDP 53 should NOT be detected as leak prevention
        // because this would also block the gateway's own DNS queries
        var firewallRules = new List<FirewallRule>
        {
            new()
            {
                Id = "rule1",
                Name = "Block DNS on LAN",
                Enabled = true,
                Action = "drop",
                Protocol = "udp",
                DestinationPort = "53",
                Ruleset = "LAN_IN",
                SourceZoneId = FirewallRuleParser.LegacyInternalZoneId,
                DestinationZoneId = FirewallRuleParser.LegacyInternalZoneId
            }
        };

        var result = await _analyzer.AnalyzeAsync(
            settingsData: null,
            firewallRules: firewallRules,
            switches: null,
            networks: null,
            deviceData: null,
            customDnsManagementPort: null,
            natRulesData: null,
            dnatExcludedVlanIds: null,
            externalZoneId: FirewallRuleParser.LegacyExternalZoneId);

        // UDP 53 on LAN_IN should NOT be detected - it would break gateway DNS
        result.HasDns53BlockRule.Should().BeFalse();
    }

    [Fact]
    public async Task Analyze_LegacyWanOutRule_Dns53Udp_DetectedAsBlocking()
    {
        // WAN_OUT rules blocking UDP 53 should be detected - this is best practice
        // Gateway DNS queries don't go through WAN_OUT (they originate from gateway)
        var firewallRules = new List<FirewallRule>
        {
            new()
            {
                Id = "rule1",
                Name = "Block DNS to Internet",
                Enabled = true,
                Action = "drop",
                Protocol = "udp",
                DestinationPort = "53",
                Ruleset = "WAN_OUT",
                SourceZoneId = FirewallRuleParser.LegacyInternalZoneId,
                DestinationZoneId = FirewallRuleParser.LegacyExternalZoneId
            }
        };

        var result = await _analyzer.AnalyzeAsync(
            settingsData: null,
            firewallRules: firewallRules,
            switches: null,
            networks: null,
            deviceData: null,
            customDnsManagementPort: null,
            natRulesData: null,
            dnatExcludedVlanIds: null,
            externalZoneId: FirewallRuleParser.LegacyExternalZoneId);

        result.HasDns53BlockRule.Should().BeTrue();
        result.Dns53RuleName.Should().Be("Block DNS to Internet");
    }

    [Fact]
    public async Task Analyze_LegacyGuestInRule_DoT853_NotDetectedAsBlocking()
    {
        // GUEST_IN rules should NOT be accepted for DoT blocking
        // Guest networks typically use external DNS directly
        var firewallRules = new List<FirewallRule>
        {
            new()
            {
                Id = "rule1",
                Name = "Block DoT on Guest",
                Enabled = true,
                Action = "drop",
                Protocol = "tcp",
                DestinationPort = "853",
                Ruleset = "GUEST_IN",
                SourceZoneId = FirewallRuleParser.LegacyInternalZoneId,
                DestinationZoneId = FirewallRuleParser.LegacyInternalZoneId
            }
        };

        var result = await _analyzer.AnalyzeAsync(
            settingsData: null,
            firewallRules: firewallRules,
            switches: null,
            networks: null,
            deviceData: null,
            customDnsManagementPort: null,
            natRulesData: null,
            dnatExcludedVlanIds: null,
            externalZoneId: FirewallRuleParser.LegacyExternalZoneId);

        // GUEST_IN should NOT be detected - guests use external DNS
        result.HasDotBlockRule.Should().BeFalse();
    }

    #endregion

    #region DMZ and Guest Network DNS Consistency Tests

    [Fact]
    public async Task Analyze_DmzNetworkWithoutThirdPartyDns_GetsInfoIssueNotInconsistentConfig()
    {
        // Arrange - Third-party DNS on one network, DMZ network uses gateway DNS
        // DMZ networks should get an informational issue, not consistency error
        var dmzZoneId = "zone-dmz-001";
        var zones = new List<UniFiFirewallZone>
        {
            new() { Id = "zone-internal-001", ZoneKey = "internal", Name = "Internal" },
            new() { Id = "zone-external-001", ZoneKey = "external", Name = "External" },
            new() { Id = dmzZoneId, ZoneKey = "dmz", Name = "DMZ" }
        };
        var zoneLookup = new FirewallZoneLookup(zones);

        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "Home",
                VlanId = 1,
                Purpose = NetworkPurpose.Home,
                DhcpEnabled = true,
                Gateway = "192.168.1.1",
                DnsServers = new List<string> { "192.168.1.5" }, // Pi-hole
                FirewallZoneId = "zone-internal-001"
            },
            new NetworkInfo
            {
                Id = "net2",
                Name = "DMZ Servers",
                VlanId = 100,
                Purpose = NetworkPurpose.Home,
                DhcpEnabled = true,
                Gateway = "192.168.100.1",
                DnsServers = new List<string> { "192.168.100.1" }, // Gateway DNS (no Pi-hole)
                FirewallZoneId = dmzZoneId // This network is in the DMZ zone
            }
        };
        var switches = new List<SwitchInfo>
        {
            new SwitchInfo { Name = "Gateway", IsGateway = true }
        };

        // Act
        var result = await _analyzer.AnalyzeAsync(
            settingsData: null,
            firewallRules: null,
            switches: switches,
            networks: networks,
            deviceData: null,
            customDnsManagementPort: null,
            natRulesData: null,
            dnatExcludedVlanIds: null,
            externalZoneId: null,
            zoneLookup: zoneLookup);

        // Assert - DMZ network should NOT get consistency issue, should get Info issue
        result.HasThirdPartyDns.Should().BeTrue();
        result.Issues.Should().NotContain(i => i.Type == IssueTypes.DnsInconsistentConfig);
        var dmzIssue = result.Issues.FirstOrDefault(i => i.Type == IssueTypes.DnsDmzNetworkInfo);
        dmzIssue.Should().NotBeNull();
        dmzIssue!.Severity.Should().Be(AuditSeverity.Informational);
        dmzIssue.Message.Should().Contain("DMZ Servers");
        dmzIssue.Message.Should().Contain("isolated from the gateway by design");
    }

    [Fact]
    public async Task Analyze_GuestNetworkWithoutThirdPartyDns_GetsInfoIssueNotInconsistentConfig()
    {
        // Arrange - Third-party DNS on one network, Guest network uses gateway DNS
        // Guest networks with Purpose=Guest should get an informational issue
        var zones = new List<UniFiFirewallZone>
        {
            new() { Id = "zone-internal-001", ZoneKey = "internal", Name = "Internal" },
            new() { Id = "zone-external-001", ZoneKey = "external", Name = "External" },
            new() { Id = "zone-hotspot-001", ZoneKey = "hotspot", Name = "Hotspot" }
        };
        var zoneLookup = new FirewallZoneLookup(zones);

        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "Home",
                VlanId = 1,
                Purpose = NetworkPurpose.Home,
                DhcpEnabled = true,
                Gateway = "192.168.1.1",
                DnsServers = new List<string> { "192.168.1.5" }, // Pi-hole
                FirewallZoneId = "zone-internal-001"
            },
            new NetworkInfo
            {
                Id = "net2",
                Name = "Guest WiFi",
                VlanId = 50,
                Purpose = NetworkPurpose.Guest, // Guest network by purpose
                DhcpEnabled = true,
                Gateway = "192.168.50.1",
                DnsServers = new List<string> { "192.168.50.1" }, // Gateway DNS
                FirewallZoneId = "zone-hotspot-001"
            }
        };
        var switches = new List<SwitchInfo>
        {
            new SwitchInfo { Name = "Gateway", IsGateway = true }
        };

        // Act
        var result = await _analyzer.AnalyzeAsync(
            settingsData: null,
            firewallRules: null,
            switches: switches,
            networks: networks,
            deviceData: null,
            customDnsManagementPort: null,
            natRulesData: null,
            dnatExcludedVlanIds: null,
            externalZoneId: null,
            zoneLookup: zoneLookup);

        // Assert - Guest network should NOT get consistency issue, should get Info issue
        result.HasThirdPartyDns.Should().BeTrue();
        result.Issues.Should().NotContain(i => i.Type == IssueTypes.DnsInconsistentConfig);
        var guestIssue = result.Issues.FirstOrDefault(i => i.Type == IssueTypes.DnsGuestThirdPartyInfo);
        guestIssue.Should().NotBeNull();
        guestIssue!.Severity.Should().Be(AuditSeverity.Informational);
        guestIssue.Message.Should().Contain("Guest WiFi");
        guestIssue.Message.Should().Contain("third-party LAN DNS servers require explicit firewall rules");
    }

    [Fact]
    public async Task Analyze_IsUniFiGuestNetworkWithoutThirdPartyDns_GetsInfoIssue()
    {
        // Arrange - Guest network identified by IsUniFiGuestNetwork flag
        var zones = new List<UniFiFirewallZone>
        {
            new() { Id = "zone-internal-001", ZoneKey = "internal", Name = "Internal" },
            new() { Id = "zone-external-001", ZoneKey = "external", Name = "External" }
        };
        var zoneLookup = new FirewallZoneLookup(zones);

        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "Home",
                VlanId = 1,
                Purpose = NetworkPurpose.Home,
                DhcpEnabled = true,
                Gateway = "192.168.1.1",
                DnsServers = new List<string> { "192.168.1.5" }, // Pi-hole
                FirewallZoneId = "zone-internal-001"
            },
            new NetworkInfo
            {
                Id = "net2",
                Name = "Hotspot Network",
                VlanId = 60,
                Purpose = NetworkPurpose.Home, // Purpose is not Guest
                IsUniFiGuestNetwork = true, // But this is a UniFi guest network
                DhcpEnabled = true,
                Gateway = "192.168.60.1",
                DnsServers = new List<string> { "192.168.60.1" }, // Gateway DNS
                FirewallZoneId = "zone-internal-001"
            }
        };
        var switches = new List<SwitchInfo>
        {
            new SwitchInfo { Name = "Gateway", IsGateway = true }
        };

        // Act
        var result = await _analyzer.AnalyzeAsync(
            settingsData: null,
            firewallRules: null,
            switches: switches,
            networks: networks,
            deviceData: null,
            customDnsManagementPort: null,
            natRulesData: null,
            dnatExcludedVlanIds: null,
            externalZoneId: null,
            zoneLookup: zoneLookup);

        // Assert - IsUniFiGuestNetwork should get Info issue
        result.HasThirdPartyDns.Should().BeTrue();
        result.Issues.Should().NotContain(i => i.Type == IssueTypes.DnsInconsistentConfig);
        var guestIssue = result.Issues.FirstOrDefault(i => i.Type == IssueTypes.DnsGuestThirdPartyInfo);
        guestIssue.Should().NotBeNull();
        guestIssue!.Message.Should().Contain("Hotspot Network");
    }

    [Fact]
    public async Task Analyze_RegularNetworkWithoutThirdPartyDns_StillGetsInconsistentConfig()
    {
        // Arrange - Regular network (not DMZ, not Guest) should still get consistency issue
        var zones = new List<UniFiFirewallZone>
        {
            new() { Id = "zone-internal-001", ZoneKey = "internal", Name = "Internal" },
            new() { Id = "zone-external-001", ZoneKey = "external", Name = "External" }
        };
        var zoneLookup = new FirewallZoneLookup(zones);

        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "Home",
                VlanId = 1,
                Purpose = NetworkPurpose.Home,
                DhcpEnabled = true,
                Gateway = "192.168.1.1",
                DnsServers = new List<string> { "192.168.1.5" }, // Pi-hole
                FirewallZoneId = "zone-internal-001"
            },
            new NetworkInfo
            {
                Id = "net2",
                Name = "IoT Network",
                VlanId = 30,
                Purpose = NetworkPurpose.IoT,
                DhcpEnabled = true,
                Gateway = "192.168.30.1",
                DnsServers = new List<string> { "192.168.30.1" }, // Not using Pi-hole
                FirewallZoneId = "zone-internal-001" // Regular internal network
            }
        };
        var switches = new List<SwitchInfo>
        {
            new SwitchInfo { Name = "Gateway", IsGateway = true }
        };

        // Act
        var result = await _analyzer.AnalyzeAsync(
            settingsData: null,
            firewallRules: null,
            switches: switches,
            networks: networks,
            deviceData: null,
            customDnsManagementPort: null,
            natRulesData: null,
            dnatExcludedVlanIds: null,
            externalZoneId: null,
            zoneLookup: zoneLookup);

        // Assert - Regular IoT network SHOULD get consistency issue
        result.HasThirdPartyDns.Should().BeTrue();
        var inconsistentIssue = result.Issues.FirstOrDefault(i => i.Type == IssueTypes.DnsInconsistentConfig);
        inconsistentIssue.Should().NotBeNull();
        inconsistentIssue!.Message.Should().Contain("IoT Network");

        // Should NOT get DMZ or Guest info issues
        result.Issues.Should().NotContain(i => i.Type == IssueTypes.DnsDmzNetworkInfo);
        result.Issues.Should().NotContain(i => i.Type == IssueTypes.DnsGuestThirdPartyInfo);
    }

    [Fact]
    public async Task Analyze_WithoutZoneLookup_GuestNetworkByPurposeStillGetsInfoIssue()
    {
        // Arrange - Even without zone lookup, Guest networks by Purpose should get Info issue
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "Home",
                VlanId = 1,
                Purpose = NetworkPurpose.Home,
                DhcpEnabled = true,
                Gateway = "192.168.1.1",
                DnsServers = new List<string> { "192.168.1.5" } // Pi-hole
            },
            new NetworkInfo
            {
                Id = "net2",
                Name = "Guest",
                VlanId = 50,
                Purpose = NetworkPurpose.Guest, // Guest by purpose
                DhcpEnabled = true,
                Gateway = "192.168.50.1",
                DnsServers = new List<string> { "192.168.50.1" }
            }
        };
        var switches = new List<SwitchInfo>
        {
            new SwitchInfo { Name = "Gateway", IsGateway = true }
        };

        // Act - No zone lookup provided
        var result = await _analyzer.AnalyzeAsync(
            settingsData: null,
            firewallRules: null,
            switches: switches,
            networks: networks);

        // Assert - Guest should get Info issue even without zone lookup
        result.HasThirdPartyDns.Should().BeTrue();
        result.Issues.Should().NotContain(i => i.Type == IssueTypes.DnsInconsistentConfig);
        var guestIssue = result.Issues.FirstOrDefault(i => i.Type == IssueTypes.DnsGuestThirdPartyInfo);
        guestIssue.Should().NotBeNull();
    }

    [Fact]
    public async Task Analyze_DmzNetworkWithoutZoneLookup_NotIdentifiedAsDmz()
    {
        // Arrange - Without zone lookup, DMZ networks can't be identified (no FirewallZoneId lookup)
        // This tests that DMZ detection requires zone lookup
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "Home",
                VlanId = 1,
                Purpose = NetworkPurpose.Home,
                DhcpEnabled = true,
                Gateway = "192.168.1.1",
                DnsServers = new List<string> { "192.168.1.5" } // Pi-hole
            },
            new NetworkInfo
            {
                Id = "net2",
                Name = "DMZ Servers",
                VlanId = 100,
                Purpose = NetworkPurpose.Home,
                DhcpEnabled = true,
                Gateway = "192.168.100.1",
                DnsServers = new List<string> { "192.168.100.1" },
                FirewallZoneId = "zone-dmz-001" // Zone ID exists but no lookup to verify it's DMZ
            }
        };
        var switches = new List<SwitchInfo>
        {
            new SwitchInfo { Name = "Gateway", IsGateway = true }
        };

        // Act - No zone lookup provided
        var result = await _analyzer.AnalyzeAsync(
            settingsData: null,
            firewallRules: null,
            switches: switches,
            networks: networks);

        // Assert - Without zone lookup, DMZ can't be identified, gets regular inconsistent issue
        result.HasThirdPartyDns.Should().BeTrue();
        var inconsistentIssue = result.Issues.FirstOrDefault(i => i.Type == IssueTypes.DnsInconsistentConfig);
        inconsistentIssue.Should().NotBeNull();
        inconsistentIssue!.Message.Should().Contain("DMZ Servers");

        // Should NOT get DMZ info issue since we can't identify it without zone lookup
        result.Issues.Should().NotContain(i => i.Type == IssueTypes.DnsDmzNetworkInfo);
    }

    [Fact]
    public async Task Analyze_DmzInfoIssue_HasZeroScoreImpact()
    {
        // Arrange - DMZ issues should be informational with no score impact
        var dmzZoneId = "zone-dmz-001";
        var zones = new List<UniFiFirewallZone>
        {
            new() { Id = "zone-internal-001", ZoneKey = "internal", Name = "Internal" },
            new() { Id = "zone-external-001", ZoneKey = "external", Name = "External" },
            new() { Id = dmzZoneId, ZoneKey = "dmz", Name = "DMZ" }
        };
        var zoneLookup = new FirewallZoneLookup(zones);

        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "Home",
                VlanId = 1,
                Purpose = NetworkPurpose.Home,
                DhcpEnabled = true,
                Gateway = "192.168.1.1",
                DnsServers = new List<string> { "192.168.1.5" }, // Pi-hole
                FirewallZoneId = "zone-internal-001"
            },
            new NetworkInfo
            {
                Id = "net2",
                Name = "DMZ",
                VlanId = 100,
                Purpose = NetworkPurpose.Home,
                DhcpEnabled = true,
                Gateway = "192.168.100.1",
                DnsServers = new List<string> { "192.168.100.1" },
                FirewallZoneId = dmzZoneId
            }
        };
        var switches = new List<SwitchInfo>
        {
            new SwitchInfo { Name = "Gateway", IsGateway = true }
        };

        // Act
        var result = await _analyzer.AnalyzeAsync(
            settingsData: null,
            firewallRules: null,
            switches: switches,
            networks: networks,
            deviceData: null,
            customDnsManagementPort: null,
            natRulesData: null,
            dnatExcludedVlanIds: null,
            externalZoneId: null,
            zoneLookup: zoneLookup);

        // Assert - DMZ info issue has zero score impact
        var dmzIssue = result.Issues.FirstOrDefault(i => i.Type == IssueTypes.DnsDmzNetworkInfo);
        dmzIssue.Should().NotBeNull();
        dmzIssue!.ScoreImpact.Should().Be(0);
    }

    [Fact]
    public async Task Analyze_GuestInfoIssue_HasZeroScoreImpact()
    {
        // Arrange - Guest issues should be informational with no score impact
        var zones = new List<UniFiFirewallZone>
        {
            new() { Id = "zone-internal-001", ZoneKey = "internal", Name = "Internal" },
            new() { Id = "zone-external-001", ZoneKey = "external", Name = "External" }
        };
        var zoneLookup = new FirewallZoneLookup(zones);

        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "Home",
                VlanId = 1,
                Purpose = NetworkPurpose.Home,
                DhcpEnabled = true,
                Gateway = "192.168.1.1",
                DnsServers = new List<string> { "192.168.1.5" }, // Pi-hole
                FirewallZoneId = "zone-internal-001"
            },
            new NetworkInfo
            {
                Id = "net2",
                Name = "Guest",
                VlanId = 50,
                Purpose = NetworkPurpose.Guest,
                DhcpEnabled = true,
                Gateway = "192.168.50.1",
                DnsServers = new List<string> { "192.168.50.1" }
            }
        };
        var switches = new List<SwitchInfo>
        {
            new SwitchInfo { Name = "Gateway", IsGateway = true }
        };

        // Act
        var result = await _analyzer.AnalyzeAsync(
            settingsData: null,
            firewallRules: null,
            switches: switches,
            networks: networks,
            deviceData: null,
            customDnsManagementPort: null,
            natRulesData: null,
            dnatExcludedVlanIds: null,
            externalZoneId: null,
            zoneLookup: zoneLookup);

        // Assert - Guest info issue has zero score impact
        var guestIssue = result.Issues.FirstOrDefault(i => i.Type == IssueTypes.DnsGuestThirdPartyInfo);
        guestIssue.Should().NotBeNull();
        guestIssue!.ScoreImpact.Should().Be(0);
    }

    [Fact]
    public async Task Analyze_MultipleDmzNetworks_GroupedInSingleInfoIssue()
    {
        // Arrange - Multiple DMZ networks should be grouped in a single info issue
        var dmzZoneId = "zone-dmz-001";
        var zones = new List<UniFiFirewallZone>
        {
            new() { Id = "zone-internal-001", ZoneKey = "internal", Name = "Internal" },
            new() { Id = "zone-external-001", ZoneKey = "external", Name = "External" },
            new() { Id = dmzZoneId, ZoneKey = "dmz", Name = "DMZ" }
        };
        var zoneLookup = new FirewallZoneLookup(zones);

        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "Home",
                VlanId = 1,
                Purpose = NetworkPurpose.Home,
                DhcpEnabled = true,
                Gateway = "192.168.1.1",
                DnsServers = new List<string> { "192.168.1.5" }, // Pi-hole
                FirewallZoneId = "zone-internal-001"
            },
            new NetworkInfo
            {
                Id = "net2",
                Name = "DMZ Web",
                VlanId = 100,
                Purpose = NetworkPurpose.Home,
                DhcpEnabled = true,
                Gateway = "192.168.100.1",
                DnsServers = new List<string> { "192.168.100.1" },
                FirewallZoneId = dmzZoneId
            },
            new NetworkInfo
            {
                Id = "net3",
                Name = "DMZ Database",
                VlanId = 101,
                Purpose = NetworkPurpose.Home,
                DhcpEnabled = true,
                Gateway = "192.168.101.1",
                DnsServers = new List<string> { "192.168.101.1" },
                FirewallZoneId = dmzZoneId
            }
        };
        var switches = new List<SwitchInfo>
        {
            new SwitchInfo { Name = "Gateway", IsGateway = true }
        };

        // Act
        var result = await _analyzer.AnalyzeAsync(
            settingsData: null,
            firewallRules: null,
            switches: switches,
            networks: networks,
            deviceData: null,
            customDnsManagementPort: null,
            natRulesData: null,
            dnatExcludedVlanIds: null,
            externalZoneId: null,
            zoneLookup: zoneLookup);

        // Assert - Should have single DMZ info issue mentioning both networks
        var dmzIssues = result.Issues.Where(i => i.Type == IssueTypes.DnsDmzNetworkInfo).ToList();
        dmzIssues.Should().HaveCount(1);
        dmzIssues[0].Message.Should().Contain("DMZ Web");
        dmzIssues[0].Message.Should().Contain("DMZ Database");
    }

    #endregion

    #region Infrastructure Network (Security/Management) DNS Exemption Tests

    [Fact]
    public async Task Analyze_SecurityNetworkWithoutThirdPartyDns_GetsInfoIssueNotInconsistentConfig()
    {
        // Arrange - Third-party DNS on one network, Security (cameras) network uses gateway DNS
        var zones = new List<UniFiFirewallZone>
        {
            new() { Id = "zone-internal-001", ZoneKey = "internal", Name = "Internal" },
            new() { Id = "zone-external-001", ZoneKey = "external", Name = "External" }
        };
        var zoneLookup = new FirewallZoneLookup(zones);

        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "Home",
                VlanId = 1,
                Purpose = NetworkPurpose.Home,
                DhcpEnabled = true,
                Gateway = "192.168.1.1",
                DnsServers = new List<string> { "192.168.1.5" }, // Pi-hole
                FirewallZoneId = "zone-internal-001"
            },
            new NetworkInfo
            {
                Id = "net2",
                Name = "Cameras",
                VlanId = 40,
                Purpose = NetworkPurpose.Security,
                DhcpEnabled = true,
                Gateway = "192.168.40.1",
                DnsServers = new List<string> { "192.168.40.1" }, // Gateway DNS
                FirewallZoneId = "zone-internal-001"
            }
        };
        var switches = new List<SwitchInfo>
        {
            new SwitchInfo { Name = "Gateway", IsGateway = true }
        };

        // Act
        var result = await _analyzer.AnalyzeAsync(
            settingsData: null,
            firewallRules: null,
            switches: switches,
            networks: networks,
            deviceData: null,
            customDnsManagementPort: null,
            natRulesData: null,
            dnatExcludedVlanIds: null,
            externalZoneId: null,
            zoneLookup: zoneLookup);

        // Assert - Security network should NOT get consistency issue, should get Info issue
        result.HasThirdPartyDns.Should().BeTrue();
        result.Issues.Should().NotContain(i => i.Type == IssueTypes.DnsInconsistentConfig);
        var infraIssue = result.Issues.FirstOrDefault(i => i.Type == IssueTypes.DnsInfraNetworkInfo);
        infraIssue.Should().NotBeNull();
        infraIssue!.Severity.Should().Be(AuditSeverity.Informational);
        infraIssue.ScoreImpact.Should().Be(0);
        infraIssue.Message.Should().Contain("Cameras");
        infraIssue.Message.Should().Contain("gateway DNS");
    }

    [Fact]
    public async Task Analyze_ManagementNetworkWithoutThirdPartyDns_GetsInfoIssueNotInconsistentConfig()
    {
        // Arrange - Third-party DNS on one network, Management network uses gateway DNS
        var zones = new List<UniFiFirewallZone>
        {
            new() { Id = "zone-internal-001", ZoneKey = "internal", Name = "Internal" },
            new() { Id = "zone-external-001", ZoneKey = "external", Name = "External" }
        };
        var zoneLookup = new FirewallZoneLookup(zones);

        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "Home",
                VlanId = 1,
                Purpose = NetworkPurpose.Home,
                DhcpEnabled = true,
                Gateway = "192.168.1.1",
                DnsServers = new List<string> { "192.168.1.5" }, // Pi-hole
                FirewallZoneId = "zone-internal-001"
            },
            new NetworkInfo
            {
                Id = "net2",
                Name = "Network Admin",
                VlanId = 10,
                Purpose = NetworkPurpose.Management,
                DhcpEnabled = true,
                Gateway = "192.168.10.1",
                DnsServers = new List<string> { "192.168.10.1" }, // Gateway DNS
                FirewallZoneId = "zone-internal-001"
            }
        };
        var switches = new List<SwitchInfo>
        {
            new SwitchInfo { Name = "Gateway", IsGateway = true }
        };

        // Act
        var result = await _analyzer.AnalyzeAsync(
            settingsData: null,
            firewallRules: null,
            switches: switches,
            networks: networks,
            deviceData: null,
            customDnsManagementPort: null,
            natRulesData: null,
            dnatExcludedVlanIds: null,
            externalZoneId: null,
            zoneLookup: zoneLookup);

        // Assert - Management network should NOT get consistency issue, should get Info issue
        result.HasThirdPartyDns.Should().BeTrue();
        result.Issues.Should().NotContain(i => i.Type == IssueTypes.DnsInconsistentConfig);
        var infraIssue = result.Issues.FirstOrDefault(i => i.Type == IssueTypes.DnsInfraNetworkInfo);
        infraIssue.Should().NotBeNull();
        infraIssue!.Severity.Should().Be(AuditSeverity.Informational);
        infraIssue.ScoreImpact.Should().Be(0);
        infraIssue.Message.Should().Contain("Network Admin");
        infraIssue.Message.Should().Contain("gateway DNS");
    }

    [Fact]
    public async Task Analyze_DnatPartialCoverage_SecurityNetworkIncludedInPartialCoverage()
    {
        // Arrange - DNAT covers LAN but not Security network
        // Security networks should still require DNAT coverage (cameras hardcode DNS)
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "LAN",
                VlanId = 1,
                Subnet = "192.168.1.0/24",
                Gateway = "192.168.1.1",
                DhcpEnabled = true
            },
            new NetworkInfo
            {
                Id = "net2",
                Name = "Cameras",
                VlanId = 40,
                Purpose = NetworkPurpose.Security,
                Subnet = "192.168.40.0/24",
                Gateway = "192.168.40.1",
                DhcpEnabled = true
            }
        };
        var natRules = CreateDnatNatRules(("net1", "192.168.1.1")); // Only covers LAN

        // Act
        var result = await _analyzer.AnalyzeAsync(null, null, null, networks, null, null, natRules);

        // Assert - Security network should be included in partial coverage, not exempted
        result.HasDnatDnsRules.Should().BeTrue();
        var partialIssue = result.Issues.FirstOrDefault(i => i.Type == IssueTypes.DnsDnatPartialCoverage);
        partialIssue.Should().NotBeNull();
        partialIssue!.Message.Should().Contain("Cameras");
    }

    [Fact]
    public async Task Analyze_DnatPartialCoverage_SecurityAndManagementIncludedWithOtherNetworks()
    {
        // Arrange - DNAT covers LAN but not Security, Management, or IoT networks
        // All should appear in partial coverage - DNAT to gateway is valid for infra networks
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "LAN",
                VlanId = 1,
                Subnet = "192.168.1.0/24",
                Gateway = "192.168.1.1",
                DhcpEnabled = true
            },
            new NetworkInfo
            {
                Id = "net2",
                Name = "Cameras",
                VlanId = 40,
                Purpose = NetworkPurpose.Security,
                Subnet = "192.168.40.0/24",
                Gateway = "192.168.40.1",
                DhcpEnabled = true
            },
            new NetworkInfo
            {
                Id = "net3",
                Name = "Network Admin",
                VlanId = 10,
                Purpose = NetworkPurpose.Management,
                Subnet = "192.168.10.0/24",
                Gateway = "192.168.10.1",
                DhcpEnabled = true
            },
            new NetworkInfo
            {
                Id = "net4",
                Name = "IoT Devices",
                VlanId = 20,
                Purpose = NetworkPurpose.IoT,
                Subnet = "192.168.20.0/24",
                Gateway = "192.168.20.1",
                DhcpEnabled = true
            }
        };
        var natRules = CreateDnatNatRules(("net1", "192.168.1.1")); // Only covers LAN

        // Act
        var result = await _analyzer.AnalyzeAsync(null, null, null, networks, null, null, natRules);

        // Assert - All uncovered networks should be in the partial coverage issue
        var partialIssue = result.Issues.FirstOrDefault(i => i.Type == IssueTypes.DnsDnatPartialCoverage);
        partialIssue.Should().NotBeNull();
        partialIssue!.Message.Should().Contain("Cameras");
        partialIssue.Message.Should().Contain("Network Admin");
        partialIssue.Message.Should().Contain("IoT Devices");
    }

    #endregion

    #region External DNS Bypass Issue Tests

    [Fact]
    public async Task Analyze_RegularNetworkWithPublicDns_CreatesRecommendedSeverityIssue()
    {
        // Arrange - Regular network with Cloudflare DNS
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "Printing",
                VlanId = 20,
                DhcpEnabled = true,
                Gateway = "192.168.20.1",
                Subnet = "192.168.20.0/24",
                DnsServers = new List<string> { "1.1.1.1" },
                FirewallZoneId = "zone-internal"
            }
        };

        // Act
        var result = await _analyzer.AnalyzeAsync(
            settingsData: null,
            firewallRules: null,
            switches: null,
            networks: networks,
            deviceData: null,
            customDnsManagementPort: null,
            natRulesData: null,
            dnatExcludedVlanIds: null,
            externalZoneId: null,
            zoneLookup: null);

        // Assert
        var externalDnsIssues = result.Issues.Where(i => i.Type == IssueTypes.DnsExternalBypass).ToList();
        externalDnsIssues.Should().HaveCount(1);
        externalDnsIssues[0].Severity.Should().Be(AuditSeverity.Recommended);
        externalDnsIssues[0].Message.Should().Contain("Printing");
        externalDnsIssues[0].Message.Should().Contain("Cloudflare");
        externalDnsIssues[0].Message.Should().Contain("external public DNS");
    }

    [Fact]
    public async Task Analyze_DmzNetworkWithPublicDns_CreatesInformationalSeverityIssue()
    {
        // Arrange - DMZ network with Cloudflare DNS
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "DMZ Test",
                VlanId = 50,
                DhcpEnabled = true,
                Gateway = "192.168.50.1",
                Subnet = "192.168.50.0/24",
                DnsServers = new List<string> { "1.1.1.1" },
                FirewallZoneId = "zone-dmz"
            }
        };

        var zoneLookup = new FirewallZoneLookup(new List<UniFiFirewallZone>
        {
            new UniFiFirewallZone { Id = "zone-dmz", Name = "DMZ", ZoneKey = "dmz" }
        });

        // Act
        var result = await _analyzer.AnalyzeAsync(
            settingsData: null,
            firewallRules: null,
            switches: null,
            networks: networks,
            deviceData: null,
            customDnsManagementPort: null,
            natRulesData: null,
            dnatExcludedVlanIds: null,
            externalZoneId: null,
            zoneLookup: zoneLookup);

        // Assert
        var externalDnsIssues = result.Issues.Where(i => i.Type == IssueTypes.DnsExternalBypass).ToList();
        externalDnsIssues.Should().HaveCount(1);
        externalDnsIssues[0].Severity.Should().Be(AuditSeverity.Informational);
        externalDnsIssues[0].Message.Should().Contain("DMZ Test");
        externalDnsIssues[0].Message.Should().Contain("expected for isolated DMZ");
    }

    [Fact]
    public async Task Analyze_MixedDmzAndRegularWithPublicDns_CreatesSeparateIssues()
    {
        // Arrange - One DMZ network and one regular network, both with public DNS
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "DMZ Test",
                VlanId = 50,
                DhcpEnabled = true,
                Gateway = "192.168.50.1",
                Subnet = "192.168.50.0/24",
                DnsServers = new List<string> { "1.1.1.1" },
                FirewallZoneId = "zone-dmz"
            },
            new NetworkInfo
            {
                Id = "net2",
                Name = "Printing",
                VlanId = 20,
                DhcpEnabled = true,
                Gateway = "192.168.20.1",
                Subnet = "192.168.20.0/24",
                DnsServers = new List<string> { "1.1.1.1" },
                FirewallZoneId = "zone-internal"
            }
        };

        var zoneLookup = new FirewallZoneLookup(new List<UniFiFirewallZone>
        {
            new UniFiFirewallZone { Id = "zone-dmz", Name = "DMZ", ZoneKey = "dmz" },
            new UniFiFirewallZone { Id = "zone-internal", Name = "Internal", ZoneKey = "internal" }
        });

        // Act
        var result = await _analyzer.AnalyzeAsync(
            settingsData: null,
            firewallRules: null,
            switches: null,
            networks: networks,
            deviceData: null,
            customDnsManagementPort: null,
            natRulesData: null,
            dnatExcludedVlanIds: null,
            externalZoneId: null,
            zoneLookup: zoneLookup);

        // Assert - Should have 2 separate issues
        var externalDnsIssues = result.Issues.Where(i => i.Type == IssueTypes.DnsExternalBypass).ToList();
        externalDnsIssues.Should().HaveCount(2);

        // DMZ network should have Informational severity
        var dmzIssue = externalDnsIssues.FirstOrDefault(i => i.Message.Contains("DMZ Test"));
        dmzIssue.Should().NotBeNull();
        dmzIssue!.Severity.Should().Be(AuditSeverity.Informational);

        // Regular network should have Recommended severity
        var regularIssue = externalDnsIssues.FirstOrDefault(i => i.Message.Contains("Printing"));
        regularIssue.Should().NotBeNull();
        regularIssue!.Severity.Should().Be(AuditSeverity.Recommended);
    }

    [Fact]
    public async Task Analyze_PrivateDnsOutsideSubnets_CreatesRecommendedSeverityIssue()
    {
        // Arrange - Network with private DNS that's not in any configured subnet
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "Office Network",
                VlanId = 30,
                DhcpEnabled = true,
                Gateway = "192.168.30.1",
                Subnet = "192.168.30.0/24",
                DnsServers = new List<string> { "192.168.3.254" }, // Private but outside configured subnets
                FirewallZoneId = "zone-internal"
            }
        };

        // Act
        var result = await _analyzer.AnalyzeAsync(
            settingsData: null,
            firewallRules: null,
            switches: null,
            networks: networks,
            deviceData: null,
            customDnsManagementPort: null,
            natRulesData: null,
            dnatExcludedVlanIds: null,
            externalZoneId: null,
            zoneLookup: null);

        // Assert
        var externalDnsIssues = result.Issues.Where(i => i.Type == IssueTypes.DnsExternalBypass).ToList();
        externalDnsIssues.Should().HaveCount(1);
        externalDnsIssues[0].Severity.Should().Be(AuditSeverity.Recommended);
        externalDnsIssues[0].Message.Should().Contain("Office Network");
        externalDnsIssues[0].Message.Should().Contain("private DNS servers outside configured subnets");
        externalDnsIssues[0].Message.Should().Contain("192.168.3.254");
    }

    [Fact]
    public async Task Analyze_NetworkWithInternalDns_NoExternalDnsIssue()
    {
        // Arrange - Network with DNS that's in a configured subnet
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "Home",
                VlanId = 1,
                DhcpEnabled = true,
                Gateway = "192.168.1.1",
                Subnet = "192.168.1.0/24",
                DnsServers = new List<string> { "192.168.1.5" }, // Pi-hole in same subnet
                FirewallZoneId = "zone-internal"
            }
        };

        // Act
        var result = await _analyzer.AnalyzeAsync(
            settingsData: null,
            firewallRules: null,
            switches: null,
            networks: networks,
            deviceData: null,
            customDnsManagementPort: null,
            natRulesData: null,
            dnatExcludedVlanIds: null,
            externalZoneId: null,
            zoneLookup: null);

        // Assert - No external DNS bypass issue
        var externalDnsIssues = result.Issues.Where(i => i.Type == IssueTypes.DnsExternalBypass).ToList();
        externalDnsIssues.Should().BeEmpty();
    }

    [Fact]
    public async Task Analyze_NetworkWithGatewayAsDns_NoExternalDnsIssue()
    {
        // Arrange - Network using gateway as DNS (normal config)
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "Home",
                VlanId = 1,
                DhcpEnabled = true,
                Gateway = "192.168.1.1",
                Subnet = "192.168.1.0/24",
                DnsServers = new List<string> { "192.168.1.1" }, // Gateway
                FirewallZoneId = "zone-internal"
            }
        };

        // Act
        var result = await _analyzer.AnalyzeAsync(
            settingsData: null,
            firewallRules: null,
            switches: null,
            networks: networks,
            deviceData: null,
            customDnsManagementPort: null,
            natRulesData: null,
            dnatExcludedVlanIds: null,
            externalZoneId: null,
            zoneLookup: null);

        // Assert - No external DNS bypass issue
        var externalDnsIssues = result.Issues.Where(i => i.Type == IssueTypes.DnsExternalBypass).ToList();
        externalDnsIssues.Should().BeEmpty();
    }

    #endregion

    #region IdentifyExpectedDnsProvider Tests

    [Fact]
    public async Task Analyze_WithSdnsStamp_IdentifiesProviderFromStampHostname()
    {
        // Arrange - DoH configured with actual SDNS stamp that decodes to cloudflare hostname
        var settings = JsonDocument.Parse(@"[
            {
                ""key"": ""doh"",
                ""state"": ""custom"",
                ""custom_servers"": [
                    {
                        ""enabled"": true,
                        ""server_name"": ""my-custom-cloudflare"",
                        ""sdns_stamp"": ""sdns://AgcAAAAAAAAABzEuMC4wLjEAEmRucy5jbG91ZGZsYXJlLmNvbQovZG5zLXF1ZXJ5""
                    }
                ]
            }
        ]").RootElement;

        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""udm"",
                ""port_table"": [
                    {
                        ""network_name"": ""wan"",
                        ""name"": ""WAN"",
                        ""up"": true,
                        ""dns"": [""1.1.1.1"", ""1.0.0.1""]
                    }
                ]
            }
        ]").RootElement;

        // Act
        var result = await _analyzer.AnalyzeAsync(settings, null, null, null, deviceData);

        // Assert - Provider should be identified from stamp hostname (dns.cloudflare.com)
        result.DohConfigured.Should().BeTrue();
        result.ExpectedDnsProvider.Should().Be("Cloudflare");
    }

    [Fact]
    public async Task Analyze_WithGoogleSdnsStamp_IdentifiesProviderFromStampHostname()
    {
        // Arrange - DoH with Google SDNS stamp
        var settings = JsonDocument.Parse(@"[
            {
                ""key"": ""doh"",
                ""state"": ""custom"",
                ""custom_servers"": [
                    {
                        ""enabled"": true,
                        ""server_name"": ""custom-dns-server"",
                        ""sdns_stamp"": ""sdns://AgUAAAAAAAAAAAAKZG5zLmdvb2dsZQovZG5zLXF1ZXJ5""
                    }
                ]
            }
        ]").RootElement;

        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""udm"",
                ""port_table"": [
                    {
                        ""network_name"": ""wan"",
                        ""name"": ""WAN"",
                        ""up"": true,
                        ""dns"": [""8.8.8.8"", ""8.8.4.4""]
                    }
                ]
            }
        ]").RootElement;

        // Act
        var result = await _analyzer.AnalyzeAsync(settings, null, null, null, deviceData);

        // Assert - Provider should be identified from stamp hostname (dns.google)
        result.DohConfigured.Should().BeTrue();
        result.ExpectedDnsProvider.Should().Be("Google");
        result.WanDnsMatchesDoH.Should().BeTrue();
    }

    [Fact]
    public async Task Analyze_WithNoEnabledServers_DoesNotIdentifyProvider()
    {
        // Arrange - DoH configured but all custom servers disabled (need stamps for custom_servers to be parsed)
        var settings = JsonDocument.Parse(@"[
            {
                ""key"": ""doh"",
                ""state"": ""custom"",
                ""custom_servers"": [
                    {
                        ""enabled"": false,
                        ""server_name"": ""cloudflare"",
                        ""sdns_stamp"": ""sdns://AgcAAAAAAAAABzEuMC4wLjEAEmRucy5jbG91ZGZsYXJlLmNvbQovZG5zLXF1ZXJ5""
                    }
                ]
            }
        ]").RootElement;

        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""udm"",
                ""port_table"": [
                    {
                        ""network_name"": ""wan"",
                        ""name"": ""WAN"",
                        ""up"": true,
                        ""dns"": [""1.1.1.1""]
                    }
                ]
            }
        ]").RootElement;

        // Act
        var result = await _analyzer.AnalyzeAsync(settings, null, null, null, deviceData);

        // Assert - Server is disabled so DohConfigured is false (no enabled servers)
        result.DohConfigured.Should().BeFalse();
        result.ExpectedDnsProvider.Should().BeNull();
    }

    [Fact]
    public async Task Analyze_WithUnrecognizedServerName_DoesNotIdentifyProvider()
    {
        // Arrange - DoH with server_names containing an unrecognized name
        var settings = JsonDocument.Parse(@"[
            {
                ""key"": ""doh"",
                ""state"": ""auto"",
                ""server_names"": [""my-unknown-dns-provider""]
            }
        ]").RootElement;

        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""udm"",
                ""port_table"": [
                    {
                        ""network_name"": ""wan"",
                        ""name"": ""WAN"",
                        ""up"": true,
                        ""dns"": [""10.0.0.53""]
                    }
                ]
            }
        ]").RootElement;

        // Act
        var result = await _analyzer.AnalyzeAsync(settings, null, null, null, deviceData);

        // Assert - Unrecognized server name means no provider identified
        result.DohConfigured.Should().BeTrue();
        result.ExpectedDnsProvider.Should().BeNull();
    }

    [Fact]
    public async Task Analyze_WithMixedEnabledAndDisabledServers_UsesFirstEnabled()
    {
        // Arrange - Multiple custom servers with stamps, first disabled, second enabled
        var settings = JsonDocument.Parse(@"[
            {
                ""key"": ""doh"",
                ""state"": ""custom"",
                ""custom_servers"": [
                    {
                        ""enabled"": false,
                        ""server_name"": ""google"",
                        ""sdns_stamp"": ""sdns://AgUAAAAAAAAAAAAKZG5zLmdvb2dsZQovZG5zLXF1ZXJ5""
                    },
                    {
                        ""enabled"": true,
                        ""server_name"": ""cloudflare"",
                        ""sdns_stamp"": ""sdns://AgcAAAAAAAAABzEuMC4wLjEAEmRucy5jbG91ZGZsYXJlLmNvbQovZG5zLXF1ZXJ5""
                    }
                ]
            }
        ]").RootElement;

        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""udm"",
                ""port_table"": [
                    {
                        ""network_name"": ""wan"",
                        ""name"": ""WAN"",
                        ""up"": true,
                        ""dns"": [""1.1.1.1""]
                    }
                ]
            }
        ]").RootElement;

        // Act
        var result = await _analyzer.AnalyzeAsync(settings, null, null, null, deviceData);

        // Assert - Should use cloudflare (first enabled), not google (disabled)
        result.ExpectedDnsProvider.Should().Be("Cloudflare");
    }

    [Fact]
    public async Task Analyze_WithNextDnsServerName_IdentifiesNextDns()
    {
        // Arrange - DoH with NextDNS server name format (includes profile ID)
        var settings = JsonDocument.Parse(@"[
            {
                ""key"": ""doh"",
                ""state"": ""auto"",
                ""server_names"": [""NextDNS-abc123""]
            }
        ]").RootElement;

        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""udm"",
                ""port_table"": [
                    {
                        ""network_name"": ""wan"",
                        ""name"": ""WAN"",
                        ""up"": true,
                        ""dns"": [""45.90.28.0""]
                    }
                ]
            }
        ]").RootElement;

        // Act
        var result = await _analyzer.AnalyzeAsync(settings, null, null, null, deviceData);

        // Assert - Should identify NextDNS from server name prefix
        result.ExpectedDnsProvider.Should().Be("NextDNS");
    }

    #endregion

    #region Third-Party DNS WAN Validation Tests

    /// <summary>
    /// Creates a DnsSecurityAnalyzer with a mock HTTP client that simulates Pi-hole detection
    /// </summary>
    private DnsSecurityAnalyzer CreateAnalyzerWithPiholeDetection()
    {
        var detectorLoggerMock = new Mock<ILogger<ThirdPartyDnsDetector>>();

        // Mock HTTP client that returns Pi-hole API response for any IP
        // Pi-hole detector probes /api/info/login and expects {"dns":true}
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage request, CancellationToken _) =>
            {
                var path = request.RequestUri?.AbsolutePath ?? "";
                // Pi-hole v6 API endpoint
                if (path.Contains("/api/info/login"))
                {
                    return new HttpResponseMessage
                    {
                        StatusCode = HttpStatusCode.OK,
                        Content = new StringContent("{\"dns\":true,\"https_port\":443}")
                    };
                }
                return new HttpResponseMessage { StatusCode = HttpStatusCode.NotFound };
            });

        var httpClient = new HttpClient(handlerMock.Object) { Timeout = TimeSpan.FromSeconds(1) };
        var thirdPartyDetector = new ThirdPartyDnsDetector(detectorLoggerMock.Object, httpClient);
        return new DnsSecurityAnalyzer(_loggerMock.Object, thirdPartyDetector);
    }

    [Fact]
    public async Task Analyze_WanDnsMatchesThirdPartyDns_MarksAsMatched()
    {
        // Arrange - DoH configured with third-party (Pi-hole) DNS
        var analyzer = CreateAnalyzerWithPiholeDetection();

        var settings = JsonDocument.Parse(@"[
            {
                ""key"": ""doh"",
                ""state"": ""custom"",
                ""custom_servers"": [
                    { ""server_name"": ""Pi-hole"", ""sdns_stamp"": ""sdns://AgAAAAAAAAAAAAAPMTcyLjE2LjAuMTY6NTQ0MwovZG5zLXF1ZXJ5"", ""enabled"": true }
                ]
            }
        ]").RootElement;

        // Networks with Pi-hole DNS configured
        var networks = new List<NetworkInfo>
        {
            new()
            {
                Id = "net-1",
                Name = "Trusted",
                VlanId = 10,
                Subnet = "172.16.0.0/24",
                DhcpEnabled = true,
                DnsServers = new List<string> { "172.16.0.16", "172.16.0.26" }
            }
        };

        // Device data with WAN DNS pointing to Pi-hole IPs (same as network DNS)
        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""udm"",
                ""port_table"": [
                    {
                        ""network_name"": ""wan"",
                        ""name"": ""WAN1"",
                        ""up"": true,
                        ""dns"": [""172.16.0.16"", ""172.16.0.26""]
                    }
                ]
            }
        ]").RootElement;

        // Act
        var result = await analyzer.AnalyzeAsync(settings, null, null, networks, deviceData);

        // Assert - WAN DNS should be marked as matched since it points to Pi-hole
        result.WanDnsMatchesDoH.Should().BeTrue("WAN DNS servers match the detected Pi-hole IPs");
        result.ExpectedDnsProvider.Should().Be("Pi-hole");
        result.WanDnsProvider.Should().Be("Pi-hole");
        result.HasThirdPartyDns.Should().BeTrue();
    }

    [Fact]
    public async Task Analyze_WanDnsDoesNotMatchThirdPartyDns_UnknownIp_MarksAsMismatched()
    {
        // Arrange - DoH configured with third-party (Pi-hole) DNS, WAN DNS is unknown IP
        var analyzer = CreateAnalyzerWithPiholeDetection();

        var settings = JsonDocument.Parse(@"[
            {
                ""key"": ""doh"",
                ""state"": ""custom"",
                ""custom_servers"": [
                    { ""server_name"": ""Pi-hole"", ""sdns_stamp"": ""sdns://AgAAAAAAAAAAAAAPMTcyLjE2LjAuMTY6NTQ0MwovZG5zLXF1ZXJ5"", ""enabled"": true }
                ]
            }
        ]").RootElement;

        // Networks with Pi-hole DNS configured
        var networks = new List<NetworkInfo>
        {
            new()
            {
                Id = "net-1",
                Name = "Trusted",
                VlanId = 10,
                Subnet = "172.16.0.0/24",
                DhcpEnabled = true,
                DnsServers = new List<string> { "172.16.0.16" }
            }
        };

        // Device data with WAN DNS pointing to unknown IPs (RFC 5737 test range - not a known provider)
        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""udm"",
                ""port_table"": [
                    {
                        ""network_name"": ""wan"",
                        ""name"": ""WAN1"",
                        ""up"": true,
                        ""dns"": [""192.0.2.1"", ""192.0.2.2""]
                    }
                ]
            }
        ]").RootElement;

        // Act
        var result = await analyzer.AnalyzeAsync(settings, null, null, networks, deviceData);

        // Assert - WAN DNS should NOT be marked as matched (unknown IPs don't match Pi-hole or any known provider)
        result.HasThirdPartyDns.Should().BeTrue("Pi-hole should be detected on network DNS");
        result.WanDnsMatchesDoH.Should().BeFalse("WAN DNS (192.0.2.x) doesn't match Pi-hole or any known provider");
    }

    [Fact]
    public async Task Analyze_WanDnsPointsToKnownProvider_WithThirdPartyDetected_MarksAsMatched()
    {
        // Arrange - Pi-hole detected on LAN, but WAN DNS correctly points to Google
        // This is a valid configuration - user may want Pi-hole for LAN and Google for WAN fallback
        var analyzer = CreateAnalyzerWithPiholeDetection();

        var settings = JsonDocument.Parse(@"[
            {
                ""key"": ""doh"",
                ""state"": ""custom"",
                ""custom_servers"": [
                    { ""server_name"": ""Pi-hole"", ""sdns_stamp"": ""sdns://AgAAAAAAAAAAAAAPMTcyLjE2LjAuMTY6NTQ0MwovZG5zLXF1ZXJ5"", ""enabled"": true }
                ]
            }
        ]").RootElement;

        // Networks with Pi-hole DNS configured
        var networks = new List<NetworkInfo>
        {
            new()
            {
                Id = "net-1",
                Name = "Trusted",
                VlanId = 10,
                Subnet = "172.16.0.0/24",
                DhcpEnabled = true,
                DnsServers = new List<string> { "172.16.0.16" }
            }
        };

        // Device data with WAN DNS pointing to Google DNS
        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""udm"",
                ""port_table"": [
                    {
                        ""network_name"": ""wan"",
                        ""name"": ""WAN1"",
                        ""up"": true,
                        ""dns"": [""8.8.8.8"", ""8.8.4.4""]
                    }
                ]
            }
        ]").RootElement;

        // Act
        var result = await analyzer.AnalyzeAsync(settings, null, null, networks, deviceData);

        // Assert - WAN DNS pointing to Google is correctly configured
        result.HasThirdPartyDns.Should().BeTrue("Pi-hole should be detected on network DNS");
        result.WanDnsMatchesDoH.Should().BeTrue("WAN DNS (8.8.8.8) is correctly configured for Google");
        result.WanDnsProvider.Should().Be("Google");
    }

    [Fact]
    public async Task Analyze_PartialWanDnsMatchThirdPartyDns_RequiresAllToMatch()
    {
        // Arrange - One WAN DNS matches Pi-hole, other is unknown
        // The third-party match requires ALL WAN DNS to match Pi-hole
        var analyzer = CreateAnalyzerWithPiholeDetection();

        var settings = JsonDocument.Parse(@"[
            {
                ""key"": ""doh"",
                ""state"": ""custom"",
                ""custom_servers"": [
                    { ""server_name"": ""Pi-hole"", ""sdns_stamp"": ""sdns://AgAAAAAAAAAAAAAPMTcyLjE2LjAuMTY6NTQ0MwovZG5zLXF1ZXJ5"", ""enabled"": true }
                ]
            }
        ]").RootElement;

        // Networks with Pi-hole DNS configured
        var networks = new List<NetworkInfo>
        {
            new()
            {
                Id = "net-1",
                Name = "Trusted",
                VlanId = 10,
                Subnet = "172.16.0.0/24",
                DhcpEnabled = true,
                DnsServers = new List<string> { "172.16.0.16" }
            }
        };

        // Device data with WAN DNS - one Pi-hole, one unknown (not a known provider)
        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""udm"",
                ""port_table"": [
                    {
                        ""network_name"": ""wan"",
                        ""name"": ""WAN1"",
                        ""up"": true,
                        ""dns"": [""172.16.0.16"", ""192.0.2.1""]
                    }
                ]
            }
        ]").RootElement;

        // Act
        var result = await analyzer.AnalyzeAsync(settings, null, null, networks, deviceData);

        // Assert - Partial match doesn't count as fully matched for third-party DNS
        // But since 172.16.0.16 is in the WAN DNS, the third-party check will pass (all in thirdPartyIps or unknown)
        result.HasThirdPartyDns.Should().BeTrue();
        // This should be false because not all WAN DNS match Pi-hole (192.0.2.1 is unknown)
        result.WanDnsMatchesDoH.Should().BeFalse("Partial match - 172.16.0.16 matches Pi-hole but 192.0.2.1 is unknown");
    }

    [Fact]
    public async Task Analyze_MultipleWanInterfacesWithThirdPartyDns_AllMatch()
    {
        // Arrange - Multiple WAN interfaces all pointing to Pi-hole
        var analyzer = CreateAnalyzerWithPiholeDetection();

        var settings = JsonDocument.Parse(@"[
            {
                ""key"": ""doh"",
                ""state"": ""custom"",
                ""custom_servers"": [
                    { ""server_name"": ""Pi-hole"", ""sdns_stamp"": ""sdns://AgAAAAAAAAAAAAAPMTcyLjE2LjAuMTY6NTQ0MwovZG5zLXF1ZXJ5"", ""enabled"": true }
                ]
            }
        ]").RootElement;

        // Networks with Pi-hole DNS configured
        var networks = new List<NetworkInfo>
        {
            new()
            {
                Id = "net-1",
                Name = "Trusted",
                VlanId = 10,
                Subnet = "172.16.0.0/24",
                DhcpEnabled = true,
                DnsServers = new List<string> { "172.16.0.16", "172.16.0.26" }
            }
        };

        // Device data with multiple WAN interfaces all using Pi-hole
        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""udm"",
                ""port_table"": [
                    {
                        ""network_name"": ""wan"",
                        ""name"": ""WAN1"",
                        ""up"": true,
                        ""dns"": [""172.16.0.16""]
                    },
                    {
                        ""network_name"": ""wan2"",
                        ""name"": ""WAN2"",
                        ""up"": true,
                        ""dns"": [""172.16.0.26""]
                    }
                ]
            }
        ]").RootElement;

        // Act
        var result = await analyzer.AnalyzeAsync(settings, null, null, networks, deviceData);

        // Assert - All WAN interfaces use Pi-hole, should be matched
        result.WanDnsMatchesDoH.Should().BeTrue("All WAN interfaces point to Pi-hole servers");
        result.ExpectedDnsProvider.Should().Be("Pi-hole");
        result.WanInterfaces.Should().HaveCount(2);
        result.WanInterfaces.Should().OnlyContain(w => w.MatchesDoH);
    }

    [Fact]
    public async Task Analyze_NoDoh_WithThirdPartyDns_WanDnsMatchesPihole_MarksAsMatched()
    {
        // Arrange - NO DoH configured, but Pi-hole detected on network, WAN DNS points to Pi-hole
        // This is the bug scenario from GitHub issue #187 - user has DoH off but Pi-hole as DNS
        var analyzer = CreateAnalyzerWithPiholeDetection();

        // NO DoH settings - pass null or empty
        JsonElement? settings = null;

        // Networks with Pi-hole DNS configured (will trigger third-party DNS detection)
        var networks = new List<NetworkInfo>
        {
            new()
            {
                Id = "net-1",
                Name = "Trusted",
                VlanId = 10,
                Subnet = "172.16.0.0/24",
                DhcpEnabled = true,
                DnsServers = new List<string> { "172.16.0.16", "172.16.0.26" }
            }
        };

        // Device data with WAN DNS pointing to Pi-hole IPs
        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""udm"",
                ""port_table"": [
                    {
                        ""network_name"": ""wan"",
                        ""name"": ""WAN1"",
                        ""up"": true,
                        ""dns"": [""172.16.0.16"", ""172.16.0.26""]
                    },
                    {
                        ""network_name"": ""wan2"",
                        ""name"": ""Cell Backup WAN"",
                        ""up"": true,
                        ""dns"": [""172.16.0.16"", ""172.16.0.26""]
                    }
                ]
            }
        ]").RootElement;

        // Act
        var result = await analyzer.AnalyzeAsync(settings, null, null, networks, deviceData);

        // Assert - WAN DNS should be marked as matched since it points to Pi-hole
        result.DohConfigured.Should().BeFalse("DoH is not configured");
        result.HasThirdPartyDns.Should().BeTrue("Pi-hole should be detected from network DNS");
        result.WanDnsMatchesDoH.Should().BeTrue("WAN DNS servers match the detected Pi-hole IPs");
        result.ExpectedDnsProvider.Should().Be("Pi-hole");
        result.WanDnsProvider.Should().Be("Pi-hole");
        result.WanInterfaces.Should().HaveCount(2);
        result.WanInterfaces.Should().OnlyContain(w => w.MatchesDoH, "All WAN interfaces point to Pi-hole");
    }

    [Fact]
    public async Task Analyze_NoDoh_WithThirdPartyDns_WanDnsDoesNotMatchPihole_MarksAsMismatched()
    {
        // Arrange - NO DoH configured, Pi-hole detected, but WAN DNS points elsewhere
        var analyzer = CreateAnalyzerWithPiholeDetection();

        JsonElement? settings = null;

        var networks = new List<NetworkInfo>
        {
            new()
            {
                Id = "net-1",
                Name = "Trusted",
                VlanId = 10,
                Subnet = "172.16.0.0/24",
                DhcpEnabled = true,
                DnsServers = new List<string> { "172.16.0.16", "172.16.0.26" }
            }
        };

        // WAN DNS pointing to external IPs (not Pi-hole)
        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""udm"",
                ""port_table"": [
                    {
                        ""network_name"": ""wan"",
                        ""name"": ""WAN1"",
                        ""up"": true,
                        ""dns"": [""8.8.8.8"", ""8.8.4.4""]
                    }
                ]
            }
        ]").RootElement;

        // Act
        var result = await analyzer.AnalyzeAsync(settings, null, null, networks, deviceData);

        // Assert - WAN DNS should NOT match since it doesn't point to Pi-hole
        result.DohConfigured.Should().BeFalse("DoH is not configured");
        result.HasThirdPartyDns.Should().BeTrue("Pi-hole should be detected from network DNS");
        result.WanDnsMatchesDoH.Should().BeFalse("WAN DNS (8.8.8.8) doesn't match Pi-hole IPs");
    }

    [Fact]
    public async Task Analyze_NoDoh_NoThirdPartyDns_SkipsWanDnsValidation()
    {
        // Arrange - NO DoH configured, NO third-party DNS, just regular WAN DNS
        // Validation should be skipped entirely (no issues generated for WAN DNS)
        var settings = JsonDocument.Parse(@"[]").RootElement;

        // Networks using gateway as DNS (no third-party DNS)
        var networks = new List<NetworkInfo>
        {
            new()
            {
                Id = "net-1",
                Name = "Default",
                VlanId = 1,
                Subnet = "192.168.1.0/24",
                Gateway = "192.168.1.1",
                DhcpEnabled = true,
                DnsServers = new List<string> { "192.168.1.1" } // Gateway as DNS
            }
        };

        // Device data with WAN DNS pointing to external DNS
        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""udm"",
                ""port_table"": [
                    {
                        ""network_name"": ""wan"",
                        ""name"": ""WAN1"",
                        ""up"": true,
                        ""dns"": [""8.8.8.8"", ""8.8.4.4""]
                    }
                ]
            }
        ]").RootElement;

        // Act
        var result = await _analyzer.AnalyzeAsync(settings, null, null, networks, deviceData);

        // Assert - Validation should be skipped (no DoH, no third-party DNS)
        result.DohConfigured.Should().BeFalse("DoH is not configured");
        result.HasThirdPartyDns.Should().BeFalse("No third-party DNS detected");
        result.WanDnsServers.Should().Contain("8.8.8.8");
        // WanDnsMatchesDoH should remain false (default) but no mismatch issues should be generated
        result.Issues.Should().NotContain(i => i.Type == "DNS_WAN_MISMATCH",
            "WAN DNS validation should be skipped when neither DoH nor third-party DNS is configured");
    }

    [Fact]
    public async Task Analyze_NoDoh_WithThirdPartyDns_PartialWanDnsMatch_MarksAsMismatched()
    {
        // Arrange - NO DoH, Pi-hole detected, but only some WAN DNS matches
        var analyzer = CreateAnalyzerWithPiholeDetection();

        JsonElement? settings = null;

        var networks = new List<NetworkInfo>
        {
            new()
            {
                Id = "net-1",
                Name = "Trusted",
                VlanId = 10,
                Subnet = "172.16.0.0/24",
                DhcpEnabled = true,
                DnsServers = new List<string> { "172.16.0.16", "172.16.0.26" }
            }
        };

        // One WAN interface matches Pi-hole, other doesn't
        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""udm"",
                ""port_table"": [
                    {
                        ""network_name"": ""wan"",
                        ""name"": ""WAN1"",
                        ""up"": true,
                        ""dns"": [""172.16.0.16"", ""172.16.0.26""]
                    },
                    {
                        ""network_name"": ""wan2"",
                        ""name"": ""Cell Backup"",
                        ""up"": true,
                        ""dns"": [""8.8.8.8"", ""8.8.4.4""]
                    }
                ]
            }
        ]").RootElement;

        // Act
        var result = await analyzer.AnalyzeAsync(settings, null, null, networks, deviceData);

        // Assert - Should detect the mismatch on wan2
        result.DohConfigured.Should().BeFalse();
        result.HasThirdPartyDns.Should().BeTrue();
        // Overall WanDnsMatchesDoH should be false because not all match
        result.WanDnsMatchesDoH.Should().BeFalse("Not all WAN interfaces point to Pi-hole");
        result.WanInterfaces.Should().HaveCount(2);
    }

    #endregion
}
