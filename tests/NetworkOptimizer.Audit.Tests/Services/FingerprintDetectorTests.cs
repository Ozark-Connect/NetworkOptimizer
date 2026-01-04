using FluentAssertions;
using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.Audit.Services.Detectors;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.UniFi.Models;
using Xunit;

namespace NetworkOptimizer.Audit.Tests.Services;

/// <summary>
/// Tests for FingerprintDetector - device detection from UniFi fingerprint data
/// </summary>
public class FingerprintDetectorTests
{
    private readonly FingerprintDetector _detector;

    public FingerprintDetectorTests()
    {
        _detector = new FingerprintDetector();
    }

    #region Constructor Tests

    [Fact]
    public void Constructor_WithoutDatabase_CreatesInstance()
    {
        var detector = new FingerprintDetector();
        detector.Should().NotBeNull();
    }

    [Fact]
    public void Constructor_WithDatabase_CreatesInstance()
    {
        var database = new UniFiFingerprintDatabase();
        var detector = new FingerprintDetector(database);
        detector.Should().NotBeNull();
    }

    #endregion

    #region Camera Detection Tests (dev_cat / dev_type_id)

    [Theory]
    [InlineData(9)]   // IP Network Camera
    [InlineData(57)]  // Smart Security Camera
    [InlineData(106)] // Camera
    [InlineData(124)] // Network Video Recorder
    [InlineData(147)] // Doorbell Camera
    [InlineData(161)] // Video Doorbell
    public void Detect_CameraDevCat_ReturnsCamera(int devCat)
    {
        var client = new UniFiClientResponse { DevCat = devCat };

        var result = _detector.Detect(client);

        result.Category.Should().Be(ClientDeviceCategory.Camera);
        result.Source.Should().Be(DetectionSource.UniFiFingerprint);
        result.ConfidenceScore.Should().BeGreaterThan(90);
    }

    [Theory]
    [InlineData(116)] // Surveillance System
    [InlineData(111)] // Security Panel
    public void Detect_SecuritySystemDevCat_ReturnsSecuritySystem(int devCat)
    {
        var client = new UniFiClientResponse { DevCat = devCat };

        var result = _detector.Detect(client);

        result.Category.Should().Be(ClientDeviceCategory.SecuritySystem);
        result.Source.Should().Be(DetectionSource.UniFiFingerprint);
    }

    #endregion

    #region Smart Lighting Detection Tests

    [Theory]
    [InlineData(35)]  // Wireless Lighting
    [InlineData(53)]  // Smart Lighting Device
    [InlineData(179)] // LED Lighting
    [InlineData(184)] // Smart Light Strip
    public void Detect_SmartLightingDevCat_ReturnsSmartLighting(int devCat)
    {
        var client = new UniFiClientResponse { DevCat = devCat };

        var result = _detector.Detect(client);

        result.Category.Should().Be(ClientDeviceCategory.SmartLighting);
    }

    #endregion

    #region Smart Plug Detection Tests

    [Theory]
    [InlineData(42)]  // Smart Plug
    [InlineData(97)]  // Smart Power Strip
    [InlineData(153)] // Smart Socket
    public void Detect_SmartPlugDevCat_ReturnsSmartPlug(int devCat)
    {
        var client = new UniFiClientResponse { DevCat = devCat };

        var result = _detector.Detect(client);

        result.Category.Should().Be(ClientDeviceCategory.SmartPlug);
    }

    #endregion

    #region Thermostat Detection Tests

    [Theory]
    [InlineData(63)] // Smart Thermostat
    [InlineData(70)] // Smart Heating Device
    public void Detect_ThermostatDevCat_ReturnsSmartThermostat(int devCat)
    {
        var client = new UniFiClientResponse { DevCat = devCat };

        var result = _detector.Detect(client);

        result.Category.Should().Be(ClientDeviceCategory.SmartThermostat);
    }

    #endregion

    #region Smart Lock Detection Tests

    [Theory]
    [InlineData(133)] // Door Lock
    [InlineData(125)] // Touch Screen Deadbolt
    public void Detect_SmartLockDevCat_ReturnsSmartLock(int devCat)
    {
        var client = new UniFiClientResponse { DevCat = devCat };

        var result = _detector.Detect(client);

        result.Category.Should().Be(ClientDeviceCategory.SmartLock);
    }

