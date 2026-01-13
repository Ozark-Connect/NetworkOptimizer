using FluentAssertions;
using NetworkOptimizer.UniFi.Models;
using Xunit;

namespace NetworkOptimizer.UniFi.Tests;

public class NetworkPathTests
{
    #region Default Values Tests

    [Fact]
    public void NetworkPath_DefaultValues_AreCorrect()
    {
        // Act
        var path = new NetworkPath();

        // Assert
        path.SourceHost.Should().BeEmpty();
        path.SourceMac.Should().BeEmpty();
        path.SourceVlanId.Should().BeNull();
        path.SourceNetworkName.Should().BeNull();
        path.DestinationHost.Should().BeEmpty();
        path.DestinationMac.Should().BeEmpty();
        path.DestinationVlanId.Should().BeNull();
        path.DestinationNetworkName.Should().BeNull();
        path.Hops.Should().NotBeNull().And.BeEmpty();
        path.RequiresRouting.Should().BeFalse();
        path.GatewayDevice.Should().BeNull();
        path.GatewayModel.Should().BeNull();
        path.TheoreticalMaxMbps.Should().Be(0);
        path.RealisticMaxMbps.Should().Be(0);
        path.BottleneckDescription.Should().BeNull();
        path.IsValid.Should().BeTrue();
        path.ErrorMessage.Should().BeNull();
        path.HasRealBottleneck.Should().BeFalse();
        path.TargetIsGateway.Should().BeFalse();
        path.TargetIsAccessPoint.Should().BeFalse();
        path.TargetIsCellularModem.Should().BeFalse();
    }

