using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.Audit.Services.Detectors;

/// <summary>
/// Detects device type from UniFi fingerprint data (dev_cat, dev_type_id, etc.)
/// This is the highest confidence detection method.
/// </summary>
public class FingerprintDetector
{
    private readonly UniFiFingerprintDatabase? _database;

    /// <summary>
    /// Map UniFi dev_type_ids to our categories
    /// Based on the fingerprint database dev_type_ids mapping
    /// </summary>
    private static readonly Dictionary<int, ClientDeviceCategory> DevTypeMapping = new()
    {
        // Cameras/Surveillance
        { 9, ClientDeviceCategory.Camera },         // IP Network Camera
        { 57, ClientDeviceCategory.Camera },        // Smart Security Camera
        { 106, ClientDeviceCategory.Camera },       // Camera
        { 116, ClientDeviceCategory.SecuritySystem }, // Surveillance System
        { 111, ClientDeviceCategory.SecuritySystem }, // Security Panel
        { 124, ClientDeviceCategory.Camera },       // Network Video Recorder
        { 147, ClientDeviceCategory.Camera },       // Doorbell Camera
        { 161, ClientDeviceCategory.Camera },       // Video Doorbell

        // Smart Lighting
        { 35, ClientDeviceCategory.SmartLighting }, // Wireless Lighting
        { 53, ClientDeviceCategory.SmartLighting }, // Smart Lighting Device
        { 179, ClientDeviceCategory.SmartLighting }, // LED Lighting
        { 184, ClientDeviceCategory.SmartLighting }, // Smart Light Strip

        // Smart Plugs/Outlets
        { 42, ClientDeviceCategory.SmartPlug },     // Smart Plug
        { 97, ClientDeviceCategory.SmartPlug },     // Smart Power Strip
        { 153, ClientDeviceCategory.SmartPlug },    // Smart Socket

        // Thermostats/HVAC
        { 63, ClientDeviceCategory.SmartThermostat }, // Smart Thermostat
        { 70, ClientDeviceCategory.SmartThermostat }, // Smart Heating Device
        { 71, ClientDeviceCategory.SmartAppliance },  // Air Conditioner

        // Smart Locks
        { 133, ClientDeviceCategory.SmartLock },    // Door Lock
        { 125, ClientDeviceCategory.SmartLock },    // Touch Screen Deadbolt

        // Smart Sensors
        { 100, ClientDeviceCategory.SmartSensor },  // Weather Station
        { 148, ClientDeviceCategory.SmartSensor },  // Air Quality Monitor
        { 234, ClientDeviceCategory.SmartSensor },  // Weather Monitor
        { 139, ClientDeviceCategory.SmartSensor },  // Water Monitor
        { 109, ClientDeviceCategory.SmartSensor },  // Sleep Monitor

        // Smart Appliances
        { 48, ClientDeviceCategory.SmartAppliance }, // Intelligent Home Appliances
        { 131, ClientDeviceCategory.SmartAppliance }, // Washing Machine
        { 140, ClientDeviceCategory.SmartAppliance }, // Dishwasher
        { 118, ClientDeviceCategory.SmartAppliance }, // Dryer
        { 92, ClientDeviceCategory.SmartAppliance },  // Air Purifier
        { 149, ClientDeviceCategory.SmartAppliance }, // Smart Kettle

        // Smart Hubs
        { 144, ClientDeviceCategory.SmartHub },     // Smart Hub
        { 93, ClientDeviceCategory.SmartHub },      // Home Automation
        { 154, ClientDeviceCategory.SmartHub },     // Smart Bridge

        // Robotic Vacuums
        { 41, ClientDeviceCategory.RoboticVacuum }, // Robotic Vacuums
        { 65, ClientDeviceCategory.RoboticVacuum }, // Smart Cleaning Device

        // Smart TVs
        { 31, ClientDeviceCategory.SmartTV },       // SmartTV
        { 47, ClientDeviceCategory.SmartTV },       // Smart TV & Set-top box
        { 50, ClientDeviceCategory.SmartTV },       // Smart TV & Set-top box

        // Streaming Devices
        { 5, ClientDeviceCategory.StreamingDevice }, // IPTV
        { 238, ClientDeviceCategory.StreamingDevice }, // Media Player
        { 242, ClientDeviceCategory.StreamingDevice }, // Streaming Media Device
        { 186, ClientDeviceCategory.StreamingDevice }, // IPTV Set Top Box

        // Smart Speakers
        { 37, ClientDeviceCategory.SmartSpeaker },  // Smart Speaker
        { 52, ClientDeviceCategory.SmartSpeaker },  // Smart Audio Device
        { 170, ClientDeviceCategory.SmartSpeaker }, // Wifi Speaker

        // Media Players
        { 20, ClientDeviceCategory.MediaPlayer },   // Multimedia Device
        { 69, ClientDeviceCategory.MediaPlayer },   // AV Receiver
        { 73, ClientDeviceCategory.MediaPlayer },   // Soundbar
        { 96, ClientDeviceCategory.MediaPlayer },   // Audio Streamer
        { 132, ClientDeviceCategory.MediaPlayer },  // Music Server
        { 152, ClientDeviceCategory.MediaPlayer },  // Blu Ray Player

        // Game Consoles
        { 17, ClientDeviceCategory.GameConsole },   // Game Console

        // Computers
        { 1, ClientDeviceCategory.Laptop },         // Desktop/Laptop
        { 46, ClientDeviceCategory.Desktop },       // Computer
        { 56, ClientDeviceCategory.Server },        // Server
        { 28, ClientDeviceCategory.Desktop },       // Workstation
        { 25, ClientDeviceCategory.Desktop },       // Thin Client

        // NAS
        { 18, ClientDeviceCategory.NAS },           // NAS
        { 91, ClientDeviceCategory.NAS },           // Network Storage

        // Mobile Devices
        { 6, ClientDeviceCategory.Smartphone },     // Smartphone
        { 44, ClientDeviceCategory.Smartphone },    // Handheld
        { 29, ClientDeviceCategory.Smartphone },    // Apple iOS Device
        { 32, ClientDeviceCategory.Smartphone },    // Android Device
        { 30, ClientDeviceCategory.Tablet },        // Tablet

        // VoIP
        { 3, ClientDeviceCategory.VoIP },           // VoIP Phone
        { 10, ClientDeviceCategory.VoIP },          // VoIP Gateway
        { 26, ClientDeviceCategory.VoIP },          // Phone
        { 27, ClientDeviceCategory.VoIP },          // Video Phone

        // Network Infrastructure
        { 12, ClientDeviceCategory.AccessPoint },   // Access Point
        { 14, ClientDeviceCategory.AccessPoint },   // Wireless Controller
        { 13, ClientDeviceCategory.Switch },        // Switch
        { 2, ClientDeviceCategory.Router },         // Router
        { 8, ClientDeviceCategory.Router },         // Router
        { 82, ClientDeviceCategory.Router },        // Firewall System

        // Printers
        { 11, ClientDeviceCategory.Printer },       // Printer
        { 22, ClientDeviceCategory.Scanner },       // Scanner
        { 146, ClientDeviceCategory.Printer },      // 3D Printer
        { 171, ClientDeviceCategory.Printer },      // Label Printer

        // Generic IoT
        { 51, ClientDeviceCategory.IoTGeneric },    // Smart Device
        { 66, ClientDeviceCategory.IoTGeneric },    // IoT Device
        { 64, ClientDeviceCategory.IoTGeneric },    // Smart Garden Device
        { 60, ClientDeviceCategory.IoTGeneric },    // Alarm System
        { 80, ClientDeviceCategory.SecuritySystem }, // Smart Home Security System
        { 120, ClientDeviceCategory.IoTGeneric },   // Garage Opener
        { 83, ClientDeviceCategory.IoTGeneric },    // Garage Door
        { 77, ClientDeviceCategory.IoTGeneric },    // Sprinkler Controller
        { 130, ClientDeviceCategory.IoTGeneric },   // Irrigation Controller
        { 45, ClientDeviceCategory.SmartSensor },   // Wearable devices
        { 36, ClientDeviceCategory.SmartSensor },   // Smart Watch
    };

