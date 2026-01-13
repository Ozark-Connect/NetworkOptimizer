using System.Text.RegularExpressions;

namespace NetworkOptimizer.Audit;

/// <summary>
/// Centralized device name pattern matching hints.
/// Used by audit rules to identify device types from port names.
/// </summary>
public static class DeviceNameHints
{
    // Word boundary patterns for short keywords that could match within other words
    private static readonly Regex ApWordBoundaryRegex = new(@"\b(ap|wap)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

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
    public static readonly string[] AccessPointHints = { "ap", "wap", "access point", "wifi" };

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
    /// Check if a port name suggests an access point.
    /// Uses word boundary matching for "ap" to avoid false positives like "application" or "laptop".
    /// </summary>
    public static bool IsAccessPointName(string? portName)
    {
        if (string.IsNullOrEmpty(portName))
            return false;

        // Use word boundary regex for "ap" to avoid false positives
        if (ApWordBoundaryRegex.IsMatch(portName))
            return true;

        // Check other hints with simple contains (they're long enough to be unambiguous)
        var nameLower = portName.ToLowerInvariant();
        return nameLower.Contains("access point") || nameLower.Contains("wifi");
    }
}