    [Fact]
    public void NetworkPath_CalculatedAt_DefaultsToNearNow()
    {
        // Act
        var path = new NetworkPath();

        // Assert
        path.CalculatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    #endregion

    #region SwitchHopCount Tests

    [Fact]
    public void SwitchHopCount_NoHops_ReturnsZero()
    {
        // Arrange
        var path = new NetworkPath();

        // Act & Assert
        path.SwitchHopCount.Should().Be(0);
    }

    [Fact]
    public void SwitchHopCount_NoSwitches_ReturnsZero()
    {
        // Arrange
        var path = new NetworkPath
        {
            Hops = new List<NetworkHop>
            {
                new() { Type = HopType.Client },
                new() { Type = HopType.AccessPoint },
                new() { Type = HopType.Gateway }
            }
        };

        // Act & Assert
        path.SwitchHopCount.Should().Be(0);
    }

    [Fact]
    public void SwitchHopCount_MultipleSwitches_CountsCorrectly()
    {
        // Arrange
        var path = new NetworkPath
        {
            Hops = new List<NetworkHop>
            {
                new() { Type = HopType.Client },
                new() { Type = HopType.Switch },
                new() { Type = HopType.Switch },
                new() { Type = HopType.Switch },
                new() { Type = HopType.Gateway }
            }
        };

        // Act & Assert
        path.SwitchHopCount.Should().Be(3);
    }

    #endregion

    #region HasWirelessSegment Tests

    [Fact]
    public void HasWirelessSegment_NoHops_ReturnsFalse()
    {
        // Arrange
        var path = new NetworkPath();

        // Act & Assert
        path.HasWirelessSegment.Should().BeFalse();
    }

    [Fact]
    public void HasWirelessSegment_NoAps_ReturnsFalse()
    {
        // Arrange
        var path = new NetworkPath
        {
            Hops = new List<NetworkHop>
            {
                new() { Type = HopType.Client },
                new() { Type = HopType.Switch },
                new() { Type = HopType.Gateway }
            }
        };

        // Act & Assert
        path.HasWirelessSegment.Should().BeFalse();
    }

    [Fact]
    public void HasWirelessSegment_WithAp_ReturnsTrue()
    {
        // Arrange
        var path = new NetworkPath
        {
            Hops = new List<NetworkHop>
            {
                new() { Type = HopType.WirelessClient },
                new() { Type = HopType.AccessPoint },
                new() { Type = HopType.Switch }
            }
        };

        // Act & Assert
        path.HasWirelessSegment.Should().BeTrue();
    }

    #endregion

    #region HasWirelessConnection Tests

    [Fact]
    public void HasWirelessConnection_NoHops_ReturnsFalse()
    {
        // Arrange
        var path = new NetworkPath();

        // Act & Assert
        path.HasWirelessConnection.Should().BeFalse();
    }

    [Fact]
    public void HasWirelessConnection_SingleHop_ReturnsFalse()
    {
        // Arrange
        var path = new NetworkPath
        {
            Hops = new List<NetworkHop>
            {
                new() { Type = HopType.AccessPoint }
            }
        };

        // Act & Assert
        path.HasWirelessConnection.Should().BeFalse();
    }

    [Fact]
    public void HasWirelessConnection_ClientToAp_ReturnsTrue()
    {
        // Arrange - Wireless client connecting to AP
        var path = new NetworkPath
        {
            Hops = new List<NetworkHop>
            {
                new() { Type = HopType.Client },
                new() { Type = HopType.AccessPoint }
            }
        };

        // Act & Assert
        path.HasWirelessConnection.Should().BeTrue();
    }

    [Fact]
    public void HasWirelessConnection_ApToAp_ReturnsTrue()
    {
        // Arrange - Wireless mesh backhaul
        var path = new NetworkPath
        {
            Hops = new List<NetworkHop>
            {
                new() { Type = HopType.AccessPoint },
                new() { Type = HopType.AccessPoint }
            }
        };

        // Act & Assert
        path.HasWirelessConnection.Should().BeTrue();
    }

    [Fact]
    public void HasWirelessConnection_ApToSwitch_ReturnsFalse()
    {
        // Arrange - AP with wired uplink (NOT a wireless connection)
        var path = new NetworkPath
        {
            Hops = new List<NetworkHop>
            {
                new() { Type = HopType.AccessPoint },
                new() { Type = HopType.Switch }
            }
        };

        // Act & Assert
        path.HasWirelessConnection.Should().BeFalse();
    }

    [Fact]
    public void HasWirelessConnection_SwitchToAp_ReturnsFalse()
    {
        // Arrange - Not a wireless connection direction
        var path = new NetworkPath
        {
            Hops = new List<NetworkHop>
            {
                new() { Type = HopType.Switch },
                new() { Type = HopType.AccessPoint }
            }
        };

        // Act & Assert
        path.HasWirelessConnection.Should().BeFalse();
    }

    [Fact]
    public void HasWirelessConnection_ComplexPathWithWirelessClient_ReturnsTrue()
    {
        // Arrange - Complex path: Client -> AP -> Switch -> Gateway -> Server
        var path = new NetworkPath
        {
            Hops = new List<NetworkHop>
            {
                new() { Type = HopType.Client },
                new() { Type = HopType.AccessPoint },
                new() { Type = HopType.Switch },
                new() { Type = HopType.Gateway },
                new() { Type = HopType.Server }
            }
        };

        // Act & Assert
        path.HasWirelessConnection.Should().BeTrue();
    }

    [Fact]
    public void HasWirelessConnection_ComplexPathWithMesh_ReturnsTrue()
    {
        // Arrange - Path with mesh: AP -> AP -> Switch -> Gateway
        var path = new NetworkPath
        {
            Hops = new List<NetworkHop>
            {
                new() { Type = HopType.AccessPoint },
                new() { Type = HopType.AccessPoint },
                new() { Type = HopType.Switch },
                new() { Type = HopType.Gateway }
            }
        };

        // Act & Assert
        path.HasWirelessConnection.Should().BeTrue();
    }

    [Fact]
    public void HasWirelessConnection_WiredPathWithAp_ReturnsFalse()
    {
        // Arrange - AP in path but using wired connections
        var path = new NetworkPath
        {
            Hops = new List<NetworkHop>
            {
                new() { Type = HopType.Switch },
                new() { Type = HopType.AccessPoint },  // Testing AP via its wired port
                new() { Type = HopType.Server }
            }
        };

        // Act & Assert
        path.HasWirelessConnection.Should().BeFalse();
    }

    #endregion

    #region Property Setting Tests

    [Fact]
    public void NetworkPath_CanSetAllProperties()
    {
        // Arrange & Act
        var now = DateTime.UtcNow;
        var path = new NetworkPath
        {
            SourceHost = "192.0.2.1",
            SourceMac = "aa:bb:cc:dd:ee:ff",
            SourceVlanId = 1,
            SourceNetworkName = "Default",
            DestinationHost = "192.0.2.100",
            DestinationMac = "11:22:33:44:55:66",
            DestinationVlanId = 10,
            DestinationNetworkName = "IoT",
            RequiresRouting = true,
            GatewayDevice = "UDM-Pro",
            GatewayModel = "UDMPRO",
            TheoreticalMaxMbps = 1000,
            RealisticMaxMbps = 940,
            BottleneckDescription = "1G uplink on Switch A",
            CalculatedAt = now,
            IsValid = true,
            ErrorMessage = null,
            HasRealBottleneck = true,
            TargetIsGateway = false,
            TargetIsAccessPoint = false,
            TargetIsCellularModem = false
        };

        // Assert
        path.SourceHost.Should().Be("192.0.2.1");
        path.SourceMac.Should().Be("aa:bb:cc:dd:ee:ff");
        path.SourceVlanId.Should().Be(1);
        path.SourceNetworkName.Should().Be("Default");
        path.DestinationHost.Should().Be("192.0.2.100");
        path.DestinationMac.Should().Be("11:22:33:44:55:66");
        path.DestinationVlanId.Should().Be(10);
        path.DestinationNetworkName.Should().Be("IoT");
        path.RequiresRouting.Should().BeTrue();
        path.GatewayDevice.Should().Be("UDM-Pro");
        path.GatewayModel.Should().Be("UDMPRO");
        path.TheoreticalMaxMbps.Should().Be(1000);
        path.RealisticMaxMbps.Should().Be(940);
        path.BottleneckDescription.Should().Be("1G uplink on Switch A");
        path.CalculatedAt.Should().Be(now);
        path.IsValid.Should().BeTrue();
        path.HasRealBottleneck.Should().BeTrue();
    }

    [Fact]
    public void NetworkPath_InvalidPath_CanSetErrorMessage()
    {
        // Arrange
        var path = new NetworkPath
        {
            IsValid = false,
            ErrorMessage = "Could not trace path to destination"
        };

        // Assert
        path.IsValid.Should().BeFalse();
        path.ErrorMessage.Should().Be("Could not trace path to destination");
    }

    #endregion
}

public class NetworkHopTests
{
    #region Default Values Tests

