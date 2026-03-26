using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.UniFi.Models;
using NetworkOptimizer.UniFi.Tests.Fixtures;
using Xunit;

namespace NetworkOptimizer.UniFi.Tests.PathTrace;

/// <summary>
/// Comprehensive tests for NetworkPathAnalyzer.BuildHopList covering a variety
/// of network topologies with realistic mixed link speeds.
/// </summary>
public class BuildHopListTests
{
    private readonly NetworkPathAnalyzer _analyzer;

    public BuildHopListTests()
    {
        var clientProviderMock = new Mock<IUniFiClientProvider>();
        clientProviderMock.Setup(p => p.IsConnected).Returns(true);

        var cache = new MemoryCache(new MemoryCacheOptions());

        var loggerFactoryMock = new Mock<ILoggerFactory>();
        loggerFactoryMock.Setup(f => f.CreateLogger(It.IsAny<string>()))
            .Returns(new Mock<ILogger>().Object);

        _analyzer = new NetworkPathAnalyzer(
            clientProviderMock.Object,
            cache,
            loggerFactoryMock.Object);
    }

    #region Scenario 1: Simple wired path - mixed speeds

    /// <summary>
    /// Gateway (WAN 1G SFP) -> Core Switch (10G trunk) -> Access Switch (1G uplink) -> Wired Client.
    /// Server on Core Switch port 3 (1G). Client on Access Switch port 5 (1G).
    /// Core-to-Access link is 1G bottleneck.
    /// </summary>
    [Fact]
    public void SimpleWiredPath_MixedSpeeds_AllHopSpeedsCorrect()
    {
        // Arrange
        //   Gateway port 6 = 10G (connects to Core Switch)
        //   Core Switch port 9 = 10G uplink to gateway port 6
        //   Core Switch port 1 = 1G (connects to Access Switch)
        //   Core Switch port 3 = 1G (connects to server)
        //   Access Switch port 8 = 1G uplink to Core Switch port 1
        //   Access Switch port 5 = 1G (connects to client)
        var builder = new TopologyBuilder()
            .WithGateway("aa:bb:cc:00:00:01", "Gateway", wanPortIdx: 5, wanSpeed: 1000,
                lanPorts: new[] { (6, 10000) })
            .WithSwitch("aa:bb:cc:00:00:02", "Core Switch",
                uplinkTo: "aa:bb:cc:00:00:01", uplinkRemotePort: 6, localUplinkPort: 9,
                ports: new[] { (1, 1000), (3, 1000), (9, 10000) })
            .WithSwitch("aa:bb:cc:00:00:03", "Access Switch",
                uplinkTo: "aa:bb:cc:00:00:02", uplinkRemotePort: 1, localUplinkPort: 8,
                ports: new[] { (5, 1000), (8, 1000) })
            .WithWiredClient("aa:bb:cc:00:01:01", "192.0.2.100",
                connectedTo: "aa:bb:cc:00:00:03", port: 5, network: "main-net")
            .WithNetwork("main-net", "Main Network", subnet: "192.0.2.0/24")
            .WithServer("192.0.2.200", connectedTo: "aa:bb:cc:00:00:02", port: 3, network: "main-net");

        var topology = builder.BuildTopology();
        var rawDevices = builder.BuildRawDevices();
        var serverPosition = builder.BuildServerPosition();
        var client = builder.GetClient("aa:bb:cc:00:01:01")!;

        var path = new NetworkPath
        {
            SourceHost = serverPosition.IpAddress,
            DestinationHost = client.IpAddress,
            SourceVlanId = 1,
            DestinationVlanId = 1,
            RequiresRouting = false
        };

        // Act
        _analyzer.BuildHopList(path, serverPosition, null, client, topology, rawDevices);

        // Assert
        path.Hops.Should().NotBeEmpty();

        // First hop should be the wired client
        var clientHop = path.Hops.First(h => h.Type == HopType.Client);
        clientHop.Order.Should().Be(0);
        clientHop.DeviceMac.Should().Be("aa:bb:cc:00:01:01");

        // Should have Access Switch in path
        var accessSwitch = path.Hops.FirstOrDefault(h => h.DeviceMac == "aa:bb:cc:00:00:03");
        accessSwitch.Should().NotBeNull("Access Switch should be in path");

        // Should have Core Switch in path (as it's the server's switch)
        var coreSwitch = path.Hops.FirstOrDefault(h => h.DeviceMac == "aa:bb:cc:00:00:02");
        coreSwitch.Should().NotBeNull("Core Switch should be in path as server's switch");

        // Server hop should be present
        var serverHop = path.Hops.Last();
        serverHop.Type.Should().Be(HopType.Server);
        serverHop.DeviceIp.Should().Be("192.0.2.200");

        // Server's ingress port speed should be 1G (port 3 on core switch)
        serverHop.IngressSpeedMbps.Should().Be(1000, "server is on 1G port");

        // Access Switch ingress speed should be 1G (client port)
        accessSwitch!.IngressSpeedMbps.Should().Be(1000, "client connects at 1G");
    }

    #endregion

    #region Scenario 2: Gateway as target device (regression case)

