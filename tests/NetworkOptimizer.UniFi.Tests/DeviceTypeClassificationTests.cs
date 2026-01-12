using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
    // Shared test fixtures
    private static readonly ILogger NullLogger = new NullLoggerFactory().CreateLogger("Test");
    private static readonly HashSet<string> EmptyDeviceMacs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Creates a device MAC set containing the specified MACs (for simulating network with multiple devices)
    /// </summary>
    private static HashSet<string> CreateDeviceMacSet(params string[] macs) =>
        new(macs.Select(m => m.ToLowerInvariant()), StringComparer.OrdinalIgnoreCase);

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

    #region DetermineDeviceType - Gateway Detection (No Uplink to UniFi Device)

    [Fact]
    public void DetermineDeviceType_UdmWithNoUplink_ReturnsGateway()
    {
        // Arrange - UDM Pro with no uplink (it's the gateway)
        var device = new UniFiDeviceResponse
        {
            Mac = "aa:bb:cc:dd:ee:01",
            Type = "udm",
            Model = "UDMPRO",
            Name = "Main Gateway",
            Uplink = null
        };
        var allMacs = CreateDeviceMacSet("aa:bb:cc:dd:ee:01", "aa:bb:cc:dd:ee:02");

        // Act
        var result = UniFiDiscovery.DetermineDeviceType(device, allMacs, NullLogger);

        // Assert
        result.Should().Be(DeviceType.Gateway);
    }

    [Fact]
    public void DetermineDeviceType_UdmWithUplinkToNonUniFiDevice_ReturnsGateway()
    {
        // Arrange - UDM Pro uplinked to ISP modem (not a UniFi device)
        var device = new UniFiDeviceResponse
        {
            Mac = "aa:bb:cc:dd:ee:01",
            Type = "udm",
            Model = "UDMPRO",
            Name = "Main Gateway",
            Uplink = new UplinkInfo { UplinkMac = "11:22:33:44:55:66" } // ISP modem MAC
        };
        // ISP modem not in our device list
        var allMacs = CreateDeviceMacSet("aa:bb:cc:dd:ee:01", "aa:bb:cc:dd:ee:02");

        // Act
        var result = UniFiDiscovery.DetermineDeviceType(device, allMacs, NullLogger);

        // Assert
        result.Should().Be(DeviceType.Gateway);
    }

    [Fact]
    public void DetermineDeviceType_UcgWithNoUplink_ReturnsGateway()
    {
        // Arrange - Cloud Gateway as the main gateway
        var device = new UniFiDeviceResponse
        {
            Mac = "aa:bb:cc:dd:ee:01",
            Type = "ucg",
            Model = "UCG",
            Name = "Cloud Gateway",
            Uplink = null
        };

        // Act
        var result = UniFiDiscovery.DetermineDeviceType(device, EmptyDeviceMacs, NullLogger);

        // Assert
        result.Should().Be(DeviceType.Gateway);
    }

    [Fact]
    public void DetermineDeviceType_UxgWithNoUplink_ReturnsGateway()
    {
        // Arrange - UXG Pro as gateway
        var device = new UniFiDeviceResponse
        {
            Mac = "aa:bb:cc:dd:ee:01",
            Type = "uxg",
            Model = "UXGPRO",
            Uplink = null
        };

        // Act
        var result = UniFiDiscovery.DetermineDeviceType(device, EmptyDeviceMacs, NullLogger);

        // Assert
        result.Should().Be(DeviceType.Gateway);
    }

    #endregion

    #region DetermineDeviceType - UX Express as Access Point (Uplinks to UniFi Device)

    [Fact]
    public void DetermineDeviceType_UxExpressUplinkToGateway_ReturnsAccessPoint()
    {
        // Arrange - UX Express uplinked to a UDM Pro (mesh AP mode)
        var gatewayMac = "aa:bb:cc:dd:ee:01";
        var device = new UniFiDeviceResponse
        {
            Mac = "aa:bb:cc:dd:ee:02",
            Type = "udm",
            Model = "UX",
            Shortname = "UX",
            Name = "Living Room Express",
            Ip = "192.168.1.50",
            Uplink = new UplinkInfo { UplinkMac = gatewayMac }
        };
        var allMacs = CreateDeviceMacSet(gatewayMac, "aa:bb:cc:dd:ee:02");

        // Act
        var result = UniFiDiscovery.DetermineDeviceType(device, allMacs, NullLogger);

        // Assert
        result.Should().Be(DeviceType.AccessPoint);
    }

    [Fact]
    public void DetermineDeviceType_Ux7UplinkToSwitch_ReturnsAccessPoint()
    {
        // Arrange - UX7 (Express 7) uplinked to a UniFi switch
        var switchMac = "aa:bb:cc:dd:ee:03";
        var device = new UniFiDeviceResponse
        {
            Mac = "aa:bb:cc:dd:ee:04",
            Type = "udm",
            Model = "UX7",
            Shortname = "UX7",
            Name = "Bedroom Express",
            Uplink = new UplinkInfo { UplinkMac = switchMac }
        };
        var allMacs = CreateDeviceMacSet("aa:bb:cc:dd:ee:01", switchMac, "aa:bb:cc:dd:ee:04");

        // Act
        var result = UniFiDiscovery.DetermineDeviceType(device, allMacs, NullLogger);

        // Assert
        result.Should().Be(DeviceType.AccessPoint);
    }

    [Fact]
    public void DetermineDeviceType_UxExpressAsStandaloneGateway_ReturnsGateway()
    {
        // Arrange - UX Express configured as the main gateway (no uplink to UniFi device)
        var device = new UniFiDeviceResponse
        {
            Mac = "aa:bb:cc:dd:ee:01",
            Type = "udm",
            Model = "UX",
            Shortname = "UX",
            Name = "Office Gateway",
            Uplink = null // No uplink - it's the gateway
        };
        var allMacs = CreateDeviceMacSet("aa:bb:cc:dd:ee:01");

        // Act
        var result = UniFiDiscovery.DetermineDeviceType(device, allMacs, NullLogger);

        // Assert
        result.Should().Be(DeviceType.Gateway);
    }

    [Fact]
    public void DetermineDeviceType_DreamRouterUplinkToGateway_ReturnsAccessPoint()
    {
        // Arrange - Dream Router (UDR) being used as mesh AP
        var gatewayMac = "aa:bb:cc:dd:ee:01";
        var device = new UniFiDeviceResponse
        {
            Mac = "aa:bb:cc:dd:ee:02",
            Type = "udm",
            Model = "UDR",
            Shortname = "UDR",
            Name = "Guest House Router",
            Uplink = new UplinkInfo { UplinkMac = gatewayMac }
        };
        var allMacs = CreateDeviceMacSet(gatewayMac, "aa:bb:cc:dd:ee:02");

        // Act
        var result = UniFiDiscovery.DetermineDeviceType(device, allMacs, NullLogger);

        // Assert
        result.Should().Be(DeviceType.AccessPoint);
    }

    [Fact]
    public void DetermineDeviceType_UxExpressWirelessUplink_ReturnsAccessPoint()
    {
        // Arrange - UX Express with wireless mesh uplink to another AP
        var parentApMac = "aa:bb:cc:dd:ee:05";
        var device = new UniFiDeviceResponse
        {
            Mac = "aa:bb:cc:dd:ee:06",
            Type = "udm",
            Model = "UX",
            Name = "Garage Express",
            Uplink = new UplinkInfo
            {
                UplinkMac = parentApMac,
                Type = "wireless"
            }
        };
        var allMacs = CreateDeviceMacSet("aa:bb:cc:dd:ee:01", parentApMac, "aa:bb:cc:dd:ee:06");

        // Act
        var result = UniFiDiscovery.DetermineDeviceType(device, allMacs, NullLogger);

        // Assert
        result.Should().Be(DeviceType.AccessPoint);
    }

    #endregion

    #region DetermineDeviceType - Non-Gateway Types Unchanged

    [Fact]
    public void DetermineDeviceType_Switch_ReturnsSwitch()
    {
        // Arrange - Switch should always be classified as switch regardless of uplink
        var device = new UniFiDeviceResponse
        {
            Mac = "aa:bb:cc:dd:ee:10",
            Type = "usw",
            Model = "USW-Pro-24-POE",
            Uplink = new UplinkInfo { UplinkMac = "aa:bb:cc:dd:ee:01" }
        };
        var allMacs = CreateDeviceMacSet("aa:bb:cc:dd:ee:01", "aa:bb:cc:dd:ee:10");

        // Act
        var result = UniFiDiscovery.DetermineDeviceType(device, allMacs, NullLogger);

        // Assert
        result.Should().Be(DeviceType.Switch);
    }

    [Fact]
    public void DetermineDeviceType_AccessPoint_ReturnsAccessPoint()
    {
        // Arrange - Regular AP should always be classified as AP
        var device = new UniFiDeviceResponse
        {
            Mac = "aa:bb:cc:dd:ee:20",
            Type = "uap",
            Model = "U6-Pro",
            Uplink = new UplinkInfo { UplinkMac = "aa:bb:cc:dd:ee:01" }
        };

        // Act
        var result = UniFiDiscovery.DetermineDeviceType(device, EmptyDeviceMacs, NullLogger);

        // Assert
        result.Should().Be(DeviceType.AccessPoint);
    }

    [Fact]
    public void DetermineDeviceType_CellularModem_ReturnsCellularModem()
    {
        // Arrange
        var device = new UniFiDeviceResponse
        {
            Mac = "aa:bb:cc:dd:ee:30",
            Type = "umbb",
            Model = "U-LTE-Pro"
        };

        // Act
        var result = UniFiDiscovery.DetermineDeviceType(device, EmptyDeviceMacs, NullLogger);

        // Assert
        result.Should().Be(DeviceType.CellularModem);
    }

    [Fact]
    public void DetermineDeviceType_CloudKey_ReturnsCloudKey()
    {
        // Arrange
        var device = new UniFiDeviceResponse
        {
            Mac = "aa:bb:cc:dd:ee:40",
            Type = "uck",
            Model = "UCK-G2-Plus"
        };

        // Act
        var result = UniFiDiscovery.DetermineDeviceType(device, EmptyDeviceMacs, NullLogger);

        // Assert
        result.Should().Be(DeviceType.CloudKey);
    }

    [Fact]
    public void DetermineDeviceType_BuildingBridge_ReturnsBuildingBridge()
    {
        // Arrange
        var device = new UniFiDeviceResponse
        {
            Mac = "aa:bb:cc:dd:ee:50",
            Type = "ubb",
            Model = "UBB"
        };

        // Act
        var result = UniFiDiscovery.DetermineDeviceType(device, EmptyDeviceMacs, NullLogger);

        // Assert
        result.Should().Be(DeviceType.BuildingBridge);
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void DetermineDeviceType_UdmWithEmptyUplinkMac_ReturnsGateway()
    {
        // Arrange - Uplink object exists but MAC is empty
        var device = new UniFiDeviceResponse
        {
            Mac = "aa:bb:cc:dd:ee:01",
            Type = "udm",
            Model = "UDMPRO",
            Uplink = new UplinkInfo { UplinkMac = "" }
        };

        // Act
        var result = UniFiDiscovery.DetermineDeviceType(device, EmptyDeviceMacs, NullLogger);

        // Assert
        result.Should().Be(DeviceType.Gateway);
    }

    [Fact]
    public void DetermineDeviceType_UdmWithNullUplinkMac_ReturnsGateway()
    {
        // Arrange - Uplink object exists but MAC is null
        var device = new UniFiDeviceResponse
        {
            Mac = "aa:bb:cc:dd:ee:01",
            Type = "udm",
            Model = "UDMPRO",
            Uplink = new UplinkInfo { UplinkMac = null! }
        };

        // Act
        var result = UniFiDiscovery.DetermineDeviceType(device, EmptyDeviceMacs, NullLogger);

        // Assert
        result.Should().Be(DeviceType.Gateway);
    }

    [Fact]
    public void DetermineDeviceType_CaseInsensitiveUplinkMacMatching()
    {
        // Arrange - Uplink MAC in different case than device list
        var device = new UniFiDeviceResponse
        {
            Mac = "aa:bb:cc:dd:ee:02",
            Type = "udm",
            Model = "UX",
            Uplink = new UplinkInfo { UplinkMac = "AA:BB:CC:DD:EE:01" } // Uppercase
        };
        var allMacs = CreateDeviceMacSet("aa:bb:cc:dd:ee:01", "aa:bb:cc:dd:ee:02"); // Lowercase

        // Act
        var result = UniFiDiscovery.DetermineDeviceType(device, allMacs, NullLogger);

        // Assert - Should still detect as AP due to case-insensitive matching
        result.Should().Be(DeviceType.AccessPoint);
    }

    [Fact]
    public void DetermineDeviceType_UnknownType_ReturnsUnknown()
    {
        // Arrange
        var device = new UniFiDeviceResponse
        {
            Mac = "aa:bb:cc:dd:ee:99",
            Type = "xyz",
            Model = "Unknown-Model"
        };

        // Act
        var result = UniFiDiscovery.DetermineDeviceType(device, EmptyDeviceMacs, NullLogger);

        // Assert
        result.Should().Be(DeviceType.Unknown);
    }

    [Fact]
    public void DetermineDeviceType_NullType_ReturnsUnknown()
    {
        // Arrange
        var device = new UniFiDeviceResponse
        {
            Mac = "aa:bb:cc:dd:ee:99",
            Type = null!,
            Model = "Unknown-Model"
        };

        // Act
        var result = UniFiDiscovery.DetermineDeviceType(device, EmptyDeviceMacs, NullLogger);

        // Assert
        result.Should().Be(DeviceType.Unknown);
    }

    #endregion

    #region Real-World Scenario Tests

    [Fact]
    public void DetermineDeviceType_TypicalHomeNetwork_GatewayAndMeshAp()
    {
        // Arrange - Typical setup: UDM Pro as gateway + UX Express as mesh AP
        var gatewayMac = "aa:bb:cc:dd:ee:01";
        var meshApMac = "aa:bb:cc:dd:ee:02";
        var allMacs = CreateDeviceMacSet(gatewayMac, meshApMac);

        var gateway = new UniFiDeviceResponse
        {
            Mac = gatewayMac,
            Type = "udm",
            Model = "UDMPRO",
            Name = "Main Gateway",
            Ip = "192.168.1.1",
            Uplink = null // Gateway has no uplink to UniFi device
        };

        var meshAp = new UniFiDeviceResponse
        {
            Mac = meshApMac,
            Type = "udm",
            Model = "UX",
            Name = "Living Room Express",
            Ip = "192.168.1.50",
            Uplink = new UplinkInfo { UplinkMac = gatewayMac } // Uplinks to gateway
        };

        // Act
        var gatewayType = UniFiDiscovery.DetermineDeviceType(gateway, allMacs, NullLogger);
        var meshApType = UniFiDiscovery.DetermineDeviceType(meshAp, allMacs, NullLogger);

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
            Mac = "aa:bb:cc:dd:ee:01",
            Type = "udm",
            Model = "UX",
            Name = "Office Gateway",
            Uplink = null // No uplink - it's the only device/gateway
        };
        var allMacs = CreateDeviceMacSet("aa:bb:cc:dd:ee:01");

        // Act
        var result = UniFiDiscovery.DetermineDeviceType(device, allMacs, NullLogger);

        // Assert
        result.Should().Be(DeviceType.Gateway);
    }

    [Fact]
    public void DetermineDeviceType_EnterpriseNetwork_MultipleDeviceTypes()
    {
        // Arrange - Enterprise setup with various device types
        var gatewayMac = "aa:bb:cc:dd:ee:01";
        var switchMac = "aa:bb:cc:dd:ee:02";
        var apMac = "aa:bb:cc:dd:ee:03";
        var ux7Mac = "aa:bb:cc:dd:ee:04";

        var allMacs = CreateDeviceMacSet(gatewayMac, switchMac, apMac, ux7Mac);

        var devices = new[]
        {
            new UniFiDeviceResponse
            {
                Mac = gatewayMac,
                Type = "ucg",
                Model = "UCG-Fiber",
                Uplink = null // Gateway
            },
            new UniFiDeviceResponse
            {
                Mac = switchMac,
                Type = "usw",
                Model = "USW-Enterprise-48-PoE",
                Uplink = new UplinkInfo { UplinkMac = gatewayMac }
            },
            new UniFiDeviceResponse
            {
                Mac = apMac,
                Type = "uap",
                Model = "U7-Pro",
                Uplink = new UplinkInfo { UplinkMac = switchMac }
            },
            new UniFiDeviceResponse
            {
                Mac = ux7Mac,
                Type = "udm",
                Model = "UX7",
                Uplink = new UplinkInfo { UplinkMac = switchMac } // Mesh AP via switch
            }
        };

        // Act
        var results = devices.Select(d => UniFiDiscovery.DetermineDeviceType(d, allMacs, NullLogger)).ToList();

        // Assert
        results[0].Should().Be(DeviceType.Gateway);     // UCG-Fiber
        results[1].Should().Be(DeviceType.Switch);      // Switch
        results[2].Should().Be(DeviceType.AccessPoint); // U7-Pro AP
        results[3].Should().Be(DeviceType.AccessPoint); // UX7 as mesh AP
    }

    [Fact]
    public void DetermineDeviceType_ChainedMeshNetwork()
    {
        // Arrange - Gateway -> UX Express -> Another UX Express (chained mesh)
        var gatewayMac = "aa:bb:cc:dd:ee:01";
        var meshAp1Mac = "aa:bb:cc:dd:ee:02";
        var meshAp2Mac = "aa:bb:cc:dd:ee:03";

        var allMacs = CreateDeviceMacSet(gatewayMac, meshAp1Mac, meshAp2Mac);

        var gateway = new UniFiDeviceResponse
        {
            Mac = gatewayMac,
            Type = "udm",
            Model = "UDMPRO",
            Uplink = null
        };

        var meshAp1 = new UniFiDeviceResponse
        {
            Mac = meshAp1Mac,
            Type = "udm",
            Model = "UX",
            Name = "First Hop",
            Uplink = new UplinkInfo { UplinkMac = gatewayMac }
        };

        var meshAp2 = new UniFiDeviceResponse
        {
            Mac = meshAp2Mac,
            Type = "udm",
            Model = "UX",
            Name = "Second Hop",
            Uplink = new UplinkInfo { UplinkMac = meshAp1Mac } // Chains through first mesh AP
        };

        // Act
        var gatewayType = UniFiDiscovery.DetermineDeviceType(gateway, allMacs, NullLogger);
        var meshAp1Type = UniFiDiscovery.DetermineDeviceType(meshAp1, allMacs, NullLogger);
        var meshAp2Type = UniFiDiscovery.DetermineDeviceType(meshAp2, allMacs, NullLogger);

        // Assert
        gatewayType.Should().Be(DeviceType.Gateway);
        meshAp1Type.Should().Be(DeviceType.AccessPoint);
        meshAp2Type.Should().Be(DeviceType.AccessPoint);
    }

    #endregion
}
