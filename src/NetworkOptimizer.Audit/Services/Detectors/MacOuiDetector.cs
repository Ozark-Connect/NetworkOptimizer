using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.Core.Enums;

namespace NetworkOptimizer.Audit.Services.Detectors;

/// <summary>
/// Detects device type from MAC address OUI (vendor prefix).
/// Contains vendor-to-category mappings for common IoT manufacturers.
/// </summary>
public class MacOuiDetector
{
    /// <summary>
    /// MAC OUI prefix (first 3 bytes) to vendor and likely category
    /// Format: "AA:BB:CC" -> (VendorName, LikelyCategory, Confidence)
    /// </summary>
    private static readonly Dictionary<string, (string Vendor, ClientDeviceCategory Category, int Confidence)> OuiMappings = new(StringComparer.OrdinalIgnoreCase)
    {
        // Ring devices (cameras, doorbells)
        { "0C:47:C9", ("Ring", ClientDeviceCategory.Camera, 85) },
        { "34:1F:4F", ("Ring", ClientDeviceCategory.Camera, 85) },
        { "44:73:D6", ("Ring", ClientDeviceCategory.Camera, 85) },
        { "A4:DA:22", ("Ring", ClientDeviceCategory.Camera, 85) },
        { "90:48:9A", ("Ring", ClientDeviceCategory.Camera, 85) },

        // Nest/Google Home
        { "18:B4:30", ("Nest", ClientDeviceCategory.SmartThermostat, 80) },
        { "64:16:66", ("Nest", ClientDeviceCategory.SmartThermostat, 80) },
        { "F4:F5:D8", ("Google/Nest", ClientDeviceCategory.SmartSpeaker, 80) },
        { "30:FD:38", ("Google/Nest", ClientDeviceCategory.SmartSpeaker, 80) },
        { "1C:F2:9A", ("Google", ClientDeviceCategory.SmartSpeaker, 80) },
        { "54:60:09", ("Google", ClientDeviceCategory.SmartSpeaker, 80) },

        // Amazon Echo/Alexa/Fire
        { "84:D6:D0", ("Amazon Echo", ClientDeviceCategory.SmartSpeaker, 85) },
        { "FC:65:DE", ("Amazon Echo", ClientDeviceCategory.SmartSpeaker, 85) },
        { "68:54:FD", ("Amazon Echo", ClientDeviceCategory.SmartSpeaker, 85) },
        { "F0:F0:A4", ("Amazon Echo", ClientDeviceCategory.SmartSpeaker, 85) },
        { "74:C2:46", ("Amazon Echo", ClientDeviceCategory.SmartSpeaker, 85) },
        { "4C:EF:C0", ("Amazon Fire", ClientDeviceCategory.StreamingDevice, 85) },
        { "00:FC:8B", ("Amazon Fire", ClientDeviceCategory.StreamingDevice, 85) },
        { "44:65:0D", ("Amazon", ClientDeviceCategory.SmartSpeaker, 80) },

        // Sonos
        { "00:0E:58", ("Sonos", ClientDeviceCategory.MediaPlayer, 90) },
        { "5C:AA:FD", ("Sonos", ClientDeviceCategory.MediaPlayer, 90) },
        { "94:9F:3E", ("Sonos", ClientDeviceCategory.MediaPlayer, 90) },
        { "78:28:CA", ("Sonos", ClientDeviceCategory.MediaPlayer, 90) },
        { "B8:E9:37", ("Sonos", ClientDeviceCategory.MediaPlayer, 90) },
        { "54:2A:1B", ("Sonos", ClientDeviceCategory.MediaPlayer, 90) },
        { "34:7E:5C", ("Sonos", ClientDeviceCategory.MediaPlayer, 90) },

        // Philips Hue
        { "00:17:88", ("Philips Hue", ClientDeviceCategory.SmartLighting, 90) },
        { "EC:B5:FA", ("Philips Hue", ClientDeviceCategory.SmartLighting, 90) },

        // IKEA Tradfri
        { "94:54:93", ("IKEA", ClientDeviceCategory.SmartLighting, 85) },
        { "D0:CF:5E", ("IKEA", ClientDeviceCategory.SmartLighting, 85) },
        { "CC:50:E3", ("IKEA", ClientDeviceCategory.SmartLighting, 85) },

        // LIFX
        { "D0:73:D5", ("LIFX", ClientDeviceCategory.SmartLighting, 90) },

        // Lutron
        { "00:0D:5C", ("Lutron", ClientDeviceCategory.SmartLighting, 85) },
        { "74:F9:4C", ("Lutron", ClientDeviceCategory.SmartLighting, 85) },

        // Roku
        { "08:05:81", ("Roku", ClientDeviceCategory.StreamingDevice, 90) },
        { "D4:3A:2E", ("Roku", ClientDeviceCategory.StreamingDevice, 90) },
        { "B0:A7:37", ("Roku", ClientDeviceCategory.StreamingDevice, 90) },
        { "CC:6D:A0", ("Roku", ClientDeviceCategory.StreamingDevice, 90) },
        { "B8:3E:59", ("Roku", ClientDeviceCategory.StreamingDevice, 90) },
        { "C8:3A:6B", ("Roku", ClientDeviceCategory.StreamingDevice, 90) },

        // Apple TV (note: Apple devices can be many things)
        { "40:CB:C0", ("Apple TV", ClientDeviceCategory.StreamingDevice, 75) },
        { "70:56:81", ("Apple TV", ClientDeviceCategory.StreamingDevice, 75) },
        { "68:D9:3C", ("Apple TV", ClientDeviceCategory.StreamingDevice, 75) },

        // Chromecast
        { "54:60:09", ("Chromecast", ClientDeviceCategory.StreamingDevice, 85) },
        { "6C:AD:F8", ("Chromecast", ClientDeviceCategory.StreamingDevice, 85) },

        // PlayStation
        { "00:04:1F", ("Sony PlayStation", ClientDeviceCategory.GameConsole, 90) },
        { "00:15:C1", ("Sony PlayStation", ClientDeviceCategory.GameConsole, 90) },
        { "00:19:C5", ("Sony PlayStation", ClientDeviceCategory.GameConsole, 90) },
        { "78:C8:81", ("Sony PlayStation", ClientDeviceCategory.GameConsole, 90) },
        { "28:3F:69", ("Sony PlayStation", ClientDeviceCategory.GameConsole, 90) },
        { "70:9E:29", ("Sony PlayStation", ClientDeviceCategory.GameConsole, 90) },
        { "F8:D0:AC", ("Sony PlayStation", ClientDeviceCategory.GameConsole, 90) },
        { "A8:E3:EE", ("Sony PlayStation", ClientDeviceCategory.GameConsole, 90) },

        // Xbox
        { "00:0D:3A", ("Microsoft Xbox", ClientDeviceCategory.GameConsole, 90) },
        { "7C:ED:8D", ("Microsoft Xbox", ClientDeviceCategory.GameConsole, 90) },
        { "60:45:BD", ("Microsoft Xbox", ClientDeviceCategory.GameConsole, 90) },
        { "94:9A:A9", ("Microsoft Xbox", ClientDeviceCategory.GameConsole, 90) },
        { "98:5F:D3", ("Microsoft Xbox", ClientDeviceCategory.GameConsole, 90) },

        // Nintendo
        { "00:1F:32", ("Nintendo", ClientDeviceCategory.GameConsole, 90) },
        { "00:1A:E9", ("Nintendo", ClientDeviceCategory.GameConsole, 90) },
        { "00:1E:A9", ("Nintendo", ClientDeviceCategory.GameConsole, 90) },
        { "00:22:D7", ("Nintendo", ClientDeviceCategory.GameConsole, 90) },
        { "00:23:31", ("Nintendo", ClientDeviceCategory.GameConsole, 90) },
        { "7C:BB:8A", ("Nintendo", ClientDeviceCategory.GameConsole, 90) },
        { "E8:4E:CE", ("Nintendo", ClientDeviceCategory.GameConsole, 90) },
        { "04:03:D6", ("Nintendo", ClientDeviceCategory.GameConsole, 90) },
        { "98:B6:E9", ("Nintendo", ClientDeviceCategory.GameConsole, 90) },

        // Synology
        { "00:11:32", ("Synology", ClientDeviceCategory.NAS, 95) },

        // QNAP
        { "00:08:9B", ("QNAP", ClientDeviceCategory.NAS, 95) },
        { "24:5E:BE", ("QNAP", ClientDeviceCategory.NAS, 95) },

        // TP-Link Kasa/Smart devices
        { "50:C7:BF", ("TP-Link", ClientDeviceCategory.SmartPlug, 70) },
        { "98:DA:C4", ("TP-Link", ClientDeviceCategory.SmartPlug, 70) },
        { "1C:61:B4", ("TP-Link", ClientDeviceCategory.SmartPlug, 70) },
        { "68:FF:7B", ("TP-Link", ClientDeviceCategory.SmartPlug, 70) },
        { "54:AF:97", ("TP-Link", ClientDeviceCategory.SmartPlug, 70) },

        // Wyze
        { "2C:AA:8E", ("Wyze", ClientDeviceCategory.Camera, 85) },
        { "D0:3F:27", ("Wyze", ClientDeviceCategory.Camera, 85) },

        // Arlo
        { "4C:77:6D", ("Arlo", ClientDeviceCategory.Camera, 90) },

        // Eufy
        { "8C:85:80", ("Eufy", ClientDeviceCategory.Camera, 85) },
        { "AC:0B:FB", ("Eufy", ClientDeviceCategory.RoboticVacuum, 85) },

        // Reolink
        { "EC:71:DB", ("Reolink", ClientDeviceCategory.Camera, 90) },

        // Blink
        { "9C:55:B4", ("Blink", ClientDeviceCategory.Camera, 85) },

        // Ecobee
        { "44:61:32", ("Ecobee", ClientDeviceCategory.SmartThermostat, 90) },

        // Honeywell
        { "00:D0:2D", ("Honeywell", ClientDeviceCategory.SmartThermostat, 75) },

        // iRobot Roomba
        { "50:14:79", ("iRobot", ClientDeviceCategory.RoboticVacuum, 90) },

        // Roborock
        { "50:EC:50", ("Roborock", ClientDeviceCategory.RoboticVacuum, 90) },
        { "78:11:DC", ("Roborock", ClientDeviceCategory.RoboticVacuum, 90) },

        // Ecovacs
        { "C8:95:2C", ("Ecovacs", ClientDeviceCategory.RoboticVacuum, 90) },

        // August (smart locks)
        { "D8:6C:63", ("August", ClientDeviceCategory.SmartLock, 85) },

        // Yale (smart locks)
        { "00:17:C9", ("Yale", ClientDeviceCategory.SmartLock, 80) },
        { "00:1C:97", ("Yale", ClientDeviceCategory.SmartLock, 80) },

        // Schlage
        { "00:1A:22", ("Schlage", ClientDeviceCategory.SmartLock, 85) },

        // Samsung SmartThings
        { "28:6D:97", ("Samsung SmartThings", ClientDeviceCategory.SmartHub, 85) },
        { "D0:52:A8", ("Samsung SmartThings", ClientDeviceCategory.SmartHub, 85) },
        { "24:DF:A7", ("Samsung SmartThings", ClientDeviceCategory.SmartHub, 85) },

        // Wemo (Belkin)
        { "24:F5:A2", ("Wemo", ClientDeviceCategory.SmartPlug, 85) },
        { "B4:75:0E", ("Wemo", ClientDeviceCategory.SmartPlug, 85) },
        { "94:10:3E", ("Wemo", ClientDeviceCategory.SmartPlug, 85) },

        // Meross
        { "48:E1:E9", ("Meross", ClientDeviceCategory.SmartPlug, 85) },

        // UniFi Protect cameras
        { "FC:EC:DA", ("UniFi Protect", ClientDeviceCategory.Camera, 95) },
        { "24:5A:4C", ("UniFi Protect", ClientDeviceCategory.Camera, 95) },
        { "B4:FB:E4", ("UniFi Protect", ClientDeviceCategory.Camera, 95) },

        // Hikvision
        { "C4:2F:90", ("Hikvision", ClientDeviceCategory.Camera, 90) },
        { "44:19:B6", ("Hikvision", ClientDeviceCategory.Camera, 90) },

        // Dahua
        { "3C:EF:8C", ("Dahua", ClientDeviceCategory.Camera, 90) },
        { "A0:BD:1D", ("Dahua", ClientDeviceCategory.Camera, 90) },

        // Amcrest
        { "9C:8E:CD", ("Amcrest", ClientDeviceCategory.Camera, 90) },
    };

