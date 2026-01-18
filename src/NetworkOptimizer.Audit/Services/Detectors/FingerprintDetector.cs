using Microsoft.Extensions.Logging;
using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.Audit.Services.Detectors;

/// <summary>
/// Detects device type from UniFi fingerprint data.
/// This is the highest confidence detection method.
///
/// UniFi uses two different ID spaces that must not be confused:
///
/// 1. dev_id (device ID): Identifies a specific device model.
///    Examples: 14 = Apple TV HD, 4405 = Apple TV 4K, 4904 = IKEA Dirigera
///    This is what dev_id_override contains when a user selects an icon in the UI.
///
/// 2. dev_type_id (device type ID): Identifies a device category.
///    Examples: 9 = IP Network Camera, 47 = Smart TV, 14 = Wireless Controller
///    This is what dev_cat contains (auto-detected category).
///
/// IMPORTANT: These ID spaces overlap! For example:
/// - dev_id 14 = Apple TV HD (a streaming device)
/// - dev_type_id 14 = Wireless Controller (network infrastructure)
///
/// To correctly categorize a device with dev_id_override, we must look up the
/// dev_id in the fingerprint database to get its dev_type_id, then map that.
/// Direct mapping of dev_id_override would cause collisions (e.g., Apple TV → AccessPoint).
/// </summary>
public class FingerprintDetector
{
    private readonly UniFiFingerprintDatabase? _database;
    private readonly ILogger? _logger;