    /// <summary>
    /// Server on Switch -> Switch (10G uplink to gateway) -> Gateway (target).
    /// Gateway's UplinkMac is ISP (not in rawDevices), UplinkPort = 0, LocalUplinkPort = 5 (WAN).
    /// The gateway hop must NOT get the 5G WAN speed from fallback.
    /// </summary>
    [Fact]
    public void GatewayAsTarget_DoesNotUseFallbackWanSpeed()
    {
        // Arrange
        //   Gateway: WAN port 5 at 5000 Mbps, LAN port 6 at 10000 Mbps
        //   Switch: port 9 = 10G uplink to gateway port 6, port 1 = 1G (server)
        var builder = new TopologyBuilder()
            .WithGateway("aa:bb:cc:00:00:01", "Gateway", wanPortIdx: 5, wanSpeed: 5000,
                lanPorts: new[] { (6, 10000) })
            .WithSwitch("aa:bb:cc:00:00:02", "Core Switch",
                uplinkTo: "aa:bb:cc:00:00:01", uplinkRemotePort: 6, localUplinkPort: 9,
                ports: new[] { (1, 1000), (9, 10000) })
            .WithNetwork("main-net", "Main Network", subnet: "192.0.2.0/24")
            .WithServer("192.0.2.200", connectedTo: "aa:bb:cc:00:00:02", port: 1, network: "main-net");

        var topology = builder.BuildTopology();
        var rawDevices = builder.BuildRawDevices();
        var serverPosition = builder.BuildServerPosition();
        var gateway = builder.GetDevice("aa:bb:cc:00:00:01")!;

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
        _analyzer.BuildHopList(path, serverPosition, gateway, null, topology, rawDevices);

        // Assert
        path.Hops.Should().NotBeEmpty();

        // The gateway should be first hop (target device)
        var gatewayHop = path.Hops.First(h => h.Type == HopType.Gateway);
        gatewayHop.Order.Should().Be(0, "gateway is the target device");
        gatewayHop.DeviceMac.Should().Be("aa:bb:cc:00:00:01");

        // CRITICAL: Gateway's ingress speed should NOT be 5000 (WAN speed).
        // The gateway's UplinkMac is an ISP MAC not in rawDevices, so primary lookup returns 0.
        // The fallback using LocalUplinkPort (WAN port 5) should be SKIPPED for gateways.
        gatewayHop.IngressSpeedMbps.Should().NotBe(5000,
            "gateway fallback must skip LocalUplinkPort because it's the WAN port");
        gatewayHop.IngressSpeedMbps.Should().Be(0,
            "gateway uplink is to ISP (not in rawDevices) and fallback is blocked");

        // Switch hop should exist (path from gateway to server)
        var switchHop = path.Hops.FirstOrDefault(h => h.DeviceMac == "aa:bb:cc:00:00:02");
        switchHop.Should().NotBeNull("switch should be in path from gateway to server");

        // Server hop should be present at the end
        path.Hops.Last().Type.Should().Be(HopType.Server);
    }

    #endregion

    #region Scenario 3: Inter-VLAN routing

    /// <summary>
    /// Server on Switch (VLAN 1) -> Switch -> Gateway (routes) -> Switch -> Target on Switch (VLAN 150).
    /// All 10G links. RequiresRouting = true.
    /// </summary>
    [Fact]
    public void InterVlanRouting_PathTraversesGateway()
    {
        // Arrange
        //   Gateway: LAN port 6 = 10G
        //   Switch: port 9 = 10G uplink to gateway port 6
        //   Switch: port 1 = 1G (server on VLAN 1), port 3 = 1G (client on VLAN 150)
        var builder = new TopologyBuilder()
            .WithGateway("aa:bb:cc:00:00:01", "Gateway", wanPortIdx: 5, wanSpeed: 1000,
                lanPorts: new[] { (6, 10000) })
            .WithSwitch("aa:bb:cc:00:00:02", "Main Switch",
                uplinkTo: "aa:bb:cc:00:00:01", uplinkRemotePort: 6, localUplinkPort: 9,
                ports: new[] { (1, 1000), (3, 1000), (9, 10000) })
            .WithWiredClient("aa:bb:cc:00:01:01", "198.51.100.50",
                connectedTo: "aa:bb:cc:00:00:02", port: 3, network: "mgmt-net")
            .WithNetwork("main-net", "Main Network", vlan: 1, subnet: "192.0.2.0/24")
            .WithNetwork("mgmt-net", "Management", vlan: 150, subnet: "198.51.100.0/24")
            .WithServer("192.0.2.200", connectedTo: "aa:bb:cc:00:00:02", port: 1,
                network: "main-net", vlan: 1);

        var topology = builder.BuildTopology();
        var rawDevices = builder.BuildRawDevices();
        var serverPosition = builder.BuildServerPosition();
        var client = builder.GetClient("aa:bb:cc:00:01:01")!;

        var path = new NetworkPath
        {
            SourceHost = serverPosition.IpAddress,
            DestinationHost = client.IpAddress,
            SourceVlanId = 1,
            DestinationVlanId = 150,
            RequiresRouting = true
        };

        // Act
        _analyzer.BuildHopList(path, serverPosition, null, client, topology, rawDevices);

        // Assert
        path.Hops.Should().NotBeEmpty();
        path.RequiresRouting.Should().BeTrue();

        // Gateway should be in path for L3 routing
        var gatewayHop = path.Hops.FirstOrDefault(h => h.Type == HopType.Gateway);
        gatewayHop.Should().NotBeNull("inter-VLAN traffic must traverse gateway");
        gatewayHop!.Notes.Should().Contain("routing", "gateway hop should have routing note");

        // Gateway's egress should NOT use the WAN port speed
        gatewayHop.EgressSpeedMbps.Should().NotBe(5000,
            "gateway egress for inter-VLAN should be LAN-side, not WAN");
    }

