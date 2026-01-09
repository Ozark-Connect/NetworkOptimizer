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
        result[0].Network.Name.Should().Be("Security");
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