    /// <summary>
    /// Maps dev_type_id values to our device categories.
    ///
    /// IMPORTANT: This dictionary contains ONLY dev_type_id values (category IDs),
    /// NOT dev_id values (device-specific IDs). Do not add entries like 4405 (Apple TV 4K)
    /// or 4904 (IKEA Dirigera) here - those are dev_id values that must be resolved
    /// via database lookup to get their dev_type_id.
    /// </summary>
    private static readonly Dictionary<int, ClientDeviceCategory> DevTypeMapping = new()
    {
        // ============================================================
        // CAMERAS / SURVEILLANCE
        // Note: Vendor-based reclassification to CloudCamera happens later
        // ============================================================
        { 9, ClientDeviceCategory.Camera },           // IP Network Camera
        { 57, ClientDeviceCategory.Camera },          // Smart Security Camera
        { 106, ClientDeviceCategory.Camera },         // Camera
        { 124, ClientDeviceCategory.Camera },         // Network Video Recorder
        { 147, ClientDeviceCategory.Camera },         // Doorbell Camera
        { 161, ClientDeviceCategory.Camera },         // Video Doorbell

        // Cloud Cameras (internet-dependent)
        { 114, ClientDeviceCategory.CloudCamera },    // Baby Monitor
        { 151, ClientDeviceCategory.CloudCamera },    // Video Door Phone
        { 163, ClientDeviceCategory.CloudCamera },    // Dashcam

        // Security Systems
        { 111, ClientDeviceCategory.SecuritySystem }, // Security Panel
        { 116, ClientDeviceCategory.SecuritySystem }, // Surveillance System
        { 80, ClientDeviceCategory.SecuritySystem },  // Smart Home Security System
        { 173, ClientDeviceCategory.SecuritySystem }, // Home Security System
        { 199, ClientDeviceCategory.SecuritySystem }, // Smart Smoke Detector
        { 248, ClientDeviceCategory.SecuritySystem }, // Smart Access Control
        { 278, ClientDeviceCategory.SecuritySystem }, // Biometric Reader

        // ============================================================
        // SMART LIGHTING
        // ============================================================
        { 35, ClientDeviceCategory.SmartLighting },   // Wireless Lighting
        { 53, ClientDeviceCategory.SmartLighting },   // Smart Lighting Device
        { 102, ClientDeviceCategory.SmartLighting },  // Smart Dimmer
        { 179, ClientDeviceCategory.SmartLighting },  // LED Lighting
        { 184, ClientDeviceCategory.SmartLighting },  // Smart Light Strip
        { 240, ClientDeviceCategory.SmartLighting },  // Flood Light

        // ============================================================
        // SMART PLUGS / POWER
        // ============================================================
        { 33, ClientDeviceCategory.SmartPlug },       // Smart Switch
        { 42, ClientDeviceCategory.SmartPlug },       // Smart Plug
        { 97, ClientDeviceCategory.SmartPlug },       // Smart Power Strip
        { 107, ClientDeviceCategory.SmartPlug },      // AC Switch
        { 153, ClientDeviceCategory.SmartPlug },      // Smart Socket
        { 236, ClientDeviceCategory.SmartSensor },    // Smart Meter

        // ============================================================
        // THERMOSTATS / HVAC
        // ============================================================
        { 63, ClientDeviceCategory.SmartThermostat }, // Smart Thermostat
        { 70, ClientDeviceCategory.SmartThermostat }, // Smart Heating Device
        { 253, ClientDeviceCategory.SmartThermostat }, // Smart Heater
        { 277, ClientDeviceCategory.SmartThermostat }, // Heat Pump
        { 279, ClientDeviceCategory.SmartThermostat }, // Smart Radiator

        // ============================================================
        // SMART LOCKS / ACCESS
        // ============================================================
        { 125, ClientDeviceCategory.SmartLock },      // Touch Screen Deadbolt
        { 133, ClientDeviceCategory.SmartLock },      // Door Lock
        { 159, ClientDeviceCategory.SmartLock },      // Rolling Shutter
        { 172, ClientDeviceCategory.SmartLock },      // Gate Controller
        { 275, ClientDeviceCategory.SmartLock },      // Door Fob

        // ============================================================
        // SMART SENSORS
        // ============================================================
        { 94, ClientDeviceCategory.SmartSensor },     // Smart Scale
        { 100, ClientDeviceCategory.SmartSensor },    // Weather Station
        { 109, ClientDeviceCategory.SmartSensor },    // Sleep Monitor
        { 110, ClientDeviceCategory.SmartSensor },    // Blood Pressure Monitor
        { 127, ClientDeviceCategory.SmartSensor },    // Solar Inverter Monitor
        { 139, ClientDeviceCategory.SmartSensor },    // Water Monitor
        { 148, ClientDeviceCategory.SmartSensor },    // Air Quality Monitor
        { 158, ClientDeviceCategory.SmartSensor },    // Air Monitor
        { 189, ClientDeviceCategory.SmartSensor },    // Meat Thermometer
        { 201, ClientDeviceCategory.SmartSensor },    // Smart Temperature Sensor
        { 202, ClientDeviceCategory.SmartSensor },    // Smart Thermometer
        { 234, ClientDeviceCategory.SmartSensor },    // Weather Monitor
        { 245, ClientDeviceCategory.SmartSensor },    // Smart Motion Sensor
        { 267, ClientDeviceCategory.SmartSensor },    // Power Monitor
        { 269, ClientDeviceCategory.SmartSensor },    // Home Energy Monitor

        // ============================================================
        // SMART APPLIANCES
        // ============================================================
        { 48, ClientDeviceCategory.SmartAppliance },  // Intelligent Home Appliances
        { 71, ClientDeviceCategory.SmartAppliance },  // Air Conditioner
        { 74, ClientDeviceCategory.SmartAppliance },  // Machine Wash
        { 86, ClientDeviceCategory.SmartAppliance },  // Smart Bed
        { 92, ClientDeviceCategory.SmartAppliance },  // Air Purifier
        { 99, ClientDeviceCategory.SmartAppliance },  // Smart Ceiling Fan
        { 113, ClientDeviceCategory.SmartAppliance }, // Water Filter
        { 118, ClientDeviceCategory.SmartAppliance }, // Dryer
        { 131, ClientDeviceCategory.SmartAppliance }, // Washing Machine
        { 137, ClientDeviceCategory.SmartAppliance }, // Electric Cooktop
        { 140, ClientDeviceCategory.SmartAppliance }, // Dishwasher
        { 149, ClientDeviceCategory.SmartAppliance }, // Smart Kettle
        { 177, ClientDeviceCategory.SmartAppliance }, // Oven
        { 181, ClientDeviceCategory.SmartAppliance }, // Smart Grill
        { 195, ClientDeviceCategory.SmartAppliance }, // Smart Fragrance Device
        { 235, ClientDeviceCategory.SmartAppliance }, // Wifi Fan
        { 250, ClientDeviceCategory.SmartAppliance }, // Fan
        { 262, ClientDeviceCategory.SmartAppliance }, // Air Diffuser
        { 264, ClientDeviceCategory.SmartAppliance }, // Toothbrush
        { 272, ClientDeviceCategory.SmartAppliance }, // Mattress

        // ============================================================
        // SMART HUBS / CONTROLLERS
        // ============================================================
        { 93, ClientDeviceCategory.SmartHub },        // Home Automation
        { 95, ClientDeviceCategory.SmartHub },        // System Controller
        { 126, ClientDeviceCategory.SmartHub },       // Solar Communication Gateway
        { 144, ClientDeviceCategory.SmartHub },       // Smart Hub
        { 154, ClientDeviceCategory.SmartHub },       // Smart Bridge
        { 187, ClientDeviceCategory.SmartHub },       // Smart Gateway
        { 251, ClientDeviceCategory.SmartHub },       // Smart Controller
        { 274, ClientDeviceCategory.SmartHub },       // Device Controller

        // ============================================================
        // ROBOTIC DEVICES
        // ============================================================
        { 41, ClientDeviceCategory.RoboticVacuum },   // Robotic Vacuums
        { 65, ClientDeviceCategory.RoboticVacuum },   // Smart Cleaning Device
        { 81, ClientDeviceCategory.RoboticVacuum },   // Robot
        { 276, ClientDeviceCategory.RoboticVacuum },  // Smart Mower

        // ============================================================
        // SMART TVS / DISPLAYS
        // ============================================================
        { 31, ClientDeviceCategory.SmartTV },         // SmartTV
        { 34, ClientDeviceCategory.SmartTV },         // Projector
        { 38, ClientDeviceCategory.SmartTV },         // Dashboard
        { 47, ClientDeviceCategory.SmartTV },         // Smart TV & Set-top box
        { 50, ClientDeviceCategory.SmartTV },         // Smart TV & Set-top box
        { 143, ClientDeviceCategory.SmartTV },        // Picture Frame
        { 188, ClientDeviceCategory.SmartTV },        // Digital Canvas
        { 203, ClientDeviceCategory.SmartTV },        // Smart Display
        { 254, ClientDeviceCategory.SmartTV },        // Smart Clock
        { 259, ClientDeviceCategory.SmartTV },        // Collaboration Display

        // ============================================================
        // STREAMING DEVICES
        // ============================================================
        { 5, ClientDeviceCategory.StreamingDevice },  // IPTV
        { 186, ClientDeviceCategory.StreamingDevice }, // IPTV Set Top Box
        { 190, ClientDeviceCategory.StreamingDevice }, // TV IP Media Receiver
        { 238, ClientDeviceCategory.StreamingDevice }, // Media Player
        { 242, ClientDeviceCategory.StreamingDevice }, // Streaming Media Device
        { 271, ClientDeviceCategory.StreamingDevice }, // Media Streamer

        // ============================================================
        // SMART SPEAKERS
        // ============================================================
        { 37, ClientDeviceCategory.SmartSpeaker },    // Smart Speaker
        { 52, ClientDeviceCategory.SmartSpeaker },    // Smart Audio Device
        { 170, ClientDeviceCategory.SmartSpeaker },   // Wifi Speaker
        { 263, ClientDeviceCategory.SmartSpeaker },   // Network Speaker

        // ============================================================
        // MEDIA PLAYERS / AUDIO
        // ============================================================
        { 20, ClientDeviceCategory.MediaPlayer },     // Multimedia Device
        { 69, ClientDeviceCategory.MediaPlayer },     // AV Receiver
        { 73, ClientDeviceCategory.MediaPlayer },     // Soundbar
        { 96, ClientDeviceCategory.MediaPlayer },     // Audio Streamer
        { 103, ClientDeviceCategory.MediaPlayer },    // Radio
        { 122, ClientDeviceCategory.MediaPlayer },    // Digital Radio
        { 132, ClientDeviceCategory.MediaPlayer },    // Music Server
        { 141, ClientDeviceCategory.MediaPlayer },    // Digital Mixer
        { 152, ClientDeviceCategory.MediaPlayer },    // Blu Ray Player
        { 155, ClientDeviceCategory.MediaPlayer },    // Amplifier
        { 178, ClientDeviceCategory.MediaPlayer },    // Home Entertainment System
        { 191, ClientDeviceCategory.MediaPlayer },    // Headphone AMP
        { 192, ClientDeviceCategory.MediaPlayer },    // Smart Home Theater Device
        { 193, ClientDeviceCategory.MediaPlayer },    // Music Streamer
        { 247, ClientDeviceCategory.MediaPlayer },    // Receiver
        { 260, ClientDeviceCategory.MediaPlayer },    // Sound Machine

        // ============================================================
        // GAME CONSOLES
        // ============================================================
        { 17, ClientDeviceCategory.GameConsole },     // Game Console
        { 164, ClientDeviceCategory.GameConsole },    // Joystick

        // ============================================================
        // COMPUTERS
        // ============================================================
        { 1, ClientDeviceCategory.Laptop },           // Desktop/Laptop
        { 25, ClientDeviceCategory.Desktop },         // Thin Client
        { 28, ClientDeviceCategory.Desktop },         // Workstation
        { 46, ClientDeviceCategory.Desktop },         // Computer
        { 76, ClientDeviceCategory.Desktop },         // Computer Stick Board
        { 104, ClientDeviceCategory.Desktop },        // Motherboard
        { 117, ClientDeviceCategory.Desktop },        // Single Board Computer
        { 265, ClientDeviceCategory.Desktop },        // Docking Station

        // Servers
        { 56, ClientDeviceCategory.Server },          // Server
        { 225, ClientDeviceCategory.Server },         // Operating System

        // ============================================================
        // NAS / STORAGE
        // ============================================================
        { 18, ClientDeviceCategory.NAS },             // NAS
        { 91, ClientDeviceCategory.NAS },             // Network Storage
        { 134, ClientDeviceCategory.NAS },            // Media Server
        { 157, ClientDeviceCategory.NAS },            // Wireless Storage

        // ============================================================
        // MOBILE / WEARABLES
        // ============================================================
        { 6, ClientDeviceCategory.Smartphone },       // Smartphone
        { 19, ClientDeviceCategory.Smartphone },      // PDA
        { 26, ClientDeviceCategory.Smartphone },      // Phone (generic)
        { 29, ClientDeviceCategory.Smartphone },      // Apple iOS Device
        { 32, ClientDeviceCategory.Smartphone },      // Android Device
        { 36, ClientDeviceCategory.Smartphone },      // Smart Watch
        { 39, ClientDeviceCategory.Smartphone },      // Tizen Device
        { 44, ClientDeviceCategory.Smartphone },      // Handheld
        { 45, ClientDeviceCategory.Smartphone },      // Wearable devices
        { 112, ClientDeviceCategory.Smartphone },     // VR
        { 123, ClientDeviceCategory.Smartphone },     // Earphones
        { 160, ClientDeviceCategory.Smartphone },     // Social Media Device
        { 167, ClientDeviceCategory.Smartphone },     // GPS Bike
        { 198, ClientDeviceCategory.Smartphone },     // GPS
        { 243, ClientDeviceCategory.Smartphone },     // Navigation System
        { 256, ClientDeviceCategory.Smartphone },     // Spatial Computer
        { 266, ClientDeviceCategory.Smartphone },     // Head Mounted Device

        // Tablets
        { 21, ClientDeviceCategory.Tablet },          // eBook Reader
        { 30, ClientDeviceCategory.Tablet },          // Tablet

        // ============================================================
        // VOIP / COMMUNICATION
        // ============================================================
        { 3, ClientDeviceCategory.VoIP },             // VoIP Phone
        { 10, ClientDeviceCategory.VoIP },            // VoIP Gateway
        { 23, ClientDeviceCategory.VoIP },            // Video Conferencing
        { 27, ClientDeviceCategory.VoIP },            // Video Phone
        { 85, ClientDeviceCategory.VoIP },            // IP Station
        { 87, ClientDeviceCategory.VoIP },            // Conference Camera
        { 121, ClientDeviceCategory.VoIP },           // Smart Video Caller
        { 145, ClientDeviceCategory.VoIP },           // VoIP Server
        { 156, ClientDeviceCategory.VoIP },           // Call Station
        { 249, ClientDeviceCategory.VoIP },           // Conference Phone
        { 268, ClientDeviceCategory.VoIP },           // Intercom
        { 270, ClientDeviceCategory.VoIP },           // Conference System

        // ============================================================
        // NETWORK INFRASTRUCTURE
        // ============================================================
        // Access Points / Wireless
        { 12, ClientDeviceCategory.AccessPoint },     // Access Point
        { 14, ClientDeviceCategory.AccessPoint },     // Wireless Controller
        { 55, ClientDeviceCategory.AccessPoint },     // Wireless Antenna
        { 128, ClientDeviceCategory.AccessPoint },    // Wifi Extender
        { 135, ClientDeviceCategory.AccessPoint },    // Powerline
        { 185, ClientDeviceCategory.AccessPoint },    // Wireless Hot Spot
        { 255, ClientDeviceCategory.AccessPoint },    // Bluetooth Extender
        { 258, ClientDeviceCategory.AccessPoint },    // Wifi Module

        // Switches
        { 13, ClientDeviceCategory.Switch },          // Switch
        { 54, ClientDeviceCategory.Switch },          // Wired Ethernet

        // Routers / Gateways
        { 2, ClientDeviceCategory.Router },           // Router
        { 8, ClientDeviceCategory.Router },           // Router
        { 16, ClientDeviceCategory.Router },          // Network Diagnostics
        { 82, ClientDeviceCategory.Router },          // Firewall System
        { 142, ClientDeviceCategory.Router },         // IP Gateway
        { 237, ClientDeviceCategory.Router },         // Wireless Modem
        { 239, ClientDeviceCategory.Router },         // Routerboard
        { 273, ClientDeviceCategory.Router },         // Ad Blocker

        // ============================================================
        // PRINTERS / SCANNERS
        // ============================================================
        { 11, ClientDeviceCategory.Printer },         // Printer
        { 146, ClientDeviceCategory.Printer },        // 3D Printer
        { 171, ClientDeviceCategory.Printer },        // Label Printer
        { 176, ClientDeviceCategory.Printer },        // Print Server
        { 22, ClientDeviceCategory.Scanner },         // Scanner

        // ============================================================
        // IOT GENERIC
        // ============================================================
        { 4, ClientDeviceCategory.IoTGeneric },       // Miscellaneous
        { 7, ClientDeviceCategory.IoTGeneric },       // UPS
        { 49, ClientDeviceCategory.IoTGeneric },      // Network & Peripheral
        { 51, ClientDeviceCategory.IoTGeneric },      // Smart Device
        { 60, ClientDeviceCategory.IoTGeneric },      // Alarm System
        { 64, ClientDeviceCategory.IoTGeneric },      // Smart Garden Device
        { 66, ClientDeviceCategory.IoTGeneric },      // IoT Device
        { 67, ClientDeviceCategory.IoTGeneric },      // Smart Cars
        { 72, ClientDeviceCategory.IoTGeneric },      // EV Charging Station
        { 77, ClientDeviceCategory.IoTGeneric },      // Sprinkler Controller
        { 78, ClientDeviceCategory.IoTGeneric },      // Inverter System
        { 83, ClientDeviceCategory.IoTGeneric },      // Garage Door
        { 88, ClientDeviceCategory.IoTGeneric },      // Energy System
        { 89, ClientDeviceCategory.IoTGeneric },      // Smart Remote Control
        { 115, ClientDeviceCategory.IoTGeneric },     // Solar Power
        { 119, ClientDeviceCategory.IoTGeneric },     // Generator
        { 120, ClientDeviceCategory.IoTGeneric },     // Garage Opener
        { 130, ClientDeviceCategory.IoTGeneric },     // Irrigation Controller
        { 136, ClientDeviceCategory.IoTGeneric },     // Vehicle Charger
        { 138, ClientDeviceCategory.IoTGeneric },     // Network Doorbell
        { 165, ClientDeviceCategory.IoTGeneric },     // Fireplace
        { 166, ClientDeviceCategory.IoTGeneric },     // Home Battery
        { 168, ClientDeviceCategory.IoTGeneric },     // Vehicles
        { 169, ClientDeviceCategory.IoTGeneric },     // Toy
        { 174, ClientDeviceCategory.IoTGeneric },     // Smart Garden
        { 180, ClientDeviceCategory.IoTGeneric },     // Smart Payment Solution
        { 183, ClientDeviceCategory.IoTGeneric },     // Smart Pet Device
        { 196, ClientDeviceCategory.IoTGeneric },     // Solar Energy System
        { 200, ClientDeviceCategory.IoTGeneric },     // Smart Pool Control
        { 244, ClientDeviceCategory.IoTGeneric },     // Smart Pool Device
        { 246, ClientDeviceCategory.IoTGeneric },     // Parking System
    };