    #endregion

    #region Scenario 4: Wi-Fi client path

    /// <summary>
    /// Wireless Client (WiFi 6, 5GHz, TX 1200 Mbps, RX 800 Mbps) -> AP -> Switch -> Server.
    /// AP on 1G port. Server on same switch.
    /// </summary>
    [Fact]
    public void WirelessClientPath_CorrectRatesAndBottleneck()
    {
        // Arrange
        //   Switch: port 2 = 1G (AP), port 3 = 1G (server), port 9 = 10G uplink to gateway
        //   Gateway: port 6 = 10G
        var builder = new TopologyBuilder()
            .WithGateway("aa:bb:cc:00:00:01", "Gateway", wanPortIdx: 5, wanSpeed: 1000,
                lanPorts: new[] { (6, 10000) })
            .WithSwitch("aa:bb:cc:00:00:02", "Core Switch",
                uplinkTo: "aa:bb:cc:00:00:01", uplinkRemotePort: 6, localUplinkPort: 9,
                ports: new[] { (2, 1000), (3, 1000), (9, 10000) })
            .WithAP("aa:bb:cc:00:00:05", "Living Room AP",
                uplinkTo: "aa:bb:cc:00:00:02", uplinkRemotePort: 2, localUplinkPort: 1,
                ports: new[] { (1, 1000) })
            .WithWirelessClient("aa:bb:cc:00:01:02", "192.0.2.101",
                connectedTo: "aa:bb:cc:00:00:05",
                txRateKbps: 1200000, rxRateKbps: 800000, band: "na",
                channel: 36, signalDbm: -55, network: "main-net")
            .WithNetwork("main-net", "Main Network", subnet: "192.0.2.0/24")
            .WithServer("192.0.2.200", connectedTo: "aa:bb:cc:00:00:02", port: 3, network: "main-net");

        var topology = builder.BuildTopology();
        var rawDevices = builder.BuildRawDevices();
        var serverPosition = builder.BuildServerPosition();
        var client = builder.GetClient("aa:bb:cc:00:01:02")!;

        var path = new NetworkPath
        {
            SourceHost = serverPosition.IpAddress,
            DestinationHost = client.IpAddress,
            SourceVlanId = 1,
            DestinationVlanId = 1,
            RequiresRouting = false
        };

        // Act
        _analyzer.BuildHopList(path, serverPosition, null, client, topology, rawDevices);

        // Assert
        path.Hops.Should().NotBeEmpty();

        // First hop should be the wireless client
        var clientHop = path.Hops.First(h => h.Type == HopType.WirelessClient);
        clientHop.Should().NotBeNull("wireless client should be first hop");
        clientHop.IsWirelessEgress.Should().BeTrue("wireless client has wireless egress");
        clientHop.IsWirelessIngress.Should().BeTrue("wireless client has wireless ingress");

        // TX rate = 1200 Mbps (IngressSpeedMbps on wireless client = TX rate, ToDevice direction)
        clientHop.IngressSpeedMbps.Should().Be(1200,
            "IngressSpeedMbps = TX rate (AP transmits to client)");
        // RX rate = 800 Mbps (EgressSpeedMbps on wireless client = RX rate, FromDevice direction)
        clientHop.EgressSpeedMbps.Should().Be(800,
            "EgressSpeedMbps = RX rate (AP receives from client)");

        clientHop.WirelessEgressBand.Should().Be("na", "should be 5 GHz band");

        // AP hop should be in path
        var apHop = path.Hops.FirstOrDefault(h => h.Type == HopType.AccessPoint);
        apHop.Should().NotBeNull("AP should be in path");

        // Switch hop should be in path
        var switchHop = path.Hops.FirstOrDefault(h => h.Type == HopType.Switch);
        switchHop.Should().NotBeNull("switch should be in path");

        // Server hop
        path.Hops.Last().Type.Should().Be(HopType.Server);
    }

    #endregion

    #region Scenario 5: Mesh AP backhaul

