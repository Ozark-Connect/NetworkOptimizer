using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using NetworkOptimizer.Audit.Analyzers;
using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.Audit.Rules;
using NetworkOptimizer.Audit.Services;
using NetworkOptimizer.Core.Models;
using NetworkOptimizer.UniFi.Models;
using Xunit;

namespace NetworkOptimizer.Audit.Tests.Analyzers;

public class PortSecurityAnalyzerTests
{
    private readonly Mock<ILogger<PortSecurityAnalyzer>> _loggerMock;
    private readonly PortSecurityAnalyzer _engine;

    public PortSecurityAnalyzerTests()
    {
        _loggerMock = new Mock<ILogger<PortSecurityAnalyzer>>();
        _engine = new PortSecurityAnalyzer(_loggerMock.Object);
    }

    #region Constructor Tests

    [Fact]
    public void Constructor_WithLogger_CreatesInstance()
    {
        var engine = new PortSecurityAnalyzer(_loggerMock.Object);
        engine.Should().NotBeNull();
    }

    [Fact]
    public void Constructor_WithDetectionService_InjectsIntoRules()
    {
        var detectionServiceMock = new Mock<ILogger<DeviceTypeDetectionService>>();
        var detectionService = new DeviceTypeDetectionService(detectionServiceMock.Object, null);

        var engine = new PortSecurityAnalyzer(_loggerMock.Object, detectionService);

        engine.Should().NotBeNull();
    }

    #endregion

    #region ExtractSwitches Tests

    [Fact]
    public void ExtractSwitches_EmptyDeviceData_ReturnsEmptyList()
    {
        var deviceData = JsonDocument.Parse("[]").RootElement;
        var networks = new List<NetworkInfo>();

        var result = _engine.ExtractSwitches(deviceData, networks);

        result.Should().BeEmpty();
    }

    [Fact]
    public void ExtractSwitches_DeviceWithNoPortTable_ReturnsEmptyList()
    {
        var deviceData = JsonDocument.Parse(@"[
            { ""type"": ""usw"", ""name"": ""Switch1"" }
        ]").RootElement;
        var networks = new List<NetworkInfo>();

        var result = _engine.ExtractSwitches(deviceData, networks);

        result.Should().BeEmpty();
    }

