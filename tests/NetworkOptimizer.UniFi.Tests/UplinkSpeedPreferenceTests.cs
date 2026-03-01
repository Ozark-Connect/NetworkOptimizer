using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.UniFi.Models;
using NetworkOptimizer.UniFi.Tests.Fixtures;
using Xunit;

namespace NetworkOptimizer.UniFi.Tests;

/// <summary>
/// Tests for uplink speed preference when device-reported speed differs from port table.
/// Verifies fix for issue #189: When an unmanaged switch sits between a UniFi device and
/// the upstream UniFi switch/gateway, the device's reported uplink speed should be trusted
/// over the upstream port table.
/// </summary>
public class UplinkSpeedPreferenceTests
{
    private readonly NetworkPathAnalyzer _analyzer;
    private readonly Mock<IUniFiClientProvider> _clientProviderMock;
    private readonly IMemoryCache _cache;
    private readonly Mock<ILoggerFactory> _loggerFactoryMock;

    // Test constants
    private const string GatewayMac = "aa:bb:cc:00:00:01";
    private const string ApMac = "aa:bb:cc:00:00:03";
    private const string ServerMac = "aa:bb:cc:00:02:01";

    public UplinkSpeedPreferenceTests()
    {
        _clientProviderMock = new Mock<IUniFiClientProvider>();
        _cache = new MemoryCache(new MemoryCacheOptions());
        _loggerFactoryMock = new Mock<ILoggerFactory>();
        _loggerFactoryMock.Setup(f => f.CreateLogger(It.IsAny<string>()))
            .Returns(new Mock<ILogger>().Object);

        _analyzer = new NetworkPathAnalyzer(
            _clientProviderMock.Object,
            _cache,
            _loggerFactoryMock.Object);
    }

    /// <summary>
    /// When a target device (AP) reports 1 GbE uplink but the upstream gateway port shows 10 GbE
    /// (unmanaged switch scenario), the path should use the device's reported 1 GbE speed.
    /// </summary>
    [Fact]
    public void BuildHopList_TargetDeviceReportsDifferentSpeed_UsesDeviceSpeed()
    {
        // Arrange - AP reports 1 GbE, but gateway port 9 shows 10 GbE (unmanaged switch in between)
        var gateway = NetworkTestData.CreateGateway(mac: GatewayMac);
        var ap = NetworkTestData.CreateWiredAccessPoint(
            mac: ApMac,
            uplinkMac: GatewayMac,
            uplinkPort: 9,
            uplinkSpeed: 1000);  // AP reports 1 GbE

        var topology = new NetworkTopology
        {
            Devices = new List<DiscoveredDevice> { gateway, ap },
            Clients = new List<DiscoveredClient>(),
            Networks = new List<NetworkInfo>
            {
                new NetworkInfo { Id = "default", Name = "Default", VlanId = 1, IpSubnet = "192.0.2.0/24" }
            }
        };

        var serverPosition = new ServerPosition
        {
            IpAddress = "192.0.2.200",
            Mac = ServerMac,
            SwitchMac = GatewayMac,
            SwitchPort = 1,
            VlanId = 1
        };

        // Raw devices with port table showing 10 GbE on port 9 (the gateway's SFP+ port)
        var rawDevices = new Dictionary<string, UniFiDeviceResponse>
        {
            [GatewayMac] = new UniFiDeviceResponse
            {
                Mac = GatewayMac,
                PortTable = new List<SwitchPort>
                {
                    new SwitchPort { PortIdx = 1, Speed = 1000, Up = true },  // Server port
                    new SwitchPort { PortIdx = 9, Speed = 10000, Up = true }  // SFP+ to unmanaged switch
                }
            }
        };

        var path = new NetworkPath
        {
            SourceHost = serverPosition.IpAddress,
            DestinationHost = ap.IpAddress,
            RequiresRouting = false
        };

        // Act
        _analyzer.BuildHopList(path, serverPosition, ap, null, topology, rawDevices);

        // Assert - AP hop should use device-reported 1 GbE, not port table's 10 GbE
        var apHop = path.Hops.FirstOrDefault(h => h.DeviceMac == ApMac);
        apHop.Should().NotBeNull("AP should be in the path");
        apHop!.IngressSpeedMbps.Should().Be(1000,
            "should use AP's reported uplink speed (1 GbE), not upstream port table (10 GbE)");
        apHop.EgressSpeedMbps.Should().Be(1000,
            "egress should match ingress for symmetric link");
    }