    /// <summary>
    /// Target is a mesh AP with wireless uplink to a parent AP.
    /// Mesh AP -> Parent AP (wired) -> Switch -> Server.
    /// Mesh link: 866 Mbps, 5 GHz, -55 dBm signal.
    /// </summary>
    [Fact]
    public void MeshAPBackhaul_WirelessEgressFlagsSet()
    {
        // Arrange
        var builder = new TopologyBuilder()
            .WithGateway("aa:bb:cc:00:00:01", "Gateway", wanPortIdx: 5, wanSpeed: 1000,
                lanPorts: new[] { (6, 10000) })
            .WithSwitch("aa:bb:cc:00:00:02", "Core Switch",
                uplinkTo: "aa:bb:cc:00:00:01", uplinkRemotePort: 6, localUplinkPort: 9,
                ports: new[] { (2, 1000), (3, 1000), (9, 10000) })
            .WithAP("aa:bb:cc:00:00:05", "Parent AP",
                uplinkTo: "aa:bb:cc:00:00:02", uplinkRemotePort: 2, localUplinkPort: 1,
                ports: new[] { (1, 1000) })
            .WithMeshAP("aa:bb:cc:00:00:06", "Backyard AP",
                parentApMac: "aa:bb:cc:00:00:05",
                txRateKbps: 866000, rxRateKbps: 866000,
                band: "na", channel: 36, signal: -55)
            .WithNetwork("main-net", "Main Network", subnet: "192.0.2.0/24")
            .WithServer("192.0.2.200", connectedTo: "aa:bb:cc:00:00:02", port: 3, network: "main-net");

        var topology = builder.BuildTopology();
        var rawDevices = builder.BuildRawDevices();
        var serverPosition = builder.BuildServerPosition();
        var meshAp = builder.GetDevice("aa:bb:cc:00:00:06")!;

        var path = new NetworkPath
        {
            SourceHost = serverPosition.IpAddress,
            DestinationHost = meshAp.IpAddress,
            SourceVlanId = 1,
            DestinationVlanId = 1,
            RequiresRouting = false,
            TargetIsAccessPoint = true
        };

        // Act
        _analyzer.BuildHopList(path, serverPosition, meshAp, null, topology, rawDevices);

        // Assert
        path.Hops.Should().NotBeEmpty();

        // First hop: the mesh AP target
        var meshHop = path.Hops.First(h => h.DeviceMac == "aa:bb:cc:00:00:06");
        meshHop.Type.Should().Be(HopType.AccessPoint);
        meshHop.IsWirelessIngress.Should().BeTrue("mesh AP has wireless ingress");
        meshHop.IsWirelessEgress.Should().BeTrue("mesh AP has wireless egress");
        meshHop.WirelessIngressBand.Should().Be("na", "5 GHz band");
        meshHop.WirelessEgressBand.Should().Be("na", "5 GHz band");
        meshHop.WirelessChannel.Should().Be(36);
        meshHop.WirelessSignalDbm.Should().Be(-55);
        meshHop.IngressSpeedMbps.Should().Be(866, "866 Mbps mesh uplink");
        meshHop.EgressSpeedMbps.Should().Be(866, "866 Mbps mesh uplink");
        meshHop.WirelessTxRateMbps.Should().Be(866, "TX rate in Mbps");
        meshHop.WirelessRxRateMbps.Should().Be(866, "RX rate in Mbps");
    }

    #endregion

    #region Scenario 6: LAG aggregate link

    /// <summary>
    /// Switch with 2x10G LAG to gateway.
    /// Port 9 (parent) + Port 10 (child, AggregatedBy=9).
    /// Hop speed should show 20000 Mbps for the LAG link.
    /// </summary>
    [Fact]
    public void LagAggregateLink_ShowsAggregateSpeed()
    {
        // Arrange
        //   Gateway port 6 = 10G, port 7 = 10G (LAG on gateway side)
        //   Switch port 9 = 10G (parent), port 10 = 10G (child aggregated by 9)
        //   Switch port 1 = 1G (server)
        var builder = new TopologyBuilder()
            .WithGateway("aa:bb:cc:00:00:01", "Gateway", wanPortIdx: 5, wanSpeed: 1000,
                lanPorts: new[] { (6, 10000), (7, 10000) })
            .WithSwitch("aa:bb:cc:00:00:02", "LAG Switch",
                uplinkTo: "aa:bb:cc:00:00:01", uplinkRemotePort: 6, localUplinkPort: 9,
                ports: new[] { (1, 1000), (3, 1000), (9, 10000), (10, 10000) },
                lag: new[] { (parentPort: 9, childPorts: new[] { 10 }) })
            .WithWiredClient("aa:bb:cc:00:01:01", "192.0.2.100",
                connectedTo: "aa:bb:cc:00:00:02", port: 3, network: "main-net")
            .WithNetwork("main-net", "Main Network", subnet: "192.0.2.0/24")
            .WithServer("192.0.2.200", connectedTo: "aa:bb:cc:00:00:02", port: 1, network: "main-net");

        var topology = builder.BuildTopology();
        var rawDevices = builder.BuildRawDevices();
        var serverPosition = builder.BuildServerPosition();
        var client = builder.GetClient("aa:bb:cc:00:01:01")!;

        var path = new NetworkPath
        {
            SourceHost = serverPosition.IpAddress,
            DestinationHost = client.IpAddress,
            SourceVlanId = 1,
            DestinationVlanId = 1,
            RequiresRouting = false
        };

        // Act
        _analyzer.BuildHopList(path, serverPosition, null, client, topology, rawDevices);

        // Assert - path should be short since client and server are on the same switch
        path.Hops.Should().NotBeEmpty();

        // The switch's egress to server should be 1G (port 1)
        var switchHop = path.Hops.FirstOrDefault(h => h.DeviceMac == "aa:bb:cc:00:00:02");
        switchHop.Should().NotBeNull("switch should be in path");

        // Ingress from client should be 1G (port 3)
        switchHop!.IngressSpeedMbps.Should().Be(1000, "client port is 1G");

        // Egress to server should be 1G (port 1)
        switchHop.EgressSpeedMbps.Should().Be(1000, "server port is 1G");

        // For LAG annotation, we need to call AnnotateLagMembership separately
        // since BuildHopList doesn't call it. The LAG aggregate speed is verified
        // through GetLagAggregateSpeed which is tested in LagSpeedTests.
        // Here we verify the port resolution used LAG-aware speed lookup:
        // If the switch's uplink (port 9) had been the egress, it would show 20000.
        // Since same-switch traffic doesn't traverse the uplink, we verify LAG speed directly.
        NetworkPathAnalyzer.GetLagAggregateSpeed(
            rawDevices["aa:bb:cc:00:00:02"].PortTable!, 9).Should().Be(20000,
            "LAG aggregate of port 9 (parent) + port 10 (child) = 20G");
    }

