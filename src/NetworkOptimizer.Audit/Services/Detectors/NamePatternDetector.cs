using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.Core.Enums;

namespace NetworkOptimizer.Audit.Services.Detectors;

/// <summary>
/// Detects device type from device/port names using pattern matching.
/// This is the lowest priority detection method but works when no other data is available.
/// </summary>
public class NamePatternDetector
{
    /// <summary>
    /// Pattern groups with category mapping
    /// Ordered by specificity (more specific patterns first)
    /// </summary>
    private static readonly List<(string[] Patterns, ClientDeviceCategory Category, int Confidence)> PatternGroups = new()
    {
        // Smart Plugs - CHECK FIRST before cameras (Cync/GE/WYZE make both cameras and plugs)
        (new[] { "plug", "outlet", "power strip" },
            ClientDeviceCategory.SmartPlug, 88),
        (new[] { "cync plug", "ge plug", "wyze plug" },
            ClientDeviceCategory.SmartPlug, 92),

        // Cameras (high confidence, specific patterns)
        // Note: "cam" with word boundary is handled in CheckObviousNameOverride (Priority 0)
        // Note: "protect" removed - UniFi Protect cameras are detected via API; "protect" also matches Nest Protect smoke alarms
        (new[] { "camera", "ptz", "nvr", "ipc", "surveillance", "cctv", "security cam", "webcam", "driveway cam", "front cam", "back cam", "garage cam" },
            ClientDeviceCategory.Camera, 85),

        // Doorbells (often have video)
        (new[] { "doorbell", "ring doorbell", "nest doorbell" },
            ClientDeviceCategory.Camera, 80),

        // Smart Speakers (specific brands first)
        (new[] { "alexa", "echo dot", "echo show", "echo plus", "echo studio" },
            ClientDeviceCategory.SmartSpeaker, 90),
        (new[] { "google home", "nest mini", "nest hub", "nest audio" },
            ClientDeviceCategory.SmartSpeaker, 90),
        (new[] { "homepod" },
            ClientDeviceCategory.SmartSpeaker, 90),
        (new[] { "smart speaker" },
            ClientDeviceCategory.SmartSpeaker, 75),

        // Streaming Devices (specific brands)
        (new[] { "roku", "roku tv", "roku stick", "roku ultra" },
            ClientDeviceCategory.StreamingDevice, 90),
        (new[] { "apple tv", "appletv" },
            ClientDeviceCategory.StreamingDevice, 90),
        (new[] { "fire tv", "firetv", "fire stick", "firestick" },
            ClientDeviceCategory.StreamingDevice, 90),
        (new[] { "chromecast" },
            ClientDeviceCategory.StreamingDevice, 90),
        (new[] { "nvidia shield" },
            ClientDeviceCategory.StreamingDevice, 90),

        // Media Players (Sonos, audio equipment)
        (new[] { "sonos", "sonos one", "sonos beam", "sonos arc", "sonos sub", "sonos port" },
            ClientDeviceCategory.MediaPlayer, 90),
        (new[] { "soundbar", "sound bar" },
            ClientDeviceCategory.MediaPlayer, 80),
        (new[] { "receiver", "av receiver" },
            ClientDeviceCategory.MediaPlayer, 75),

        // Smart TVs
        (new[] { "samsung tv", "lg tv", "sony tv", "vizio tv", "tcl tv", "hisense tv" },
            ClientDeviceCategory.SmartTV, 85),
        (new[] { "smart tv", "smarttv", "television", " tv " },
            ClientDeviceCategory.SmartTV, 70),

        // Game Consoles
        (new[] { "playstation", "ps4", "ps5", "ps3" },
            ClientDeviceCategory.GameConsole, 90),
        (new[] { "xbox", "xbox one", "xbox series" },
            ClientDeviceCategory.GameConsole, 90),
        (new[] { "nintendo", "switch", "wii" },
            ClientDeviceCategory.GameConsole, 85),

        // VR Headsets (gaming devices)
        (new[] { "quest", "quest 2", "quest 3", "quest pro", "oculus", "meta quest" },
            ClientDeviceCategory.GameConsole, 90),
        (new[] { "vr", "vive", "htc vive", "valve index", "psvr", "pico" },
            ClientDeviceCategory.GameConsole, 85),

        // Smart Lighting (specific brands)
        (new[] { "hue", "philips hue", "hue bridge" },
            ClientDeviceCategory.SmartLighting, 90),
        (new[] { "ikea", "tradfri" },
            ClientDeviceCategory.SmartLighting, 85),
        (new[] { "lifx" },
            ClientDeviceCategory.SmartLighting, 90),
        (new[] { "lutron", "caseta" },
            ClientDeviceCategory.SmartLighting, 85),
        (new[] { "smart bulb", "led strip", "light" },
            ClientDeviceCategory.SmartLighting, 75),

        // Smart Plugs
        (new[] { "kasa", "tp-link plug", "tp-link smart" },
            ClientDeviceCategory.SmartPlug, 85),
        (new[] { "wemo", "wemo plug" },
            ClientDeviceCategory.SmartPlug, 85),
        (new[] { "meross" },
            ClientDeviceCategory.SmartPlug, 85),
        (new[] { "smart plug", "smartplug", "smart outlet" },
            ClientDeviceCategory.SmartPlug, 75),

        // Thermostats
        (new[] { "nest thermostat", "nest learning" },
            ClientDeviceCategory.SmartThermostat, 90),
        (new[] { "ecobee" },
            ClientDeviceCategory.SmartThermostat, 90),
        (new[] { "thermostat", "hvac" },
            ClientDeviceCategory.SmartThermostat, 75),

        // Robotic Vacuums
        (new[] { "roomba", "irobot" },
            ClientDeviceCategory.RoboticVacuum, 90),
        (new[] { "roborock" },
            ClientDeviceCategory.RoboticVacuum, 90),
        (new[] { "ecovacs", "deebot" },
            ClientDeviceCategory.RoboticVacuum, 90),
        (new[] { "eufy", "robovac" },
            ClientDeviceCategory.RoboticVacuum, 85),
        (new[] { "neato" },
            ClientDeviceCategory.RoboticVacuum, 90),
        (new[] { "robot vacuum", "vacuum" },
            ClientDeviceCategory.RoboticVacuum, 70),

        // Smart Locks
        (new[] { "august", "august lock" },
            ClientDeviceCategory.SmartLock, 90),
        (new[] { "yale", "yale lock" },
            ClientDeviceCategory.SmartLock, 85),
        (new[] { "schlage", "schlage lock" },
            ClientDeviceCategory.SmartLock, 85),
        (new[] { "smart lock", "deadbolt" },
            ClientDeviceCategory.SmartLock, 70),

        // Smart Hubs
        (new[] { "smartthings", "smart things" },
            ClientDeviceCategory.SmartHub, 85),
        (new[] { "hubitat" },
            ClientDeviceCategory.SmartHub, 90),
        (new[] { "home assistant", "homeassistant" },
            ClientDeviceCategory.SmartHub, 85),
        (new[] { "smart hub" },
            ClientDeviceCategory.SmartHub, 70),

        // NAS
        (new[] { "synology", "diskstation", "ds920", "ds720", "ds220", "ds418", "ds918" },
            ClientDeviceCategory.NAS, 95),
        (new[] { "qnap", "qnap ts-", "qnap tvs-" },
            ClientDeviceCategory.NAS, 95),
        (new[] { "nas", "network storage" },
            ClientDeviceCategory.NAS, 75),

        // Servers
        (new[] { "server", "proxmox", "esxi", "truenas", "unraid", "docker" },
            ClientDeviceCategory.Server, 80),

        // VoIP
        (new[] { "voip", "polycom", "yealink", "grandstream", "cisco phone" },
            ClientDeviceCategory.VoIP, 85),
        (new[] { "sip phone", "ip phone" },
            ClientDeviceCategory.VoIP, 75),

        // Printers
        (new[] { "printer", "print server", "hp printer", "epson", "canon printer", "brother" },
            ClientDeviceCategory.Printer, 80),

        // Access Points
        (new[] { "unifi ap", "uap", "access point", "wifi ap", "u6" },
            ClientDeviceCategory.AccessPoint, 85),

        // Generic IoT (lowest priority catch-all)
        (new[] { "iot", "smart home" },
            ClientDeviceCategory.IoTGeneric, 50),

        // Generic smart device mention (very low confidence)
        (new[] { "smart" },
            ClientDeviceCategory.IoTGeneric, 30),
    };