    public FingerprintDetector(UniFiFingerprintDatabase? database = null, ILogger<FingerprintDetector>? logger = null)
    {
        _database = database;
        _logger = logger;
    }

    /// <summary>
    /// Detect device type from UniFi client fingerprint data.
    /// Checks user-selected device type (DevIdOverride) first, then auto-detected (DevCat).
    /// Uses database lookup to resolve dev_id to dev_type_id for accurate categorization.
    /// </summary>
    public DeviceDetectionResult Detect(UniFiClientResponse? clientFingerprint)
    {
        if (clientFingerprint == null)
            return DeviceDetectionResult.Unknown;

        // Priority 1: User-selected device type - lookup from database to get dev_type_id
        // dev_id values are device-specific (e.g., 14 = Apple TV HD), while dev_type_id
        // values are category IDs (e.g., 47 = Smart TV). The database lookup resolves the
        // dev_id to its actual dev_type_id for correct categorization.
        if (clientFingerprint.DevIdOverride.HasValue && _database != null)
        {
            var deviceIdStr = clientFingerprint.DevIdOverride.Value.ToString();
            if (_database.DevIds.TryGetValue(deviceIdStr, out var deviceEntry) &&
                !string.IsNullOrEmpty(deviceEntry.DevTypeId) &&
                int.TryParse(deviceEntry.DevTypeId, out var devTypeId) &&
                DevTypeMapping.TryGetValue(devTypeId, out var mappedCategory))
            {
                var deviceName = deviceEntry.Name?.Trim();

                // When user explicitly selects a device type (dev_id_override), prefer the vendor
                // from the fingerprint database entry for that device over the client's DevVendor.
                // The client's DevVendor may be incorrect (e.g., reporting "Avaya" for a HomePod).
                // Fall back to client's DevVendor if the database entry has no vendor.
                int? vendorId = null;
                if (!string.IsNullOrEmpty(deviceEntry.VendorId) &&
                    int.TryParse(deviceEntry.VendorId, out var entryVendorId))
                {
                    vendorId = entryVendorId;
                    _logger?.LogDebug("[FingerprintDetector] dev_id_override={DevIdOverride}: using DB entry VendorId={EntryVendorId} (client DevVendor={ClientVendor})",
                        clientFingerprint.DevIdOverride, deviceEntry.VendorId, clientFingerprint.DevVendor);
                }
                else if (clientFingerprint.DevVendor.HasValue)
                {
                    vendorId = clientFingerprint.DevVendor;
                    _logger?.LogDebug("[FingerprintDetector] dev_id_override={DevIdOverride}: DB entry has no vendor, falling back to client DevVendor={ClientVendor}",
                        clientFingerprint.DevIdOverride, clientFingerprint.DevVendor);
                }
                var vendorName = _database.GetVendorName(vendorId);

                return new DeviceDetectionResult
                {
                    Category = mappedCategory,
                    Source = DetectionSource.UniFiFingerprint,
                    ConfidenceScore = 98, // Highest confidence - user override resolved via database
                    VendorName = vendorName,
                    ProductName = deviceName,
                    RecommendedNetwork = GetRecommendedNetwork(mappedCategory),
                    Metadata = new Dictionary<string, object>
                    {
                        ["dev_id_override"] = clientFingerprint.DevIdOverride.Value,
                        ["dev_type_id"] = devTypeId,
                        ["dev_cat"] = clientFingerprint.DevCat ?? 0,
                        ["dev_family"] = clientFingerprint.DevFamily ?? 0,
                        ["dev_vendor"] = clientFingerprint.DevVendor ?? 0,
                        ["user_override"] = true
                    }
                };
            }
        }

        // Priority 2: Auto-detected device category (dev_cat is a dev_type_id)
        if (clientFingerprint.DevCat.HasValue && DevTypeMapping.TryGetValue(clientFingerprint.DevCat.Value, out var category))
        {
            var vendorName = _database?.GetVendorName(clientFingerprint.DevVendor);
            var typeName = _database?.GetDeviceTypeName(clientFingerprint.DevCat);

            var metadata = new Dictionary<string, object>
            {
                ["dev_cat"] = clientFingerprint.DevCat.Value,
                ["dev_family"] = clientFingerprint.DevFamily ?? 0,
                ["dev_vendor"] = clientFingerprint.DevVendor ?? 0
            };

            // Include unmatched dev_id_override so we can see what user selected
            if (clientFingerprint.DevIdOverride.HasValue)
            {
                metadata["dev_id_override_unmatched"] = clientFingerprint.DevIdOverride.Value;
            }

            return new DeviceDetectionResult
            {
                Category = category,
                Source = DetectionSource.UniFiFingerprint,
                ConfidenceScore = 95, // High confidence from fingerprint
                VendorName = vendorName,
                ProductName = typeName,
                RecommendedNetwork = GetRecommendedNetwork(category),
                Metadata = metadata
            };
        }

        return DeviceDetectionResult.Unknown;
    }