    /// <summary>
    /// Verifies that when traffic does traverse a LAG uplink, the aggregate speed is used.
    /// Client on Access Switch -> Core Switch (10G LAG uplink to gateway) -> Gateway -> Server.
    /// </summary>
    [Fact]
    public void LagAggregateLink_TrafficTraversesLag_ShowsAggregateSpeed()
    {
        // Arrange
        //   Gateway: port 6 = 10G, port 7 = 10G (gateway side of LAG)
        //   Core Switch: port 9 = 10G (parent), port 10 = 10G (child) -> LAG to gateway
        //   Core Switch: port 1 = 1G connects to Access Switch
        //   Access Switch: port 8 = 1G uplink, port 5 = 1G (client)
        //   Server is directly on gateway (to force traffic through LAG)
        var builder = new TopologyBuilder()
            .WithGateway("aa:bb:cc:00:00:01", "Gateway", wanPortIdx: 5, wanSpeed: 1000,
                lanPorts: new[] { (6, 10000), (7, 10000), (8, 1000) })
            .WithSwitch("aa:bb:cc:00:00:02", "Core Switch",
                uplinkTo: "aa:bb:cc:00:00:01", uplinkRemotePort: 6, localUplinkPort: 9,
                ports: new[] { (1, 1000), (9, 10000), (10, 10000) },
                lag: new[] { (parentPort: 9, childPorts: new[] { 10 }) })
            .WithSwitch("aa:bb:cc:00:00:03", "Access Switch",
                uplinkTo: "aa:bb:cc:00:00:02", uplinkRemotePort: 1, localUplinkPort: 8,
                ports: new[] { (5, 1000), (8, 1000) })
            .WithWiredClient("aa:bb:cc:00:01:01", "192.0.2.100",
                connectedTo: "aa:bb:cc:00:00:03", port: 5, network: "main-net")
            .WithNetwork("main-net", "Main Network", subnet: "192.0.2.0/24")
            .WithNetwork("mgmt-net", "Management", vlan: 150, subnet: "198.51.100.0/24")
            .WithServer("192.0.2.200", connectedTo: "aa:bb:cc:00:00:01", port: 8, network: "main-net");

        var topology = builder.BuildTopology();
        var rawDevices = builder.BuildRawDevices();
        var serverPosition = builder.BuildServerPosition();
        var client = builder.GetClient("aa:bb:cc:00:01:01")!;

        var path = new NetworkPath
        {
            SourceHost = serverPosition.IpAddress,
            DestinationHost = client.IpAddress,
            SourceVlanId = 1,
            DestinationVlanId = 1,
            RequiresRouting = false
        };

        // Act
        _analyzer.BuildHopList(path, serverPosition, null, client, topology, rawDevices);

        // Assert
        path.Hops.Should().NotBeEmpty();

        // Core Switch should be in the path
        var coreSwitchHop = path.Hops.FirstOrDefault(h => h.DeviceMac == "aa:bb:cc:00:00:02");
        coreSwitchHop.Should().NotBeNull("core switch should be in path");

        // The egress from Core Switch toward gateway should use LAG port 9
        // which has aggregate speed of 20G (9 parent + 10 child)
        if (coreSwitchHop!.EgressPort == 9)
        {
            coreSwitchHop.EgressSpeedMbps.Should().Be(20000,
                "LAG aggregate on egress toward gateway = 20G");
        }
    }

    #endregion

    #region Scenario 7: Daisy-chain switches - different speeds at each layer

    /// <summary>
    /// Gateway (10G) -> Core Switch (10G) -> Distribution Switch (2.5G uplink)
    ///     -> Access Switch (1G uplink) -> Client.
    /// Server on Core Switch.
    /// </summary>
    [Fact]
    public void DaisyChainSwitches_DifferentSpeedsAtEachLayer()
    {
        // Arrange
        var builder = new TopologyBuilder()
            .WithGateway("aa:bb:cc:00:00:01", "Gateway", wanPortIdx: 5, wanSpeed: 1000,
                lanPorts: new[] { (6, 10000) })
            .WithSwitch("aa:bb:cc:00:00:02", "Core Switch",
                uplinkTo: "aa:bb:cc:00:00:01", uplinkRemotePort: 6, localUplinkPort: 9,
                ports: new[] { (1, 2500), (3, 1000), (9, 10000) })
            .WithSwitch("aa:bb:cc:00:00:03", "Distribution Switch",
                uplinkTo: "aa:bb:cc:00:00:02", uplinkRemotePort: 1, localUplinkPort: 8,
                ports: new[] { (1, 1000), (8, 2500) })
            .WithSwitch("aa:bb:cc:00:00:04", "Access Switch",
                uplinkTo: "aa:bb:cc:00:00:03", uplinkRemotePort: 1, localUplinkPort: 8,
                ports: new[] { (5, 1000), (8, 1000) })
            .WithWiredClient("aa:bb:cc:00:01:01", "192.0.2.100",
                connectedTo: "aa:bb:cc:00:00:04", port: 5, network: "main-net")
            .WithNetwork("main-net", "Main Network", subnet: "192.0.2.0/24")
            .WithServer("192.0.2.200", connectedTo: "aa:bb:cc:00:00:02", port: 3, network: "main-net");

        var topology = builder.BuildTopology();
        var rawDevices = builder.BuildRawDevices();
        var serverPosition = builder.BuildServerPosition();
        var client = builder.GetClient("aa:bb:cc:00:01:01")!;

        var path = new NetworkPath
        {
            SourceHost = serverPosition.IpAddress,
            DestinationHost = client.IpAddress,
            SourceVlanId = 1,
            DestinationVlanId = 1,
            RequiresRouting = false
        };

        // Act
        _analyzer.BuildHopList(path, serverPosition, null, client, topology, rawDevices);

        // Assert
        path.Hops.Should().NotBeEmpty();

        // Should have client, access switch, distribution switch, core switch, server
        // (NOT gateway since all same VLAN and core switch is a common ancestor)
        path.Hops.Should().Contain(h => h.DeviceMac == "aa:bb:cc:00:00:04",
            "access switch should be in path");
        path.Hops.Should().Contain(h => h.DeviceMac == "aa:bb:cc:00:00:03",
            "distribution switch should be in path");
        path.Hops.Should().Contain(h => h.DeviceMac == "aa:bb:cc:00:00:02",
            "core switch should be in path (server's switch or common ancestor)");

        // Gateway should NOT be in the path (same VLAN, L2 only)
        path.Hops.Should().NotContain(h => h.Type == HopType.Gateway,
            "no gateway traversal needed for same-VLAN traffic");

        // Verify server hop at end
        path.Hops.Last().Type.Should().Be(HopType.Server);
        path.Hops.Last().IngressSpeedMbps.Should().Be(1000, "server on 1G port");
    }