    #endregion

    #region Smart Sensor Detection Tests

    [Theory]
    [InlineData(100)] // Weather Station
    [InlineData(148)] // Air Quality Monitor
    [InlineData(234)] // Weather Monitor
    [InlineData(139)] // Water Monitor
    [InlineData(109)] // Sleep Monitor
    [InlineData(45)]  // Wearable devices
    [InlineData(36)]  // Smart Watch
    public void Detect_SmartSensorDevCat_ReturnsSmartSensor(int devCat)
    {
        var client = new UniFiClientResponse { DevCat = devCat };

        var result = _detector.Detect(client);

        result.Category.Should().Be(ClientDeviceCategory.SmartSensor);
    }

    #endregion

    #region Smart Appliance Detection Tests

    [Theory]
    [InlineData(48)]  // Intelligent Home Appliances
    [InlineData(131)] // Washing Machine
    [InlineData(140)] // Dishwasher
    [InlineData(118)] // Dryer
    [InlineData(92)]  // Air Purifier
    [InlineData(149)] // Smart Kettle
    [InlineData(71)]  // Air Conditioner
    public void Detect_SmartApplianceDevCat_ReturnsSmartAppliance(int devCat)
    {
        var client = new UniFiClientResponse { DevCat = devCat };

        var result = _detector.Detect(client);

        result.Category.Should().Be(ClientDeviceCategory.SmartAppliance);
    }

    #endregion

    #region Smart Hub Detection Tests

    [Theory]
    [InlineData(144)]  // Smart Hub
    [InlineData(93)]   // Home Automation
    [InlineData(154)]  // Smart Bridge
    [InlineData(4904)] // IKEA Dirigera Gateway (user override)
    public void Detect_SmartHubDevCat_ReturnsSmartHub(int devCat)
    {
        var client = new UniFiClientResponse { DevCat = devCat };

        var result = _detector.Detect(client);

        result.Category.Should().Be(ClientDeviceCategory.SmartHub);
    }

    #endregion

    #region Robotic Vacuum Detection Tests

    [Theory]
    [InlineData(41)] // Robotic Vacuums
    [InlineData(65)] // Smart Cleaning Device
    public void Detect_RoboticVacuumDevCat_ReturnsRoboticVacuum(int devCat)
    {
        var client = new UniFiClientResponse { DevCat = devCat };

        var result = _detector.Detect(client);

        result.Category.Should().Be(ClientDeviceCategory.RoboticVacuum);
    }

    #endregion

    #region Smart TV Detection Tests

    [Theory]
    [InlineData(31)] // SmartTV
    [InlineData(47)] // Smart TV & Set-top box
    [InlineData(50)] // Smart TV & Set-top box
    public void Detect_SmartTVDevCat_ReturnsSmartTV(int devCat)
    {
        var client = new UniFiClientResponse { DevCat = devCat };

        var result = _detector.Detect(client);

        result.Category.Should().Be(ClientDeviceCategory.SmartTV);
    }

    #endregion

    #region Streaming Device Detection Tests

    [Theory]
    [InlineData(5)]    // IPTV
    [InlineData(238)]  // Media Player
    [InlineData(242)]  // Streaming Media Device
    [InlineData(186)]  // IPTV Set Top Box
    [InlineData(4405)] // Apple TV (user override)
    public void Detect_StreamingDeviceDevCat_ReturnsStreamingDevice(int devCat)
    {
        var client = new UniFiClientResponse { DevCat = devCat };

        var result = _detector.Detect(client);

        result.Category.Should().Be(ClientDeviceCategory.StreamingDevice);
    }

    #endregion

    #region Smart Speaker Detection Tests

    [Theory]
    [InlineData(37)]  // Smart Speaker
    [InlineData(52)]  // Smart Audio Device
    [InlineData(170)] // Wifi Speaker
    public void Detect_SmartSpeakerDevCat_ReturnsSmartSpeaker(int devCat)
    {
        var client = new UniFiClientResponse { DevCat = devCat };

        var result = _detector.Detect(client);

        result.Category.Should().Be(ClientDeviceCategory.SmartSpeaker);
    }

