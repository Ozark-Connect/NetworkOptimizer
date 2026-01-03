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
    public async Task Analyze_WithQuicBlockRule_DetectsRule()
    {
        var firewall = JsonDocument.Parse(@"[
            {
                ""name"": ""Block QUIC DoH"",
                ""enabled"": true,
                ""action"": ""drop"",
                ""protocol"": ""udp"",
                ""destination"": { ""port"": ""443"" }
            }
        ]").RootElement;

        var result = await _analyzer.AnalyzeAsync(null, firewall);

        result.HasQuicBlockRule.Should().BeTrue();
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
            WanDnsMatchesDoH = true,
            DeviceDnsPointsToGateway = true
        };

        var summary = _analyzer.GetSummary(analysisResult);

        summary.FullyProtected.Should().BeTrue();
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
}