    public FingerprintDetector(UniFiFingerprintDatabase? database = null)
    {
        _database = database;
    }

    /// <summary>
    /// Detect device type from UniFi client fingerprint data.
    /// Checks user-selected device type (DevIdOverride) first, then auto-detected (DevCat).
    /// </summary>
    public DeviceDetectionResult Detect(UniFiClientResponse client)
    {
        // Priority 1: User-selected device type override (from UniFi UI)
        if (client.DevIdOverride.HasValue && DevTypeMapping.TryGetValue(client.DevIdOverride.Value, out var overrideCategory))
        {
            var vendorName = _database?.GetVendorName(client.DevVendor);
            var typeName = _database?.GetDeviceTypeName(client.DevIdOverride);

            return new DeviceDetectionResult
            {
                Category = overrideCategory,
                Source = DetectionSource.UniFiFingerprint,
                ConfidenceScore = 98, // Highest confidence - user explicitly selected this
                VendorName = vendorName,
                ProductName = typeName,
                RecommendedNetwork = GetRecommendedNetwork(overrideCategory),
                Metadata = new Dictionary<string, object>
                {
                    ["dev_id_override"] = client.DevIdOverride.Value,
                    ["dev_cat"] = client.DevCat ?? 0,
                    ["dev_family"] = client.DevFamily ?? 0,
                    ["dev_vendor"] = client.DevVendor ?? 0,
                    ["user_override"] = true
                }
            };
        }

        // Priority 2: Auto-detected device category
        if (client.DevCat.HasValue && DevTypeMapping.TryGetValue(client.DevCat.Value, out var category))
        {
            var vendorName = _database?.GetVendorName(client.DevVendor);
            var typeName = _database?.GetDeviceTypeName(client.DevCat);

            return new DeviceDetectionResult
            {
                Category = category,
                Source = DetectionSource.UniFiFingerprint,
                ConfidenceScore = 95, // High confidence from fingerprint
                VendorName = vendorName,
                ProductName = typeName,
                RecommendedNetwork = GetRecommendedNetwork(category),
                Metadata = new Dictionary<string, object>
                {
                    ["dev_cat"] = client.DevCat.Value,
                    ["dev_family"] = client.DevFamily ?? 0,
                    ["dev_vendor"] = client.DevVendor ?? 0
                }
            };
        }

        return DeviceDetectionResult.Unknown;
    }

    /// <summary>
    /// Map device category to recommended network purpose
    /// </summary>
    public static NetworkPurpose GetRecommendedNetwork(ClientDeviceCategory category)
    {
        return category switch
        {
            // Surveillance -> Security VLAN
            ClientDeviceCategory.Camera => NetworkPurpose.Security,
            ClientDeviceCategory.SecuritySystem => NetworkPurpose.Security,

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