    #endregion

    #region Media Player Detection Tests

    [Theory]
    [InlineData(20)]  // Multimedia Device
    [InlineData(69)]  // AV Receiver
    [InlineData(73)]  // Soundbar
    [InlineData(96)]  // Audio Streamer
    [InlineData(132)] // Music Server
    [InlineData(152)] // Blu Ray Player
    public void Detect_MediaPlayerDevCat_ReturnsMediaPlayer(int devCat)
    {
        var client = new UniFiClientResponse { DevCat = devCat };

        var result = _detector.Detect(client);

        result.Category.Should().Be(ClientDeviceCategory.MediaPlayer);
    }

    #endregion

    #region Game Console Detection Tests

    [Fact]
    public void Detect_GameConsoleDevCat_ReturnsGameConsole()
    {
        var client = new UniFiClientResponse { DevCat = 17 }; // Game Console

        var result = _detector.Detect(client);

        result.Category.Should().Be(ClientDeviceCategory.GameConsole);
    }

    #endregion

    #region Computer Detection Tests

    [Fact]
    public void Detect_LaptopDevCat_ReturnsLaptop()
    {
        var client = new UniFiClientResponse { DevCat = 1 }; // Desktop/Laptop

        var result = _detector.Detect(client);

        result.Category.Should().Be(ClientDeviceCategory.Laptop);
    }

    [Theory]
    [InlineData(46)] // Computer
    [InlineData(28)] // Workstation
    [InlineData(25)] // Thin Client
    public void Detect_DesktopDevCat_ReturnsDesktop(int devCat)
    {
        var client = new UniFiClientResponse { DevCat = devCat };

        var result = _detector.Detect(client);

        result.Category.Should().Be(ClientDeviceCategory.Desktop);
    }

    [Fact]
    public void Detect_ServerDevCat_ReturnsServer()
    {
        var client = new UniFiClientResponse { DevCat = 56 }; // Server

        var result = _detector.Detect(client);

        result.Category.Should().Be(ClientDeviceCategory.Server);
    }

    #endregion

    #region Mobile Device Detection Tests

    [Theory]
    [InlineData(6)]  // Smartphone
    [InlineData(44)] // Handheld
    [InlineData(29)] // Apple iOS Device
    [InlineData(32)] // Android Device
    public void Detect_SmartphoneDevCat_ReturnsSmartphone(int devCat)
    {
        var client = new UniFiClientResponse { DevCat = devCat };

        var result = _detector.Detect(client);

        result.Category.Should().Be(ClientDeviceCategory.Smartphone);
    }

    [Fact]
    public void Detect_TabletDevCat_ReturnsTablet()
    {
        var client = new UniFiClientResponse { DevCat = 30 }; // Tablet

        var result = _detector.Detect(client);

        result.Category.Should().Be(ClientDeviceCategory.Tablet);
    }

    #endregion

    #region NAS Detection Tests

    [Theory]
    [InlineData(18)] // NAS
    [InlineData(91)] // Network Storage
    public void Detect_NasDevCat_ReturnsNas(int devCat)
    {
        var client = new UniFiClientResponse { DevCat = devCat };

        var result = _detector.Detect(client);

        result.Category.Should().Be(ClientDeviceCategory.NAS);
    }

    #endregion

    #region VoIP Detection Tests

    [Theory]
    [InlineData(3)]  // VoIP Phone (specific)
    [InlineData(10)] // VoIP Gateway
    [InlineData(27)] // Video Phone
    public void Detect_VoIPDevCat_ReturnsVoIP(int devCat)
    {
        var client = new UniFiClientResponse { DevCat = devCat };

        var result = _detector.Detect(client);

        result.Category.Should().Be(ClientDeviceCategory.VoIP);
    }

    [Fact]
    public void Detect_GenericPhoneDevCat26_ReturnsSmartphone()
    {
        // DevCat 26 is "Phone" - generic category more likely to be smartphone than VoIP
        // VoIP phones have specific dev_cat values (3, 10, 27)
        var client = new UniFiClientResponse { DevCat = 26 };

        var result = _detector.Detect(client);

        result.Category.Should().Be(ClientDeviceCategory.Smartphone);
    }

    #endregion

