using System.Text.Json.Serialization;

namespace NetworkOptimizer.UniFi.Models;

/// <summary>
/// Response from v2 device API containing all UniFi device types
/// GET /proxy/network/v2/api/site/{site}/device
/// </summary>
public class UniFiAllDevicesResponse
{
    [JsonPropertyName("network_devices")]
    public List<UniFiDeviceResponse>? NetworkDevices { get; set; }

    [JsonPropertyName("protect_devices")]
    public List<UniFiProtectDeviceResponse>? ProtectDevices { get; set; }

    [JsonPropertyName("access_devices")]
    public List<UniFiProtectDeviceResponse>? AccessDevices { get; set; }

    [JsonPropertyName("connect_devices")]
    public List<UniFiProtectDeviceResponse>? ConnectDevices { get; set; }

    [JsonPropertyName("led_devices")]
    public List<UniFiProtectDeviceResponse>? LedDevices { get; set; }
}

/// <summary>
/// UniFi Protect device (camera, doorbell, NVR, sensor, etc.)
/// </summary>
public class UniFiProtectDeviceResponse
{
    [JsonPropertyName("_id")]
    public string? Id { get; set; }

    [JsonPropertyName("mac")]
    public string Mac { get; set; } = string.Empty;

    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("product_line")]
    public string ProductLine { get; set; } = string.Empty;

    [JsonPropertyName("state")]
    public int State { get; set; }

    [JsonPropertyName("tags")]
    public List<string>? Tags { get; set; }

    [JsonPropertyName("ip")]
    public string? Ip { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("uptime")]
    public long Uptime { get; set; }

    [JsonPropertyName("connection_network_id")]
    public string? ConnectionNetworkId { get; set; }

    [JsonPropertyName("connection_network_name")]
    public string? ConnectionNetworkName { get; set; }

    [JsonPropertyName("uplink_mac")]
    public string? UplinkMac { get; set; }

    /// <summary>
    /// Check if this is a camera device based on model name
    /// </summary>
    public bool IsCamera => IsCameraModel(Model);

    /// <summary>
    /// Check if this is a doorbell device
    /// </summary>
    public bool IsDoorbell => Model.Contains("Doorbell", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Check if this is an NVR device
    /// </summary>
    public bool IsNvr => Model.Contains("NVR", StringComparison.OrdinalIgnoreCase) ||
                         Model.Contains("UNVR", StringComparison.OrdinalIgnoreCase) ||
                         Model.Contains("Cloud Key", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Check if this is a sensor device (not a camera)
    /// </summary>
    public bool IsSensor => Model.Contains("Sensor", StringComparison.OrdinalIgnoreCase) ||
                            Model.Equals("AI Key", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Determine if a model name represents a camera
    /// </summary>
    private static bool IsCameraModel(string model)
    {
        if (string.IsNullOrEmpty(model))
            return false;

        // Known camera model patterns
        var cameraPatterns = new[]
        {
            "G3", "G4", "G5", "G6",  // UniFi Protect camera generations
            "Bullet", "Dome", "Flex", "Instant", "Pro", "PTZ", "Turret",
            "AI Turret", "AI Bullet", "AI Dome", "AI Pro",
            "UVC"  // Legacy UniFi Video Camera prefix
        };

        // Exclude non-camera models
        var excludePatterns = new[]
        {
            "AI Key", "SuperLink", "Gateway", "NVR", "UNVR", "Cloud Key",
            "Sensor", "Chime", "Bridge", "Hub"
        };

        // Check exclusions first
        foreach (var exclude in excludePatterns)
        {
            if (model.Contains(exclude, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        // Check if it matches camera patterns
        foreach (var pattern in cameraPatterns)
        {
            if (model.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