    /// <summary>
    /// Detect device type from name
    /// </summary>
    public DeviceDetectionResult Detect(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return DeviceDetectionResult.Unknown;

        var nameLower = name.ToLowerInvariant();

        foreach (var (patterns, category, confidence) in PatternGroups)
        {
            var matchedPattern = patterns.FirstOrDefault(p => nameLower.Contains(p));
            if (matchedPattern != null)
            {
                return new DeviceDetectionResult
                {
                    Category = category,
                    Source = DetectionSource.DeviceName,
                    ConfidenceScore = confidence,
                    RecommendedNetwork = FingerprintDetector.GetRecommendedNetwork(category),
                    Metadata = new Dictionary<string, object>
                    {
                        ["matched_name"] = name,
                        ["matched_pattern"] = matchedPattern
                    }
                };
            }
        }

        return DeviceDetectionResult.Unknown;
    }

    /// <summary>
    /// Detect device type from port name (slightly lower confidence than device name)
    /// </summary>
    public DeviceDetectionResult DetectFromPortName(string portName)
    {
        var result = Detect(portName);
        if (result.Category != ClientDeviceCategory.Unknown)
        {
            // Port names are slightly less reliable than device names
            return new DeviceDetectionResult
            {
                Category = result.Category,
                Source = DetectionSource.PortName,
                ConfidenceScore = Math.Max(result.ConfidenceScore - 10, 20),
                VendorName = result.VendorName,
                ProductName = result.ProductName,
                RecommendedNetwork = result.RecommendedNetwork,
                Metadata = result.Metadata
            };
        }
        return DeviceDetectionResult.Unknown;
    }
}