    #region IoT Generic Detection Tests

    [Theory]
    [InlineData(51)]  // Smart Device
    [InlineData(66)]  // IoT Device
    [InlineData(64)]  // Smart Garden Device
    [InlineData(60)]  // Alarm System
    [InlineData(120)] // Garage Opener
    [InlineData(83)]  // Garage Door
    [InlineData(77)]  // Sprinkler Controller
    [InlineData(130)] // Irrigation Controller
    public void Detect_IoTGenericDevCat_ReturnsIoTGeneric(int devCat)
    {
        var client = new UniFiClientResponse { DevCat = devCat };

        var result = _detector.Detect(client);

        result.Category.Should().Be(ClientDeviceCategory.IoTGeneric);
    }

    #endregion

    #region Network Equipment Detection Tests

    [Theory]
    [InlineData(12)] // Access Point
    [InlineData(14)] // Wireless Controller
    public void Detect_AccessPointDevCat_ReturnsAccessPoint(int devCat)
    {
        var client = new UniFiClientResponse { DevCat = devCat };

        var result = _detector.Detect(client);

        result.Category.Should().Be(ClientDeviceCategory.AccessPoint);
    }

    [Fact]
    public void Detect_SwitchDevCat_ReturnsSwitch()
    {
        var client = new UniFiClientResponse { DevCat = 13 }; // Switch

        var result = _detector.Detect(client);

        result.Category.Should().Be(ClientDeviceCategory.Switch);
    }

    [Theory]
    [InlineData(2)]  // Router
    [InlineData(8)]  // Router
    [InlineData(82)] // Firewall System
    public void Detect_RouterDevCat_ReturnsRouter(int devCat)
    {
        var client = new UniFiClientResponse { DevCat = devCat };

        var result = _detector.Detect(client);

        result.Category.Should().Be(ClientDeviceCategory.Router);
    }

    #endregion

    #region Printer Detection Tests

    [Theory]
    [InlineData(11)]  // Printer
    [InlineData(146)] // 3D Printer
    [InlineData(171)] // Label Printer
    public void Detect_PrinterDevCat_ReturnsPrinter(int devCat)
    {
        var client = new UniFiClientResponse { DevCat = devCat };

        var result = _detector.Detect(client);

        result.Category.Should().Be(ClientDeviceCategory.Printer);
    }

    [Fact]
    public void Detect_ScannerDevCat_ReturnsScanner()
    {
        var client = new UniFiClientResponse { DevCat = 22 }; // Scanner

        var result = _detector.Detect(client);

        result.Category.Should().Be(ClientDeviceCategory.Scanner);
    }

    #endregion

    #region DevIdOverride Priority Tests

    [Fact]
    public void Detect_DevIdOverrideHasPriority_ReturnsOverrideCategory()
    {
        // DevIdOverride=9 (Camera) should take priority over DevCat=31 (SmartTV)
        var client = new UniFiClientResponse
        {
            DevIdOverride = 9, // Camera
            DevCat = 31        // SmartTV
        };

        var result = _detector.Detect(client);

        result.Category.Should().Be(ClientDeviceCategory.Camera);
        result.ConfidenceScore.Should().BeGreaterThan(95);
    }

    [Fact]
    public void Detect_DevIdOverrideWithMetadata_IncludesMetadata()
    {
        var client = new UniFiClientResponse
        {
            DevIdOverride = 9,
            DevCat = 100,
            DevFamily = 50,
            DevVendor = 25
        };

        var result = _detector.Detect(client);

        result.Metadata.Should().ContainKey("dev_id_override");
        result.Metadata.Should().ContainKey("dev_cat");
        result.Metadata.Should().ContainKey("dev_family");
        result.Metadata.Should().ContainKey("dev_vendor");
    }

    #endregion

    #region Unknown Detection Tests

    [Fact]
    public void Detect_NoFingerprintData_ReturnsUnknown()
    {
        var client = new UniFiClientResponse(); // No dev_cat or dev_id_override

        var result = _detector.Detect(client);

        result.Category.Should().Be(ClientDeviceCategory.Unknown);
    }

