namespace NetworkOptimizer.Audit.Constants;

/// <summary>
/// Constants used for device type detection confidence scoring
/// </summary>
public static class DetectionConstants
{
    // Confidence scores - higher = more certain
    public const int MaxConfidence = 100;
    public const int ProtectCameraConfidence = 100;  // 100% confidence from UniFi Protect
    public const int NameOverrideConfidence = 95;    // User set the name explicitly
    public const int VendorOverrideConfidence = 97;   // Fingerprint vendor override (e.g., GoPro not a security camera)
    public const int AppleWatchConfidence = 90;      // Apple Watch detected via fingerprint
    public const int OuiHighConfidence = 90;         // Dedicated IoT vendors (ecobee, sonos, arlo)
    public const int VendorDefaultConfidence = 85;   // Vendor default device type
    public const int OuiMediumConfidence = 85;       // Strong signal vendors (philips, ring)
    public const int OuiStandardConfidence = 80;     // General IoT vendors
    public const int OuiLowerConfidence = 75;        // Multi-purpose vendors (belkin, tp-link)
    public const int OuiLowestConfidence = 70;       // Broad vendors (amazon, google, honeywell)

    // Confidence modifiers
    public const int MultiSourceAgreementBoost = 10; // Boost when multiple sources agree

    // Time spans for client analysis
    public static readonly TimeSpan HistoricalClientWindow = TimeSpan.FromDays(14);
    public static readonly TimeSpan OfflineThreshold = TimeSpan.FromDays(30);
}
