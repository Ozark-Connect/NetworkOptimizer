using FluentAssertions;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.UniFi.Models;
using Xunit;

namespace NetworkOptimizer.UniFi.Tests;

/// <summary>
/// Tests for device type classification logic, including the special handling
/// for UDM-family devices that may operate as access points.
/// </summary>
public class DeviceTypeClassificationTests
{
    #region FromUniFiApiType Base Classification Tests

    [Theory]
    [InlineData("ugw", DeviceType.Gateway)]
    [InlineData("usg", DeviceType.Gateway)]
    [InlineData("udm", DeviceType.Gateway)]
    [InlineData("uxg", DeviceType.Gateway)]
    [InlineData("ucg", DeviceType.Gateway)]
    [InlineData("UDM", DeviceType.Gateway)] // Case insensitive
    [InlineData("Udm", DeviceType.Gateway)]
    public void FromUniFiApiType_GatewayTypes_ReturnsGateway(string apiType, DeviceType expected)
    {
        // Act
        var result = DeviceTypeExtensions.FromUniFiApiType(apiType);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("usw", DeviceType.Switch)]
    [InlineData("USW", DeviceType.Switch)]
    public void FromUniFiApiType_SwitchTypes_ReturnsSwitch(string apiType, DeviceType expected)
    {
        // Act
        var result = DeviceTypeExtensions.FromUniFiApiType(apiType);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("uap", DeviceType.AccessPoint)]
    [InlineData("UAP", DeviceType.AccessPoint)]
    public void FromUniFiApiType_AccessPointTypes_ReturnsAccessPoint(string apiType, DeviceType expected)
    {
        // Act
        var result = DeviceTypeExtensions.FromUniFiApiType(apiType);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("umbb", DeviceType.CellularModem)]
    [InlineData("UMBB", DeviceType.CellularModem)]
    public void FromUniFiApiType_CellularModemTypes_ReturnsCellularModem(string apiType, DeviceType expected)
    {
        // Act
        var result = DeviceTypeExtensions.FromUniFiApiType(apiType);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("ubb", DeviceType.BuildingBridge)]
    [InlineData("UBB", DeviceType.BuildingBridge)]
    public void FromUniFiApiType_BuildingBridgeTypes_ReturnsBuildingBridge(string apiType, DeviceType expected)
    {
        // Act
        var result = DeviceTypeExtensions.FromUniFiApiType(apiType);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("uck", DeviceType.CloudKey)]
    [InlineData("UCK", DeviceType.CloudKey)]
    public void FromUniFiApiType_CloudKeyTypes_ReturnsCloudKey(string apiType, DeviceType expected)
    {
        // Act
        var result = DeviceTypeExtensions.FromUniFiApiType(apiType);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown")]
    [InlineData("xyz")]
    public void FromUniFiApiType_UnknownOrEmptyTypes_ReturnsUnknown(string? apiType)
    {
        // Act
        var result = DeviceTypeExtensions.FromUniFiApiType(apiType);

        // Assert
        result.Should().Be(DeviceType.Unknown);
    }

    #endregion

    #region DetermineDeviceType - Gateway with LAN Config Tests

    [Fact]
    public void DetermineDeviceType_UdmWithLanConfig_ReturnsGateway()
    {
        // Arrange - UDM Pro acting as gateway (has LAN config)
        var device = new UniFiDeviceResponse
        {
            Type = "udm",
            Model = "UDMPRO",
            ConfigNetworkLan = new ConfigNetworkLan
            {
                DhcpEnabled = true,
                Cidr = "192.168.1.1/24"
            }
        };

        // Act
        var result = UniFiDiscovery.DetermineDeviceType(device);

        // Assert
        result.Should().Be(DeviceType.Gateway);
    }

    [Fact]
    public void DetermineDeviceType_UxgWithLanConfig_ReturnsGateway()
    {
        // Arrange - UXG Pro acting as gateway
        var device = new UniFiDeviceResponse
        {
            Type = "uxg",
            Model = "UXGPRO",
            ConfigNetworkLan = new ConfigNetworkLan
            {
                DhcpEnabled = true,
                Cidr = "192.168.1.1/24"
            }
        };

        // Act
        var result = UniFiDiscovery.DetermineDeviceType(device);

        // Assert
        result.Should().Be(DeviceType.Gateway);
    }

    [Fact]
    public void DetermineDeviceType_UcgWithLanConfig_ReturnsGateway()
    {
        // Arrange - Cloud Gateway acting as gateway
        var device = new UniFiDeviceResponse
        {
            Type = "ucg",
            Model = "UCG",
            ConfigNetworkLan = new ConfigNetworkLan
            {
                DhcpEnabled = true,
                Cidr = "192.168.1.1/24"
            }
        };

        // Act
        var result = UniFiDiscovery.DetermineDeviceType(device);

        // Assert
        result.Should().Be(DeviceType.Gateway);
    }

    [Fact]
    public void DetermineDeviceType_GatewayWithStaticWanAndLanConfig_ReturnsGateway()
    {
        // Arrange - Gateway with static public IP (config_network.type = "static")
        // but still has LAN config because it's managing the network
        var device = new UniFiDeviceResponse
        {
            Type = "udm",
            Model = "UDMPRO",
            ConfigNetwork = new ConfigNetwork
            {
                Type = "static",
                Ip = "203.0.113.50"
            },
            ConfigNetworkLan = new ConfigNetworkLan
            {
                DhcpEnabled = true,
                Cidr = "192.168.1.1/24"
            }
        };

        // Act
        var result = UniFiDiscovery.DetermineDeviceType(device);

        // Assert
        result.Should().Be(DeviceType.Gateway);
    }

    #endregion

    #region DetermineDeviceType - UX Express as Access Point Tests

    [Fact]
    public void DetermineDeviceType_UxExpressWithoutLanConfig_ReturnsAccessPoint()
    {
        // Arrange - UX Express operating as mesh AP (no LAN config)
        var device = new UniFiDeviceResponse
        {
            Type = "udm",
            Model = "UX",
            Shortname = "UX",
            ConfigNetwork = new ConfigNetwork
            {
                Type = "static",
                Ip = "192.168.1.50"
            },
            ConfigNetworkLan = null
        };

        // Act
        var result = UniFiDiscovery.DetermineDeviceType(device);

        // Assert
        result.Should().Be(DeviceType.AccessPoint);
    }

    [Fact]
    public void DetermineDeviceType_Ux7WithoutLanConfig_ReturnsAccessPoint()
    {
        // Arrange - UX7 (Express 7) operating as mesh AP
        var device = new UniFiDeviceResponse
        {
            Type = "udm",
            Model = "UX7",
            Shortname = "UX7",
            ConfigNetwork = new ConfigNetwork
            {
                Type = "static",
                Ip = "192.168.1.51"
            },
            ConfigNetworkLan = null
        };

        // Act
        var result = UniFiDiscovery.DetermineDeviceType(device);

        // Assert
        result.Should().Be(DeviceType.AccessPoint);
    }

    [Fact]
    public void DetermineDeviceType_UxExpressAsGateway_ReturnsGateway()
    {
        // Arrange - UX Express configured as the network gateway
        var device = new UniFiDeviceResponse
        {
            Type = "udm",
            Model = "UX",
            Shortname = "UX",
            ConfigNetwork = new ConfigNetwork
            {
                Type = "dhcp"
            },
            ConfigNetworkLan = new ConfigNetworkLan
            {
                DhcpEnabled = true,
                Cidr = "192.168.1.1/24"
            }
        };

        // Act
        var result = UniFiDiscovery.DetermineDeviceType(device);

        // Assert
        result.Should().Be(DeviceType.Gateway);
    }

    [Fact]
    public void DetermineDeviceType_DreamRouterWithoutLanConfig_ReturnsAccessPoint()
    {
        // Arrange - Dream Router (UDR) being used as mesh AP
        var device = new UniFiDeviceResponse
        {
            Type = "udm",
            Model = "UDR",
            Shortname = "UDR",
            ConfigNetworkLan = null
        };

        // Act
        var result = UniFiDiscovery.DetermineDeviceType(device);

        // Assert
        result.Should().Be(DeviceType.AccessPoint);
    }

    #endregion

    #region DetermineDeviceType - Non-Gateway Types Unchanged

    [Fact]
    public void DetermineDeviceType_Switch_ReturnsSwitch()
    {
        // Arrange - Switch should always be classified as switch
        var device = new UniFiDeviceResponse
        {
            Type = "usw",
            Model = "USW-Pro-24-POE",
            ConfigNetworkLan = null
        };

        // Act
        var result = UniFiDiscovery.DetermineDeviceType(device);

        // Assert
        result.Should().Be(DeviceType.Switch);
    }

    [Fact]
    public void DetermineDeviceType_AccessPoint_ReturnsAccessPoint()
    {
        // Arrange - Regular AP should always be classified as AP
        var device = new UniFiDeviceResponse
        {
            Type = "uap",
            Model = "U6-Pro",
            ConfigNetworkLan = null
        };

        // Act
        var result = UniFiDiscovery.DetermineDeviceType(device);

        // Assert
        result.Should().Be(DeviceType.AccessPoint);
    }

    [Fact]
    public void DetermineDeviceType_CellularModem_ReturnsCellularModem()
    {
        // Arrange
        var device = new UniFiDeviceResponse
        {
            Type = "umbb",
            Model = "U-LTE-Pro",
            ConfigNetworkLan = null
        };

        // Act
        var result = UniFiDiscovery.DetermineDeviceType(device);

        // Assert
        result.Should().Be(DeviceType.CellularModem);
    }

    [Fact]
    public void DetermineDeviceType_CloudKey_ReturnsCloudKey()
    {
        // Arrange
        var device = new UniFiDeviceResponse
        {
            Type = "uck",
            Model = "UCK-G2-Plus",
            ConfigNetworkLan = null
        };

        // Act
        var result = UniFiDiscovery.DetermineDeviceType(device);

        // Assert
        result.Should().Be(DeviceType.CloudKey);
    }

    [Fact]
    public void DetermineDeviceType_BuildingBridge_ReturnsBuildingBridge()
    {
        // Arrange
        var device = new UniFiDeviceResponse
        {
            Type = "ubb",
            Model = "UBB",
            ConfigNetworkLan = null
        };

        // Act
        var result = UniFiDiscovery.DetermineDeviceType(device);

        // Assert
        result.Should().Be(DeviceType.BuildingBridge);
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void DetermineDeviceType_UdmWithEmptyLanConfig_ReturnsGateway()
    {
        // Arrange - LAN config object exists but with default values
        var device = new UniFiDeviceResponse
        {
            Type = "udm",
            Model = "UDMPRO",
            ConfigNetworkLan = new ConfigNetworkLan()
        };

        // Act
        var result = UniFiDiscovery.DetermineDeviceType(device);

        // Assert - presence of config_network_lan indicates gateway role
        result.Should().Be(DeviceType.Gateway);
    }

    [Fact]
    public void DetermineDeviceType_UnknownType_ReturnsUnknown()
    {
        // Arrange
        var device = new UniFiDeviceResponse
        {
            Type = "xyz",
            Model = "Unknown-Model",
            ConfigNetworkLan = null
        };

        // Act
        var result = UniFiDiscovery.DetermineDeviceType(device);

        // Assert
        result.Should().Be(DeviceType.Unknown);
    }

    [Fact]
    public void DetermineDeviceType_NullType_ReturnsUnknown()
    {
        // Arrange
        var device = new UniFiDeviceResponse
        {
            Type = null!,
            Model = "Unknown-Model"
        };

        // Act
        var result = UniFiDiscovery.DetermineDeviceType(device);

        // Assert
        result.Should().Be(DeviceType.Unknown);
    }

    [Fact]
    public void DetermineDeviceType_EmptyType_ReturnsUnknown()
    {
        // Arrange
        var device = new UniFiDeviceResponse
        {
            Type = "",
            Model = "Unknown-Model"
        };

        // Act
        var result = UniFiDiscovery.DetermineDeviceType(device);

        // Assert
        result.Should().Be(DeviceType.Unknown);
    }

    #endregion

    #region Real-World Scenario Tests

    [Fact]
    public void DetermineDeviceType_TypicalHomeNetwork_GatewayAndMeshAp()
    {
        // Arrange - Typical setup: UDM Pro as gateway + UX Express as mesh AP
        var gateway = new UniFiDeviceResponse
        {
            Type = "udm",
            Model = "UDMPRO",
            Name = "Main Gateway",
            Ip = "192.168.1.1",
            ConfigNetwork = new ConfigNetwork { Type = "dhcp" },
            ConfigNetworkLan = new ConfigNetworkLan
            {
                DhcpEnabled = true,
                Cidr = "192.168.1.1/24"
            }
        };

        var meshAp = new UniFiDeviceResponse
        {
            Type = "udm",
            Model = "UX",
            Name = "Living Room Express",
            Ip = "192.168.1.50",
            ConfigNetwork = new ConfigNetwork { Type = "static", Ip = "192.168.1.50" },
            ConfigNetworkLan = null
        };

        // Act
        var gatewayType = UniFiDiscovery.DetermineDeviceType(gateway);
        var meshApType = UniFiDiscovery.DetermineDeviceType(meshAp);

        // Assert
        gatewayType.Should().Be(DeviceType.Gateway);
        meshApType.Should().Be(DeviceType.AccessPoint);
    }

    [Fact]
    public void DetermineDeviceType_SmallOffice_UxExpressAsOnlyGateway()
    {
        // Arrange - Small office using just a UX Express as the gateway
        var device = new UniFiDeviceResponse
        {
            Type = "udm",
            Model = "UX",
            Name = "Office Gateway",
            ConfigNetwork = new ConfigNetwork { Type = "dhcp" },
            ConfigNetworkLan = new ConfigNetworkLan
            {
                DhcpEnabled = true,
                Cidr = "192.168.1.1/24"
            }
        };

        // Act
        var result = UniFiDiscovery.DetermineDeviceType(device);

        // Assert
        result.Should().Be(DeviceType.Gateway);
    }

    [Fact]
    public void DetermineDeviceType_EnterpriseNetwork_MultipleDeviceTypes()
    {
        // Arrange - Enterprise setup with various device types
        var devices = new[]
        {
            new UniFiDeviceResponse
            {
                Type = "ucg",
                Model = "UCG-Fiber",
                ConfigNetworkLan = new ConfigNetworkLan { DhcpEnabled = true }
            },
            new UniFiDeviceResponse
            {
                Type = "usw",
                Model = "USW-Enterprise-48-PoE"
            },
            new UniFiDeviceResponse
            {
                Type = "uap",
                Model = "U7-Pro"
            },
            new UniFiDeviceResponse
            {
                Type = "udm",
                Model = "UX7",
                ConfigNetworkLan = null // Mesh AP
            }
        };

        // Act
        var results = devices.Select(d => UniFiDiscovery.DetermineDeviceType(d)).ToList();

        // Assert
        results[0].Should().Be(DeviceType.Gateway);     // UCG-Fiber
        results[1].Should().Be(DeviceType.Switch);      // Switch
        results[2].Should().Be(DeviceType.AccessPoint); // U7-Pro AP
        results[3].Should().Be(DeviceType.AccessPoint); // UX7 as mesh AP
    }

    #endregion
}