    [Fact]
    public void Detect_UnmappedDevCat_ReturnsUnknown()
    {
        var client = new UniFiClientResponse { DevCat = 99999 }; // Non-existent category

        var result = _detector.Detect(client);

        result.Category.Should().Be(ClientDeviceCategory.Unknown);
    }

    #endregion

    #region Confidence Score Tests

    [Fact]
    public void Detect_DevIdOverride_HasHighestConfidence()
    {
        var client = new UniFiClientResponse { DevIdOverride = 9 };

        var result = _detector.Detect(client);

        result.ConfidenceScore.Should().BeGreaterThanOrEqualTo(98);
    }

    [Fact]
    public void Detect_DevCat_HasHighConfidence()
    {
        var client = new UniFiClientResponse { DevCat = 9 };

        var result = _detector.Detect(client);

        result.ConfidenceScore.Should().BeGreaterThanOrEqualTo(95);
    }

    #endregion

    #region GetRecommendedNetwork Tests

    [Theory]
    [InlineData(9)]   // Camera -> Security
    [InlineData(116)] // Surveillance System -> Security
    public void Detect_SecurityDevices_RecommendSecurityNetwork(int devCat)
    {
        var client = new UniFiClientResponse { DevCat = devCat };

        var result = _detector.Detect(client);

        result.RecommendedNetwork.Should().Be(NetworkPurpose.Security);
    }

    [Theory]
    [InlineData(42)]  // Smart Plug -> IoT
    [InlineData(35)]  // Smart Lighting -> IoT
    [InlineData(63)]  // Smart Thermostat -> IoT
    [InlineData(41)]  // Robotic Vacuum -> IoT
    public void Detect_IoTDevices_RecommendIoTNetwork(int devCat)
    {
        var client = new UniFiClientResponse { DevCat = devCat };

        var result = _detector.Detect(client);

        result.RecommendedNetwork.Should().Be(NetworkPurpose.IoT);
    }

    [Theory]
    [InlineData(17)] // Game Console -> Corporate
    [InlineData(46)] // Desktop -> Corporate
    [InlineData(1)]  // Laptop -> Corporate
    [InlineData(6)]  // Smartphone -> Corporate
    public void Detect_UserDevices_RecommendCorporateNetwork(int devCat)
    {
        var client = new UniFiClientResponse { DevCat = devCat };

        var result = _detector.Detect(client);

        result.RecommendedNetwork.Should().Be(NetworkPurpose.Corporate);
    }

    [Theory]
    [InlineData(31)] // Smart TV -> IoT
    [InlineData(5)]  // Streaming Device -> IoT
    [InlineData(37)] // Smart Speaker -> IoT
    public void Detect_MediaDevices_RecommendIoTNetwork(int devCat)
    {
        var client = new UniFiClientResponse { DevCat = devCat };

        var result = _detector.Detect(client);

        result.RecommendedNetwork.Should().Be(NetworkPurpose.IoT);
    }

    [Theory]
    [InlineData(12)] // Access Point -> Management
    [InlineData(13)] // Switch -> Management
    [InlineData(2)]  // Router -> Management
    public void Detect_InfrastructureDevices_RecommendManagementNetwork(int devCat)
    {
        var client = new UniFiClientResponse { DevCat = devCat };

        var result = _detector.Detect(client);

        result.RecommendedNetwork.Should().Be(NetworkPurpose.Management);
    }

    #endregion

    #region Database Lookup Tests

    [Fact]
    public void Detect_WithDatabase_IncludesVendorName()
    {
        // Create a database with vendor info
        var database = new UniFiFingerprintDatabase();
        database.VendorIds["1"] = "Test Vendor";

        var detector = new FingerprintDetector(database);
        var client = new UniFiClientResponse
        {
            DevCat = 9, // Camera
            DevVendor = 1
        };

        var result = detector.Detect(client);

        result.VendorName.Should().Be("Test Vendor");
    }

    [Fact]
    public void Detect_WithDatabase_IncludesTypeName()
    {
        // Create a database with type info
        var database = new UniFiFingerprintDatabase();
        database.DevTypeIds["9"] = "IP Camera";

        var detector = new FingerprintDetector(database);
        var client = new UniFiClientResponse { DevCat = 9 };

        var result = detector.Detect(client);

        result.ProductName.Should().Be("IP Camera");
    }

