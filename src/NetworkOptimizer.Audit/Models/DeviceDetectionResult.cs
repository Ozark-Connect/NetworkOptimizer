using NetworkOptimizer.Core.Enums;

namespace NetworkOptimizer.Audit.Models;

/// <summary>
/// Source of the device type detection
/// </summary>
public enum DetectionSource
{
    /// <summary>
    /// Unknown source
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// From UniFi fingerprint database
    /// </summary>
    UniFiFingerprint = 1,

    /// <summary>
    /// From MAC address vendor lookup
    /// </summary>
    MacOui = 2,

    /// <summary>
    /// From user-assigned device name
    /// </summary>
    DeviceName = 3,

    /// <summary>
    /// From switch port name
    /// </summary>
    PortName = 4,

    /// <summary>
    /// Multiple sources agreed
    /// </summary>
    Combined = 5
}

/// <summary>
/// Result of device type detection with confidence scoring
/// </summary>
public class DeviceDetectionResult
{
    /// <summary>
    /// Detected device category
    /// </summary>
    public ClientDeviceCategory Category { get; init; }

    /// <summary>
    /// Display name for the category
    /// </summary>
    public string CategoryName => Category.GetDisplayName();

    /// <summary>
    /// Source of the detection
    /// </summary>
    public DetectionSource Source { get; init; }

    /// <summary>
    /// Confidence score (0-100)
    /// </summary>
    public int ConfidenceScore { get; init; }

    /// <summary>
    /// Vendor name if detected
    /// </summary>
    public string? VendorName { get; init; }

    /// <summary>
    /// Product name if detected
    /// </summary>
    public string? ProductName { get; init; }

    /// <summary>
    /// Additional metadata from detection
    /// </summary>
    public Dictionary<string, object> Metadata { get; init; } = new();

    /// <summary>
    /// Recommended network purpose for this device type
    /// </summary>
    public NetworkPurpose RecommendedNetwork { get; init; }

    /// <summary>
    /// Create an unknown detection result
    /// </summary>
    public static DeviceDetectionResult Unknown => new()
    {
        Category = ClientDeviceCategory.Unknown,
        Source = DetectionSource.Unknown,
        ConfidenceScore = 0,
        RecommendedNetwork = NetworkPurpose.Unknown
    };
}
