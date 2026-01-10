using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.UniFi.Models;
using NetworkOptimizer.UniFi.Tests.Fixtures;
using Xunit;

namespace NetworkOptimizer.UniFi.Tests;

/// <summary>
/// Tests for daisy-chain network topology path analysis.
/// These tests verify correct L2 path calculation when switches are connected in series
/// and the server is downstream from the client's switch (common ancestor scenario).
/// </summary>
public class DaisyChainPathTests
{
    private readonly NetworkPathAnalyzer _analyzer;
    private readonly Mock<IUniFiClientProvider> _clientProviderMock;
    private readonly IMemoryCache _cache;
    private readonly Mock<ILoggerFactory> _loggerFactoryMock;

    public DaisyChainPathTests()
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

    #region Same Switch Tests (Baseline - Should Pass)

    /// <summary>
    /// When client and server are on the same switch, the path should only include that switch.
    /// Topology: Gateway -> Switch1 -> [NAS, Server]
    /// Path: NAS -> Switch1 -> Server
    /// </summary>
    [Fact]
    public void BuildHopList_ClientAndServerOnSameSwitch_PathOnlyIncludesSwitch()
    {
        // Arrange
        var topology = NetworkTestData.CreateDaisyChainTopology();
        var serverPosition = NetworkTestData.CreateSameSwitchServerPosition();
        var client = topology.Clients.First(c => c.Mac == NetworkTestData.NasMac);
        var path = new NetworkPath
        {
            SourceHost = serverPosition.IpAddress,
            DestinationHost = client.IpAddress,
            SourceVlanId = serverPosition.VlanId ?? 1,
            DestinationVlanId = 1,
            RequiresRouting = false
        };

        // Act
        _analyzer.BuildHopList(path, serverPosition, null, client, topology, new Dictionary<string, UniFiDeviceResponse>());

        // Assert
        path.Hops.Should().NotBeEmpty();

        // Gateway should NOT be in the path (same VLAN, same switch)
        path.Hops.Should().NotContain(h => h.Type == HopType.Gateway,
            "gateway should not be in path when client and server are on same switch");

        // Should have Switch1
        path.Hops.Should().Contain(h => h.DeviceMac == NetworkTestData.Switch1Mac,
            "Switch1 should be in path as it connects both devices");
    }

    #endregion

    #region Daisy-Chain Tests (Bug Scenario)

    /// <summary>
    /// When server is downstream from client's switch (daisy-chain), the path should NOT include the gateway.
    /// Topology: Gateway -> Switch1 -> Switch2 -> Server
    ///                           \-> NAS
    /// Expected Path: NAS -> Switch1 -> Switch2 -> Server
    /// Bug: Currently returns NAS -> Switch1 -> Gateway -> Server (missing Switch2!)
    /// </summary>
    [Fact]
    public void BuildHopList_ServerDownstreamFromClientSwitch_GatewayNotInPath()
    {
        // Arrange
        var topology = NetworkTestData.CreateDaisyChainTopology();
        var serverPosition = NetworkTestData.CreateDaisyChainServerPosition();
        var client = topology.Clients.First(c => c.Mac == NetworkTestData.NasMac);
        var path = new NetworkPath
        {
            SourceHost = serverPosition.IpAddress,
            DestinationHost = client.IpAddress,
            SourceVlanId = serverPosition.VlanId ?? 1,
            DestinationVlanId = 1,
            RequiresRouting = false  // Same VLAN - L2 only
        };

        // Act
        _analyzer.BuildHopList(path, serverPosition, null, client, topology, new Dictionary<string, UniFiDeviceResponse>());

        // Assert
        path.Hops.Should().NotBeEmpty();

        // Gateway should NOT be in the path (same VLAN, L2 traffic)
        path.Hops.Should().NotContain(h => h.Type == HopType.Gateway,
            "gateway should not be in L2 path when server is downstream on same VLAN");
    }

    /// <summary>
    /// When server is downstream from client's switch, Switch2 (server's switch) should be in the path.
    /// </summary>
    [Fact]
    public void BuildHopList_ServerDownstreamFromClientSwitch_ServerSwitchInPath()
    {
        // Arrange
        var topology = NetworkTestData.CreateDaisyChainTopology();
        var serverPosition = NetworkTestData.CreateDaisyChainServerPosition();
        var client = topology.Clients.First(c => c.Mac == NetworkTestData.NasMac);
        var path = new NetworkPath
        {
            SourceHost = serverPosition.IpAddress,
            DestinationHost = client.IpAddress,
            SourceVlanId = serverPosition.VlanId ?? 1,
            DestinationVlanId = 1,
            RequiresRouting = false
        };

        // Act
        _analyzer.BuildHopList(path, serverPosition, null, client, topology, new Dictionary<string, UniFiDeviceResponse>());

        // Assert
        path.Hops.Should().NotBeEmpty();

        // Switch2 (server's switch) should be in the path
        path.Hops.Should().Contain(h => h.DeviceMac == NetworkTestData.Switch2Mac,
            "Switch2 (server's switch) should be in path");
    }