    [Fact]
    public void Detect_DevIdOverrideUnknownType_FallsBackToDevCat()
    {
        // DevIdOverride doesn't map to anything known, but DevCat does
        var client = new UniFiClientResponse
        {
            DevIdOverride = 99999, // Unknown
            DevCat = 9 // Camera
        };

        var result = _detector.Detect(client);

        result.Category.Should().Be(ClientDeviceCategory.Camera);
        result.Metadata.Should().ContainKey("dev_id_override_unmatched");
    }

    [Fact]
    public void Detect_WithDatabaseLookup_InfersFromName()
    {
        // Test database lookup path where DevIdOverride leads to name inference
        var database = new UniFiFingerprintDatabase();
        database.DevIds["12345"] = new FingerprintDeviceEntry { Name = "Apple TV 4K" };

        var detector = new FingerprintDetector(database);
        var client = new UniFiClientResponse { DevIdOverride = 12345 };

        var result = detector.Detect(client);

        result.Category.Should().Be(ClientDeviceCategory.StreamingDevice);
        result.Source.Should().Be(DetectionSource.UniFiFingerprint);
        result.Metadata.Should().ContainKey("inferred_from_name");
    }

    [Fact]
    public void Detect_DevIdOverrideCameraName_ReturnsCamera()
    {
        var database = new UniFiFingerprintDatabase();
        database.DevIds["54321"] = new FingerprintDeviceEntry { Name = "Ring Doorbell Pro" };

        var detector = new FingerprintDetector(database);
        var client = new UniFiClientResponse { DevIdOverride = 54321 };

        var result = detector.Detect(client);

        result.Category.Should().Be(ClientDeviceCategory.Camera);
    }

    [Fact]
    public void Detect_DevIdOverrideSmartHubName_ReturnsSmartHub()
    {
        var database = new UniFiFingerprintDatabase();
        database.DevIds["11111"] = new FingerprintDeviceEntry { Name = "IKEA Dirigera Gateway" };

        var detector = new FingerprintDetector(database);
        var client = new UniFiClientResponse { DevIdOverride = 11111 };

        var result = detector.Detect(client);

        result.Category.Should().Be(ClientDeviceCategory.SmartHub);
    }

    [Fact]
    public void Detect_DevIdOverrideSpeakerName_ReturnsSmartSpeaker()
    {
        var database = new UniFiFingerprintDatabase();
        database.DevIds["22222"] = new FingerprintDeviceEntry { Name = "Amazon Echo Dot" };

        var detector = new FingerprintDetector(database);
        var client = new UniFiClientResponse { DevIdOverride = 22222 };

        var result = detector.Detect(client);

        result.Category.Should().Be(ClientDeviceCategory.SmartSpeaker);
    }

    [Fact]
    public void Detect_DevIdOverrideLightingName_ReturnsSmartLighting()
    {
        var database = new UniFiFingerprintDatabase();
        database.DevIds["33333"] = new FingerprintDeviceEntry { Name = "Philips Hue Bulb" };

        var detector = new FingerprintDetector(database);
        var client = new UniFiClientResponse { DevIdOverride = 33333 };

        var result = detector.Detect(client);

        result.Category.Should().Be(ClientDeviceCategory.SmartLighting);
    }

    [Fact]
    public void Detect_DevIdOverrideThermostatName_ReturnsSmartThermostat()
    {
        var database = new UniFiFingerprintDatabase();
        database.DevIds["44444"] = new FingerprintDeviceEntry { Name = "Nest Thermostat" };

        var detector = new FingerprintDetector(database);
        var client = new UniFiClientResponse { DevIdOverride = 44444 };

        var result = detector.Detect(client);

        result.Category.Should().Be(ClientDeviceCategory.SmartThermostat);
    }

    [Fact]
    public void Detect_DevIdOverrideLockName_ReturnsSmartLock()
    {
        var database = new UniFiFingerprintDatabase();
        database.DevIds["55555"] = new FingerprintDeviceEntry { Name = "August Smart Lock" };

        var detector = new FingerprintDetector(database);
        var client = new UniFiClientResponse { DevIdOverride = 55555 };

        var result = detector.Detect(client);

        result.Category.Should().Be(ClientDeviceCategory.SmartLock);
    }