    [Fact]
    public void NetworkHop_DefaultValues_AreCorrect()
    {
        // Act
        var hop = new NetworkHop();

        // Assert
        hop.Order.Should().Be(0);
        hop.Type.Should().Be(HopType.Client);  // Default enum value
        hop.DeviceMac.Should().BeEmpty();
        hop.DeviceName.Should().BeEmpty();
        hop.DeviceModel.Should().BeEmpty();
        hop.DeviceFirmware.Should().BeNull();
        hop.DeviceIp.Should().BeEmpty();
        hop.IngressPort.Should().BeNull();
        hop.IngressPortName.Should().BeNull();
        hop.IngressSpeedMbps.Should().Be(0);
        hop.EgressPort.Should().BeNull();
        hop.EgressPortName.Should().BeNull();
        hop.EgressSpeedMbps.Should().Be(0);
        hop.IsBottleneck.Should().BeFalse();
        hop.IsWirelessIngress.Should().BeFalse();
        hop.IsWirelessEgress.Should().BeFalse();
        hop.WirelessIngressBand.Should().BeNull();
        hop.WirelessEgressBand.Should().BeNull();
        hop.WirelessChannel.Should().BeNull();
        hop.WirelessSignalDbm.Should().BeNull();
        hop.WirelessNoiseDbm.Should().BeNull();
        hop.WirelessTxRateMbps.Should().BeNull();
        hop.WirelessRxRateMbps.Should().BeNull();
        hop.Notes.Should().BeNull();
    }

    #endregion

    #region HopType Enum Tests

