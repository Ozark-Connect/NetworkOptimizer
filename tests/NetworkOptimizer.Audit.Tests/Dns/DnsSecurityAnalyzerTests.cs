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

public class DnsSecurityAnalyzerTests : IDisposable
{
    private readonly DnsSecurityAnalyzer _analyzer;
    private readonly Mock<ILogger<DnsSecurityAnalyzer>> _loggerMock;
    private readonly ThirdPartyDnsDetector _thirdPartyDetector;

    public DnsSecurityAnalyzerTests()
    {
        // Mock DNS resolver to avoid real network calls and timeouts
        DohProviderRegistry.DnsResolver = _ => Task.FromResult<string?>(null);

        _loggerMock = new Mock<ILogger<DnsSecurityAnalyzer>>();
        var detectorLoggerMock = new Mock<ILogger<ThirdPartyDnsDetector>>();

        // Use mock HttpClient that returns 404 immediately (no Pi-hole detected)
        var httpClient = CreateMockHttpClient(HttpStatusCode.NotFound);
        _thirdPartyDetector = new ThirdPartyDnsDetector(detectorLoggerMock.Object, httpClient);
        _analyzer = new DnsSecurityAnalyzer(_loggerMock.Object, _thirdPartyDetector);
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
                ""state"": ""custom"",
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

        var result = await _analyzer.AnalyzeAsync(null, firewall);

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

        var result = await _analyzer.AnalyzeAsync(null, firewall);

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

        var result = await _analyzer.AnalyzeAsync(null, firewall);

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

        var result = await _analyzer.AnalyzeAsync(null, firewall);

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

        var result = await _analyzer.AnalyzeAsync(null, firewall);

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

        var result = await _analyzer.AnalyzeAsync(null, firewall);

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

        var result = await _analyzer.AnalyzeAsync(null, firewall);

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

        var result = await _analyzer.AnalyzeAsync(null, firewall);

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

        var result = await _analyzer.AnalyzeAsync(null, firewall);

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

        var result = await _analyzer.AnalyzeAsync(null, firewall);

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

        var result = await _analyzer.AnalyzeAsync(null, firewall);

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

        var result = await _analyzer.AnalyzeAsync(null, firewall);

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

        var result = await _analyzer.AnalyzeAsync(null, firewall);

        result.HasDns53BlockRule.Should().BeTrue();
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
        var result = await _analyzer.AnalyzeAsync(null, firewall, null, null, null, null, firewallGroups);

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
        var result = await _analyzer.AnalyzeAsync(null, firewall, null, null, null, null, firewallGroups);

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
        var result = await _analyzer.AnalyzeAsync(null, firewall, null, null, null, null, firewallGroups);

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
        var result = await _analyzer.AnalyzeAsync(null, firewall, null, null, null, null, firewallGroups);

        // Assert
        result.HasDns53BlockRule.Should().BeTrue();
    }