    [Fact]
    public void Detect_DevIdOverrideVacuumName_ReturnsRoboticVacuum()
    {
        var database = new UniFiFingerprintDatabase();
        database.DevIds["66666"] = new FingerprintDeviceEntry { Name = "iRobot Roomba" };

        var detector = new FingerprintDetector(database);
        var client = new UniFiClientResponse { DevIdOverride = 66666 };

        var result = detector.Detect(client);

        result.Category.Should().Be(ClientDeviceCategory.RoboticVacuum);
    }

    [Fact]
    public void Detect_DevIdOverrideGameConsoleName_ReturnsGameConsole()
    {
        var database = new UniFiFingerprintDatabase();
        database.DevIds["77777"] = new FingerprintDeviceEntry { Name = "PlayStation 5" };

        var detector = new FingerprintDetector(database);
        var client = new UniFiClientResponse { DevIdOverride = 77777 };

        var result = detector.Detect(client);

        result.Category.Should().Be(ClientDeviceCategory.GameConsole);
    }

    [Fact]
    public void Detect_DevIdOverridePrinterName_ReturnsPrinter()
    {
        var database = new UniFiFingerprintDatabase();
        database.DevIds["88888"] = new FingerprintDeviceEntry { Name = "HP LaserJet Printer" };

        var detector = new FingerprintDetector(database);
        var client = new UniFiClientResponse { DevIdOverride = 88888 };

        var result = detector.Detect(client);

        result.Category.Should().Be(ClientDeviceCategory.Printer);
    }

    [Fact]
    public void Detect_DevIdOverrideNasName_ReturnsNas()
    {
        var database = new UniFiFingerprintDatabase();
        database.DevIds["99999"] = new FingerprintDeviceEntry { Name = "Synology NAS DS920+" };

        var detector = new FingerprintDetector(database);
        var client = new UniFiClientResponse { DevIdOverride = 99999 };

        var result = detector.Detect(client);

        result.Category.Should().Be(ClientDeviceCategory.NAS);
    }

    [Fact]
    public void Detect_DevIdOverrideSmartPlugName_ReturnsSmartPlug()
    {
        var database = new UniFiFingerprintDatabase();
        database.DevIds["10101"] = new FingerprintDeviceEntry { Name = "Wemo Smart Plug" };

        var detector = new FingerprintDetector(database);
        var client = new UniFiClientResponse { DevIdOverride = 10101 };

        var result = detector.Detect(client);

        result.Category.Should().Be(ClientDeviceCategory.SmartPlug);
    }

    [Fact]
    public void Detect_DevIdOverrideSmartTvName_ReturnsSmartTV()
    {
        var database = new UniFiFingerprintDatabase();
        database.DevIds["20202"] = new FingerprintDeviceEntry { Name = "Samsung Smart TV" };

        var detector = new FingerprintDetector(database);
        var client = new UniFiClientResponse { DevIdOverride = 20202 };

        var result = detector.Detect(client);

        result.Category.Should().Be(ClientDeviceCategory.SmartTV);
    }

    [Fact]
    public void Detect_DevIdOverrideVoIPName_ReturnsVoIP()
    {
        var database = new UniFiFingerprintDatabase();
        database.DevIds["30303"] = new FingerprintDeviceEntry { Name = "Cisco VoIP Phone" };

        var detector = new FingerprintDetector(database);
        var client = new UniFiClientResponse { DevIdOverride = 30303 };

        var result = detector.Detect(client);

        result.Category.Should().Be(ClientDeviceCategory.VoIP);
    }

    [Fact]
    public void Detect_DevIdOverrideEmptyName_ReturnsUnknown()
    {
        var database = new UniFiFingerprintDatabase();
        database.DevIds["40404"] = new FingerprintDeviceEntry { Name = "" };

        var detector = new FingerprintDetector(database);
        var client = new UniFiClientResponse { DevIdOverride = 40404 };

        var result = detector.Detect(client);

        result.Category.Should().Be(ClientDeviceCategory.Unknown);
    }

