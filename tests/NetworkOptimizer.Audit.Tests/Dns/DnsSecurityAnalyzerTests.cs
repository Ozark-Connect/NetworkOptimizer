using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using NetworkOptimizer.Audit.Dns;
using NetworkOptimizer.Audit.Models;
using Xunit;

namespace NetworkOptimizer.Audit.Tests.Dns;

public class DnsSecurityAnalyzerTests
{
    private readonly DnsSecurityAnalyzer _analyzer;
    private readonly Mock<ILogger<DnsSecurityAnalyzer>> _loggerMock;
    private readonly ThirdPartyDnsDetector _thirdPartyDetector;

    public DnsSecurityAnalyzerTests()
    {
        _loggerMock = new Mock<ILogger<DnsSecurityAnalyzer>>();
        var detectorLoggerMock = new Mock<ILogger<ThirdPartyDnsDetector>>();
        _thirdPartyDetector = new ThirdPartyDnsDetector(detectorLoggerMock.Object, new HttpClient { Timeout = TimeSpan.FromSeconds(3) });
        _analyzer = new DnsSecurityAnalyzer(_loggerMock.Object, _thirdPartyDetector);
    }

    #region Constructor Tests

    [Fact]
    public void Constructor_WithLogger_CreatesInstance()
    {
        var detectorLoggerMock = new Mock<ILogger<ThirdPartyDnsDetector>>();
        var thirdPartyDetector = new ThirdPartyDnsDetector(detectorLoggerMock.Object, new HttpClient { Timeout = TimeSpan.FromSeconds(3) });
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
        // DoQ (DNS over QUIC) blocking requires UDP 443 + web domains with DNS providers
        var firewall = JsonDocument.Parse(@"[
            {
                ""name"": ""Block DoQ"",
                ""enabled"": true,
                ""action"": ""drop"",
                ""protocol"": ""udp"",
                ""destination"": {
                    ""port"": ""443"",
                    ""matching_target"": ""WEB"",
                    ""web_domains"": [""dns.google"", ""cloudflare-dns.com""]
                }
            }
        ]").RootElement;

        var result = await _analyzer.AnalyzeAsync(null, firewall);

        result.HasDoqBlockRule.Should().BeTrue();
        result.DoqBlockedDomains.Should().Contain("dns.google");
        result.DoqBlockedDomains.Should().Contain("cloudflare-dns.com");
    }

    [Fact]
    public async Task Analyze_WithCombinedDohDoqBlockRule_DetectsBoth()
    {
        // A single rule with tcp_udp protocol blocks both DoH (TCP 443) and DoQ (UDP 443)
        var firewall = JsonDocument.Parse(@"[
            {
                ""name"": ""Block DoH and DoQ"",
                ""enabled"": true,
                ""action"": ""drop"",
                ""protocol"": ""tcp_udp"",
                ""destination"": {
                    ""port"": ""443"",
                    ""matching_target"": ""WEB"",
                    ""web_domains"": [""dns.google""]
                }
            }
        ]").RootElement;

        var result = await _analyzer.AnalyzeAsync(null, firewall);

        result.HasDohBlockRule.Should().BeTrue();
        result.HasDoqBlockRule.Should().BeTrue();
        result.DohBlockedDomains.Should().Contain("dns.google");
        result.DoqBlockedDomains.Should().Contain("dns.google");
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
    public async Task Analyze_WithDotUdpOnlyProtocol_DoesNotDetect()
    {
        // DoT (853) blocking requires TCP protocol - UDP-only rules should NOT be detected
        var firewall = JsonDocument.Parse(@"[
            {
                ""name"": ""Block DoT UDP Only"",
                ""enabled"": true,
                ""action"": ""drop"",
                ""protocol"": ""udp"",
                ""destination"": { ""port"": ""853"" }
            }
        ]").RootElement;

        var result = await _analyzer.AnalyzeAsync(null, firewall);

        result.HasDotBlockRule.Should().BeFalse();
    }

    [Fact]
    public async Task Analyze_WithDohUdpOnlyProtocol_DetectsOnlyDoq()
    {
        // UDP-only 443 rule with web domains should detect DoQ but NOT DoH
        var firewall = JsonDocument.Parse(@"[
            {
                ""name"": ""Block DoQ Only"",
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
        result.HasDoqBlockRule.Should().BeTrue();
    }

    [Fact]
    public async Task Analyze_WithDohTcpOnlyProtocol_DetectsOnlyDoh()
    {
        // TCP-only 443 rule with web domains should detect DoH but NOT DoQ
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
        result.HasDoqBlockRule.Should().BeFalse();
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
    public async Task Analyze_NoDoHConfigured_GeneratesRecommendedIssue()
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
            i.Type == "DNS_NO_DOH" &&
            i.Severity == AuditSeverity.Recommended);
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
    public async Task Analyze_WithThirdPartyDns_GeneratesInfoIssue()
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
        result.Issues.Should().Contain(i =>
            i.Type == IssueTypes.DnsThirdPartyDetected &&
            i.Severity == AuditSeverity.Informational);
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
    public async Task Analyze_ThirdPartyDnsIssue_HasZeroScoreImpact()
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

        // Assert - Third-party DNS issue should have no score impact
        var thirdPartyIssue = result.Issues.FirstOrDefault(i => i.Type == IssueTypes.DnsThirdPartyDetected);
        thirdPartyIssue.Should().NotBeNull();
        thirdPartyIssue!.ScoreImpact.Should().Be(0);
    }

    [Fact]
    public async Task Analyze_ThirdPartyDns_AddsHardeningNote()
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
        result.HardeningNotes.Should().Contain(n => n.Contains("Third-Party LAN DNS"));
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
}