    /// <summary>
    /// Detect device type from MAC address
    /// </summary>
    public DeviceDetectionResult Detect(string macAddress)
    {
        if (string.IsNullOrEmpty(macAddress))
            return DeviceDetectionResult.Unknown;

        // Normalize MAC to format "XX:XX:XX"
        var oui = NormalizeOui(macAddress);

        if (OuiMappings.TryGetValue(oui, out var mapping))
        {
            return new DeviceDetectionResult
            {
                Category = mapping.Category,
                Source = DetectionSource.MacOui,
                ConfidenceScore = mapping.Confidence,
                VendorName = mapping.Vendor,
                RecommendedNetwork = FingerprintDetector.GetRecommendedNetwork(mapping.Category),
                Metadata = new Dictionary<string, object>
                {
                    ["oui"] = oui,
                    ["vendor"] = mapping.Vendor
                }
            };
        }

        return DeviceDetectionResult.Unknown;
    }

    /// <summary>
    /// Normalize MAC address to OUI format (first 3 octets, uppercase, colon-separated)
    /// </summary>
    private static string NormalizeOui(string mac)
    {
        // Remove common separators and take first 6 characters
        var cleaned = mac.Replace(":", "").Replace("-", "").Replace(".", "").ToUpperInvariant();
        if (cleaned.Length >= 6)
        {
            return $"{cleaned[0..2]}:{cleaned[2..4]}:{cleaned[4..6]}";
        }
        return mac.ToUpperInvariant();
    }
}