    /// <summary>
    /// When a device reports 0 for uplink speed (not available), should fall back to port table.
    /// </summary>
    [Fact]
    public void BuildHopList_DeviceReportsZeroSpeed_FallsBackToPortTable()
    {
        // Arrange - AP reports 0 (unknown), port table shows 2500
        var gateway = NetworkTestData.CreateGateway(mac: GatewayMac);
        var ap = NetworkTestData.CreateWiredAccessPoint(
            mac: ApMac,
            uplinkMac: GatewayMac,
            uplinkPort: 9,
            uplinkSpeed: 0);  // AP doesn't report speed

        var topology = new NetworkTopology
        {
            Devices = new List<DiscoveredDevice> { gateway, ap },
            Clients = new List<DiscoveredClient>(),
            Networks = new List<NetworkInfo>
            {
                new NetworkInfo { Id = "default", Name = "Default", VlanId = 1, IpSubnet = "192.0.2.0/24" }
            }
        };

        var serverPosition = new ServerPosition
        {
            IpAddress = "192.0.2.200",
            Mac = ServerMac,
            SwitchMac = GatewayMac,
            SwitchPort = 1,
            VlanId = 1
        };

        var rawDevices = new Dictionary<string, UniFiDeviceResponse>
        {
            [GatewayMac] = new UniFiDeviceResponse
            {
                Mac = GatewayMac,
                PortTable = new List<SwitchPort>
                {
                    new SwitchPort { PortIdx = 1, Speed = 1000, Up = true },
                    new SwitchPort { PortIdx = 9, Speed = 2500, Up = true }
                }
            }
        };

        var path = new NetworkPath
        {
            SourceHost = serverPosition.IpAddress,
            DestinationHost = ap.IpAddress,
            RequiresRouting = false
        };

        // Act
        _analyzer.BuildHopList(path, serverPosition, ap, null, topology, rawDevices);

        // Assert - Should fall back to port table when device reports 0
        var apHop = path.Hops.FirstOrDefault(h => h.DeviceMac == ApMac);
        apHop.Should().NotBeNull();
        apHop!.IngressSpeedMbps.Should().Be(2500,
            "should fall back to port table (2500) when device reports 0");
    }

    /// <summary>
    /// When device and port table report the same speed, should use that speed (no override needed).
    /// </summary>
    [Fact]
    public void BuildHopList_SpeedsMatch_UsesThatSpeed()
    {
        // Arrange - Both report 2500
        var gateway = NetworkTestData.CreateGateway(mac: GatewayMac);
        var ap = NetworkTestData.CreateWiredAccessPoint(
            mac: ApMac,
            uplinkMac: GatewayMac,
            uplinkPort: 9,
            uplinkSpeed: 2500);

        var topology = new NetworkTopology
        {
            Devices = new List<DiscoveredDevice> { gateway, ap },
            Clients = new List<DiscoveredClient>(),
            Networks = new List<NetworkInfo>
            {
                new NetworkInfo { Id = "default", Name = "Default", VlanId = 1, IpSubnet = "192.0.2.0/24" }
            }
        };

        var serverPosition = new ServerPosition
        {
            IpAddress = "192.0.2.200",
            Mac = ServerMac,
            SwitchMac = GatewayMac,
            SwitchPort = 1,
            VlanId = 1
        };

        var rawDevices = new Dictionary<string, UniFiDeviceResponse>
        {
            [GatewayMac] = new UniFiDeviceResponse
            {
                Mac = GatewayMac,
                PortTable = new List<SwitchPort>
                {
                    new SwitchPort { PortIdx = 1, Speed = 1000, Up = true },
                    new SwitchPort { PortIdx = 9, Speed = 2500, Up = true }
                }
            }
        };

        var path = new NetworkPath
        {
            SourceHost = serverPosition.IpAddress,
            DestinationHost = ap.IpAddress,
            RequiresRouting = false
        };

        // Act
        _analyzer.BuildHopList(path, serverPosition, ap, null, topology, rawDevices);

        // Assert
        var apHop = path.Hops.FirstOrDefault(h => h.DeviceMac == ApMac);
        apHop.Should().NotBeNull();
        apHop!.IngressSpeedMbps.Should().Be(2500);
    }

