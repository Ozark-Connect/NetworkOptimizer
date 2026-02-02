using FluentAssertions;
using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.Audit.Services;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Core.Models;
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

    #region Apple TV Name Override Tests

    [Theory]
    [InlineData("Apple TV")]
    [InlineData("Living Room Apple TV")]
    [InlineData("AppleTV Bedroom")]
    [InlineData("[Media] Tiny Home - Apple TV")]
    [InlineData("[Mac] AppleTV Bedroom")]
    public void DetectDeviceType_NameContainsAppleTV_ReturnsStreamingDevice(string deviceName)
    {
        // Arrange - Apple TV is categorized as SmartTV by UniFi (dev_type_id=47)
        // but should be overridden to StreamingDevice
        var client = new UniFiClientResponse
        {
            Mac = "aa:bb:cc:dd:ee:ff",
            Name = deviceName,
            DevCat = 47 // SmartTV fingerprint (should be overridden)
        };

        // Act
        var result = _service.DetectDeviceType(client);

        // Assert
        result.Category.Should().Be(ClientDeviceCategory.StreamingDevice);
        result.VendorName.Should().Be("Apple");
        result.ConfidenceScore.Should().BeGreaterThanOrEqualTo(95);
    }

    [Fact]
    public void DetectDeviceType_AppleTVWithDevIdOverride_NameOverrideWins()
    {
        // Arrange - Even with a dev_id_override that maps to SmartTV,
        // the name override should take priority
        var client = new UniFiClientResponse
        {
            Mac = "aa:bb:cc:dd:ee:ff",
            Name = "Apple TV 4K",
            DevIdOverride = 14, // Apple TV HD in fingerprint DB → SmartTV
            DevCat = 47
        };

        // Act
        var result = _service.DetectDeviceType(client);

        // Assert
        result.Category.Should().Be(ClientDeviceCategory.StreamingDevice);
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
    [InlineData("Wyze", "Video Doorbell")]
    public void DetectDeviceType_WyzeCameraName_ReturnsCloudCamera(string oui, string deviceName)
    {
        // Arrange - Wyze cameras are cloud cameras (require internet)
        var client = new UniFiClientResponse
        {
            Mac = "aa:bb:cc:dd:ee:ff",
            Name = deviceName,
            Oui = oui,
            DevCat = 4 // Camera fingerprint
        };

        // Act
        var result = _service.DetectDeviceType(client);

        // Assert - Wyze cameras are cloud cameras
        result.Category.Should().Be(ClientDeviceCategory.CloudCamera);
    }

    [Theory]
    [InlineData("Cync by Savant", "Doorbell Camera")]
    public void DetectDeviceType_CyncCameraName_ReturnsSelfHostedCamera(string oui, string deviceName)
    {
        // Arrange - Cync is NOT a cloud camera vendor, so camera name makes it a self-hosted Camera
        var client = new UniFiClientResponse
        {
            Mac = "aa:bb:cc:dd:ee:ff",
            Name = deviceName,
            Oui = oui,
            DevCat = 4 // Camera fingerprint
        };

        // Act
        var result = _service.DetectDeviceType(client);

        // Assert - Cync cameras are self-hosted (not a known cloud camera vendor)
        result.Category.Should().Be(ClientDeviceCategory.Camera);
    }

    [Fact]
    public void DetectDeviceType_RingVendor_ReturnsCloudCamera()
    {
        // Arrange - Ring is a cloud camera vendor
        var client = new UniFiClientResponse
        {
            Mac = "aa:bb:cc:dd:ee:ff",
            Name = "Front Door",
            Oui = "Ring Inc",
            DevCat = 9 // Camera fingerprint (9 = IP Network Camera)
        };

        // Act
        var result = _service.DetectDeviceType(client);

        // Assert - Ring cameras are cloud cameras
        result.Category.Should().Be(ClientDeviceCategory.CloudCamera);
    }

    #endregion

    #region Camera Name Override Tests (Nest/Google cameras)

    [Theory]
    [InlineData("Nest Labs Inc.", "[IoT] Nest Doorbell")]
    [InlineData("Nest Labs Inc.", "[IoT] Nest Driveway Cam")]
    [InlineData("Google, Inc.", "Front Door Camera")]
    [InlineData("Nest Labs Inc.", "Garage Cam")]
    [InlineData("Google, Inc.", "Video Doorbell Pro")]
    public void DetectDeviceType_NestOrGoogleWithCameraName_ReturnsCloudCamera(string oui, string deviceName)
    {
        // Arrange - Nest/Google OUI would normally map to SmartThermostat/SmartSpeaker,
        // but camera-indicating names should override that to CloudCamera (requires internet)
        var client = new UniFiClientResponse
        {
            Mac = "18:b4:30:12:34:56", // Nest MAC prefix
            Name = deviceName,
            Oui = oui,
            DevCat = 51 // IoT fingerprint
        };

        // Act
        var result = _service.DetectDeviceType(client);

        // Assert - Camera name + Nest vendor = CloudCamera (not self-hosted Camera)
        // Confidence may vary based on detection path (name supplement vs direct detection)
        result.Category.Should().Be(ClientDeviceCategory.CloudCamera);
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

    #region GoPro Tests

    [Theory]
    [InlineData("GoPro", "HERO12")]
    [InlineData("GoPro Inc", "GoPro Camera")]
    [InlineData("GoPro", "Living Room")]
    public void DetectDeviceType_GoProWithOuiAndCameraFingerprint_ReturnsIoT(string oui, string deviceName)
    {
        // Arrange - GoPro action cameras use the same devCat (106) as security cameras
        // but they're consumer electronics, not security devices
        var client = new UniFiClientResponse
        {
            Mac = "aa:bb:cc:dd:ee:ff",
            Name = deviceName,
            Oui = oui,
            DevCat = 106 // Camera fingerprint - same as security cameras
        };

        // Act
        var result = _service.DetectDeviceType(client);

        // Assert - Should be IoT (consumer electronics), NOT Camera (security)
        result.Category.Should().Be(ClientDeviceCategory.IoTGeneric);
        result.VendorName.Should().Be("GoPro");
        result.RecommendedNetwork.Should().Be(NetworkPurpose.IoT);
        result.ConfidenceScore.Should().BeGreaterThan(95, "override confidence must beat fingerprint confidence");
        result.Metadata.Should().ContainKey("vendor_override_reason");
    }

    [Fact]
    public void DetectDeviceType_GoProWithVendorIdAndCameraFingerprint_ReturnsIoT()
    {
        // Arrange - GoPro detected via fingerprint database vendor_id=567, not OUI string
        // This is how it appears in the UniFi fingerprint database
        var client = new UniFiClientResponse
        {
            Mac = "aa:bb:cc:dd:ee:ff",
            Name = "HERO12 Black",
            Oui = "Some Chip Manufacturer",  // OUI might not say GoPro
            DevCat = 106,   // Camera fingerprint
            DevVendor = 567 // GoPro's vendor ID in fingerprint database
        };

        // Act
        var result = _service.DetectDeviceType(client);

        // Assert - Should be IoT (consumer electronics), NOT Camera (security)
        result.Category.Should().Be(ClientDeviceCategory.IoTGeneric);
        result.RecommendedNetwork.Should().Be(NetworkPurpose.IoT);
        result.Metadata.Should().ContainKey("vendor_override_reason");
        result.Metadata.Should().ContainKey("dev_vendor");
    }

    [Fact]
    public void DetectDeviceType_NonGoProCameraWithDevCat106_ReturnsCamera()
    {
        // Arrange - A regular security camera with devCat 106 should still be Camera
        var client = new UniFiClientResponse
        {
            Mac = "aa:bb:cc:dd:ee:ff",
            Name = "Front Door Camera",
            Oui = "Hikvision",
            DevCat = 106,    // Camera fingerprint
            DevVendor = 100  // Some other vendor, not GoPro (567)
        };

        // Act
        var result = _service.DetectDeviceType(client);

        // Assert - Should be detected as Camera (security camera)
        result.Category.Should().Be(ClientDeviceCategory.Camera);
    }

    #endregion

    #region Pixel Phone Tests

    [Theory]
    [InlineData("Pixel 6")]
    [InlineData("Pixel 7 Pro")]
    [InlineData("Pixel 8a")]
    [InlineData("John's Pixel 9")]
    [InlineData("[Phone] Pixel8")]
    public void DetectDeviceType_PixelPhone_ReturnsSmartphone(string deviceName)
    {
        // Arrange - Pixel phones should be categorized as smartphone
        var client = new UniFiClientResponse
        {
            Mac = "aa:bb:cc:dd:ee:ff",
            Name = deviceName,
            DevCat = 4 // Some other fingerprint (should be overridden)
        };

        // Act
        var result = _service.DetectDeviceType(client);

        // Assert
        result.Category.Should().Be(ClientDeviceCategory.Smartphone);
        result.VendorName.Should().Be("Google");
        result.RecommendedNetwork.Should().Be(NetworkPurpose.Corporate);
    }

    [Theory]
    [InlineData("Pixel Tablet", ClientDeviceCategory.SmartTV)] // DevCat 47 = Smart TV
    [InlineData("Pixelbook", ClientDeviceCategory.Laptop)] // DevCat 1 = Laptop
    [InlineData("Pixel Slate", ClientDeviceCategory.Tablet)] // DevCat 2 = Tablet
    public void DetectDeviceType_PixelNonPhone_DoesNotOverrideToSmartphone(string deviceName, ClientDeviceCategory expectedFromFingerprint)
    {
        // Arrange - Pixel Tablet, Pixelbook, Pixel Slate should NOT be overridden to smartphone
        var devCatForCategory = expectedFromFingerprint switch
        {
            ClientDeviceCategory.SmartTV => 47,
            ClientDeviceCategory.Laptop => 1,
            ClientDeviceCategory.Tablet => 2,
            _ => 0
        };
        var client = new UniFiClientResponse
        {
            Mac = "aa:bb:cc:dd:ee:ff",
            Name = deviceName,
            DevCat = devCatForCategory
        };

        // Act
        var result = _service.DetectDeviceType(client);

        // Assert - Should NOT be overridden to Smartphone
        result.Category.Should().NotBe(ClientDeviceCategory.Smartphone);
    }

    #endregion

    #region Watch Misfingerprint Correction Tests

    [Theory]
    [InlineData("Samsung Watch", 25)]  // Desktop (Thin Client)
    [InlineData("Galaxy Watch 5", 9)]  // Camera
    [InlineData("My Watch", 47)]       // SmartTV
    [InlineData("Fitbit Watch", 4)]    // IoTGeneric (Miscellaneous)
    public void DetectDeviceType_WatchMisfingerprinted_CorrectToSmartphone(string deviceName, int devCat)
    {
        // Arrange - device named "Watch" but misfingerprinted as something else
        var client = new UniFiClientResponse
        {
            Mac = "aa:bb:cc:dd:ee:ff",
            Name = deviceName,
            DevCat = devCat
        };

        // Act
        var result = _service.DetectDeviceType(client);

        // Assert - Should be corrected to Smartphone
        result.Category.Should().Be(ClientDeviceCategory.Smartphone);
        result.RecommendedNetwork.Should().Be(NetworkPurpose.Corporate);
    }

    [Fact]
    public void DetectDeviceType_WatchWithNoFingerprint_CorrectToSmartphone()
    {
        // Arrange - device named "Watch" with no fingerprint (Unknown)
        var client = new UniFiClientResponse
        {
            Mac = "aa:bb:cc:dd:ee:ff",
            Name = "Garmin Watch",
            DevCat = null
        };

        // Act
        var result = _service.DetectDeviceType(client);

        // Assert - Should be corrected to Smartphone
        result.Category.Should().Be(ClientDeviceCategory.Smartphone);
        result.RecommendedNetwork.Should().Be(NetworkPurpose.Corporate);
    }

    [Theory]
    [InlineData("Watcher")] // Not a watch
    [InlineData("Night Watcher Camera")] // Not a watch
    [InlineData("Bird Watching Camera")] // Not a watch
    [InlineData("Watchdog")] // Not a watch
    public void DetectDeviceType_WatchSubstring_DoesNotCorrectToSmartphone(string deviceName)
    {
        // Arrange - names containing "watch" as substring should NOT be corrected
        var client = new UniFiClientResponse
        {
            Mac = "aa:bb:cc:dd:ee:ff",
            Name = deviceName,
            DevCat = 9 // Camera fingerprint
        };

        // Act
        var result = _service.DetectDeviceType(client);

        // Assert - Should remain as Camera, not corrected to Smartphone
        result.Category.Should().NotBe(ClientDeviceCategory.Smartphone);
    }

    [Theory]
    [InlineData("John's Watch", "Samsung", "Samsung")]
    [InlineData("Apple Watch SE", "", "Apple")]
    [InlineData("Galaxy Watch 6", "", "Samsung")]
    [InlineData("Fitbit Watch", "", "Fitbit")]
    [InlineData("Garmin Watch", "", "Garmin")]
    public void DetectDeviceType_WatchCorrection_PreservesVendor(string deviceName, string oui, string expectedVendor)
    {
        // Arrange - watch with known vendor
        var client = new UniFiClientResponse
        {
            Mac = "aa:bb:cc:dd:ee:ff",
            Name = deviceName,
            Oui = oui,
            DevCat = 25 // Desktop fingerprint (Thin Client) - should be overridden
        };

        // Act
        var result = _service.DetectDeviceType(client);

        // Assert
        result.Category.Should().Be(ClientDeviceCategory.Smartphone);
        result.VendorName.Should().Be(expectedVendor);
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
        var history = new List<UniFiClientDetailResponse>
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
        var history = new List<UniFiClientDetailResponse>
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
        var history = new List<UniFiClientDetailResponse>
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
        _service.SetClientHistory(new List<UniFiClientDetailResponse>());

        // Assert - should now fall back to OUI detection
        var result = _service.DetectFromMac("aa:bb:cc:dd:ee:ff");
        result.Source.Should().NotBe(DetectionSource.UniFiFingerprint);
    }

    [Fact]
    public void DetectFromMac_WithHistoryFingerprint_ReturnsCorrectCategory()
    {
        // Arrange - history with camera fingerprint
        // DevCat 9 = "IP Network Camera" in UniFi fingerprint database
        var history = new List<UniFiClientDetailResponse>
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
        var history = new List<UniFiClientDetailResponse>
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
        var history = new List<UniFiClientDetailResponse>
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
        var history = new List<UniFiClientDetailResponse>
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
        var history = new List<UniFiClientDetailResponse>
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

    [Theory]
    [InlineData("SimpliSafe", ClientDeviceCategory.CloudCamera)]
    [InlineData("Ring", ClientDeviceCategory.CloudCamera)]
    [InlineData("Nest", ClientDeviceCategory.CloudCamera)]
    [InlineData("Wyze", ClientDeviceCategory.CloudCamera)]
    [InlineData("Arlo", ClientDeviceCategory.CloudCamera)]
    [InlineData("Blink", ClientDeviceCategory.CloudCamera)]
    [InlineData("TP-Link", ClientDeviceCategory.CloudCamera)]
    [InlineData("Canary", ClientDeviceCategory.CloudCamera)]
    public void DetectFromMac_HistoryCameraFingerprint_WithCloudVendor_ReturnsCloudCamera(string vendor, ClientDeviceCategory expected)
    {
        // Arrange - history with camera fingerprint (DevCat 9) and cloud vendor via OUI
        var history = new List<UniFiClientDetailResponse>
        {
            new()
            {
                Mac = "11:22:33:44:55:66",
                Name = "Doorbell",
                Oui = vendor,
                Fingerprint = new ClientFingerprintData { DevCat = 9, DevVendor = 100 }
            }
        };
        _service.SetClientHistory(history);

        // Act
        var result = _service.DetectFromMac("11:22:33:44:55:66");

        // Assert - should be upgraded to CloudCamera due to cloud vendor
        result.Category.Should().Be(expected);
        result.Source.Should().Be(DetectionSource.UniFiFingerprint);
    }

    [Theory]
    [InlineData("SimpliSafe", ClientDeviceCategory.CloudSecuritySystem)]
    public void DetectFromMac_HistorySecuritySystemFingerprint_WithCloudVendor_ReturnsCloudSecuritySystem(string vendor, ClientDeviceCategory expected)
    {
        // Arrange - history with security system fingerprint (DevCat 80 = Smart Home Security System) and cloud vendor via OUI
        var history = new List<UniFiClientDetailResponse>
        {
            new()
            {
                Mac = "11:22:33:44:55:66",
                Name = "Basestation",
                Oui = vendor,
                Fingerprint = new ClientFingerprintData { DevCat = 80, DevVendor = 100 }
            }
        };
        _service.SetClientHistory(history);

        // Act
        var result = _service.DetectFromMac("11:22:33:44:55:66");

        // Assert - should be upgraded to CloudSecuritySystem due to cloud vendor
        result.Category.Should().Be(expected);
        result.Source.Should().Be(DetectionSource.UniFiFingerprint);
    }

    [Fact]
    public void DetectFromMac_HistoryCameraFingerprint_WithLocalVendor_ReturnsCamera()
    {
        // Arrange - history with camera fingerprint (DevCat 9) and non-cloud vendor
        var history = new List<UniFiClientDetailResponse>
        {
            new()
            {
                Mac = "11:22:33:44:55:66",
                Name = "Backyard Camera",
                Oui = "Hikvision", // Local NVR vendor, not cloud-dependent
                Fingerprint = new ClientFingerprintData { DevCat = 9, DevVendor = 100 }
            }
        };
        _service.SetClientHistory(history);

        // Act
        var result = _service.DetectFromMac("11:22:33:44:55:66");

        // Assert - should stay as Camera (not upgraded to CloudCamera)
        result.Category.Should().Be(ClientDeviceCategory.Camera);
        result.Source.Should().Be(DetectionSource.UniFiFingerprint);
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

    #region Vendor Preservation Tests - Speakers

    [Theory]
    [InlineData("HomePod")]
    [InlineData("Living Room HomePod")]
    [InlineData("Kitchen HomePod Mini")]
    [InlineData("[IoT] HomePod")]
    public void DetectDeviceType_HomePod_SetsAppleVendor(string deviceName)
    {
        // Arrange
        var client = new UniFiClientResponse
        {
            Mac = "aa:bb:cc:dd:ee:ff",
            Name = deviceName
        };

        // Act
        var result = _service.DetectDeviceType(client);

        // Assert
        result.Category.Should().Be(ClientDeviceCategory.SmartSpeaker);
        result.VendorName.Should().Be("Apple");
    }

    [Theory]
    [InlineData("Echo Dot")]
    [InlineData("Kitchen Echo Dot")]
    [InlineData("Echo Show 10")]
    [InlineData("Echo Pop")]
    [InlineData("Echo Studio")]
    [InlineData("Amazon Echo")]
    public void DetectDeviceType_EchoDevices_SetsAmazonVendor(string deviceName)
    {
        // Arrange
        var client = new UniFiClientResponse
        {
            Mac = "aa:bb:cc:dd:ee:ff",
            Name = deviceName
        };

        // Act
        var result = _service.DetectDeviceType(client);

        // Assert
        result.Category.Should().Be(ClientDeviceCategory.SmartSpeaker);
        result.VendorName.Should().Be("Amazon");
    }

    [Theory]
    [InlineData("Google Home")]
    [InlineData("Living Room Google Home")]
    [InlineData("Nest Mini")]
    [InlineData("Kitchen Nest Audio")]
    [InlineData("Nest Hub Max")]
    public void DetectDeviceType_GoogleNestSpeakers_SetsGoogleVendor(string deviceName)
    {
        // Arrange
        var client = new UniFiClientResponse
        {
            Mac = "aa:bb:cc:dd:ee:ff",
            Name = deviceName
        };

        // Act
        var result = _service.DetectDeviceType(client);

        // Assert
        result.Category.Should().Be(ClientDeviceCategory.SmartSpeaker);
        result.VendorName.Should().Be("Google");
    }

    #endregion

    #region Vendor Preservation Tests - VR Headsets

    [Theory]
    [InlineData("Quest 3")]
    [InlineData("Meta Quest Pro")]
    [InlineData("Oculus Quest 2")]
    public void DetectDeviceType_MetaVR_SetsMetaVendor(string deviceName)
    {
        // Arrange
        var client = new UniFiClientResponse
        {
            Mac = "aa:bb:cc:dd:ee:ff",
            Name = deviceName
        };

        // Act
        var result = _service.DetectDeviceType(client);

        // Assert
        result.Category.Should().Be(ClientDeviceCategory.GameConsole);
        result.VendorName.Should().Be("Meta");
    }

    [Fact]
    public void DetectDeviceType_HTCVive_SetsHTCVendor()
    {
        // Arrange
        var client = new UniFiClientResponse
        {
            Mac = "aa:bb:cc:dd:ee:ff",
            Name = "HTC Vive Pro"
        };

        // Act
        var result = _service.DetectDeviceType(client);

        // Assert
        result.Category.Should().Be(ClientDeviceCategory.GameConsole);
        result.VendorName.Should().Be("HTC");
    }

    [Fact]
    public void DetectDeviceType_ValveIndex_SetsValveVendor()
    {
        // Arrange
        var client = new UniFiClientResponse
        {
            Mac = "aa:bb:cc:dd:ee:ff",
            Name = "Valve Index"
        };

        // Act
        var result = _service.DetectDeviceType(client);

        // Assert
        result.Category.Should().Be(ClientDeviceCategory.GameConsole);
        result.VendorName.Should().Be("Valve");
    }

    [Fact]
    public void DetectDeviceType_PSVR_SetsSonyVendor()
    {
        // Arrange
        var client = new UniFiClientResponse
        {
            Mac = "aa:bb:cc:dd:ee:ff",
            Name = "PSVR 2"
        };

        // Act
        var result = _service.DetectDeviceType(client);

        // Assert
        result.Category.Should().Be(ClientDeviceCategory.GameConsole);
        result.VendorName.Should().Be("Sony");
    }

    [Fact]
    public void DetectDeviceType_Pico_SetsPicoVendor()
    {
        // Arrange
        var client = new UniFiClientResponse
        {
            Mac = "aa:bb:cc:dd:ee:ff",
            Name = "Pico 4"
        };

        // Act
        var result = _service.DetectDeviceType(client);

        // Assert
        result.Category.Should().Be(ClientDeviceCategory.GameConsole);
        result.VendorName.Should().Be("Pico");
    }

    [Fact]
    public void DetectDeviceType_GenericVRTag_PreservesOuiVendor()
    {
        // Arrange - [VR] tag should preserve OUI vendor
        var client = new UniFiClientResponse
        {
            Mac = "aa:bb:cc:dd:ee:ff",
            Name = "[VR] Custom Headset",
            Oui = "Some VR Company"
        };

        // Act
        var result = _service.DetectDeviceType(client);

        // Assert
        result.Category.Should().Be(ClientDeviceCategory.GameConsole);
        result.VendorName.Should().Be("Some VR Company");
    }

    #endregion

    #region UniFi Protect Camera Tests

    [Theory]
    [InlineData("G5 Turret Ultra")]           // Default product name
    [InlineData("Front Door Camera")]          // User alias
    [InlineData("[Cam] Driveway")]             // User alias with tag
    [InlineData("Garage - Road View")]         // User alias with location
    [InlineData("")]                           // Empty name
    public void DetectDeviceType_UniFiProtectCamera_Wired_ReturnsCamera(string cameraName)
    {
        // Arrange - Simulate wired UniFi Protect camera
        var protectCameras = new ProtectCameraCollection();
        protectCameras.Add("aa:bb:cc:dd:ee:ff", cameraName);

        var service = new DeviceTypeDetectionService();
        service.SetProtectCameras(protectCameras);

        var client = new UniFiClientResponse
        {
            Mac = "aa:bb:cc:dd:ee:ff",
            Name = cameraName,
            Oui = "Ubiquiti Inc"  // Wired cameras show Ubiquiti OUI
        };

        // Act
        var result = service.DetectDeviceType(client);

        // Assert - Should be Camera on Security network, NOT CloudCamera
        result.Category.Should().Be(ClientDeviceCategory.Camera);
        result.RecommendedNetwork.Should().Be(NetworkPurpose.Security);
        result.VendorName.Should().Be("Ubiquiti");
        result.ConfidenceScore.Should().Be(100);
    }

    [Theory]
    [InlineData("G6 Instant")]                 // Default product name
    [InlineData("Back Yard Camera")]           // User alias
    [InlineData("[Cam] Dogs")]                 // User alias with tag
    [InlineData("Tiny Home - Front Door")]     // User alias with location
    [InlineData("")]                           // Empty name
    public void DetectDeviceType_UniFiProtectCamera_WiFi_ReturnsCamera(string cameraName)
    {
        // Arrange - Simulate Wi-Fi UniFi Protect camera
        var protectCameras = new ProtectCameraCollection();
        protectCameras.Add("aa:bb:cc:dd:ee:ff", cameraName);

        var service = new DeviceTypeDetectionService();
        service.SetProtectCameras(protectCameras);

        var client = new UniFiClientResponse
        {
            Mac = "aa:bb:cc:dd:ee:ff",
            Name = cameraName,
            Oui = "Ubiquiti Inc",
            IsWired = false,  // Wireless
            ApMac = "11:22:33:44:55:66"  // Connected to AP
        };

        // Act
        var result = service.DetectDeviceType(client);

        // Assert - Should be Camera on Security network, NOT CloudCamera
        result.Category.Should().Be(ClientDeviceCategory.Camera);
        result.RecommendedNetwork.Should().Be(NetworkPurpose.Security);
        result.VendorName.Should().Be("Ubiquiti");
        result.ConfidenceScore.Should().Be(100);
    }

    [Fact]
    public void DetectDeviceType_UniFiProtectCamera_WithCloudKeywordInName_StillReturnsCamera()
    {
        // Arrange - Protect camera with name containing cloud camera vendor keyword
        // This ensures Ubiquiti Protect cameras aren't accidentally classified as CloudCamera
        var protectCameras = new ProtectCameraCollection();
        protectCameras.Add("aa:bb:cc:dd:ee:ff", "Ring-style Doorbell");  // Contains "Ring"

        var service = new DeviceTypeDetectionService();
        service.SetProtectCameras(protectCameras);

        var client = new UniFiClientResponse
        {
            Mac = "aa:bb:cc:dd:ee:ff",
            Name = "Ring-style Doorbell",
            Oui = "Ubiquiti Inc"
        };

        // Act
        var result = service.DetectDeviceType(client);

        // Assert - Protect cameras should NEVER become CloudCamera
        result.Category.Should().Be(ClientDeviceCategory.Camera);
        result.RecommendedNetwork.Should().Be(NetworkPurpose.Security);
        result.VendorName.Should().Be("Ubiquiti");
    }

    [Fact]
    public void DetectDeviceType_UniFiProtectCamera_BypassesFingerprint()
    {
        // Arrange - Protect camera that also has fingerprint data
        // Protect API should take priority over fingerprint
        var protectCameras = new ProtectCameraCollection();
        protectCameras.Add("aa:bb:cc:dd:ee:ff", "Test Camera");

        var service = new DeviceTypeDetectionService();
        service.SetProtectCameras(protectCameras);

        var client = new UniFiClientResponse
        {
            Mac = "aa:bb:cc:dd:ee:ff",
            Name = "Test Camera",
            Oui = "Ubiquiti Inc",
            DevCat = 14,  // SmartSensor fingerprint (would normally return different category)
            DevVendor = 999
        };

        // Act
        var result = service.DetectDeviceType(client);

        // Assert - Protect API takes priority (confidence 100)
        result.Category.Should().Be(ClientDeviceCategory.Camera);
        result.ConfidenceScore.Should().Be(100);
        result.Metadata.Should().ContainKey("detection_method");
        result.Metadata!["detection_method"].Should().Be("unifi_protect_api");
    }

    [Fact]
    public void DetectDeviceType_UniFiProtectCamera_BypassesNameOverride()
    {
        // Arrange - Protect camera with name that would normally trigger a different category
        var protectCameras = new ProtectCameraCollection();
        protectCameras.Add("aa:bb:cc:dd:ee:ff", "Smart Plug Camera");  // Contains "Plug"

        var service = new DeviceTypeDetectionService();
        service.SetProtectCameras(protectCameras);

        var client = new UniFiClientResponse
        {
            Mac = "aa:bb:cc:dd:ee:ff",
            Name = "Smart Plug Camera",  // Would normally match plug pattern
            Oui = "Ubiquiti Inc"
        };

        // Act
        var result = service.DetectDeviceType(client);

        // Assert - Protect API takes priority over name-based detection
        result.Category.Should().Be(ClientDeviceCategory.Camera);
        result.RecommendedNetwork.Should().Be(NetworkPurpose.Security);
    }

    [Fact]
    public void DetectDeviceType_NotInProtectCollection_UsesNormalDetection()
    {
        // Arrange - Device NOT in Protect collection, but has camera fingerprint
        var protectCameras = new ProtectCameraCollection();
        protectCameras.Add("11:22:33:44:55:66", "Different Camera");  // Different MAC

        var service = new DeviceTypeDetectionService();
        service.SetProtectCameras(protectCameras);

        var client = new UniFiClientResponse
        {
            Mac = "aa:bb:cc:dd:ee:ff",  // NOT in Protect collection
            Name = "Some Camera",
            Oui = "Ring LLC",  // Cloud camera vendor
            DevCat = 9  // Camera fingerprint
        };

        // Act
        var result = service.DetectDeviceType(client);

        // Assert - Should use normal detection (Ring = CloudCamera)
        result.Category.Should().Be(ClientDeviceCategory.CloudCamera);
        result.RecommendedNetwork.Should().Be(NetworkPurpose.IoT);
    }

    #endregion

    #region Vendor Preservation Tests - Cloud Cameras

    [Theory]
    [InlineData("Ring Doorbell")]
    [InlineData("Ring Camera")]
    [InlineData("Front Door Ring Cam")]
    public void DetectDeviceType_RingCamera_SetsRingVendor(string deviceName)
    {
        // Arrange
        var client = new UniFiClientResponse
        {
            Mac = "aa:bb:cc:dd:ee:ff",
            Name = deviceName
        };

        // Act
        var result = _service.DetectDeviceType(client);

        // Assert
        result.Category.Should().Be(ClientDeviceCategory.CloudCamera);
        result.VendorName.Should().Be("Ring");
    }

    [Theory]
    [InlineData("Nest Doorbell")]
    [InlineData("Nest Cam")]
    [InlineData("Google Camera")]
    public void DetectDeviceType_NestGoogleCamera_SetsGoogleVendor(string deviceName)
    {
        // Arrange
        var client = new UniFiClientResponse
        {
            Mac = "aa:bb:cc:dd:ee:ff",
            Name = deviceName
        };

        // Act
        var result = _service.DetectDeviceType(client);

        // Assert
        result.Category.Should().Be(ClientDeviceCategory.CloudCamera);
        result.VendorName.Should().Be("Google");
    }

    [Theory]
    [InlineData("Wyze Cam")]
    [InlineData("Wyze Doorbell")]
    [InlineData("Wyze Video Camera")]
    public void DetectDeviceType_WyzeCamera_SetsWyzeVendor(string deviceName)
    {
        // Arrange
        var client = new UniFiClientResponse
        {
            Mac = "aa:bb:cc:dd:ee:ff",
            Name = deviceName
        };

        // Act
        var result = _service.DetectDeviceType(client);

        // Assert
        result.Category.Should().Be(ClientDeviceCategory.CloudCamera);
        result.VendorName.Should().Be("Wyze");
    }

    [Theory]
    [InlineData("Blink Camera")]
    [InlineData("Blink Doorbell")]
    public void DetectDeviceType_BlinkCamera_SetsAmazonVendor(string deviceName)
    {
        // Arrange - Blink is owned by Amazon
        var client = new UniFiClientResponse
        {
            Mac = "aa:bb:cc:dd:ee:ff",
            Name = deviceName
        };

        // Act
        var result = _service.DetectDeviceType(client);

        // Assert
        result.Category.Should().Be(ClientDeviceCategory.CloudCamera);
        result.VendorName.Should().Be("Amazon");
    }

    [Theory]
    [InlineData("Arlo Camera")]
    [InlineData("Arlo Doorbell")]
    [InlineData("Arlo Pro Cam")]
    public void DetectDeviceType_ArloCamera_SetsArloVendor(string deviceName)
    {
        // Arrange
        var client = new UniFiClientResponse
        {
            Mac = "aa:bb:cc:dd:ee:ff",
            Name = deviceName
        };

        // Act
        var result = _service.DetectDeviceType(client);

        // Assert
        result.Category.Should().Be(ClientDeviceCategory.CloudCamera);
        result.VendorName.Should().Be("Arlo");
    }

    [Fact]
    public void DetectDeviceType_SimpliSafeVendor_ReturnsCloudCamera()
    {
        // Arrange - SimpliSafe cameras require cloud services
        var client = new UniFiClientResponse
        {
            Mac = "aa:bb:cc:dd:ee:ff",
            Name = "Front Door Camera",
            Oui = "SimpliSafe Inc",
            DevCat = 9 // Camera fingerprint
        };

        // Act
        var result = _service.DetectDeviceType(client);

        // Assert
        result.Category.Should().Be(ClientDeviceCategory.CloudCamera);
    }

    [Fact]
    public void DetectDeviceType_TpLinkCameraVendor_ReturnsCloudCamera()
    {
        // Arrange - TP-Link Tapo/Kasa cameras are cloud-dependent
        var client = new UniFiClientResponse
        {
            Mac = "aa:bb:cc:dd:ee:ff",
            Name = "Garage Camera",
            Oui = "TP-Link Technologies",
            DevCat = 9 // Camera fingerprint
        };

        // Act
        var result = _service.DetectDeviceType(client);

        // Assert
        result.Category.Should().Be(ClientDeviceCategory.CloudCamera);
    }

    [Fact]
    public void DetectDeviceType_CanaryVendor_ReturnsCloudCamera()
    {
        // Arrange - Canary cameras are cloud-dependent
        var client = new UniFiClientResponse
        {
            Mac = "aa:bb:cc:dd:ee:ff",
            Name = "Living Room Camera",
            Oui = "Canary Connect",
            DevCat = 9 // Camera fingerprint
        };

        // Act
        var result = _service.DetectDeviceType(client);

        // Assert
        result.Category.Should().Be(ClientDeviceCategory.CloudCamera);
    }

    [Fact]
    public void DetectDeviceType_CloudCameraVendor_FingerprintVendorTakesPriorityOverOui()
    {
        // Arrange - Fingerprint vendor is SimpliSafe, but OUI is generic manufacturer
        // This simulates a device where UniFi fingerprint correctly identifies vendor
        // but MAC OUI shows the actual chip manufacturer
        var client = new UniFiClientResponse
        {
            Mac = "aa:bb:cc:dd:ee:ff",
            Name = "Front Door",  // No camera keyword - relies on fingerprint
            Oui = "Realtek Semiconductor",  // Generic chip manufacturer
            DevCat = 9,  // Camera fingerprint
            DevVendor = 999  // Would resolve to SimpliSafe in fingerprint DB
        };

        // Act
        var result = _service.DetectDeviceType(client);

        // Assert - Without fingerprint DB, falls back to OUI which isn't a cloud vendor
        // So this should be Camera, not CloudCamera (proving OUI fallback works)
        result.Category.Should().Be(ClientDeviceCategory.Camera);
    }

    [Fact]
    public void DetectDeviceType_NonCloudCameraVendor_RemainsCamera()
    {
        // Arrange - Generic camera vendor that is NOT a cloud camera
        var client = new UniFiClientResponse
        {
            Mac = "aa:bb:cc:dd:ee:ff",
            Name = "Security Camera",
            Oui = "Hikvision",  // Local NVR camera, not cloud
            DevCat = 9  // Camera fingerprint
        };

        // Act
        var result = _service.DetectDeviceType(client);

        // Assert - Should remain Camera (self-hosted), not CloudCamera
        result.Category.Should().Be(ClientDeviceCategory.Camera);
        result.RecommendedNetwork.Should().Be(NetworkPurpose.Security);
    }

    [Fact]
    public void DetectDeviceType_CameraNameWithCloudOui_BecomesCloudCamera()
    {
        // Arrange - Name indicates camera, OUI indicates cloud vendor
        var client = new UniFiClientResponse
        {
            Mac = "aa:bb:cc:dd:ee:ff",
            Name = "[Cam] Driveway",  // Camera name tag
            Oui = "Ring LLC"  // Cloud vendor
        };

        // Act
        var result = _service.DetectDeviceType(client);

        // Assert - Should be CloudCamera due to Ring OUI
        result.Category.Should().Be(ClientDeviceCategory.CloudCamera);
        result.RecommendedNetwork.Should().Be(NetworkPurpose.IoT);
    }

    [Fact]
    public void DetectDeviceType_CameraNameWithNonCloudOui_RemainsCamera()
    {
        // Arrange - Name indicates camera, OUI is NOT a cloud vendor
        var client = new UniFiClientResponse
        {
            Mac = "aa:bb:cc:dd:ee:ff",
            Name = "[Cam] Backyard",  // Camera name tag
            Oui = "Axis Communications"  // Professional/self-hosted camera
        };

        // Act
        var result = _service.DetectDeviceType(client);

        // Assert - Should remain Camera (for Security VLAN)
        result.Category.Should().Be(ClientDeviceCategory.Camera);
        result.RecommendedNetwork.Should().Be(NetworkPurpose.Security);
    }

    [Theory]
    [InlineData("Springfield Security")]  // Contains "ring" as substring
    [InlineData("Honest Labs")]           // Contains "nest" as substring
    [InlineData("Blinking Lights Co")]    // Contains "blink" as substring
    [InlineData("Carlo Industries")]      // Contains "arlo" as substring
    public void DetectDeviceType_VendorWithCloudKeywordSubstring_DoesNotFalsePositive(string oui)
    {
        // Arrange - OUI contains cloud vendor as substring but is NOT a cloud vendor
        var client = new UniFiClientResponse
        {
            Mac = "aa:bb:cc:dd:ee:ff",
            Name = "Front Door Camera",
            Oui = oui,
            DevCat = 9  // Camera fingerprint
        };

        // Act
        var result = _service.DetectDeviceType(client);

        // Assert - Should remain Camera, NOT CloudCamera (word boundary prevents false positive)
        result.Category.Should().Be(ClientDeviceCategory.Camera);
        result.RecommendedNetwork.Should().Be(NetworkPurpose.Security);
    }

    [Theory]
    // Full vendor names with suffixes
    [InlineData("Ring Inc")]
    [InlineData("Ring LLC")]
    [InlineData("Nest Labs")]
    [InlineData("Google Inc")]
    [InlineData("Wyze Labs")]
    [InlineData("Blink Home")]
    [InlineData("Arlo Technologies")]
    [InlineData("SimpliSafe Inc")]
    [InlineData("TP-Link Technologies")]
    [InlineData("Canary Connect")]
    // Bare vendor names (word boundary should still match)
    [InlineData("Ring")]
    [InlineData("Nest")]
    [InlineData("Google")]
    [InlineData("Wyze")]
    [InlineData("Blink")]
    [InlineData("Arlo")]
    [InlineData("SimpliSafe")]
    [InlineData("TP-Link")]
    [InlineData("Canary")]
    public void DetectDeviceType_ActualCloudVendor_BecomesCloudCamera(string oui)
    {
        // Arrange - Actual cloud camera vendor OUI
        var client = new UniFiClientResponse
        {
            Mac = "aa:bb:cc:dd:ee:ff",
            Name = "Front Door Camera",
            Oui = oui,
            DevCat = 9  // Camera fingerprint
        };

        // Act
        var result = _service.DetectDeviceType(client);

        // Assert - Should be CloudCamera
        result.Category.Should().Be(ClientDeviceCategory.CloudCamera);
        result.RecommendedNetwork.Should().Be(NetworkPurpose.IoT);
    }

    #endregion

    #region Vendor Preservation Tests - Thermostats

    [Theory]
    [InlineData("Ecobee")]
    [InlineData("Living Room Ecobee")]
    [InlineData("Ecobee Smart Thermostat")]
    public void DetectDeviceType_Ecobee_SetsEcobeeVendor(string deviceName)
    {
        // Arrange
        var client = new UniFiClientResponse
        {
            Mac = "aa:bb:cc:dd:ee:ff",
            Name = deviceName
        };

        // Act
        var result = _service.DetectDeviceType(client);

        // Assert
        result.Category.Should().Be(ClientDeviceCategory.SmartThermostat);
        result.VendorName.Should().Be("Ecobee");
    }

    [Theory]
    [InlineData("Nest Thermostat")]
    [InlineData("Nest Learning Thermostat")]
    public void DetectDeviceType_NestThermostat_SetsGoogleVendor(string deviceName)
    {
        // Arrange
        var client = new UniFiClientResponse
        {
            Mac = "aa:bb:cc:dd:ee:ff",
            Name = deviceName
        };

        // Act
        var result = _service.DetectDeviceType(client);

        // Assert
        result.Category.Should().Be(ClientDeviceCategory.SmartThermostat);
        result.VendorName.Should().Be("Google");
    }

    [Fact]
    public void DetectDeviceType_GenericThermostat_PreservesOuiVendor()
    {
        // Arrange - Generic thermostat should preserve OUI vendor
        var client = new UniFiClientResponse
        {
            Mac = "aa:bb:cc:dd:ee:ff",
            Name = "Living Room Thermostat",
            Oui = "Honeywell Inc"
        };

        // Act
        var result = _service.DetectDeviceType(client);

        // Assert
        result.Category.Should().Be(ClientDeviceCategory.SmartThermostat);
        result.VendorName.Should().Be("Honeywell Inc");
    }

    #endregion

    #region Vendor Preservation Tests - Generic Matches

    [Fact]
    public void DetectDeviceType_GenericPlug_PreservesOuiVendor()
    {
        // Arrange
        var client = new UniFiClientResponse
        {
            Mac = "aa:bb:cc:dd:ee:ff",
            Name = "Living Room Plug",
            Oui = "TP-Link Technologies"
        };

        // Act
        var result = _service.DetectDeviceType(client);

        // Assert
        result.Category.Should().Be(ClientDeviceCategory.SmartPlug);
        result.VendorName.Should().Be("TP-Link Technologies");
    }

    [Fact]
    public void DetectDeviceType_GenericBulb_PreservesOuiVendor()
    {
        // Arrange
        var client = new UniFiClientResponse
        {
            Mac = "aa:bb:cc:dd:ee:ff",
            Name = "Bedroom Bulb",
            Oui = "Philips Lighting"
        };

        // Act
        var result = _service.DetectDeviceType(client);

        // Assert
        result.Category.Should().Be(ClientDeviceCategory.SmartLighting);
        result.VendorName.Should().Be("Philips Lighting");
    }

    [Fact]
    public void DetectDeviceType_GenericPrinter_PreservesOuiVendor()
    {
        // Arrange
        var client = new UniFiClientResponse
        {
            Mac = "aa:bb:cc:dd:ee:ff",
            Name = "Office Printer",
            Oui = "HP Inc"
        };

        // Act
        var result = _service.DetectDeviceType(client);

        // Assert
        result.Category.Should().Be(ClientDeviceCategory.Printer);
        result.VendorName.Should().Be("HP Inc");
    }

    [Fact]
    public void DetectDeviceType_GenericCamera_PreservesOuiVendor()
    {
        // Arrange - Generic camera (not a cloud vendor) should preserve OUI
        var client = new UniFiClientResponse
        {
            Mac = "aa:bb:cc:dd:ee:ff",
            Name = "Garage Camera",
            Oui = "Reolink Innovation"
        };

        // Act
        var result = _service.DetectDeviceType(client);

        // Assert
        result.Category.Should().Be(ClientDeviceCategory.Camera);  // Self-hosted, not CloudCamera
        result.VendorName.Should().Be("Reolink Innovation");
    }

    [Fact]
    public void DetectDeviceType_GenericCamera_WithCloudOui_SetsCloudCameraAndPreservesVendor()
    {
        // Arrange - Camera with cloud vendor OUI
        var client = new UniFiClientResponse
        {
            Mac = "aa:bb:cc:dd:ee:ff",
            Name = "Front Door Camera",  // Generic name without specific vendor
            Oui = "Ring LLC"  // Cloud vendor OUI
        };

        // Act
        var result = _service.DetectDeviceType(client);

        // Assert
        result.Category.Should().Be(ClientDeviceCategory.CloudCamera);
        result.VendorName.Should().Be("Ring LLC");
    }

    #endregion

    #region SimpliSafe Device Tests

    [Theory]
    [InlineData("SimpliSafe Basestation")]
    [InlineData("SimpliSafe Base Station")]
    [InlineData("[Security] SimpliSafe Basestation")]
    public void DetectDeviceType_SimpliSafeBasestation_ReturnsCloudSecuritySystem(string deviceName)
    {
        // Arrange - SimpliSafe basestations are cloud-dependent security hubs
        var client = new UniFiClientResponse
        {
            Mac = "aa:bb:cc:dd:ee:ff",
            Name = deviceName
        };

        // Act
        var result = _service.DetectDeviceType(client);

        // Assert - Should be CloudSecuritySystem (new category for cloud-dependent security systems)
        result.Category.Should().Be(ClientDeviceCategory.CloudSecuritySystem);
        result.VendorName.Should().Be("SimpliSafe");
        result.RecommendedNetwork.Should().Be(NetworkPurpose.IoT);
    }

    [Theory]
    [InlineData("SimpliSafe Camera")]
    [InlineData("SimpliSafe Outdoor Camera")]
    [InlineData("[Cam] SimpliSafe Indoor")]
    public void DetectDeviceType_SimpliSafeCameraByName_ReturnsCloudCamera(string deviceName)
    {
        // Arrange - SimpliSafe cameras are cloud cameras requiring internet
        var client = new UniFiClientResponse
        {
            Mac = "aa:bb:cc:dd:ee:ff",
            Name = deviceName
        };

        // Act
        var result = _service.DetectDeviceType(client);

        // Assert - Should be CloudCamera
        result.Category.Should().Be(ClientDeviceCategory.CloudCamera);
        result.VendorName.Should().Be("SimpliSafe");
        result.RecommendedNetwork.Should().Be(NetworkPurpose.IoT);
    }

    [Fact]
    public void DetectDeviceType_SimpliSafeVendorWithCameraFingerprint_ReturnsCloudCamera()
    {
        // Arrange - SimpliSafe OUI with camera fingerprint
        var client = new UniFiClientResponse
        {
            Mac = "aa:bb:cc:dd:ee:ff",
            Name = "Front Door",
            Oui = "SimpliSafe Inc",
            DevCat = 9 // Camera fingerprint
        };

        // Act
        var result = _service.DetectDeviceType(client);

        // Assert
        result.Category.Should().Be(ClientDeviceCategory.CloudCamera);
    }

    [Theory]
    [InlineData("Basestation")]
    [InlineData("Base Station")]
    [InlineData("[Security] Basestation")]
    public void DetectDeviceType_SimpliSafeOuiWithBasestationName_ReturnsCloudSecuritySystem(string deviceName)
    {
        // Arrange - SimpliSafe OUI with "Basestation" in name but NOT "SimpliSafe"
        // This tests OUI-based vendor detection with device name disambiguation
        var client = new UniFiClientResponse
        {
            Mac = "aa:bb:cc:dd:ee:ff",
            Name = deviceName,
            Oui = "SimpliSafe Inc"
        };

        // Act
        var result = _service.DetectDeviceType(client);

        // Assert - Should be CloudSecuritySystem based on OUI + basestation keyword
        result.Category.Should().Be(ClientDeviceCategory.CloudSecuritySystem);
        result.VendorName.Should().Contain("SimpliSafe");  // OUI returns "SimpliSafe Inc"
        result.RecommendedNetwork.Should().Be(NetworkPurpose.IoT);
    }

    #endregion

    #region Camera Name with Cloud Vendor Tests

    [Theory]
    [InlineData("Front Yard Cameras", "Ring LLC", ClientDeviceCategory.CloudCamera)]
    [InlineData("Back Yard Camera", "Wyze Labs", ClientDeviceCategory.CloudCamera)]
    [InlineData("Driveway Cam", "Nest Labs", ClientDeviceCategory.CloudCamera)]
    [InlineData("Porch Camera", "Arlo Technologies", ClientDeviceCategory.CloudCamera)]
    [InlineData("Garage Cam", "Reolink Innovation", ClientDeviceCategory.Camera)]  // Non-cloud vendor
    [InlineData("Office Camera", "Hikvision", ClientDeviceCategory.Camera)]  // Non-cloud vendor
    public void DetectDeviceType_GenericCameraNameWithVendorOui_ClassifiesCorrectly(
        string deviceName, string oui, ClientDeviceCategory expectedCategory)
    {
        // Arrange - Generic camera name (no vendor keyword) with various OUIs
        // Cloud vendors should become CloudCamera, others remain Camera
        var client = new UniFiClientResponse
        {
            Mac = "aa:bb:cc:dd:ee:ff",
            Name = deviceName,
            Oui = oui
        };

        // Act
        var result = _service.DetectDeviceType(client);

        // Assert
        result.Category.Should().Be(expectedCategory);
    }

    [Theory]
    [InlineData("Ring Front Porch Camera", "Unknown Manufacturer")]  // Vendor in name with camera keyword
    [InlineData("Wyze Garage Cam", "Generic Corp")]
    [InlineData("Nest Driveway Camera", "Some Electronics")]
    [InlineData("Blink Doorbell", "Random Inc")]
    [InlineData("Arlo Backyard Cam", "Other Vendor")]
    public void DetectDeviceType_CloudVendorInName_BecomesCloudCamera(string deviceName, string oui)
    {
        // Arrange - Vendor keyword is in NAME, not OUI
        // Should detect as CloudCamera based on name alone
        var client = new UniFiClientResponse
        {
            Mac = "aa:bb:cc:dd:ee:ff",
            Name = deviceName,
            Oui = oui  // Unrelated OUI
        };

        // Act
        var result = _service.DetectDeviceType(client);

        // Assert - Should still be CloudCamera because vendor is in the name
        result.Category.Should().Be(ClientDeviceCategory.CloudCamera);
    }

    [Fact]
    public void DetectDeviceType_SimpliSafeInName_WithCameraKeyword_ReturnsCloudCamera()
    {
        // Arrange - SimpliSafe keyword in name should trigger cloud camera detection
        var client = new UniFiClientResponse
        {
            Mac = "aa:bb:cc:dd:ee:ff",
            Name = "SimpliSafe Front Door Cam",
            Oui = "Unknown Manufacturer"  // No SimpliSafe OUI
        };

        // Act
        var result = _service.DetectDeviceType(client);

        // Assert
        result.Category.Should().Be(ClientDeviceCategory.CloudCamera);
        result.VendorName.Should().Be("SimpliSafe");
    }

    #endregion

    #region Misfingerprinted Camera Override Tests

    [Theory]
    [InlineData("A Camera", 1)]       // Desktop fingerprint
    [InlineData("Front Camera", 1)]   // Desktop fingerprint (same as above, testing different name)
    [InlineData("Garage Cam", 6)]     // Smartphone fingerprint
    [InlineData("Back Yard Camera", 30)]  // Tablet fingerprint
    public void DetectDeviceType_CameraNameWithWrongFingerprint_OverridesToCamera(string deviceName, int devCat)
    {
        // Arrange - Device has a clear camera name but wrong UniFi fingerprint
        var client = new UniFiClientResponse
        {
            Mac = "aa:bb:cc:dd:ee:ff",
            Name = deviceName,
            DevCat = devCat,  // Wrong fingerprint (Desktop/Laptop/Phone/Tablet)
            Oui = "Generic Manufacturer"
        };

        // Act
        var result = _service.DetectDeviceType(client);

        // Assert - Name should override the wrong fingerprint
        result.Category.Should().Be(ClientDeviceCategory.Camera);
        result.Source.Should().Be(DetectionSource.DeviceName);
    }

    [Theory]
    [InlineData("Ring Doorbell", 1)]   // Desktop fingerprint but Ring vendor in name
    [InlineData("Wyze Cam v3", 1)]     // Desktop fingerprint but Wyze vendor in name
    [InlineData("Nest Camera", 6)]     // Smartphone fingerprint but Nest vendor in name
    public void DetectDeviceType_CloudCameraNameWithWrongFingerprint_OverridesToCloudCamera(string deviceName, int devCat)
    {
        // Arrange - Device has cloud vendor + camera in name but wrong fingerprint
        var client = new UniFiClientResponse
        {
            Mac = "aa:bb:cc:dd:ee:ff",
            Name = deviceName,
            DevCat = devCat,  // Wrong fingerprint
            Oui = "Generic Manufacturer"
        };

        // Act
        var result = _service.DetectDeviceType(client);

        // Assert - Name should override to CloudCamera (cloud vendor in name)
        result.Category.Should().Be(ClientDeviceCategory.CloudCamera);
    }

    [Fact]
    public void DetectDeviceType_CameraNameWithCameraFingerprint_KeepsCamera()
    {
        // Arrange - Device has camera name AND camera fingerprint - don't double-process
        var client = new UniFiClientResponse
        {
            Mac = "aa:bb:cc:dd:ee:ff",
            Name = "Front Porch Camera",
            DevCat = 9,  // Camera fingerprint
            Oui = "Hikvision"
        };

        // Act
        var result = _service.DetectDeviceType(client);

        // Assert - Should stay Camera (fingerprint is correct)
        result.Category.Should().Be(ClientDeviceCategory.Camera);
    }

    #endregion

    #region CloudSecuritySystem Category Extension Tests

    [Fact]
    public void IsIoT_CloudSecuritySystem_ReturnsTrue()
    {
        // Act & Assert - CloudSecuritySystem should be IoT (needs internet)
        ClientDeviceCategory.CloudSecuritySystem.IsIoT().Should().BeTrue();
    }

    [Fact]
    public void IsSurveillance_CloudSecuritySystem_ReturnsTrue()
    {
        // Act & Assert - CloudSecuritySystem is a surveillance/security device
        ClientDeviceCategory.CloudSecuritySystem.IsSurveillance().Should().BeTrue();
    }

    [Fact]
    public void IsHighRiskIoT_CloudSecuritySystem_ReturnsTrue()
    {
        // Act & Assert - CloudSecuritySystem is high-risk
        ClientDeviceCategory.CloudSecuritySystem.IsHighRiskIoT().Should().BeTrue();
    }

    [Theory]
    [InlineData(ClientDeviceCategory.CloudCamera, true)]
    [InlineData(ClientDeviceCategory.CloudSecuritySystem, true)]
    [InlineData(ClientDeviceCategory.Camera, false)]
    [InlineData(ClientDeviceCategory.SecuritySystem, false)]
    [InlineData(ClientDeviceCategory.SmartTV, false)]
    public void IsCloudSurveillance_ReturnsCorrectValue(ClientDeviceCategory category, bool expected)
    {
        // Act & Assert - Only cloud-based surveillance devices return true
        category.IsCloudSurveillance().Should().Be(expected);
    }

    #endregion

    #region Nest Protect Smoke Alarm Tests (NOT a camera)

    [Theory]
    [InlineData("Nest Protect")]
    [InlineData("Nest Protect Smoke Alarm")]
    [InlineData("Hallway Nest Protect")]
    [InlineData("[IoT] Nest Protect")]
    public void DetectDeviceType_NestProtectSmokeAlarm_DoesNotReturnCamera(string deviceName)
    {
        // Arrange - Nest Protect is a smoke alarm, NOT a camera
        // It should NOT be detected as Camera just because it contains "Protect"
        var client = new UniFiClientResponse
        {
            Mac = "aa:bb:cc:dd:ee:ff",
            Name = deviceName,
            Oui = "Nest Labs Inc."
        };

        // Act
        var result = _service.DetectDeviceType(client);

        // Assert - Should NOT be Camera or CloudCamera
        result.Category.Should().NotBe(ClientDeviceCategory.Camera);
        result.Category.Should().NotBe(ClientDeviceCategory.CloudCamera);
    }

    [Fact]
    public void DetectDeviceType_NestProtect_WithIoTFingerprint_ReturnsIoT()
    {
        // Arrange - Nest Protect with IoT fingerprint (as seen in real-world)
        var client = new UniFiClientResponse
        {
            Mac = "98:17:3c:1a:df:54",
            Name = "Nest Protect",
            Oui = "Nest Labs Inc.",
            DevCat = 51 // IoT fingerprint
        };

        // Act
        var result = _service.DetectDeviceType(client);

        // Assert - Should be IoT, not Camera
        result.Category.Should().NotBe(ClientDeviceCategory.Camera);
        result.Category.Should().NotBe(ClientDeviceCategory.CloudCamera);
    }

    [Fact]
    public void DetectDeviceType_NestProtect_WithoutFingerprint_DoesNotReturnCamera()
    {
        // Arrange - Nest Protect without fingerprint data
        // Should NOT fall back to Camera just because of "Protect" keyword
        var client = new UniFiClientResponse
        {
            Mac = "98:17:3c:1a:df:54",
            Name = "Nest Protect",
            Oui = "Nest Labs Inc."
            // No DevCat - no fingerprint
        };

        // Act
        var result = _service.DetectDeviceType(client);

        // Assert - Should NOT be Camera or CloudCamera
        result.Category.Should().NotBe(ClientDeviceCategory.Camera);
        result.Category.Should().NotBe(ClientDeviceCategory.CloudCamera);
    }

    [Fact]
    public void DetectDeviceType_ProtectKeyword_NotInCameraPatterns()
    {
        // Arrange - Device named just "Protect" without UniFi Protect API
        // Should NOT be detected as Camera
        var client = new UniFiClientResponse
        {
            Mac = "aa:bb:cc:dd:ee:ff",
            Name = "Protect",
            Oui = "Generic Manufacturer"
        };

        // Act
        var result = _service.DetectDeviceType(client);

        // Assert - "Protect" alone should NOT trigger Camera detection
        result.Category.Should().NotBe(ClientDeviceCategory.Camera);
        result.Category.Should().NotBe(ClientDeviceCategory.CloudCamera);
    }

    [Fact]
    public void DetectDeviceType_UniFiProtectCamera_StillDetectedViaAPI()
    {
        // Arrange - Real UniFi Protect camera should still be detected via Protect API
        var protectCameras = new ProtectCameraCollection();
        protectCameras.Add("aa:bb:cc:dd:ee:ff", "G5 Turret Ultra");

        var service = new DeviceTypeDetectionService();
        service.SetProtectCameras(protectCameras);

        var client = new UniFiClientResponse
        {
            Mac = "aa:bb:cc:dd:ee:ff",
            Name = "G5 Turret Ultra",
            Oui = "Ubiquiti Inc"
        };

        // Act
        var result = service.DetectDeviceType(client);

        // Assert - UniFi Protect cameras are detected via API, not name pattern
        result.Category.Should().Be(ClientDeviceCategory.Camera);
        result.ConfidenceScore.Should().Be(100);
        result.Metadata.Should().ContainKey("detection_method");
        result.Metadata!["detection_method"].Should().Be("unifi_protect_api");
    }

    [Fact]
    public void DetectDeviceType_UniFiProtect_AIKey_StillDetectedViaAPI()
    {
        // Arrange - UniFi Protect AI Key should be detected via Protect API
        var protectCameras = new ProtectCameraCollection();
        protectCameras.Add("8c:ed:e1:12:3f:80", "SecurityAIKey");

        var service = new DeviceTypeDetectionService();
        service.SetProtectCameras(protectCameras);

        var client = new UniFiClientResponse
        {
            Mac = "8c:ed:e1:12:3f:80",
            Name = "[Security] AI Key",
            Oui = "Ubiquiti Inc"
        };

        // Act
        var result = service.DetectDeviceType(client);

        // Assert - Should be detected as Camera via Protect API
        result.Category.Should().Be(ClientDeviceCategory.Camera);
        result.ConfidenceScore.Should().Be(100);
        result.Metadata!["detection_method"].Should().Be("unifi_protect_api");
    }

    #endregion
}