    [Fact]
    public void Detect_DevIdOverrideUnknownName_ReturnsUnknown()
    {
        var database = new UniFiFingerprintDatabase();
        database.DevIds["50505"] = new FingerprintDeviceEntry { Name = "Some Random Device" };

        var detector = new FingerprintDetector(database);
        var client = new UniFiClientResponse { DevIdOverride = 50505 };

        var result = detector.Detect(client);

        result.Category.Should().Be(ClientDeviceCategory.Unknown);
    }

    #endregion

    #region GetRecommendedNetwork Static Method Tests

    [Fact]
    public void GetRecommendedNetwork_Camera_ReturnsSecurity()
    {
        var result = FingerprintDetector.GetRecommendedNetwork(ClientDeviceCategory.Camera);
        result.Should().Be(NetworkPurpose.Security);
    }

    [Fact]
    public void GetRecommendedNetwork_SmartPlug_ReturnsIoT()
    {
        var result = FingerprintDetector.GetRecommendedNetwork(ClientDeviceCategory.SmartPlug);
        result.Should().Be(NetworkPurpose.IoT);
    }

    [Fact]
    public void GetRecommendedNetwork_Desktop_ReturnsCorporate()
    {
        var result = FingerprintDetector.GetRecommendedNetwork(ClientDeviceCategory.Desktop);
        result.Should().Be(NetworkPurpose.Corporate);
    }

    [Fact]
    public void GetRecommendedNetwork_AccessPoint_ReturnsManagement()
    {
        var result = FingerprintDetector.GetRecommendedNetwork(ClientDeviceCategory.AccessPoint);
        result.Should().Be(NetworkPurpose.Management);
    }

    [Fact]
    public void GetRecommendedNetwork_Unknown_ReturnsUnknown()
    {
        var result = FingerprintDetector.GetRecommendedNetwork(ClientDeviceCategory.Unknown);
        result.Should().Be(NetworkPurpose.Unknown);
    }

    [Theory]
    [InlineData(ClientDeviceCategory.SmartLighting)]
    [InlineData(ClientDeviceCategory.SmartThermostat)]
    [InlineData(ClientDeviceCategory.SmartLock)]
    [InlineData(ClientDeviceCategory.SmartSensor)]
    [InlineData(ClientDeviceCategory.SmartAppliance)]
    [InlineData(ClientDeviceCategory.SmartHub)]
    [InlineData(ClientDeviceCategory.RoboticVacuum)]
    [InlineData(ClientDeviceCategory.IoTGeneric)]
    [InlineData(ClientDeviceCategory.SmartSpeaker)]
    [InlineData(ClientDeviceCategory.SmartTV)]
    [InlineData(ClientDeviceCategory.StreamingDevice)]
    [InlineData(ClientDeviceCategory.MediaPlayer)]
    public void GetRecommendedNetwork_IoTCategory_ReturnsIoT(ClientDeviceCategory category)
    {
        var result = FingerprintDetector.GetRecommendedNetwork(category);
        result.Should().Be(NetworkPurpose.IoT);
    }

    [Theory]
    [InlineData(ClientDeviceCategory.Switch)]
    [InlineData(ClientDeviceCategory.Router)]
    [InlineData(ClientDeviceCategory.Gateway)]
    public void GetRecommendedNetwork_InfrastructureCategory_ReturnsManagement(ClientDeviceCategory category)
    {
        var result = FingerprintDetector.GetRecommendedNetwork(category);
        result.Should().Be(NetworkPurpose.Management);
    }

    [Theory]
    [InlineData(ClientDeviceCategory.Laptop)]
    [InlineData(ClientDeviceCategory.Server)]
    [InlineData(ClientDeviceCategory.NAS)]
    [InlineData(ClientDeviceCategory.Smartphone)]
    [InlineData(ClientDeviceCategory.Tablet)]
    [InlineData(ClientDeviceCategory.VoIP)]
    [InlineData(ClientDeviceCategory.Printer)]
    [InlineData(ClientDeviceCategory.Scanner)]
    [InlineData(ClientDeviceCategory.GameConsole)]
    public void GetRecommendedNetwork_CorporateCategory_ReturnsCorporate(ClientDeviceCategory category)
    {
        var result = FingerprintDetector.GetRecommendedNetwork(category);
        result.Should().Be(NetworkPurpose.Corporate);
    }

    #endregion
}
