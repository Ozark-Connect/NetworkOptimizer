using FluentAssertions;
using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.Audit.Services;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.UniFi.Models;
using Xunit;

namespace NetworkOptimizer.Audit.Tests.Services;

/// <summary>
/// Tests for DeviceTypeDetectionService, including name-based overrides
/// </summary>
public class DeviceTypeDetectionServiceTests
{
    private readonly DeviceTypeDetectionService _service;

    public DeviceTypeDetectionServiceTests()
    {
        _service = new DeviceTypeDetectionService();
    }

    #region Name Override Tests - Plugs and WYZE

    [Theory]
    [InlineData("Living Room Plug")]
    [InlineData("Kitchen Outlet")]
    [InlineData("Power Strip Controller")]
    [InlineData("Cync Plug")]
    [InlineData("Wyze Plug")]
    public void DetectDeviceType_NameContainsPlug_ReturnsSmartPlug(string deviceName)
    {
        // Arrange
        var client = new UniFiClientResponse
        {
            Mac = "aa:bb:cc:dd:ee:ff",
            Name = deviceName,
            DevCat = 4 // Camera fingerprint (should be overridden)
        };

        // Act
        var result = _service.DetectDeviceType(client);

        // Assert
        result.Category.Should().Be(ClientDeviceCategory.SmartPlug);
        result.ConfidenceScore.Should().BeGreaterThanOrEqualTo(95);
    }

    [Theory]
    [InlineData("Smart Bulb")]
    [InlineData("Desk Lamp")]
    [InlineData("LED Light Strip")]
    public void DetectDeviceType_NameContainsLightingKeyword_ReturnsSmartLighting(string deviceName)
    {
        // Arrange
        var client = new UniFiClientResponse
        {
            Mac = "aa:bb:cc:dd:ee:ff",
            Name = deviceName,
            DevCat = 4 // Camera fingerprint (should be overridden)
        };

        // Act
        var result = _service.DetectDeviceType(client);

        // Assert
        result.Category.Should().Be(ClientDeviceCategory.SmartLighting);
        result.ConfidenceScore.Should().BeGreaterThanOrEqualTo(95);
    }

    #endregion

    #region Vendor OUI Default to Plug Tests (Cync/Wyze/GE)

    [Theory]
    [InlineData("Cync by Savant", "Plant Lights")]
    [InlineData("Wyze Labs", "Smart Plug 1")]
    [InlineData("Wyze", "Living Room")]
    [InlineData("GE Lighting", "Bedroom")]  // Generic name - defaults to SmartPlug
    [InlineData("Savant Systems", "Kitchen Outlet")]
    public void DetectDeviceType_PlugVendorOui_DefaultsToSmartPlug(string oui, string deviceName)
    {
        // Arrange - These vendors default to SmartPlug unless name indicates camera
        var client = new UniFiClientResponse
        {
            Mac = "aa:bb:cc:dd:ee:ff",
            Name = deviceName,
            Oui = oui,
            DevCat = 4 // Camera fingerprint (should be overridden by OUI)
        };

        // Act
        var result = _service.DetectDeviceType(client);

        // Assert
        result.Category.Should().Be(ClientDeviceCategory.SmartPlug);
    }

    [Theory]
    [InlineData("GE Lighting", "Desk Lamp")]
    [InlineData("Cync by Savant", "Kitchen Bulb")]
    public void DetectDeviceType_PlugVendorWithLightingName_ReturnsSmartLighting(string oui, string deviceName)
    {
        // Arrange - If name indicates lighting, override vendor default to SmartPlug
        var client = new UniFiClientResponse
        {
            Mac = "aa:bb:cc:dd:ee:ff",
            Name = deviceName,
            Oui = oui,
            DevCat = 4 // Camera fingerprint
        };

        // Act
        var result = _service.DetectDeviceType(client);

        // Assert - Lighting name overrides vendor default
        result.Category.Should().Be(ClientDeviceCategory.SmartLighting);
    }

    [Theory]
    [InlineData("Wyze Labs", "Front Door Cam")]
    [InlineData("Wyze", "Garage Camera")]
    [InlineData("Cync by Savant", "Doorbell Camera")]
    [InlineData("Wyze", "Video Doorbell")]
    public void DetectDeviceType_PlugVendorWithCameraName_ReturnsCamera(string oui, string deviceName)
    {
        // Arrange - If name indicates camera, use fingerprint detection
        var client = new UniFiClientResponse
        {
            Mac = "aa:bb:cc:dd:ee:ff",
            Name = deviceName,
            Oui = oui,
            DevCat = 4 // Camera fingerprint
        };

        // Act
        var result = _service.DetectDeviceType(client);

        // Assert - Camera name overrides vendor default
        result.Category.Should().Be(ClientDeviceCategory.Camera);
    }

    [Fact]
    public void DetectDeviceType_NonPlugVendor_UsesFingerprint()
    {
        // Arrange - Ring is a camera vendor, should use fingerprint
        var client = new UniFiClientResponse
        {
            Mac = "aa:bb:cc:dd:ee:ff",
            Name = "Front Door",
            Oui = "Ring Inc",
            DevCat = 4 // Camera fingerprint
        };

        // Act
        var result = _service.DetectDeviceType(client);

        // Assert - Ring should be detected as camera
        result.Category.Should().Be(ClientDeviceCategory.Camera);
    }