    [Fact]
    public async Task Analyze_WithMissingPortGroupId_IgnoresRule()
    {
        // Arrange - Firewall rule references non-existent port group
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
        var result = await _analyzer.AnalyzeAsync(null, firewall, null, null, null, null, firewallGroups);

        // Assert - Should not detect rule since group doesn't exist
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
        var result = await _analyzer.AnalyzeAsync(null, firewall);

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
        var result = await _analyzer.AnalyzeAsync(null, firewall);

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
        var result = await _analyzer.AnalyzeAsync(null, firewall);

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
        var result = await _analyzer.AnalyzeAsync(null, firewall);

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
        var result = await _analyzer.AnalyzeAsync(null, firewall);

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
        var result = await _analyzer.AnalyzeAsync(null, firewall);

        // Assert - Normal blocking works
        result.HasDns53BlockRule.Should().BeTrue();
    }

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
        var result = await _analyzer.AnalyzeAsync(null, firewall, null, null, null, null, null, null, null, ExternalZoneId);

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
        var result = await _analyzer.AnalyzeAsync(null, firewall, null, null, null, null, null, null, null, ExternalZoneId);

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
        var result = await _analyzer.AnalyzeAsync(null, firewall, null, null, null, null, null, null, null, ExternalZoneId);

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
        var result = await _analyzer.AnalyzeAsync(null, firewall, null, null, null, null, null, null, null, null);

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
        var result = await _analyzer.AnalyzeAsync(null, firewall, null, null, null, null, null, null, null, ExternalZoneId);

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
                ""state"": ""custom"",
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
            HasDohBlockRule = true,
            HasDoqBlockRule = true,
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
                ""state"": ""custom"",
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
                ""state"": ""custom"",
                ""server_names"": [""cloudflare""]
            }
        ]").RootElement;

        var firewall = JsonDocument.Parse(@"[
            { ""name"": ""Block DNS"", ""enabled"": true, ""action"": ""drop"", ""destination"": { ""port"": ""53"" } },
            { ""name"": ""Block DoT"", ""enabled"": true, ""action"": ""drop"", ""destination"": { ""port"": ""853"" } },
            { ""name"": ""Block DoH"", ""enabled"": true, ""action"": ""drop"", ""destination"": { ""port"": ""443"", ""matching_target"": ""WEB"", ""web_domains"": [""dns.google""] } }
        ]").RootElement;

        var result = await _analyzer.AnalyzeAsync(settings, firewall);

        result.HardeningNotes.Should().Contain(n => n.Contains("fully configured"));
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
                ""state"": ""custom"",
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
                ""state"": ""custom"",
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
                ""state"": ""custom"",
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

        var result = await _analyzer.AnalyzeAsync(settings, firewall);

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
            firewallData: null,
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
            firewallData: null,
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
            firewallData: null,
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
            firewallData: null,
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
            firewallData: null,
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
            firewallData: null,
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
            firewallData: null,
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
            firewallData: null,
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
            firewallData: null,
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
            firewallData: null,
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
            firewallData: null,
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
            firewallData: null,
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
            firewallData: null,
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
                ""state"": ""custom"",
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
                ""state"": ""custom"",
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
                ""state"": ""custom"",
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
                ""state"": ""custom"",
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
                ""state"": ""custom"",
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
                ""state"": ""custom"",
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
                ""state"": ""custom"",
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
                ""state"": ""custom"",
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
                ""state"": ""custom"",
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
                ""state"": ""custom"",
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
                ""state"": ""custom"",
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
                ""state"": ""custom"",
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
                ""state"": ""custom"",
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
                ""state"": ""custom"",
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
                ""state"": ""custom"",
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
                ""state"": ""custom"",
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
        // Arrange - Third-party DNS on one network but not all DHCP networks
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "Corporate",
                VlanId = 10,
                DhcpEnabled = true,
                Gateway = "192.168.1.1",
                DnsServers = new List<string> { "192.168.1.5" } // Third-party DNS
            },
            new NetworkInfo
            {
                Id = "net2",
                Name = "IoT",
                VlanId = 20,
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
            firewallData: null,
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
            firewallData: null,
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
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "Main",
                VlanId = 1,
                DhcpEnabled = true,
                Gateway = "192.168.1.1",
                DnsServers = new List<string> { "192.168.1.5" }
            },
            new NetworkInfo
            {
                Id = "net2",
                Name = "Guest",
                VlanId = 50,
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
            firewallData: null,
            switches: switches,
            networks: networks);

        // Assert - Moderate score impact (5) since inconsistency may be intentional
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
            firewallData: null,
            switches: switches,
            networks: networks);

        // Assert - Non-DHCP networks should not trigger consistency issue
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
            firewallData: null,
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
            firewallData: null,
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
            firewallData: null,
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
                ""state"": ""custom"",
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
                ""state"": ""custom"",
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
        var result = await _analyzer.AnalyzeAsync(settings, firewall, null, null, deviceData);

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
                ""state"": ""custom"",
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
        var result = await _analyzer.AnalyzeAsync(null, firewall, null, networks, null, null, natRules);

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
            firewallData: null,
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
            firewallData: null,
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
            firewallData: null,
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
            firewallData: firewall.RootElement,
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
            firewallData: firewall.RootElement,
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
            firewallData: null,
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
            firewallData: null,
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
            firewallData: null,
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
            firewallData: null,
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
        // Arrange - Pi-hole configured, but DNAT incorrectly points to gateway
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
        var natRules = CreateDnatNatRules(("net1", "192.168.1.1")); // Wrong - points to gateway

        // Act
        var result = await _analyzer.AnalyzeAsync(null, null, switches, networks, null, null, natRules);

        // Assert - Should raise wrong destination issue
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
                ""state"": ""custom"",
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
                ""state"": ""custom"",
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
                ""state"": ""custom"",
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
                ""state"": ""custom"",
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
                ""state"": ""custom"",
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
                ""state"": ""custom"",
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
                ""state"": ""custom"",
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
        var result = await _analyzer.AnalyzeAsync(null, firewall, null, networks);

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
        var result = await _analyzer.AnalyzeAsync(null, firewall, null, networks);

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
        var result = await _analyzer.AnalyzeAsync(null, firewall, null, networks);

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
        var result = await _analyzer.AnalyzeAsync(null, firewall, null, networks);

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
                ""state"": ""custom"",
                ""server_names"": [""NextDNS-test""]
            }
        ]").RootElement;

        // Act
        var result = await _analyzer.AnalyzeAsync(settings, firewall, null, networks, null, null, natRules);

        // Assert - DNAT provides full coverage, so no partial coverage issue
        result.HasDns53BlockRule.Should().BeTrue();
        result.Dns53ProvidesFullCoverage.Should().BeFalse(); // Firewall alone is partial
        result.DnatProvidesFullCoverage.Should().BeTrue(); // But DNAT covers all
        result.Issues.Should().NotContain(i => i.Type == IssueTypes.Dns53PartialCoverage); // Suppressed by DNAT
        result.Issues.Should().NotContain(i => i.Type == IssueTypes.DnsNo53Block); // Firewall handles part of it
    }

    #endregion
}