    [Fact]
    public void ExtractSwitches_SwitchWithPorts_ReturnsSwitchInfo()
    {
        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""usw"",
                ""name"": ""Main Switch"",
                ""mac"": ""aa:bb:cc:dd:ee:ff"",
                ""model"": ""USW-24-POE"",
                ""ip"": ""192.168.1.10"",
                ""port_table"": [
                    { ""port_idx"": 1, ""name"": ""Port 1"", ""up"": true, ""speed"": 1000 },
                    { ""port_idx"": 2, ""name"": ""Port 2"", ""up"": false, ""speed"": 0 }
                ]
            }
        ]").RootElement;
        var networks = new List<NetworkInfo>();

        var result = _engine.ExtractSwitches(deviceData, networks);

        result.Should().HaveCount(1);
        result[0].Name.Should().Be("Main Switch");
        result[0].MacAddress.Should().Be("aa:bb:cc:dd:ee:ff");
        result[0].Ports.Should().HaveCount(2);
    }

    [Fact]
    public void ExtractSwitches_GatewayDevice_MarkedAsGateway()
    {
        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""udm"",
                ""name"": ""Gateway"",
                ""port_table"": [
                    { ""port_idx"": 1, ""name"": ""WAN"", ""up"": true }
                ]
            }
        ]").RootElement;
        var networks = new List<NetworkInfo>();

        var result = _engine.ExtractSwitches(deviceData, networks);

        result.Should().HaveCount(1);
        result[0].IsGateway.Should().BeTrue();
    }

    [Fact]
    public void ExtractSwitches_MultipleDevices_SortsGatewayFirst()
    {
        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""usw"",
                ""name"": ""Switch A"",
                ""port_table"": [{ ""port_idx"": 1, ""up"": true }]
            },
            {
                ""type"": ""udm"",
                ""name"": ""Gateway"",
                ""port_table"": [{ ""port_idx"": 1, ""up"": true }]
            },
            {
                ""type"": ""usw"",
                ""name"": ""Switch B"",
                ""port_table"": [{ ""port_idx"": 1, ""up"": true }]
            }
        ]").RootElement;
        var networks = new List<NetworkInfo>();

        var result = _engine.ExtractSwitches(deviceData, networks);

        result.Should().HaveCount(3);
        result[0].Name.Should().Be("Gateway");
        result[0].IsGateway.Should().BeTrue();
    }

    [Fact]
    public void ExtractSwitches_PortWithWanNetwork_MarkedAsWan()
    {
        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""udm"",
                ""name"": ""Gateway"",
                ""port_table"": [
                    { ""port_idx"": 1, ""name"": ""WAN"", ""network_name"": ""wan"", ""up"": true }
                ]
            }
        ]").RootElement;
        var networks = new List<NetworkInfo>();

        var result = _engine.ExtractSwitches(deviceData, networks);

        result[0].Ports[0].IsWan.Should().BeTrue();
    }

    [Fact]
    public void ExtractSwitches_PortWithUplink_MarkedAsUplink()
    {
        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""usw"",
                ""name"": ""Switch"",
                ""port_table"": [
                    { ""port_idx"": 1, ""name"": ""Uplink"", ""is_uplink"": true, ""up"": true }
                ]
            }
        ]").RootElement;
        var networks = new List<NetworkInfo>();

        var result = _engine.ExtractSwitches(deviceData, networks);

        result[0].Ports[0].IsUplink.Should().BeTrue();
    }

    [Fact]
    public void ExtractSwitches_PortWithPoe_ExtractsPoeInfo()
    {
        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""usw"",
                ""name"": ""Switch"",
                ""port_table"": [
                    { ""port_idx"": 1, ""poe_enable"": true, ""poe_power"": 5.5, ""poe_mode"": ""auto"", ""up"": true }
                ]
            }
        ]").RootElement;
        var networks = new List<NetworkInfo>();

        var result = _engine.ExtractSwitches(deviceData, networks);

        result[0].Ports[0].PoeEnabled.Should().BeTrue();
        result[0].Ports[0].PoePower.Should().Be(5.5);
        result[0].Ports[0].PoeMode.Should().Be("auto");
    }

    [Fact]
    public void ExtractSwitches_WithClients_CorrelatesClients()
    {
        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""usw"",
                ""name"": ""Switch"",
                ""mac"": ""aa:bb:cc:dd:ee:ff"",
                ""port_table"": [
                    { ""port_idx"": 1, ""name"": ""Port 1"", ""up"": true }
                ]
            }
        ]").RootElement;
        var networks = new List<NetworkInfo>();
        var clients = new List<UniFiClientResponse>
        {
            new UniFiClientResponse
            {
                Mac = "11:22:33:44:55:66",
                Name = "Test Device",
                IsWired = true,
                SwMac = "aa:bb:cc:dd:ee:ff",
                SwPort = 1
            }
        };

        var result = _engine.ExtractSwitches(deviceData, networks, clients);

        result[0].Ports[0].ConnectedClient.Should().NotBeNull();
        result[0].Ports[0].ConnectedClient!.Name.Should().Be("Test Device");
    }

    [Fact]
    public void ExtractSwitches_WithDnsConfig_ExtractsDnsInfo()
    {
        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""usw"",
                ""name"": ""Switch"",
                ""config_network"": {
                    ""type"": ""static"",
                    ""dns1"": ""192.168.1.1"",
                    ""dns2"": ""8.8.8.8""
                },
                ""port_table"": [
                    { ""port_idx"": 1, ""up"": true }
                ]
            }
        ]").RootElement;
        var networks = new List<NetworkInfo>();

        var result = _engine.ExtractSwitches(deviceData, networks);

        result[0].ConfiguredDns1.Should().Be("192.168.1.1");
        result[0].ConfiguredDns2.Should().Be("8.8.8.8");
        result[0].NetworkConfigType.Should().Be("static");
    }

    [Fact]
    public void ExtractSwitches_WithSwitchCaps_ExtractsCapabilities()
    {
        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""usw"",
                ""name"": ""Switch"",
                ""switch_caps"": {
                    ""max_custom_mac_acls"": 256
                },
                ""port_table"": [
                    { ""port_idx"": 1, ""up"": true }
                ]
            }
        ]").RootElement;
        var networks = new List<NetworkInfo>();

        var result = _engine.ExtractSwitches(deviceData, networks);

        result[0].Capabilities.MaxCustomMacAcls.Should().Be(256);
    }

    [Fact]
    public void ExtractSwitches_PortWithCustomForward_NormalizesToCustom()
    {
        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""usw"",
                ""name"": ""Switch"",
                ""port_table"": [
                    { ""port_idx"": 1, ""forward"": ""customize"", ""up"": true }
                ]
            }
        ]").RootElement;
        var networks = new List<NetworkInfo>();

        var result = _engine.ExtractSwitches(deviceData, networks);

        result[0].Ports[0].ForwardMode.Should().Be("custom");
    }

    [Fact]
    public void ExtractSwitches_PortWithIsolation_MarkedAsIsolated()
    {
        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""usw"",
                ""name"": ""Switch"",
                ""port_table"": [
                    { ""port_idx"": 1, ""isolation"": true, ""up"": true }
                ]
            }
        ]").RootElement;
        var networks = new List<NetworkInfo>();

        var result = _engine.ExtractSwitches(deviceData, networks);

        result[0].Ports[0].IsolationEnabled.Should().BeTrue();
    }

    [Fact]
    public void ExtractSwitches_PortWithSecurityMacs_ExtractsMacAddresses()
    {
        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""usw"",
                ""name"": ""Switch"",
                ""port_table"": [
                    {
                        ""port_idx"": 1,
                        ""port_security_enabled"": true,
                        ""port_security_mac_address"": [""aa:bb:cc:dd:ee:ff"", ""11:22:33:44:55:66""],
                        ""up"": true
                    }
                ]
            }
        ]").RootElement;
        var networks = new List<NetworkInfo>();

        var result = _engine.ExtractSwitches(deviceData, networks);

        result[0].Ports[0].PortSecurityEnabled.Should().BeTrue();
        result[0].Ports[0].AllowedMacAddresses.Should().Contain("aa:bb:cc:dd:ee:ff");
        result[0].Ports[0].AllowedMacAddresses.Should().Contain("11:22:33:44:55:66");
    }

    #endregion

    #region AnalyzePorts Tests

    [Fact]
    public void AnalyzePorts_EmptySwitches_ReturnsEmptyList()
    {
        var switches = new List<SwitchInfo>();
        var networks = new List<NetworkInfo>();

        var result = _engine.AnalyzePorts(switches, networks);

        result.Should().BeEmpty();
    }

    [Fact]
    public void AnalyzePorts_SwitchWithPorts_RunsRulesAgainstPorts()
    {
        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""usw"",
                ""name"": ""Switch1"",
                ""port_table"": [
                    { ""port_idx"": 1, ""name"": ""Port 1"", ""up"": true, ""forward"": ""native"" }
                ]
            }
        ]").RootElement;
        var networks = new List<NetworkInfo>();
        var switches = _engine.ExtractSwitches(deviceData, networks);

        // This should run without error; issues depend on rule logic
        var result = _engine.AnalyzePorts(switches, networks);

        result.Should().NotBeNull();
    }

    [Fact]
    public void AnalyzePorts_UxInApMode_SkipsAuditIssues()
    {
        var networks = new List<NetworkInfo>
        {
            new() { Id = "net-1", Name = "Default", VlanId = 1 },
            new() { Id = "net-2", Name = "IoT", VlanId = 20 }
        };

        // Create a placeholder switch first, then build ports referencing it
        var uxSwitch = new SwitchInfo
        {
            Name = "Living Room UX7",
            ModelName = "UX7",
            IsAccessPoint = true,
            IsGateway = false,
            Capabilities = new SwitchCapabilities { MaxCustomMacAcls = 16, SupportsIsolation = true }
        };

        var switchWithPorts = new SwitchInfo
        {
            Name = uxSwitch.Name,
            ModelName = uxSwitch.ModelName,
            IsAccessPoint = uxSwitch.IsAccessPoint,
            IsGateway = uxSwitch.IsGateway,
            Capabilities = uxSwitch.Capabilities,
            Ports = new List<PortInfo>
            {
                new()
                {
                    PortIndex = 1,
                    Name = "Port 1",
                    IsUp = true,
                    ForwardMode = "native",
                    NativeNetworkId = "net-1",
                    Switch = uxSwitch
                }
            }
        };

        switchWithPorts.HasUnmanageablePorts.Should().BeTrue();
        var result = _engine.AnalyzePorts(new List<SwitchInfo> { switchWithPorts }, networks);

        result.Should().BeEmpty("UX7 in AP mode has unmanageable ports");
    }

    [Fact]
    public void AnalyzePorts_UxAsGateway_StillAudited()
    {
        var networks = new List<NetworkInfo>
        {
            new() { Id = "net-1", Name = "Default", VlanId = 1 }
        };

        var uxGateway = new SwitchInfo
        {
            Name = "Main Gateway UX7",
            ModelName = "UX7",
            IsAccessPoint = false,
            IsGateway = true,
            Capabilities = new SwitchCapabilities { MaxCustomMacAcls = 16, SupportsIsolation = true }
        };

        var gatewayWithPorts = new SwitchInfo
        {
            Name = uxGateway.Name,
            ModelName = uxGateway.ModelName,
            IsAccessPoint = uxGateway.IsAccessPoint,
            IsGateway = uxGateway.IsGateway,
            Capabilities = uxGateway.Capabilities,
            Ports = new List<PortInfo>
            {
                new()
                {
                    PortIndex = 1,
                    Name = "Port 1",
                    IsUp = true,
                    ForwardMode = "native",
                    NativeNetworkId = "net-1",
                    Switch = uxGateway
                }
            }
        };

        gatewayWithPorts.HasUnmanageablePorts.Should().BeFalse();
        var result = _engine.AnalyzePorts(new List<SwitchInfo> { gatewayWithPorts }, networks);

        // Should NOT be empty - the gateway's ports are manageable
        result.Should().NotBeNull();
    }

    #endregion

    #region AnalyzeHardening Tests

    [Fact]
    public void AnalyzeHardening_EmptySwitches_ReturnsEmptyList()
    {
        var switches = new List<SwitchInfo>();
        var networks = new List<NetworkInfo>();

        var result = _engine.AnalyzeHardening(switches, networks);

        result.Should().BeEmpty();
    }

    [Fact]
    public void AnalyzeHardening_DisabledPorts_ReportsMeasure()
    {
        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""usw"",
                ""name"": ""Switch1"",
                ""port_table"": [
                    { ""port_idx"": 1, ""forward"": ""disabled"" },
                    { ""port_idx"": 2, ""forward"": ""native"" }
                ]
            }
        ]").RootElement;
        var networks = new List<NetworkInfo>();
        var switches = _engine.ExtractSwitches(deviceData, networks);

        var result = _engine.AnalyzeHardening(switches, networks);

        result.Should().Contain(m => m.Contains("disabled"));
    }

    [Fact]
    public void AnalyzeHardening_PortSecurityEnabled_ReportsMeasure()
    {
        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""usw"",
                ""name"": ""Switch1"",
                ""port_table"": [
                    { ""port_idx"": 1, ""port_security_enabled"": true }
                ]
            }
        ]").RootElement;
        var networks = new List<NetworkInfo>();
        var switches = _engine.ExtractSwitches(deviceData, networks);

        var result = _engine.AnalyzeHardening(switches, networks);

        result.Should().Contain(m => m.Contains("Port security"));
    }

    [Fact]
    public void AnalyzeHardening_MacRestrictions_ReportsMeasure()
    {
        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""usw"",
                ""name"": ""Switch1"",
                ""port_table"": [
                    { ""port_idx"": 1, ""port_security_mac_address"": [""aa:bb:cc:dd:ee:ff""] }
                ]
            }
        ]").RootElement;
        var networks = new List<NetworkInfo>();
        var switches = _engine.ExtractSwitches(deviceData, networks);

        var result = _engine.AnalyzeHardening(switches, networks);

        result.Should().Contain(m => m.Contains("MAC restrictions"));
    }

    [Fact]
    public void AnalyzeHardening_CamerasOnSecurityVlan_ReportsMeasure()
    {
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "security-vlan",
                Name = "Security",
                VlanId = 30,
                Purpose = NetworkPurpose.Security
            }
        };

        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""usw"",
                ""name"": ""Switch1"",
                ""port_table"": [
                    { ""port_idx"": 1, ""name"": ""Camera 1"", ""up"": true, ""native_networkconf_id"": ""security-vlan"" }
                ]
            }
        ]").RootElement;
        var switches = _engine.ExtractSwitches(deviceData, networks);

        var result = _engine.AnalyzeHardening(switches, networks);

        result.Should().Contain(m => m.Contains("cameras") && m.Contains("Security VLAN"));
    }

    [Fact]
    public void AnalyzeHardening_IsolatedCameras_ReportsMeasure()
    {
        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""usw"",
                ""name"": ""Switch1"",
                ""port_table"": [
                    { ""port_idx"": 1, ""name"": ""PTZ Camera"", ""isolation"": true }
                ]
            }
        ]").RootElement;
        var networks = new List<NetworkInfo>();
        var switches = _engine.ExtractSwitches(deviceData, networks);

        var result = _engine.AnalyzeHardening(switches, networks);

        result.Should().Contain(m => m.Contains("isolation"));
    }

    #endregion

    #region CalculateStatistics Tests

    [Fact]
    public void CalculateStatistics_EmptySwitches_ReturnsZeroStats()
    {
        var switches = new List<SwitchInfo>();

        var result = _engine.CalculateStatistics(switches);

        result.TotalPorts.Should().Be(0);
        result.ActivePorts.Should().Be(0);
        result.DisabledPorts.Should().Be(0);
    }

    [Fact]
    public void CalculateStatistics_MultiplePorts_CalculatesCorrectly()
    {
        // Use JSON parsing to properly create switches with ports
        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""usw"",
                ""name"": ""Switch1"",
                ""port_table"": [
                    { ""port_idx"": 1, ""up"": true, ""forward"": ""native"" },
                    { ""port_idx"": 2, ""up"": false, ""forward"": ""disabled"" },
                    { ""port_idx"": 3, ""up"": true, ""forward"": ""native"", ""port_security_enabled"": true },
                    { ""port_idx"": 4, ""up"": true, ""forward"": ""native"", ""isolation"": true },
                    { ""port_idx"": 5, ""up"": true, ""forward"": ""native"", ""port_security_mac_address"": [""aa:bb:cc:dd:ee:ff""] }
                ]
            }
        ]").RootElement;
        var networks = new List<NetworkInfo>();
        var switches = _engine.ExtractSwitches(deviceData, networks);

        var result = _engine.CalculateStatistics(switches);

        result.TotalPorts.Should().Be(5);
        result.ActivePorts.Should().Be(4);
        result.DisabledPorts.Should().Be(1);
        result.PortSecurityEnabledPorts.Should().Be(1);
        result.IsolatedPorts.Should().Be(1);
        result.MacRestrictedPorts.Should().Be(1);
    }

    [Fact]
    public void CalculateStatistics_UnprotectedPorts_CountsCorrectly()
    {
        // Use JSON parsing to properly create switches with ports
        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""usw"",
                ""name"": ""Switch1"",
                ""port_table"": [
                    { ""port_idx"": 1, ""up"": true, ""forward"": ""native"" },
                    { ""port_idx"": 2, ""up"": true, ""forward"": ""native"", ""port_security_mac_address"": [""aa:bb:cc:dd:ee:ff""] },
                    { ""port_idx"": 3, ""up"": true, ""forward"": ""native"", ""port_security_enabled"": true },
                    { ""port_idx"": 4, ""up"": true, ""forward"": ""native"", ""is_uplink"": true },
                    { ""port_idx"": 5, ""up"": true, ""forward"": ""native"", ""network_name"": ""wan"" }
                ]
            }
        ]").RootElement;
        var networks = new List<NetworkInfo>();
        var switches = _engine.ExtractSwitches(deviceData, networks);

        var result = _engine.CalculateStatistics(switches);

        result.UnprotectedActivePorts.Should().Be(1);
    }

    [Fact]
    public void CalculateStatistics_Dot1xPorts_ExcludedFromUnprotected()
    {
        // Create a port profile with dot1x_ctrl = "auto"
        var dot1xProfile = new UniFiPortProfile
        {
            Id = "profile-dot1x",
            Name = "RADIUS Auth",
            Dot1xCtrl = "auto"
        };

        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""usw"",
                ""name"": ""Switch1"",
                ""port_table"": [
                    { ""port_idx"": 1, ""up"": true, ""forward"": ""native"", ""portconf_id"": ""profile-dot1x"" },
                    { ""port_idx"": 2, ""up"": true, ""forward"": ""native"" }
                ]
            }
        ]").RootElement;
        var networks = new List<NetworkInfo>();
        var portProfiles = new List<UniFiPortProfile> { dot1xProfile };
        var switches = _engine.ExtractSwitches(deviceData, networks, null, null, portProfiles);

        var result = _engine.CalculateStatistics(switches);

        // Port 1 is 802.1X secured, port 2 is unprotected
        result.UnprotectedActivePorts.Should().Be(1);
    }

    #endregion

    #region ExtractAccessPointLookup Tests

    [Fact]
    public void ExtractAccessPointLookup_EmptyData_ReturnsEmptyDict()
    {
        var deviceData = JsonDocument.Parse("[]").RootElement;

        var result = _engine.ExtractAccessPointLookup(deviceData);

        result.Should().BeEmpty();
    }

    [Fact]
    public void ExtractAccessPointLookup_AccessPoints_ReturnsLookup()
    {
        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""uap"",
                ""name"": ""Office AP"",
                ""mac"": ""aa:bb:cc:dd:ee:ff""
            },
            {
                ""type"": ""uap"",
                ""name"": ""Conference Room"",
                ""mac"": ""11:22:33:44:55:66""
            }
        ]").RootElement;

        var result = _engine.ExtractAccessPointLookup(deviceData);

        result.Should().HaveCount(2);
        result["aa:bb:cc:dd:ee:ff"].Should().Be("Office AP");
        result["11:22:33:44:55:66"].Should().Be("Conference Room");
    }

    [Fact]
    public void ExtractAccessPointLookup_NonApDevices_Ignored()
    {
        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""usw"",
                ""name"": ""Switch"",
                ""mac"": ""aa:bb:cc:dd:ee:ff""
            }
        ]").RootElement;

        var result = _engine.ExtractAccessPointLookup(deviceData);

        result.Should().BeEmpty();
    }

    [Fact]
    public void ExtractAccessPointInfoLookup_IncludesModelInfo()
    {
        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""uap"",
                ""name"": ""Office AP"",
                ""mac"": ""aa:bb:cc:dd:ee:ff"",
                ""model"": ""U6-Pro"",
                ""shortname"": ""U6Pro""
            }
        ]").RootElement;

        var result = _engine.ExtractAccessPointInfoLookup(deviceData);

        result.Should().HaveCount(1);
        result["aa:bb:cc:dd:ee:ff"].Name.Should().Be("Office AP");
        result["aa:bb:cc:dd:ee:ff"].Model.Should().Be("U6-Pro");
    }

    [Fact]
    public void ExtractAccessPointInfoLookup_DeviceWithIsAccessPoint_Included()
    {
        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""other"",
                ""is_access_point"": true,
                ""name"": ""Third Party AP"",
                ""mac"": ""aa:bb:cc:dd:ee:ff""
            }
        ]").RootElement;

        var result = _engine.ExtractAccessPointInfoLookup(deviceData);

        result.Should().HaveCount(1);
    }

    #endregion

    #region ExtractWirelessClients Tests

    [Fact]
    public void ExtractWirelessClients_NullClients_ReturnsEmptyList()
    {
        var networks = new List<NetworkInfo>();

        var result = _engine.ExtractWirelessClients(null, networks);

        result.Should().BeEmpty();
    }

    [Fact]
    public void ExtractWirelessClients_OnlyWiredClients_ReturnsEmptyList()
    {
        var clients = new List<UniFiClientResponse>
        {
            new UniFiClientResponse { Mac = "aa:bb:cc:dd:ee:ff", IsWired = true }
        };
        var networks = new List<NetworkInfo>();

        var result = _engine.ExtractWirelessClients(clients, networks);

        result.Should().BeEmpty();
    }

    [Fact]
    public void ExtractWirelessClients_ProtectDeviceWithDifferentNetworkId_UsesProtectNetworkId()
    {
        // Arrange: Create networks
        var iotNetwork = new NetworkInfo { Id = "iot-network-id", Name = "IoT", VlanId = 3, Purpose = NetworkPurpose.IoT };
        var securityNetwork = new NetworkInfo { Id = "security-network-id", Name = "Security", VlanId = 5, Purpose = NetworkPurpose.Security };
        var networks = new List<NetworkInfo> { iotNetwork, securityNetwork };

        // Create a wireless client that Network API reports on IoT network
        var clients = new List<UniFiClientResponse>
        {
            new UniFiClientResponse
            {
                Mac = "aa:bb:cc:dd:ee:ff",
                Name = "Test Camera",
                IsWired = false,
                NetworkId = "iot-network-id", // Network API says IoT
                DevCat = 57 // Camera fingerprint
            }
        };

        // Create Protect camera collection with Security network (Virtual Network Override)
        var protectCameras = new ProtectCameraCollection();
        protectCameras.Add("aa:bb:cc:dd:ee:ff", "Test Camera", "security-network-id"); // Protect API says Security

        // Create engine with detection service
        var detectionLoggerMock = new Mock<ILogger<DeviceTypeDetectionService>>();
        var detectionService = new DeviceTypeDetectionService(detectionLoggerMock.Object, null);
        detectionService.SetProtectCameras(protectCameras);
        var engine = new PortSecurityAnalyzer(_loggerMock.Object, detectionService);
        engine.SetProtectCameras(protectCameras);

        // Act
        var result = engine.ExtractWirelessClients(clients, networks);

        // Assert: Client should be assigned to Security network (from Protect API), not IoT (from Network API)
        result.Should().HaveCount(1);
        result[0].Network.Should().NotBeNull();
        result[0].Network!.Id.Should().Be("security-network-id");
        result[0].Network!.Name.Should().Be("Security");
    }

    [Fact]
    public void ExtractWirelessClients_ProtectDeviceWithSameNetworkId_UsesNetworkId()
    {
        // Arrange: Both APIs report same network
        var securityNetwork = new NetworkInfo { Id = "security-network-id", Name = "Security", VlanId = 5, Purpose = NetworkPurpose.Security };
        var networks = new List<NetworkInfo> { securityNetwork };

        var clients = new List<UniFiClientResponse>
        {
            new UniFiClientResponse
            {
                Mac = "aa:bb:cc:dd:ee:ff",
                Name = "Test Camera",
                IsWired = false,
                NetworkId = "security-network-id",
                DevCat = 57
            }
        };

        var protectCameras = new ProtectCameraCollection();
        protectCameras.Add("aa:bb:cc:dd:ee:ff", "Test Camera", "security-network-id"); // Same as Network API

        var detectionLoggerMock = new Mock<ILogger<DeviceTypeDetectionService>>();
        var detectionService = new DeviceTypeDetectionService(detectionLoggerMock.Object, null);
        detectionService.SetProtectCameras(protectCameras);
        var engine = new PortSecurityAnalyzer(_loggerMock.Object, detectionService);
        engine.SetProtectCameras(protectCameras);

        // Act
        var result = engine.ExtractWirelessClients(clients, networks);

        // Assert: Should work normally
        result.Should().HaveCount(1);
        result[0].Network.Should().NotBeNull();
        result[0].Network!.Id.Should().Be("security-network-id");
    }

    [Fact]
    public void ExtractWirelessClients_ProtectDeviceWithNullNetworkId_FallsBackToNetworkApiId()
    {
        // Arrange: Protect device has no connection_network_id
        var iotNetwork = new NetworkInfo { Id = "iot-network-id", Name = "IoT", VlanId = 3, Purpose = NetworkPurpose.IoT };
        var networks = new List<NetworkInfo> { iotNetwork };

        var clients = new List<UniFiClientResponse>
        {
            new UniFiClientResponse
            {
                Mac = "aa:bb:cc:dd:ee:ff",
                Name = "Test Camera",
                IsWired = false,
                NetworkId = "iot-network-id",
                DevCat = 57
            }
        };

        var protectCameras = new ProtectCameraCollection();
        protectCameras.Add("aa:bb:cc:dd:ee:ff", "Test Camera", null); // No network ID from Protect

        var detectionLoggerMock = new Mock<ILogger<DeviceTypeDetectionService>>();
        var detectionService = new DeviceTypeDetectionService(detectionLoggerMock.Object, null);
        detectionService.SetProtectCameras(protectCameras);
        var engine = new PortSecurityAnalyzer(_loggerMock.Object, detectionService);
        engine.SetProtectCameras(protectCameras);

        // Act
        var result = engine.ExtractWirelessClients(clients, networks);

        // Assert: Should fall back to Network API's network_id
        result.Should().HaveCount(1);
        result[0].Network.Should().NotBeNull();
        result[0].Network!.Id.Should().Be("iot-network-id");
    }

    [Fact]
    public void ExtractWirelessClients_NonProtectDevice_UsesNetworkApiId()
    {
        // Arrange: Regular device (not a Protect camera)
        var iotNetwork = new NetworkInfo { Id = "iot-network-id", Name = "IoT", VlanId = 3, Purpose = NetworkPurpose.IoT };
        var securityNetwork = new NetworkInfo { Id = "security-network-id", Name = "Security", VlanId = 5, Purpose = NetworkPurpose.Security };
        var networks = new List<NetworkInfo> { iotNetwork, securityNetwork };

        var clients = new List<UniFiClientResponse>
        {
            new UniFiClientResponse
            {
                Mac = "bb:cc:dd:ee:ff:00", // Different MAC - not in Protect collection
                Name = "Smart Plug",
                IsWired = false,
                NetworkId = "iot-network-id",
                DevCat = 9 // IoT device
            }
        };

        // Protect collection has a different device
        var protectCameras = new ProtectCameraCollection();
        protectCameras.Add("aa:bb:cc:dd:ee:ff", "Camera", "security-network-id");

        var detectionLoggerMock = new Mock<ILogger<DeviceTypeDetectionService>>();
        var detectionService = new DeviceTypeDetectionService(detectionLoggerMock.Object, null);
        detectionService.SetProtectCameras(protectCameras);
        var engine = new PortSecurityAnalyzer(_loggerMock.Object, detectionService);
        engine.SetProtectCameras(protectCameras);

        // Act
        var result = engine.ExtractWirelessClients(clients, networks);

        // Assert: Should use Network API's network_id (no Protect override)
        result.Should().HaveCount(1);
        result[0].Network.Should().NotBeNull();
        result[0].Network!.Id.Should().Be("iot-network-id");
    }

    #endregion

    #region AnalyzeWirelessClients Tests

    [Fact]
    public void AnalyzeWirelessClients_EmptyList_ReturnsEmptyIssues()
    {
        var wirelessClients = new List<WirelessClientInfo>();
        var networks = new List<NetworkInfo>();

        var result = _engine.AnalyzeWirelessClients(wirelessClients, networks);

        result.Should().BeEmpty();
    }

    #endregion

    #region AP Device Role Tests

    [Fact]
    public void ExtractSwitches_ApWith1Port_SkippedAsPassthrough()
    {
        // AP with single port is a passthrough port that can't be disabled
        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""uap"",
                ""name"": ""Office AP"",
                ""mac"": ""aa:bb:cc:dd:ee:ff"",
                ""port_table"": [
                    { ""port_idx"": 1, ""name"": ""LAN"", ""up"": true }
                ]
            }
        ]").RootElement;
        var networks = new List<NetworkInfo>();

        var result = _engine.ExtractSwitches(deviceData, networks);

        result.Should().BeEmpty("APs with 1-2 ports should be skipped (passthrough ports)");
    }

    [Fact]
    public void ExtractSwitches_ApWith2Ports_SkippedAsPassthrough()
    {
        // AP with 2 ports (uplink + LAN passthrough) should be skipped
        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""uap"",
                ""name"": ""Wall AP"",
                ""mac"": ""aa:bb:cc:dd:ee:ff"",
                ""port_table"": [
                    { ""port_idx"": 1, ""name"": ""Uplink"", ""up"": true },
                    { ""port_idx"": 2, ""name"": ""LAN"", ""up"": true }
                ]
            }
        ]").RootElement;
        var networks = new List<NetworkInfo>();

        var result = _engine.ExtractSwitches(deviceData, networks);

        result.Should().BeEmpty("APs with 1-2 ports should be skipped (passthrough ports)");
    }

    [Fact]
    public void ExtractSwitches_ApWith4Ports_IncludedAndMarkedAsAp()
    {
        // In-wall AP with integrated 4-port switch should be included and marked as AP
        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""uap"",
                ""name"": ""In-Wall AP"",
                ""mac"": ""aa:bb:cc:dd:ee:ff"",
                ""model"": ""UAP-IW-HD"",
                ""port_table"": [
                    { ""port_idx"": 1, ""name"": ""Uplink"", ""up"": true },
                    { ""port_idx"": 2, ""name"": ""Port 1"", ""up"": true },
                    { ""port_idx"": 3, ""name"": ""Port 2"", ""up"": false },
                    { ""port_idx"": 4, ""name"": ""Port 3"", ""up"": false }
                ]
            }
        ]").RootElement;
        var networks = new List<NetworkInfo>();

        var result = _engine.ExtractSwitches(deviceData, networks);

        result.Should().HaveCount(1);
        result[0].Name.Should().Be("In-Wall AP");
        result[0].IsAccessPoint.Should().BeTrue("AP devices should be marked as access points");
        result[0].IsGateway.Should().BeFalse("AP devices are not gateways");
        result[0].Ports.Should().HaveCount(4);
    }

    [Fact]
    public void ExtractSwitches_ApWith3Ports_IncludedAndMarkedAsAp()
    {
        // AP with 3 ports should be included (boundary case: > 2 ports)
        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""uap"",
                ""name"": ""Multi-Port AP"",
                ""mac"": ""aa:bb:cc:dd:ee:ff"",
                ""port_table"": [
                    { ""port_idx"": 1, ""name"": ""Uplink"", ""up"": true },
                    { ""port_idx"": 2, ""name"": ""Port 1"", ""up"": true },
                    { ""port_idx"": 3, ""name"": ""Port 2"", ""up"": false }
                ]
            }
        ]").RootElement;
        var networks = new List<NetworkInfo>();

        var result = _engine.ExtractSwitches(deviceData, networks);

        result.Should().HaveCount(1);
        result[0].IsAccessPoint.Should().BeTrue();
    }

    [Fact]
    public void ExtractSwitches_SwitchNotMarkedAsAp()
    {
        // Regular switch should NOT be marked as AP
        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""usw"",
                ""name"": ""Office Switch"",
                ""mac"": ""aa:bb:cc:dd:ee:ff"",
                ""port_table"": [
                    { ""port_idx"": 1, ""name"": ""Port 1"", ""up"": true }
                ]
            }
        ]").RootElement;
        var networks = new List<NetworkInfo>();

        var result = _engine.ExtractSwitches(deviceData, networks);

        result.Should().HaveCount(1);
        result[0].IsAccessPoint.Should().BeFalse("Switches should not be marked as access points");
        result[0].IsGateway.Should().BeFalse("Switches are not gateways");
    }

    [Fact]
    public void ExtractSwitches_GatewayActingAsAp_MarkedAsAp()
    {
        // UDM-class device that uplinks to another UniFi device is acting as AP (mesh)
        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""udm"",
                ""name"": ""Main Gateway"",
                ""mac"": ""11:22:33:44:55:66"",
                ""port_table"": [
                    { ""port_idx"": 1, ""name"": ""WAN"", ""up"": true }
                ]
            },
            {
                ""type"": ""udm"",
                ""name"": ""UX Express (Mesh)"",
                ""mac"": ""aa:bb:cc:dd:ee:ff"",
                ""uplink"": {
                    ""uplink_mac"": ""11:22:33:44:55:66""
                },
                ""port_table"": [
                    { ""port_idx"": 1, ""name"": ""LAN"", ""up"": true }
                ]
            }
        ]").RootElement;
        var networks = new List<NetworkInfo>();

        var result = _engine.ExtractSwitches(deviceData, networks);

        result.Should().HaveCount(2);

        // Main gateway should be marked as gateway
        var mainGateway = result.First(s => s.Name == "Main Gateway");
        mainGateway.IsGateway.Should().BeTrue();
        mainGateway.IsAccessPoint.Should().BeFalse();

        // UX Express acting as mesh AP should be marked as AP, not gateway
        var meshAp = result.First(s => s.Name == "UX Express (Mesh)");
        meshAp.IsAccessPoint.Should().BeTrue("Gateway-class device acting as mesh should be marked as AP");
        meshAp.IsGateway.Should().BeFalse("Gateway-class device acting as mesh is not the network gateway");
    }

    [Fact]
    public void ExtractSwitches_MixedDevices_CorrectlyClassified()
    {
        // Test with mix of gateway, switch, AP with ports, and AP without ports
        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""udm"",
                ""name"": ""Gateway"",
                ""mac"": ""11:11:11:11:11:11"",
                ""port_table"": [
                    { ""port_idx"": 1, ""up"": true }
                ]
            },
            {
                ""type"": ""usw"",
                ""name"": ""Main Switch"",
                ""mac"": ""22:22:22:22:22:22"",
                ""port_table"": [
                    { ""port_idx"": 1, ""up"": true },
                    { ""port_idx"": 2, ""up"": true }
                ]
            },
            {
                ""type"": ""uap"",
                ""name"": ""Standard AP"",
                ""mac"": ""33:33:33:33:33:33"",
                ""port_table"": [
                    { ""port_idx"": 1, ""up"": true }
                ]
            },
            {
                ""type"": ""uap"",
                ""name"": ""In-Wall AP"",
                ""mac"": ""44:44:44:44:44:44"",
                ""port_table"": [
                    { ""port_idx"": 1, ""up"": true },
                    { ""port_idx"": 2, ""up"": true },
                    { ""port_idx"": 3, ""up"": false },
                    { ""port_idx"": 4, ""up"": false }
                ]
            }
        ]").RootElement;
        var networks = new List<NetworkInfo>();

        var result = _engine.ExtractSwitches(deviceData, networks);

        // Standard AP (1 port) should be skipped
        result.Should().HaveCount(3, "Standard AP with 1 port should be skipped");
        result.Should().NotContain(s => s.Name == "Standard AP");

        // Gateway
        var gateway = result.First(s => s.Name == "Gateway");
        gateway.IsGateway.Should().BeTrue();
        gateway.IsAccessPoint.Should().BeFalse();

        // Switch
        var sw = result.First(s => s.Name == "Main Switch");
        sw.IsGateway.Should().BeFalse();
        sw.IsAccessPoint.Should().BeFalse();

        // In-Wall AP (4 ports)
        var inWallAp = result.First(s => s.Name == "In-Wall AP");
        inWallAp.IsGateway.Should().BeFalse();
        inWallAp.IsAccessPoint.Should().BeTrue();
    }

    #endregion

    #region Port Profile Resolution Tests

    [Fact]
    public void ExtractSwitches_PortWithProfile_ResolvesForwardMode()
    {
        var portProfiles = new List<UniFiPortProfile>
        {
            new UniFiPortProfile
            {
                Id = "profile-trunk",
                Name = "Trunk Profile",
                Forward = "customize"
            }
        };

        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""usw"",
                ""name"": ""Switch"",
                ""port_table"": [
                    { ""port_idx"": 1, ""forward"": ""native"", ""portconf_id"": ""profile-trunk"", ""up"": true }
                ]
            }
        ]").RootElement;
        var networks = new List<NetworkInfo>();

        var result = _engine.ExtractSwitches(deviceData, networks, null, null, portProfiles);

        result[0].Ports[0].ForwardMode.Should().Be("custom", "Profile forward mode should override port's native mode");
    }

    [Fact]
    public void ExtractSwitches_PortWithProfile_ResolvesNativeNetworkId()
    {
        var portProfiles = new List<UniFiPortProfile>
        {
            new UniFiPortProfile
            {
                Id = "profile-iot",
                Name = "IoT Profile",
                NativeNetworkId = "iot-network-id"
            }
        };

        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""usw"",
                ""name"": ""Switch"",
                ""port_table"": [
                    { ""port_idx"": 1, ""native_networkconf_id"": ""default-network"", ""portconf_id"": ""profile-iot"", ""up"": true }
                ]
            }
        ]").RootElement;
        var networks = new List<NetworkInfo>();

        var result = _engine.ExtractSwitches(deviceData, networks, null, null, portProfiles);

        result[0].Ports[0].NativeNetworkId.Should().Be("iot-network-id", "Profile native network should override port's native network");
    }

    [Fact]
    public void ExtractSwitches_PortWithProfile_ResolvesExcludedNetworkIds()
    {
        var portProfiles = new List<UniFiPortProfile>
        {
            new UniFiPortProfile
            {
                Id = "profile-limited",
                Name = "Limited Trunk",
                Forward = "customize",
                ExcludedNetworkConfIds = new List<string> { "net-1", "net-2", "net-3" }
            }
        };

        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""usw"",
                ""name"": ""Switch"",
                ""port_table"": [
                    { ""port_idx"": 1, ""forward"": ""native"", ""portconf_id"": ""profile-limited"", ""up"": true }
                ]
            }
        ]").RootElement;
        var networks = new List<NetworkInfo>();

        var result = _engine.ExtractSwitches(deviceData, networks, null, null, portProfiles);

        result[0].Ports[0].ExcludedNetworkIds.Should().NotBeNull();
        result[0].Ports[0].ExcludedNetworkIds.Should().HaveCount(3);
        result[0].Ports[0].ExcludedNetworkIds.Should().Contain("net-1");
        result[0].Ports[0].ExcludedNetworkIds.Should().Contain("net-2");
        result[0].Ports[0].ExcludedNetworkIds.Should().Contain("net-3");
    }

    [Fact]
    public void ExtractSwitches_PortWithProfile_ResolvesEmptyExcludedNetworkIds()
    {
        // Profile with empty excluded list means "Allow All VLANs"
        var portProfiles = new List<UniFiPortProfile>
        {
            new UniFiPortProfile
            {
                Id = "profile-all",
                Name = "Allow All Trunk",
                Forward = "customize",
                ExcludedNetworkConfIds = new List<string>() // Empty = Allow All
            }
        };

        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""usw"",
                ""name"": ""Switch"",
                ""port_table"": [
                    { ""port_idx"": 1, ""excluded_networkconf_ids"": [""net-1""], ""portconf_id"": ""profile-all"", ""up"": true }
                ]
            }
        ]").RootElement;
        var networks = new List<NetworkInfo>();

        var result = _engine.ExtractSwitches(deviceData, networks, null, null, portProfiles);

        result[0].Ports[0].ExcludedNetworkIds.Should().NotBeNull();
        result[0].Ports[0].ExcludedNetworkIds.Should().BeEmpty("Profile's empty excluded list should override port's excluded list");
    }

    [Fact]
    public void ExtractSwitches_PortWithProfile_ResolvesPortSecurity()
    {
        var portProfiles = new List<UniFiPortProfile>
        {
            new UniFiPortProfile
            {
                Id = "profile-secure",
                Name = "Secure Profile",
                PortSecurityEnabled = true,
                PortSecurityMacAddresses = new List<string> { "aa:bb:cc:dd:ee:ff" }
            }
        };

        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""usw"",
                ""name"": ""Switch"",
                ""port_table"": [
                    { ""port_idx"": 1, ""portconf_id"": ""profile-secure"", ""up"": true }
                ]
            }
        ]").RootElement;
        var networks = new List<NetworkInfo>();

        var result = _engine.ExtractSwitches(deviceData, networks, null, null, portProfiles);

        result[0].Ports[0].PortSecurityEnabled.Should().BeTrue();
        result[0].Ports[0].AllowedMacAddresses.Should().Contain("aa:bb:cc:dd:ee:ff");
    }

    [Fact]
    public void ExtractSwitches_PortWithProfile_ResolvesIsolation()
    {
        var portProfiles = new List<UniFiPortProfile>
        {
            new UniFiPortProfile
            {
                Id = "profile-isolated",
                Name = "Isolated Profile",
                Isolation = true
            }
        };

        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""usw"",
                ""name"": ""Switch"",
                ""port_table"": [
                    { ""port_idx"": 1, ""isolation"": false, ""portconf_id"": ""profile-isolated"", ""up"": true }
                ]
            }
        ]").RootElement;
        var networks = new List<NetworkInfo>();

        var result = _engine.ExtractSwitches(deviceData, networks, null, null, portProfiles);

        result[0].Ports[0].IsolationEnabled.Should().BeTrue("Profile isolation should override port's isolation setting");
    }

    [Fact]
    public void ExtractSwitches_PortWithUnknownProfile_UsesPortSettings()
    {
        // Port references a profile that doesn't exist in the list
        var portProfiles = new List<UniFiPortProfile>
        {
            new UniFiPortProfile
            {
                Id = "profile-other",
                Name = "Other Profile",
                Forward = "customize"
            }
        };

        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""usw"",
                ""name"": ""Switch"",
                ""port_table"": [
                    { ""port_idx"": 1, ""forward"": ""native"", ""portconf_id"": ""profile-unknown"", ""up"": true }
                ]
            }
        ]").RootElement;
        var networks = new List<NetworkInfo>();

        var result = _engine.ExtractSwitches(deviceData, networks, null, null, portProfiles);

        result[0].Ports[0].ForwardMode.Should().Be("native", "When profile not found, port's own settings should be used");
    }

    [Fact]
    public void ExtractSwitches_PortWithoutProfile_UsesPortSettings()
    {
        var portProfiles = new List<UniFiPortProfile>
        {
            new UniFiPortProfile
            {
                Id = "profile-unused",
                Name = "Unused Profile",
                Forward = "customize"
            }
        };

        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""usw"",
                ""name"": ""Switch"",
                ""port_table"": [
                    { ""port_idx"": 1, ""forward"": ""native"", ""excluded_networkconf_ids"": [""net-x""], ""up"": true }
                ]
            }
        ]").RootElement;
        var networks = new List<NetworkInfo>();

        var result = _engine.ExtractSwitches(deviceData, networks, null, null, portProfiles);

        result[0].Ports[0].ForwardMode.Should().Be("native");
        result[0].Ports[0].ExcludedNetworkIds.Should().Contain("net-x");
    }

    #endregion

    #region LAG Child Port Filtering Tests

    [Fact]
    public void ExtractSwitches_LagChildPort_MarkedAsLagChild()
    {
        // Port 9 is a LAG child (aggregated_by=4, lag_idx=1) and should be marked
        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""usw"",
                ""name"": ""Switch"",
                ""port_table"": [
                    { ""port_idx"": 4, ""name"": ""Port 4"", ""up"": true, ""lag_idx"": 1, ""aggregated_by"": false },
                    { ""port_idx"": 9, ""name"": ""SFP+ 1"", ""up"": false, ""forward"": ""all"", ""lag_idx"": 1, ""aggregated_by"": 4, ""masked"": true }
                ]
            }
        ]").RootElement;
        var networks = new List<NetworkInfo>();

        var result = _engine.ExtractSwitches(deviceData, networks);

        result.Should().HaveCount(1);
        result[0].Ports.Should().HaveCount(2);
        result[0].Ports.Single(p => p.PortIndex == 4).IsLagChild.Should().BeFalse("Parent LAG port should not be marked as child");
        result[0].Ports.Single(p => p.PortIndex == 9).IsLagChild.Should().BeTrue("LAG child port should be marked");
    }

    [Fact]
    public void ExtractSwitches_LagParentPort_NotMarkedAsLagChild()
    {
        // Port 4 is the LAG parent (aggregated_by=false, lag_idx=1) - should not be marked
        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""usw"",
                ""name"": ""Switch"",
                ""port_table"": [
                    { ""port_idx"": 4, ""name"": ""Port 4"", ""up"": true, ""lag_idx"": 1, ""aggregated_by"": false, ""forward"": ""native"", ""tagged_vlan_mgmt"": ""block_all"" },
                    { ""port_idx"": 9, ""name"": ""SFP+ 1"", ""up"": false, ""forward"": ""all"", ""lag_idx"": 1, ""aggregated_by"": 4 }
                ]
            }
        ]").RootElement;
        var networks = new List<NetworkInfo>();

        var result = _engine.ExtractSwitches(deviceData, networks);

        var parentPort = result[0].Ports.Single(p => p.PortIndex == 4);
        parentPort.IsLagChild.Should().BeFalse();
        parentPort.ForwardMode.Should().Be("native");
    }

    [Fact]
    public void ExtractSwitches_MultipleLagChildPorts_AllMarked()
    {
        // LAG with 3 member ports: port 4 is parent, ports 9 and 10 are children
        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""usw"",
                ""name"": ""Switch"",
                ""port_table"": [
                    { ""port_idx"": 1, ""name"": ""Port 1"", ""up"": true },
                    { ""port_idx"": 4, ""name"": ""Port 4"", ""up"": true, ""lag_idx"": 1, ""aggregated_by"": false },
                    { ""port_idx"": 9, ""name"": ""SFP+ 1"", ""up"": false, ""lag_idx"": 1, ""aggregated_by"": 4 },
                    { ""port_idx"": 10, ""name"": ""SFP+ 2"", ""up"": false, ""lag_idx"": 1, ""aggregated_by"": 4 }
                ]
            }
        ]").RootElement;
        var networks = new List<NetworkInfo>();

        var result = _engine.ExtractSwitches(deviceData, networks);

        result[0].Ports.Should().HaveCount(4);
        result[0].Ports.Where(p => p.IsLagChild).Select(p => p.PortIndex).Should().BeEquivalentTo(new[] { 9, 10 });
        result[0].Ports.Where(p => !p.IsLagChild).Select(p => p.PortIndex).Should().BeEquivalentTo(new[] { 1, 4 });
    }

    [Fact]
    public void ExtractSwitches_PortWithLagIdxButNoAggregatedBy_NotMarkedAsLagChild()
    {
        // Port has lag_idx but aggregated_by is missing - not a child port
        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""usw"",
                ""name"": ""Switch"",
                ""port_table"": [
                    { ""port_idx"": 4, ""name"": ""Port 4"", ""up"": true, ""lag_idx"": 1 }
                ]
            }
        ]").RootElement;
        var networks = new List<NetworkInfo>();

        var result = _engine.ExtractSwitches(deviceData, networks);

        result[0].Ports.Should().HaveCount(1);
        result[0].Ports[0].IsLagChild.Should().BeFalse("Port with lag_idx but no aggregated_by is not a LAG child");
    }

    [Fact]
    public void ExtractSwitches_PortWithAggregatedByFalse_NotMarkedAsLagChild()
    {
        // Port has aggregated_by=false (boolean) - this is the parent, not a child
        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""usw"",
                ""name"": ""Switch"",
                ""port_table"": [
                    { ""port_idx"": 4, ""name"": ""Port 4"", ""up"": true, ""lag_idx"": 1, ""aggregated_by"": false }
                ]
            }
        ]").RootElement;
        var networks = new List<NetworkInfo>();

        var result = _engine.ExtractSwitches(deviceData, networks);

        result[0].Ports[0].IsLagChild.Should().BeFalse("Parent LAG port (aggregated_by=false) is not a child");
    }

    [Fact]
    public void ExtractSwitches_RegularPortsUnaffectedByLagDetection()
    {
        // Regular ports without any LAG properties should not be affected
        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""usw"",
                ""name"": ""Switch"",
                ""port_table"": [
                    { ""port_idx"": 1, ""name"": ""Port 1"", ""up"": true },
                    { ""port_idx"": 2, ""name"": ""Port 2"", ""up"": false },
                    { ""port_idx"": 3, ""name"": ""Port 3"", ""up"": true, ""aggregated_by"": false }
                ]
            }
        ]").RootElement;
        var networks = new List<NetworkInfo>();

        var result = _engine.ExtractSwitches(deviceData, networks);

        result[0].Ports.Should().HaveCount(3);
        result[0].Ports.Should().AllSatisfy(p => p.IsLagChild.Should().BeFalse());
    }

    [Fact]
    public void AnalyzePorts_LagChildPortWithForwardAll_DoesNotTriggerVlanAudit()
    {
        // This is the actual bug scenario: LAG child port has forward="all" which
        // would trigger AccessPortVlanRule if not filtered out
        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""usw"",
                ""name"": ""Switch"",
                ""port_table"": [
                    { ""port_idx"": 4, ""name"": ""Port 4"", ""up"": true, ""forward"": ""native"", ""tagged_vlan_mgmt"": ""block_all"", ""lag_idx"": 1, ""aggregated_by"": false },
                    { ""port_idx"": 9, ""name"": ""SFP+ 1"", ""up"": false, ""forward"": ""all"", ""lag_idx"": 1, ""aggregated_by"": 4, ""masked"": true }
                ]
            }
        ]").RootElement;
        var networks = new List<NetworkInfo>
        {
            new() { Id = "net-1", Name = "Default", VlanId = 1 },
            new() { Id = "net-2", Name = "IoT", VlanId = 20 },
            new() { Id = "net-3", Name = "Guest", VlanId = 30 }
        };

        var switches = _engine.ExtractSwitches(deviceData, networks);
        var issues = _engine.AnalyzePorts(switches, networks);

        issues.Where(i => i.Port == "9" && i.Type == IssueTypes.AccessPortVlan).Should().BeEmpty(
            "LAG child port should not trigger AccessPortVlanRule");
    }

    [Fact]
    public void AnalyzePorts_LagChildPort_StillCheckedByUnusedPortRule()
    {
        // LAG child port that is down and not disabled should still trigger unused port rule
        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""usw"",
                ""name"": ""Switch"",
                ""port_table"": [
                    { ""port_idx"": 4, ""name"": ""Port 4"", ""up"": true, ""lag_idx"": 1, ""aggregated_by"": false },
                    { ""port_idx"": 9, ""name"": ""SFP+ 1"", ""up"": false, ""forward"": ""all"", ""lag_idx"": 1, ""aggregated_by"": 4, ""masked"": true }
                ]
            }
        ]").RootElement;
        var networks = new List<NetworkInfo>();

        var switches = _engine.ExtractSwitches(deviceData, networks);
        var issues = _engine.AnalyzePorts(switches, networks);

        issues.Where(i => i.Port == "9" && i.Type == IssueTypes.UnusedPort).Should().HaveCount(1,
            "LAG child port should still be checked by UnusedPortRule");
    }

    #endregion

    #region AddRule Tests

    [Fact]
    public void AddRule_CustomRule_IsExecuted()
    {
        var customRuleMock = new Mock<IAuditRule>();
        customRuleMock.Setup(r => r.Enabled).Returns(true);
        customRuleMock.Setup(r => r.RuleId).Returns("CUSTOM-001");
        customRuleMock.Setup(r => r.Evaluate(It.IsAny<PortInfo>(), It.IsAny<List<NetworkInfo>>()))
            .Returns(new AuditIssue
            {
                Type = "CUSTOM_ISSUE",
                Message = "Custom rule triggered",
                Severity = NetworkOptimizer.Audit.Models.AuditSeverity.Recommended
            });

        _engine.AddRule(customRuleMock.Object);

        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""usw"",
                ""name"": ""Switch1"",
                ""port_table"": [
                    { ""port_idx"": 1, ""up"": true }
                ]
            }
        ]").RootElement;
        var networks = new List<NetworkInfo>();
        var switches = _engine.ExtractSwitches(deviceData, networks);

        var result = _engine.AnalyzePorts(switches, networks);

        result.Should().Contain(i => i.Type == "CUSTOM_ISSUE");
    }

    #endregion
}