    [Fact]
    public void HopType_AllValuesAreDefined()
    {
        // Assert
        var values = Enum.GetValues<HopType>();
        values.Should().Contain(HopType.Client);
        values.Should().Contain(HopType.Switch);
        values.Should().Contain(HopType.AccessPoint);
        values.Should().Contain(HopType.Gateway);
        values.Should().Contain(HopType.Server);
        values.Should().Contain(HopType.WirelessClient);
    }

    [Fact]
    public void HopType_DefaultValue_IsClient()
    {
        // Assert
        default(HopType).Should().Be(HopType.Client);
    }

    #endregion

    #region Property Setting Tests

    [Fact]
    public void NetworkHop_CanSetAllProperties()
    {
        // Act
        var hop = new NetworkHop
        {
            Order = 1,
            Type = HopType.Switch,
            DeviceMac = "aa:bb:cc:dd:ee:ff",
            DeviceName = "Core Switch",
            DeviceModel = "USW-Pro-48-PoE",
            DeviceFirmware = "6.0.0",
            DeviceIp = "192.0.2.10",
            IngressPort = 1,
            IngressPortName = "Port 1",
            IngressSpeedMbps = 1000,
            EgressPort = 48,
            EgressPortName = "SFP+ 1",
            EgressSpeedMbps = 10000,
            IsBottleneck = false,
            IsWirelessIngress = false,
            IsWirelessEgress = false,
            Notes = "Core switch"
        };

        // Assert
        hop.Order.Should().Be(1);
        hop.Type.Should().Be(HopType.Switch);
        hop.DeviceMac.Should().Be("aa:bb:cc:dd:ee:ff");
        hop.DeviceName.Should().Be("Core Switch");
        hop.DeviceModel.Should().Be("USW-Pro-48-PoE");
        hop.DeviceFirmware.Should().Be("6.0.0");
        hop.DeviceIp.Should().Be("192.0.2.10");
        hop.IngressPort.Should().Be(1);
        hop.IngressPortName.Should().Be("Port 1");
        hop.IngressSpeedMbps.Should().Be(1000);
        hop.EgressPort.Should().Be(48);
        hop.EgressPortName.Should().Be("SFP+ 1");
        hop.EgressSpeedMbps.Should().Be(10000);
        hop.IsBottleneck.Should().BeFalse();
        hop.Notes.Should().Be("Core switch");
    }

    [Fact]
    public void NetworkHop_WirelessHop_CanSetAllWirelessProperties()
    {
        // Act
        var hop = new NetworkHop
        {
            Order = 0,
            Type = HopType.AccessPoint,
            DeviceMac = "11:22:33:44:55:66",
            DeviceName = "Office AP",
            DeviceModel = "U6-Pro",
            DeviceIp = "192.0.2.20",
            IsWirelessIngress = true,
            WirelessIngressBand = "na",
            WirelessChannel = 36,
            WirelessSignalDbm = -65,
            WirelessNoiseDbm = -95,
            WirelessTxRateMbps = 1200,
            WirelessRxRateMbps = 1000,
            EgressPort = 1,
            EgressPortName = "LAN",
            EgressSpeedMbps = 2500,
            Notes = "5GHz client connection"
        };

        // Assert
        hop.IsWirelessIngress.Should().BeTrue();
        hop.WirelessIngressBand.Should().Be("na");
        hop.WirelessChannel.Should().Be(36);
        hop.WirelessSignalDbm.Should().Be(-65);
        hop.WirelessNoiseDbm.Should().Be(-95);
        hop.WirelessTxRateMbps.Should().Be(1200);
        hop.WirelessRxRateMbps.Should().Be(1000);
    }

    [Fact]
    public void NetworkHop_BottleneckHop_CanBeMarked()
    {
        // Act
        var hop = new NetworkHop
        {
            Type = HopType.Switch,
            DeviceName = "Old Switch",
            IngressSpeedMbps = 100,
            EgressSpeedMbps = 100,
            IsBottleneck = true,
            Notes = "100M bottleneck"
        };

        // Assert
        hop.IsBottleneck.Should().BeTrue();
    }

    #endregion
}
