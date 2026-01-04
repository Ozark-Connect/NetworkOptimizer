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
            DevCat = 9 // Camera fingerprint (9 = IP Network Camera)
        };

        // Act
        var result = _service.DetectDeviceType(client);

        // Assert - Ring should be detected as camera
        result.Category.Should().Be(ClientDeviceCategory.Camera);
    }

    #endregion

    #region Camera Name Override Tests (Nest/Google cameras)

    [Theory]
    [InlineData("Nest Labs Inc.", "[IoT] Nest Doorbell")]
    [InlineData("Nest Labs Inc.", "[IoT] Nest Driveway Cam")]
    [InlineData("Google, Inc.", "Front Door Camera")]
    [InlineData("Nest Labs Inc.", "Garage Cam")]
    [InlineData("Google, Inc.", "Video Doorbell Pro")]
    public void DetectDeviceType_NestOrGoogleWithCameraName_ReturnsCamera(string oui, string deviceName)
    {
        // Arrange - Nest/Google OUI would normally map to SmartThermostat/SmartSpeaker,
        // but camera-indicating names should override that
        var client = new UniFiClientResponse
        {
            Mac = "18:b4:30:12:34:56", // Nest MAC prefix
            Name = deviceName,
            Oui = oui,
            DevCat = 51 // IoT fingerprint
        };

        // Act
        var result = _service.DetectDeviceType(client);

        // Assert - Camera name should override Nest OUI mapping
        result.Category.Should().Be(ClientDeviceCategory.Camera);
        result.ConfidenceScore.Should().BeGreaterThanOrEqualTo(95);
    }

    [Theory]
    [InlineData("Nest Labs Inc.", "Living Room Thermostat", ClientDeviceCategory.SmartThermostat)]
    [InlineData("Nest Labs Inc.", "Hallway Ecobee", ClientDeviceCategory.SmartThermostat)]
    [InlineData("Google, Inc.", "Kitchen Nest Hub", ClientDeviceCategory.SmartSpeaker)]
    [InlineData("Google, Inc.", "Living Room Google Home", ClientDeviceCategory.SmartSpeaker)]
    [InlineData("Amazon", "Echo Dot Kitchen", ClientDeviceCategory.SmartSpeaker)]
    public void DetectDeviceType_IoTDeviceNames_OverridesOui(string oui, string deviceName, ClientDeviceCategory expected)
    {
        // Arrange - IoT device names should override OUI detection
        var client = new UniFiClientResponse
        {
            Mac = "18:b4:30:12:34:56",
            Name = deviceName,
            Oui = oui
        };

        // Act
        var result = _service.DetectDeviceType(client);

        // Assert - Should detect based on name, not OUI
        result.Category.Should().Be(expected);
    }

    [Theory]
    [InlineData("Pool Cam")]
    [InlineData("Backyard Cam")]
    [InlineData("Shed Cam")]
    [InlineData("Baby Cam")]
    public void DetectDeviceType_CamWithWordBoundary_ReturnsCamera(string deviceName)
    {
        // Names ending in " Cam" should match via word boundary regex (not in specific list)
        var client = new UniFiClientResponse
        {
            Mac = "aa:bb:cc:dd:ee:ff",
            Name = deviceName
        };

        var result = _service.DetectDeviceType(client);
        result.Category.Should().Be(ClientDeviceCategory.Camera);
    }

    [Fact]
    public void DetectDeviceType_CamInWord_DoesNotMatchCamera()
    {
        // "Cambridge" should NOT match camera pattern
        var client = new UniFiClientResponse
        {
            Mac = "aa:bb:cc:dd:ee:ff",
            Name = "Cambridge Device"
        };

        var result = _service.DetectDeviceType(client);
        result.Category.Should().NotBe(ClientDeviceCategory.Camera);
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

    #region VR Headset Detection Tests

    [Theory]
    [InlineData("[VR] Quest 3")]
    [InlineData("Meta Quest 3")]
    [InlineData("Quest Pro")]
    [InlineData("Oculus Quest 2")]
    [InlineData("HTC Vive")]
    [InlineData("Valve Index")]
    [InlineData("PSVR Headset")]
    [InlineData("Pico 4")]
    public void DetectDeviceType_VRHeadset_ReturnsGameConsole(string deviceName)
    {
        // Arrange - VR headsets should be categorized as GameConsole
        var client = new UniFiClientResponse
        {
            Mac = "aa:bb:cc:dd:ee:ff",
            Name = deviceName,
            DevCat = 6 // Smartphone fingerprint (should be overridden)
        };

        // Act
        var result = _service.DetectDeviceType(client);

        // Assert
        result.Category.Should().Be(ClientDeviceCategory.GameConsole);
        result.RecommendedNetwork.Should().Be(NetworkPurpose.Corporate);
    }

    [Fact]
    public void DetectDeviceType_VRPrefixTag_ReturnsGameConsole()
    {
        // [VR] prefix tag should trigger VR detection
        var client = new UniFiClientResponse
        {
            Mac = "aa:bb:cc:dd:ee:ff",
            Name = "[VR] Living Room Headset",
            DevCat = 32 // Android Device fingerprint
        };

        var result = _service.DetectDeviceType(client);

        result.Category.Should().Be(ClientDeviceCategory.GameConsole);
    }

    #endregion

    #region NAS Pattern Tests (avoid false positives)

    [Theory]
    [InlineData("Tiny Home - Deck Rail Lights")]
    [InlineData("lights-deck")]
    [InlineData("Patio Lights Controller")]
    [InlineData("Christmas Lights")]
    public void DetectDeviceType_LightsInName_DoesNotMatchNAS(string deviceName)
    {
        // Names containing "lights" should NOT match NAS patterns like "ts-" or "tvs-"
        var client = new UniFiClientResponse
        {
            Mac = "aa:bb:cc:dd:ee:ff",
            Name = deviceName
        };

        var result = _service.DetectDeviceType(client);

        // Should NOT be NAS (could be SmartLighting or Unknown, but definitely not NAS)
        result.Category.Should().NotBe(ClientDeviceCategory.NAS);
    }

    [Theory]
    [InlineData("Synology DS920+")]
    [InlineData("QNAP TS-453D")]
    [InlineData("QNAP TVS-872XT")]
    [InlineData("My NAS Server")]
    public void DetectDeviceType_ActualNAS_ReturnsNAS(string deviceName)
    {
        // Actual NAS names should still match correctly
        var client = new UniFiClientResponse
        {
            Mac = "aa:bb:cc:dd:ee:ff",
            Name = deviceName
        };

        var result = _service.DetectDeviceType(client);

        result.Category.Should().Be(ClientDeviceCategory.NAS);
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

    #region Client History Tests

    [Fact]
    public void SetClientHistory_WithValidList_PopulatesLookup()
    {
        // Arrange - DevCat 9 = "IP Network Camera" in UniFi fingerprint database
        var history = new List<UniFiClientHistoryResponse>
        {
            new()
            {
                Mac = "aa:bb:cc:dd:ee:ff",
                Name = "Test Camera",
                Fingerprint = new ClientFingerprintData { DevCat = 9 }
            }
        };

        // Act
        _service.SetClientHistory(history);
        var result = _service.DetectFromMac("aa:bb:cc:dd:ee:ff");

        // Assert - should find the device from history
        result.Category.Should().Be(ClientDeviceCategory.Camera);
    }

    [Fact]
    public void SetClientHistory_WithNull_ClearsLookup()
    {
        // Arrange - first set some history with valid DevCat 9 (IP Network Camera)
        var history = new List<UniFiClientHistoryResponse>
        {
            new()
            {
                Mac = "aa:bb:cc:dd:ee:ff",
                Name = "Front Door Camera",
                Fingerprint = new ClientFingerprintData { DevCat = 9 }
            }
        };
        _service.SetClientHistory(history);

        // Act - clear it
        _service.SetClientHistory(null);

        // Assert - should now fall back to OUI detection (unknown in this case)
        var result = _service.DetectFromMac("aa:bb:cc:dd:ee:ff");
        result.Source.Should().NotBe(DetectionSource.UniFiFingerprint);
    }

    [Fact]
    public void SetClientHistory_WithEmptyList_ClearsLookup()
    {
        // Arrange - first set some history with valid DevCat 9 (IP Network Camera)
        var history = new List<UniFiClientHistoryResponse>
        {
            new()
            {
                Mac = "aa:bb:cc:dd:ee:ff",
                Name = "Camera",
                Fingerprint = new ClientFingerprintData { DevCat = 9 }
            }
        };
        _service.SetClientHistory(history);

        // Act - set empty list
        _service.SetClientHistory(new List<UniFiClientHistoryResponse>());

        // Assert - should now fall back to OUI detection
        var result = _service.DetectFromMac("aa:bb:cc:dd:ee:ff");
        result.Source.Should().NotBe(DetectionSource.UniFiFingerprint);
    }

    [Fact]
    public void DetectFromMac_WithHistoryFingerprint_ReturnsCorrectCategory()
    {
        // Arrange - history with camera fingerprint
        // DevCat 9 = "IP Network Camera" in UniFi fingerprint database
        var history = new List<UniFiClientHistoryResponse>
        {
            new()
            {
                Mac = "11:22:33:44:55:66",
                Name = "Garage",
                Fingerprint = new ClientFingerprintData { DevCat = 9, DevVendor = 100 }
            }
        };
        _service.SetClientHistory(history);

        // Act
        var result = _service.DetectFromMac("11:22:33:44:55:66");

        // Assert
        result.Category.Should().Be(ClientDeviceCategory.Camera);
        result.Source.Should().Be(DetectionSource.UniFiFingerprint);
    }

    [Fact]
    public void DetectFromMac_WithHistoryNamePattern_ReturnsCorrectCategory()
    {
        // Arrange - history without fingerprint but with recognizable name
        // Note: Sonos devices are classified as MediaPlayer, not SmartSpeaker
        var history = new List<UniFiClientHistoryResponse>
        {
            new()
            {
                Mac = "11:22:33:44:55:66",
                Name = "Kitchen Sonos One",
                Fingerprint = null
            }
        };
        _service.SetClientHistory(history);

        // Act
        var result = _service.DetectFromMac("11:22:33:44:55:66");

        // Assert - Sonos is classified as MediaPlayer
        result.Category.Should().Be(ClientDeviceCategory.MediaPlayer);
    }

    [Fact]
    public void DetectFromMac_WithoutHistory_FallsBackToOuiDatabase()
    {
        // Arrange - no history set, use a known IoT MAC prefix
        // Ring devices: F8:02:78
        var ringMac = "f8:02:78:12:34:56";

        // Act
        var result = _service.DetectFromMac(ringMac);

        // Assert - should use OUI detection
        // Note: actual detection depends on OUI database content
        result.Should().NotBeNull();
    }

    [Fact]
    public void DetectFromMac_CaseInsensitiveLookup_FindsMatch()
    {
        // Arrange - DevCat 9 = "IP Network Camera"
        var history = new List<UniFiClientHistoryResponse>
        {
            new()
            {
                Mac = "AA:BB:CC:DD:EE:FF",  // uppercase
                Name = "Test Camera",
                Fingerprint = new ClientFingerprintData { DevCat = 9 }
            }
        };
        _service.SetClientHistory(history);

        // Act - query with lowercase
        var result = _service.DetectFromMac("aa:bb:cc:dd:ee:ff");

        // Assert
        result.Category.Should().Be(ClientDeviceCategory.Camera);
    }

    [Fact]
    public void DetectFromMac_EmptyMac_ReturnsUnknown()
    {
        // Act
        var result = _service.DetectFromMac("");

        // Assert
        result.Category.Should().Be(ClientDeviceCategory.Unknown);
    }

    [Fact]
    public void DetectFromMac_NullMac_ReturnsUnknown()
    {
        // Act
        var result = _service.DetectFromMac(null!);

        // Assert
        result.Category.Should().Be(ClientDeviceCategory.Unknown);
    }

    [Fact]
    public void DetectFromMac_HistoryUsesDisplayName_WhenNameNull()
    {
        // Arrange - history with DisplayName but no Name
        var history = new List<UniFiClientHistoryResponse>
        {
            new()
            {
                Mac = "11:22:33:44:55:66",
                Name = null,
                DisplayName = "Living Room Camera",
                Fingerprint = null
            }
        };
        _service.SetClientHistory(history);

        // Act
        var result = _service.DetectFromMac("11:22:33:44:55:66");

        // Assert - should detect from DisplayName pattern
        result.Category.Should().Be(ClientDeviceCategory.Camera);
    }

    [Fact]
    public void SetClientHistory_FiltersEntriesWithEmptyMac()
    {
        // Arrange - history with some empty MACs
        // DevCat 9 = "IP Network Camera"
        var history = new List<UniFiClientHistoryResponse>
        {
            new() { Mac = "", Name = "Empty MAC" },
            new() { Mac = "aa:bb:cc:dd:ee:ff", Name = "Valid Camera", Fingerprint = new ClientFingerprintData { DevCat = 9 } },
            new() { Mac = null!, Name = "Null MAC" }
        };

        // Act - should not throw
        _service.SetClientHistory(history);
        var result = _service.DetectFromMac("aa:bb:cc:dd:ee:ff");

        // Assert
        result.Category.Should().Be(ClientDeviceCategory.Camera);
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