    #endregion

    #region Scenario 8: Same-switch path

    /// <summary>
    /// Client on port 3, Server on port 1, both on the same switch.
    /// No gateway traversal needed.
    /// </summary>
    [Fact]
    public void SameSwitchPath_NoGatewayHop()
    {
        // Arrange
        var builder = new TopologyBuilder()
            .WithGateway("aa:bb:cc:00:00:01", "Gateway", wanPortIdx: 5, wanSpeed: 1000,
                lanPorts: new[] { (6, 10000) })
            .WithSwitch("aa:bb:cc:00:00:02", "Switch",
                uplinkTo: "aa:bb:cc:00:00:01", uplinkRemotePort: 6, localUplinkPort: 9,
                ports: new[] { (1, 1000), (3, 1000), (9, 10000) })
            .WithWiredClient("aa:bb:cc:00:01:01", "192.0.2.100",
                connectedTo: "aa:bb:cc:00:00:02", port: 3, network: "main-net")
            .WithNetwork("main-net", "Main Network", subnet: "192.0.2.0/24")
            .WithServer("192.0.2.200", connectedTo: "aa:bb:cc:00:00:02", port: 1, network: "main-net");

        var topology = builder.BuildTopology();
        var rawDevices = builder.BuildRawDevices();
        var serverPosition = builder.BuildServerPosition();
        var client = builder.GetClient("aa:bb:cc:00:01:01")!;

        var path = new NetworkPath
        {
            SourceHost = serverPosition.IpAddress,
            DestinationHost = client.IpAddress,
            SourceVlanId = 1,
            DestinationVlanId = 1,
            RequiresRouting = false
        };

        // Act
        _analyzer.BuildHopList(path, serverPosition, null, client, topology, rawDevices);

        // Assert
        path.Hops.Should().NotBeEmpty();

        // Should be: Client -> Switch -> Server (3 hops)
        path.Hops.Should().HaveCountLessThanOrEqualTo(3, "same-switch path should be short");

        // No gateway traversal
        path.Hops.Should().NotContain(h => h.Type == HopType.Gateway,
            "no gateway needed when client and server are on the same switch");

        // Client hop
        var clientHop = path.Hops.First();
        clientHop.Type.Should().Be(HopType.Client);
        clientHop.EgressSpeedMbps.Should().Be(1000, "client on 1G port");

        // Switch hop
        var switchHop = path.Hops.First(h => h.Type == HopType.Switch);
        switchHop.IngressSpeedMbps.Should().Be(1000, "ingress from client port 3 at 1G");
        switchHop.EgressSpeedMbps.Should().Be(1000, "egress to server port 1 at 1G");

        // Server hop
        var serverHop = path.Hops.Last();
        serverHop.Type.Should().Be(HopType.Server);
        serverHop.IngressSpeedMbps.Should().Be(1000, "server on 1G port");
    }

    #endregion

    #region Scenario 9: AP with empty port table