    /// <summary>
    /// Mesh APs should continue to use wireless uplink path (not affected by this fix).
    /// </summary>
    [Fact]
    public void BuildHopList_MeshAp_UsesWirelessPath()
    {
        // Arrange
        var gateway = NetworkTestData.CreateGateway(mac: GatewayMac);
        var wiredAp = NetworkTestData.CreateWiredAccessPoint(
            mac: "aa:bb:cc:00:00:02",
            uplinkMac: GatewayMac,
            uplinkPort: 1,
            uplinkSpeed: 1000);
        var meshAp = NetworkTestData.CreateMeshAccessPoint(
            mac: ApMac,
            uplinkMac: "aa:bb:cc:00:00:02",
            txRateKbps: 866000,
            rxRateKbps: 866000);

        var topology = new NetworkTopology
        {
            Devices = new List<DiscoveredDevice> { gateway, wiredAp, meshAp },
            Clients = new List<DiscoveredClient>(),
            Networks = new List<NetworkInfo>
            {
                new NetworkInfo { Id = "default", Name = "Default", VlanId = 1, IpSubnet = "192.0.2.0/24" }
            }
        };

        var serverPosition = new ServerPosition
        {
            IpAddress = "192.0.2.200",
            Mac = ServerMac,
            SwitchMac = GatewayMac,
            SwitchPort = 2,
            VlanId = 1
        };

        var path = new NetworkPath
        {
            SourceHost = serverPosition.IpAddress,
            DestinationHost = meshAp.IpAddress,
            RequiresRouting = false
        };

        // Act
        _analyzer.BuildHopList(path, serverPosition, meshAp, null, topology, new Dictionary<string, UniFiDeviceResponse>());

        // Assert - Mesh AP should have wireless flags set
        var meshHop = path.Hops.FirstOrDefault(h => h.DeviceMac == ApMac);
        meshHop.Should().NotBeNull();
        meshHop!.IsWirelessIngress.Should().BeTrue("mesh AP uplink should be marked as wireless");
        meshHop.IsWirelessEgress.Should().BeTrue("mesh AP uplink should be marked as wireless");
        meshHop.IngressSpeedMbps.Should().Be(866, "should use mesh uplink speed (866 Mbps from 866000 Kbps)");
    }

    /// <summary>
    /// Gateways should NOT use UplinkSpeedMbps (that's WAN speed) - should use port table instead.
    /// </summary>
    [Fact]
    public void BuildHopList_GatewayTarget_UsesPortTableNotWanSpeed()
    {
        // Arrange - Gateway has 1000 Mbps WAN, but LAN port is 2500 Mbps
        var gateway = new DiscoveredDevice
        {
            Mac = GatewayMac,
            IpAddress = "192.0.2.1",
            Name = "Gateway",
            Model = "UDM-Pro",
            Type = DeviceType.Gateway,
            Adopted = true,
            State = 1,
            UplinkSpeedMbps = 1000,  // This is WAN speed - should NOT be used for LAN path
            IsUplinkConnected = true
        };

        var ap = NetworkTestData.CreateWiredAccessPoint(
            mac: ApMac,
            uplinkMac: GatewayMac,
            uplinkPort: 9,
            uplinkSpeed: 2500);

        var topology = new NetworkTopology
        {
            Devices = new List<DiscoveredDevice> { gateway, ap },
            Clients = new List<DiscoveredClient>(),
            Networks = new List<NetworkInfo>
            {
                new NetworkInfo { Id = "default", Name = "Default", VlanId = 1, IpSubnet = "192.0.2.0/24" }
            }
        };

        var serverPosition = new ServerPosition
        {
            IpAddress = "192.0.2.200",
            Mac = ServerMac,
            SwitchMac = GatewayMac,
            SwitchPort = 1,
            VlanId = 1
        };

        // Raw devices with port table - LAN ports are 2500 Mbps
        var rawDevices = new Dictionary<string, UniFiDeviceResponse>
        {
            [GatewayMac] = new UniFiDeviceResponse
            {
                Mac = GatewayMac,
                PortTable = new List<SwitchPort>
                {
                    new SwitchPort { PortIdx = 1, Speed = 2500, Up = true },  // Server port
                    new SwitchPort { PortIdx = 9, Speed = 2500, Up = true }   // AP port
                }
            }
        };

        var path = new NetworkPath
        {
            SourceHost = serverPosition.IpAddress,
            DestinationHost = gateway.IpAddress,
            RequiresRouting = false,
            TargetIsGateway = true
        };

        // Act
        _analyzer.BuildHopList(path, serverPosition, gateway, null, topology, rawDevices);

        // Assert - Gateway hop should use port table (2500), NOT UplinkSpeedMbps (1000 WAN)
        var gatewayHop = path.Hops.FirstOrDefault(h => h.DeviceMac == GatewayMac);
        gatewayHop.Should().NotBeNull("gateway should be in the path");
        // Gateway's ingress/egress should NOT be 1000 (WAN speed)
        gatewayHop!.IngressSpeedMbps.Should().NotBe(1000,
            "should NOT use gateway's WAN uplink speed for LAN path");
    }
}
