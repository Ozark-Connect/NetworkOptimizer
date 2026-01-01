namespace NetworkOptimizer.Audit;

/// <summary>
/// Centralized device name pattern matching hints.
/// Used by audit rules to identify device types from port names.
/// </summary>
public static class DeviceNameHints
{
    /// <summary>
    /// Keywords that suggest an IoT device
    /// </summary>
    public static readonly string[] IoTHints = { "ikea", "hue", "smart", "iot", "alexa", "echo", "nest", "ring", "sonos", "philips" };

    /// <summary>
    /// Keywords that suggest a security camera or surveillance device
    /// </summary>
    public static readonly string[] CameraHints = { "cam", "camera", "ptz", "nvr", "protect" };

    /// <summary>
    /// Keywords that suggest an access point
    /// </summary>
    public static readonly string[] AccessPointHints = { "ap", "access point", "wifi" };

    /// <summary>
    /// Check if a port name suggests an IoT device
    /// </summary>
    public static bool IsIoTDeviceName(string? portName)
    {
        if (string.IsNullOrEmpty(portName))
            return false;

        var nameLower = portName.ToLowerInvariant();
        return IoTHints.Any(hint => nameLower.Contains(hint));
    }

    /// <summary>
    /// Check if a port name suggests a security camera
    /// </summary>
    public static bool IsCameraDeviceName(string? portName)
    {
        if (string.IsNullOrEmpty(portName))
            return false;

        var nameLower = portName.ToLowerInvariant();
        return CameraHints.Any(hint => nameLower.Contains(hint));
    }

    /// <summary>
    /// Check if a port name suggests an access point
    /// </summary>
    public static bool IsAccessPointName(string? portName)
    {
        if (string.IsNullOrEmpty(portName))
            return false;

        var nameLower = portName.ToLowerInvariant();
        return AccessPointHints.Any(hint => nameLower.Contains(hint));
    }
}