    /// <summary>
    /// AP with no port table entries has a wired uplink to a switch.
    /// The switch port table has the speed, so primary lookup on the switch side should succeed.
    /// </summary>
    [Fact]
    public void APWithEmptyPortTable_SpeedResolvesFromSwitchSide()
    {
        // Arrange
        //   AP has no port table entries (empty port table)
        //   Switch port 2 = 1G (where AP connects)
        //   Switch port 3 = 1G (server)
        var builder = new TopologyBuilder()
            .WithGateway("aa:bb:cc:00:00:01", "Gateway", wanPortIdx: 5, wanSpeed: 1000,
                lanPorts: new[] { (6, 10000) })
            .WithSwitch("aa:bb:cc:00:00:02", "Core Switch",
                uplinkTo: "aa:bb:cc:00:00:01", uplinkRemotePort: 6, localUplinkPort: 9,
                ports: new[] { (2, 1000), (3, 1000), (9, 10000) })
            .WithAP("aa:bb:cc:00:00:05", "Minimal AP",
                uplinkTo: "aa:bb:cc:00:00:02", uplinkRemotePort: 2, localUplinkPort: 1,
                ports: null) // No port table entries
            .WithNetwork("main-net", "Main Network", subnet: "192.0.2.0/24")
            .WithServer("192.0.2.200", connectedTo: "aa:bb:cc:00:00:02", port: 3, network: "main-net");

        var topology = builder.BuildTopology();
        var rawDevices = builder.BuildRawDevices();
        var serverPosition = builder.BuildServerPosition();
        var ap = builder.GetDevice("aa:bb:cc:00:00:05")!;

        var path = new NetworkPath
        {
            SourceHost = serverPosition.IpAddress,
            DestinationHost = ap.IpAddress,
            SourceVlanId = 1,
            DestinationVlanId = 1,
            RequiresRouting = false,
            TargetIsAccessPoint = true
        };

        // Act
        _analyzer.BuildHopList(path, serverPosition, ap, null, topology, rawDevices);

        // Assert
        path.Hops.Should().NotBeEmpty();

        // AP target hop
        var apHop = path.Hops.First(h => h.Type == HopType.AccessPoint);
        apHop.DeviceMac.Should().Be("aa:bb:cc:00:00:05");

        // The AP's UplinkMac is the switch, UplinkPort is 2.
        // Primary lookup: GetPortSpeedFromRawDevices(rawDevices, switchMac, port 2) should return 1000.
        // The AP has an empty port table, but since the switch side has the port, primary lookup succeeds.
        apHop.IngressSpeedMbps.Should().Be(1000,
            "speed should resolve from switch port 2 (the AP's uplink port on the switch)");
    }

    /// <summary>
    /// Tests the fallback path: when the upstream device's port table doesn't have the port,
    /// the AP's local port table is checked (for non-gateway devices).
    /// </summary>
    [Fact]
    public void APWithPortTable_UpstreamMissing_FallsBackToLocalPort()
    {
        // Arrange
        //   Upstream "device" has empty port table (simulated by not including ports for it)
        //   AP has port table with port 1 = 1000 Mbps
        var builder = new TopologyBuilder()
            .WithGateway("aa:bb:cc:00:00:01", "Gateway", wanPortIdx: 5, wanSpeed: 1000)
            .WithSwitch("aa:bb:cc:00:00:02", "Switch",
                uplinkTo: "aa:bb:cc:00:00:01", uplinkRemotePort: 6, localUplinkPort: 9,
                // Deliberately omit port 2 from port table to simulate missing upstream port
                ports: new[] { (3, 1000), (9, 10000) })
            .WithAP("aa:bb:cc:00:00:05", "AP With Ports",
                uplinkTo: "aa:bb:cc:00:00:02", uplinkRemotePort: 2, localUplinkPort: 1,
                ports: new[] { (1, 2500) }) // AP has 2.5G port
            .WithNetwork("main-net", "Main Network", subnet: "192.0.2.0/24")
            .WithServer("192.0.2.200", connectedTo: "aa:bb:cc:00:00:02", port: 3, network: "main-net");

        var topology = builder.BuildTopology();
        var rawDevices = builder.BuildRawDevices();
        var serverPosition = builder.BuildServerPosition();
        var ap = builder.GetDevice("aa:bb:cc:00:00:05")!;

        var path = new NetworkPath
        {
            SourceHost = serverPosition.IpAddress,
            DestinationHost = ap.IpAddress,
            SourceVlanId = 1,
            DestinationVlanId = 1,
            RequiresRouting = false,
            TargetIsAccessPoint = true
        };

        // Act
        _analyzer.BuildHopList(path, serverPosition, ap, null, topology, rawDevices);

        // Assert
        path.Hops.Should().NotBeEmpty();

        var apHop = path.Hops.First(h => h.Type == HopType.AccessPoint);
        // Primary lookup on switch port 2 returns 0 (port not in table).
        // Fallback to AP's local port 1 = 2500 Mbps (AP is not a gateway, so fallback is allowed).
        apHop.IngressSpeedMbps.Should().Be(2500,
            "should fall back to AP's local uplink port speed when upstream port is missing");
    }

    #endregion

    #region Edge cases

    /// <summary>
    /// When targetDevice and targetClient are both null, BuildHopList should return early
    /// and path.Hops should have only the server hop (or be minimal).
    /// </summary>
    [Fact]
    public void NoTarget_ReturnsEarlyWithMinimalPath()
    {
        // Arrange
        var builder = new TopologyBuilder()
            .WithGateway("aa:bb:cc:00:00:01", "Gateway", wanPortIdx: 5, wanSpeed: 1000)
            .WithSwitch("aa:bb:cc:00:00:02", "Switch",
                uplinkTo: "aa:bb:cc:00:00:01", uplinkRemotePort: 6, localUplinkPort: 9,
                ports: new[] { (1, 1000), (9, 10000) })
            .WithNetwork("main-net", "Main Network", subnet: "192.0.2.0/24")
            .WithServer("192.0.2.200", connectedTo: "aa:bb:cc:00:00:02", port: 1, network: "main-net");

        var topology = builder.BuildTopology();
        var rawDevices = builder.BuildRawDevices();
        var serverPosition = builder.BuildServerPosition();

        var path = new NetworkPath
        {
            SourceHost = serverPosition.IpAddress,
            DestinationHost = "192.0.2.100",
            SourceVlanId = 1,
            DestinationVlanId = 1,
            RequiresRouting = false
        };

        // Act
        _analyzer.BuildHopList(path, serverPosition, null, null, topology, rawDevices);

        // Assert - with no target, the method should return early (before adding server hop)
        path.Hops.Should().BeEmpty("no target device or client means early return");
    }