    /// <summary>
    /// When server is downstream from client's switch, both switches should be in the path.
    /// </summary>
    [Fact]
    public void BuildHopList_ServerDownstreamFromClientSwitch_BothSwitchesInPath()
    {
        // Arrange
        var topology = NetworkTestData.CreateDaisyChainTopology();
        var serverPosition = NetworkTestData.CreateDaisyChainServerPosition();
        var client = topology.Clients.First(c => c.Mac == NetworkTestData.NasMac);
        var path = new NetworkPath
        {
            SourceHost = serverPosition.IpAddress,
            DestinationHost = client.IpAddress,
            SourceVlanId = serverPosition.VlanId ?? 1,
            DestinationVlanId = 1,
            RequiresRouting = false
        };

        // Act
        _analyzer.BuildHopList(path, serverPosition, null, client, topology, new Dictionary<string, UniFiDeviceResponse>());

        // Assert
        path.Hops.Should().NotBeEmpty();

        // Both switches should be in the path
        var switchHops = path.Hops.Where(h => h.Type == HopType.Switch).ToList();
        switchHops.Should().HaveCount(2, "path should include both Switch1 and Switch2");

        switchHops.Should().Contain(h => h.DeviceMac == NetworkTestData.Switch1Mac,
            "Switch1 (common ancestor) should be in path");
        switchHops.Should().Contain(h => h.DeviceMac == NetworkTestData.Switch2Mac,
            "Switch2 (server's switch) should be in path");
    }

    /// <summary>
    /// The path should have correct hop order: Client -> Switch1 -> Switch2 -> Server
    /// </summary>
    [Fact]
    public void BuildHopList_ServerDownstreamFromClientSwitch_CorrectHopOrder()
    {
        // Arrange
        var topology = NetworkTestData.CreateDaisyChainTopology();
        var serverPosition = NetworkTestData.CreateDaisyChainServerPosition();
        var client = topology.Clients.First(c => c.Mac == NetworkTestData.NasMac);
        var path = new NetworkPath
        {
            SourceHost = serverPosition.IpAddress,
            DestinationHost = client.IpAddress,
            SourceVlanId = serverPosition.VlanId ?? 1,
            DestinationVlanId = 1,
            RequiresRouting = false
        };

        // Act
        _analyzer.BuildHopList(path, serverPosition, null, client, topology, new Dictionary<string, UniFiDeviceResponse>());

        // Assert
        path.Hops.Should().HaveCountGreaterThanOrEqualTo(4, "should have at least: Client, Switch1, Switch2, Server");

        // Verify hop order
        var orderedHops = path.Hops.OrderBy(h => h.Order).ToList();

        // First hop should be client
        orderedHops[0].Type.Should().Be(HopType.Client);

        // Find Switch1 and Switch2 positions
        var switch1Index = orderedHops.FindIndex(h => h.DeviceMac == NetworkTestData.Switch1Mac);
        var switch2Index = orderedHops.FindIndex(h => h.DeviceMac == NetworkTestData.Switch2Mac);

        switch1Index.Should().BeGreaterThan(0, "Switch1 should come after client");
        switch2Index.Should().BeGreaterThan(switch1Index, "Switch2 should come after Switch1");

        // Last hop should be server
        orderedHops.Last().Type.Should().Be(HopType.Server);
    }

    #endregion

    #region Inter-VLAN Routing Tests (Should Include Gateway)

    /// <summary>
    /// When inter-VLAN routing is required, the gateway SHOULD be in the path.
    /// This test verifies the gateway is correctly included when routing is needed.
    /// </summary>
    [Fact]
    public void BuildHopList_InterVlanRouting_GatewayInPath()
    {
        // Arrange
        var topology = NetworkTestData.CreateDaisyChainTopology();

        // Add a second VLAN network
        topology.Networks.Add(new NetworkInfo
        {
            Id = "10",
            Name = "IoT",
            VlanId = 10,
            IpSubnet = "198.51.100.0/24",
            Purpose = "corporate"
        });

        var serverPosition = NetworkTestData.CreateDaisyChainServerPosition();
        var client = topology.Clients.First(c => c.Mac == NetworkTestData.NasMac);
        var path = new NetworkPath
        {
            SourceHost = serverPosition.IpAddress,
            DestinationHost = client.IpAddress,
            SourceVlanId = 1,
            DestinationVlanId = 10,  // Different VLAN
            RequiresRouting = true   // L3 routing required
        };

        // Act
        _analyzer.BuildHopList(path, serverPosition, null, client, topology, new Dictionary<string, UniFiDeviceResponse>());

        // Assert
        path.Hops.Should().NotBeEmpty();

        // Gateway SHOULD be in the path for inter-VLAN routing
        path.Hops.Should().Contain(h => h.Type == HopType.Gateway,
            "gateway should be in path for inter-VLAN routing");
    }

    #endregion

    #region Target is Gateway Tests (Baseline - Should Work)

    /// <summary>
    /// When the target is the gateway itself, path should go from client to gateway.
    /// </summary>
    [Fact]
    public void BuildHopList_TargetIsGateway_PathIncludesGateway()
    {
        // Arrange
        var topology = NetworkTestData.CreateDaisyChainTopology();
        var serverPosition = NetworkTestData.CreateDaisyChainServerPosition();
        var gateway = topology.Devices.First(d => d.Type == DeviceType.Gateway);
        var path = new NetworkPath
        {
            SourceHost = serverPosition.IpAddress,
            DestinationHost = gateway.IpAddress,
            SourceVlanId = 1,
            DestinationVlanId = 1,
            RequiresRouting = false,
            TargetIsGateway = true
        };

        // Act
        _analyzer.BuildHopList(path, serverPosition, gateway, null, topology, new Dictionary<string, UniFiDeviceResponse>());

        // Assert
        path.Hops.Should().NotBeEmpty();

        // Gateway should be in the path as it's the target
        path.Hops.Should().Contain(h => h.Type == HopType.Gateway,
            "gateway should be in path when it's the target");
    }

    #endregion
}