    /// <summary>
    /// Infer device category from device name using keyword matching
    /// </summary>
    private static ClientDeviceCategory InferCategoryFromDeviceName(string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
            return ClientDeviceCategory.Unknown;

        var name = deviceName.ToLowerInvariant();

        // Streaming devices
        if (name.Contains("apple tv") || name.Contains("roku") || name.Contains("chromecast") ||
            name.Contains("fire tv") || name.Contains("firestick") || name.Contains("streaming") ||
            name.Contains("nvidia shield"))
            return ClientDeviceCategory.StreamingDevice;

        // Smart TVs
        if (name.Contains("smart tv") || name.Contains("smarttv") ||
            (name.Contains("tv") && (name.Contains("samsung") || name.Contains("lg") || name.Contains("sony") || name.Contains("vizio"))))
            return ClientDeviceCategory.SmartTV;

        // Cameras
        if (name.Contains("camera") || name.Contains("doorbell") || name.Contains("nvr") ||
            name.Contains("ring") || name.Contains("arlo") || name.Contains("wyze") ||
            name.Contains("reolink") || name.Contains("hikvision") || name.Contains("dahua"))
            return ClientDeviceCategory.Camera;

        // Smart hubs
        if (name.Contains("hub") || name.Contains("bridge") || name.Contains("gateway") ||
            name.Contains("dirigera") || name.Contains("smartthings") || name.Contains("home assistant"))
            return ClientDeviceCategory.SmartHub;

        // Smart speakers
        if (name.Contains("echo") || name.Contains("alexa") || name.Contains("google home") ||
            name.Contains("homepod") || name.Contains("sonos") || name.Contains("smart speaker"))
            return ClientDeviceCategory.SmartSpeaker;

        // Smart lighting
        if (name.Contains("hue") || name.Contains("bulb") || name.Contains("light") ||
            name.Contains("lamp") || name.Contains("led strip") || name.Contains("lutron"))
            return ClientDeviceCategory.SmartLighting;

        // Smart plugs
        if (name.Contains("plug") || name.Contains("outlet") || name.Contains("power strip") ||
            name.Contains("smart switch") || name.Contains("wemo"))
            return ClientDeviceCategory.SmartPlug;

        // Thermostats
        if (name.Contains("thermostat") || name.Contains("nest") || name.Contains("ecobee"))
            return ClientDeviceCategory.SmartThermostat;

        // Smart locks
        if (name.Contains("lock") || name.Contains("deadbolt") || name.Contains("august") ||
            name.Contains("yale") || name.Contains("schlage"))
            return ClientDeviceCategory.SmartLock;

        // Robotic vacuums
        if (name.Contains("roomba") || name.Contains("vacuum") || name.Contains("roborock") ||
            name.Contains("irobot") || name.Contains("ecovacs"))
            return ClientDeviceCategory.RoboticVacuum;

        // Game consoles
        if (name.Contains("playstation") || name.Contains("xbox") || name.Contains("nintendo") ||
            name.Contains("ps4") || name.Contains("ps5") || name.Contains("switch"))
            return ClientDeviceCategory.GameConsole;

        // Printers
        if (name.Contains("printer") || name.Contains("print"))
            return ClientDeviceCategory.Printer;

        // NAS
        if (name.Contains("nas") || name.Contains("synology") || name.Contains("qnap"))
            return ClientDeviceCategory.NAS;

        // VoIP (only explicit voip keywords, not generic "phone")
        if (name.Contains("voip") || name.Contains("sip phone") || name.Contains("ip phone"))
            return ClientDeviceCategory.VoIP;

        // Smartphones (iPhones, Android phones, etc.)
        if (name.Contains("iphone") || name.Contains("galaxy") || name.Contains("pixel") ||
            name.Contains("android") || name.Contains("smartphone"))
            return ClientDeviceCategory.Smartphone;

        // Tablets
        if (name.Contains("ipad") || name.Contains("tablet") || name.Contains("galaxy tab"))
            return ClientDeviceCategory.Tablet;

        return ClientDeviceCategory.Unknown;
    }