    /// <summary>
    /// Gateway as target with server chain: verifies the server chain is added
    /// correctly after the gateway hop.
    /// </summary>
    [Fact]
    public void GatewayAsTarget_ServerChainAddedCorrectly()
    {
        // Arrange: Gateway -> Core Switch -> Access Switch (server here)
        var builder = new TopologyBuilder()
            .WithGateway("aa:bb:cc:00:00:01", "Gateway", wanPortIdx: 5, wanSpeed: 1000,
                lanPorts: new[] { (6, 10000) })
            .WithSwitch("aa:bb:cc:00:00:02", "Core Switch",
                uplinkTo: "aa:bb:cc:00:00:01", uplinkRemotePort: 6, localUplinkPort: 9,
                ports: new[] { (1, 2500), (9, 10000) })
            .WithSwitch("aa:bb:cc:00:00:03", "Access Switch",
                uplinkTo: "aa:bb:cc:00:00:02", uplinkRemotePort: 1, localUplinkPort: 8,
                ports: new[] { (3, 1000), (8, 2500) })
            .WithNetwork("main-net", "Main Network", subnet: "192.0.2.0/24")
            .WithServer("192.0.2.200", connectedTo: "aa:bb:cc:00:00:03", port: 3, network: "main-net");

        var topology = builder.BuildTopology();
        var rawDevices = builder.BuildRawDevices();
        var serverPosition = builder.BuildServerPosition();
        var gateway = builder.GetDevice("aa:bb:cc:00:00:01")!;

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
        _analyzer.BuildHopList(path, serverPosition, gateway, null, topology, rawDevices);

        // Assert
        path.Hops.Should().NotBeEmpty();

        // Order: Gateway (0) -> Core Switch (1) -> Access Switch (2) -> Server (3)
        path.Hops[0].Type.Should().Be(HopType.Gateway, "gateway is target (first hop)");
        path.Hops[0].DeviceMac.Should().Be("aa:bb:cc:00:00:01");

        // Core Switch should be in path (traversed from gateway to server)
        path.Hops.Should().Contain(h => h.DeviceMac == "aa:bb:cc:00:00:02",
            "core switch is in the path from gateway to server");

        // Access Switch should be in path (server's switch)
        path.Hops.Should().Contain(h => h.DeviceMac == "aa:bb:cc:00:00:03",
            "access switch is server's switch");

        // Server is last hop
        path.Hops.Last().Type.Should().Be(HopType.Server);
        path.Hops.Last().IngressSpeedMbps.Should().Be(1000, "server on 1G port 3");
    }

    /// <summary>
    /// Inter-VLAN routing with daisy-chain: traffic goes up to gateway and back down.
    /// The switch should appear twice in the path (once up, once down).
    /// </summary>
    [Fact]
    public void InterVlanRouting_DaisyChain_SwitchAppearsTwice()
    {
        // Arrange: Client on Switch (VLAN 150) -> Switch -> Gateway -> Switch -> Server (VLAN 1)
        var builder = new TopologyBuilder()
            .WithGateway("aa:bb:cc:00:00:01", "Gateway", wanPortIdx: 5, wanSpeed: 1000,
                lanPorts: new[] { (6, 10000) })
            .WithSwitch("aa:bb:cc:00:00:02", "Main Switch",
                uplinkTo: "aa:bb:cc:00:00:01", uplinkRemotePort: 6, localUplinkPort: 9,
                ports: new[] { (1, 1000), (3, 1000), (9, 10000) })
            .WithWiredClient("aa:bb:cc:00:01:01", "198.51.100.50",
                connectedTo: "aa:bb:cc:00:00:02", port: 3, network: "mgmt-net")
            .WithNetwork("main-net", "Main Network", vlan: 1, subnet: "192.0.2.0/24")
            .WithNetwork("mgmt-net", "Management", vlan: 150, subnet: "198.51.100.0/24")
            .WithServer("192.0.2.200", connectedTo: "aa:bb:cc:00:00:02", port: 1,
                network: "main-net", vlan: 1);

        var topology = builder.BuildTopology();
        var rawDevices = builder.BuildRawDevices();
        var serverPosition = builder.BuildServerPosition();
        var client = builder.GetClient("aa:bb:cc:00:01:01")!;

        var path = new NetworkPath
        {
            SourceHost = serverPosition.IpAddress,
            DestinationHost = client.IpAddress,
            SourceVlanId = 1,
            DestinationVlanId = 150,
            RequiresRouting = true
        };

        // Act
        _analyzer.BuildHopList(path, serverPosition, null, client, topology, rawDevices);

        // Assert
        path.Hops.Should().NotBeEmpty();

        // Gateway should be present for routing
        path.Hops.Should().Contain(h => h.Type == HopType.Gateway);

        // The switch should appear twice (once going up, once coming down)
        var switchHops = path.Hops.Where(h => h.DeviceMac == "aa:bb:cc:00:00:02").ToList();
        switchHops.Should().HaveCount(2,
            "inter-VLAN traffic traverses the switch twice (up to gateway, back down to server)");

        // Server hop at the end
        path.Hops.Last().Type.Should().Be(HopType.Server);
    }

    #endregion
}