    #endregion

    #region Apple Watch Tests

    [Theory]
    [InlineData("John's Apple Watch")]
    [InlineData("Apple Watch Series 9")]
    [InlineData("My Apple Watch Ultra")]
    public void DetectDeviceType_AppleWatch_ReturnsSmartphone(string deviceName)
    {
        // Arrange - Apple Watch should be categorized as smartphone (wearable)
        var client = new UniFiClientResponse
        {
            Mac = "aa:bb:cc:dd:ee:ff",
            Name = deviceName,
            DevCat = 14 // SmartSensor fingerprint (should be overridden)
        };

        // Act
        var result = _service.DetectDeviceType(client);

        // Assert
        result.Category.Should().Be(ClientDeviceCategory.Smartphone);
        result.VendorName.Should().Be("Apple");
        result.RecommendedNetwork.Should().Be(NetworkPurpose.Corporate);
    }

    [Theory]
    [InlineData("Apple Inc", "Living Room")]
    [InlineData("Apple", "John's Watch")]
    public void DetectDeviceType_AppleOuiWithSmartSensorFingerprint_ReturnsSmartphone(string oui, string deviceName)
    {
        // Arrange - Apple device with SmartSensor fingerprint (DevCat=14) is likely Apple Watch
        var client = new UniFiClientResponse
        {
            Mac = "aa:bb:cc:dd:ee:ff",
            Name = deviceName,
            Oui = oui,
            DevCat = 14 // SmartSensor fingerprint
        };

        // Act
        var result = _service.DetectDeviceType(client);

        // Assert - Should be detected as Smartphone (wearable)
        result.Category.Should().Be(ClientDeviceCategory.Smartphone);
        result.VendorName.Should().Be("Apple");
        result.RecommendedNetwork.Should().Be(NetworkPurpose.Corporate);
    }

    #endregion

    #region Fingerprint Detection Tests

    [Fact]
    public void DetectDeviceType_CameraFingerprint_WithoutNameOverride_ReturnsCamera()
    {
        // Arrange - Camera fingerprint without plug/bulb in name
        var client = new UniFiClientResponse
        {
            Mac = "aa:bb:cc:dd:ee:ff",
            Name = "Front Door Cam",
            DevCat = 4 // Camera fingerprint
        };

        // Act
        var result = _service.DetectDeviceType(client);

        // Assert - Should use fingerprint when name doesn't indicate otherwise
        result.Category.Should().Be(ClientDeviceCategory.Camera);
    }

    [Fact]
    public void DetectDeviceType_NoFingerprintData_UsesNamePattern()
    {
        // Arrange
        var client = new UniFiClientResponse
        {
            Mac = "aa:bb:cc:dd:ee:ff",
            Name = "Sonos Speaker"
            // No DevCat, no DevIdOverride
        };

        // Act
        var result = _service.DetectDeviceType(client);

        // Assert
        result.Category.Should().Be(ClientDeviceCategory.MediaPlayer);
    }

    #endregion

    #region Port Name Detection Tests

    [Fact]
    public void DetectFromPortName_CameraPort_ReturnsCamera()
    {
        // Arrange
        var portName = "Front Door Camera";

        // Act
        var result = _service.DetectFromPortName(portName);

        // Assert
        result.Category.Should().Be(ClientDeviceCategory.Camera);
    }

    [Fact]
    public void DetectFromPortName_PlugPort_ReturnsSmartPlug()
    {
        // Arrange
        var portName = "Patio Plug";

        // Act
        var result = _service.DetectFromPortName(portName);

        // Assert
        result.Category.Should().Be(ClientDeviceCategory.SmartPlug);
    }

    #endregion

    #region Category Extension Tests

    [Theory]
    [InlineData(ClientDeviceCategory.SmartPlug)]
    [InlineData(ClientDeviceCategory.SmartLighting)]
    [InlineData(ClientDeviceCategory.SmartSpeaker)]
    [InlineData(ClientDeviceCategory.SmartTV)]
    [InlineData(ClientDeviceCategory.StreamingDevice)]
    [InlineData(ClientDeviceCategory.RoboticVacuum)]
    public void IsIoT_IoTDeviceCategories_ReturnsTrue(ClientDeviceCategory category)
    {
        // Act & Assert
        category.IsIoT().Should().BeTrue();
    }

    [Theory]
    [InlineData(ClientDeviceCategory.Camera)]
    [InlineData(ClientDeviceCategory.SecuritySystem)]
    public void IsSurveillance_SurveillanceCategories_ReturnsTrue(ClientDeviceCategory category)
    {
        // Act & Assert
        category.IsSurveillance().Should().BeTrue();
    }

    [Theory]
    [InlineData(ClientDeviceCategory.Desktop)]
    [InlineData(ClientDeviceCategory.Laptop)]
    [InlineData(ClientDeviceCategory.Server)]
    public void IsIoT_NonIoTCategories_ReturnsFalse(ClientDeviceCategory category)
    {
        // Act & Assert
        category.IsIoT().Should().BeFalse();
    }

    #endregion
}