    /// <summary>
    /// Map device category to recommended network purpose
    /// </summary>
    public static NetworkPurpose GetRecommendedNetwork(ClientDeviceCategory category)
    {
        return category switch
        {
            // Surveillance -> Security VLAN (local/self-hosted)
            ClientDeviceCategory.Camera => NetworkPurpose.Security,
            ClientDeviceCategory.SecuritySystem => NetworkPurpose.Security,

            // Cloud-based surveillance -> IoT VLAN (needs internet)
            ClientDeviceCategory.CloudCamera => NetworkPurpose.IoT,
            ClientDeviceCategory.CloudSecuritySystem => NetworkPurpose.IoT,

            // IoT -> IoT VLAN (isolated)
            ClientDeviceCategory.SmartLighting => NetworkPurpose.IoT,
            ClientDeviceCategory.SmartPlug => NetworkPurpose.IoT,
            ClientDeviceCategory.SmartThermostat => NetworkPurpose.IoT,
            ClientDeviceCategory.SmartLock => NetworkPurpose.IoT,
            ClientDeviceCategory.SmartSensor => NetworkPurpose.IoT,
            ClientDeviceCategory.SmartAppliance => NetworkPurpose.IoT,
            ClientDeviceCategory.SmartHub => NetworkPurpose.IoT,
            ClientDeviceCategory.RoboticVacuum => NetworkPurpose.IoT,
            ClientDeviceCategory.IoTGeneric => NetworkPurpose.IoT,
            ClientDeviceCategory.SmartSpeaker => NetworkPurpose.IoT,

            // Media can go to IoT or Corporate depending on policy
            ClientDeviceCategory.SmartTV => NetworkPurpose.IoT,
            ClientDeviceCategory.StreamingDevice => NetworkPurpose.IoT,
            ClientDeviceCategory.MediaPlayer => NetworkPurpose.IoT,
            ClientDeviceCategory.GameConsole => NetworkPurpose.Corporate,

            // Computing -> Corporate
            ClientDeviceCategory.Desktop => NetworkPurpose.Corporate,
            ClientDeviceCategory.Laptop => NetworkPurpose.Corporate,
            ClientDeviceCategory.Server => NetworkPurpose.Corporate,
            ClientDeviceCategory.NAS => NetworkPurpose.Corporate,
            ClientDeviceCategory.Smartphone => NetworkPurpose.Corporate,
            ClientDeviceCategory.Tablet => NetworkPurpose.Corporate,

            // VoIP needs special consideration (often separate VLAN)
            ClientDeviceCategory.VoIP => NetworkPurpose.Corporate,

            // Infrastructure -> Management
            ClientDeviceCategory.AccessPoint => NetworkPurpose.Management,
            ClientDeviceCategory.Switch => NetworkPurpose.Management,
            ClientDeviceCategory.Router => NetworkPurpose.Management,
            ClientDeviceCategory.Gateway => NetworkPurpose.Management,

            // Printers -> Corporate
            ClientDeviceCategory.Printer => NetworkPurpose.Corporate,
            ClientDeviceCategory.Scanner => NetworkPurpose.Corporate,

            _ => NetworkPurpose.Unknown
        };
    }
}
