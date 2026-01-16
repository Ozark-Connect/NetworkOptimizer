using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.Audit.Scoring;
using NetworkOptimizer.Core.Enums;

using AuditSeverity = NetworkOptimizer.Audit.Models.AuditSeverity;

namespace NetworkOptimizer.Audit.Rules;

/// <summary>
/// Checks device VLAN placement and provides recommendations.
/// Consolidates logic shared between wired and wireless IoT/Camera rules.
/// </summary>
public static class VlanPlacementChecker
{
    /// <summary>
    /// Result of a VLAN placement check
    /// </summary>
    public record PlacementResult(
        bool IsCorrectlyPlaced,
        bool IsLowRisk,
        NetworkInfo? RecommendedNetwork,
        string RecommendedNetworkLabel,
        AuditSeverity Severity,
        int ScoreImpact,
        bool IsAllowedBySettings = false);

    /// <summary>
    /// Check if an IoT device is correctly placed on an IoT or Security VLAN.
    /// </summary>
    /// <param name="category">Device category from detection</param>
    /// <param name="currentNetwork">The network the device is currently on</param>
    /// <param name="allNetworks">All available networks</param>
    /// <param name="defaultScoreImpact">Default score impact if not low-risk</param>
    /// <returns>Placement result with recommendation</returns>
    public static PlacementResult CheckIoTPlacement(
        ClientDeviceCategory category,
        NetworkInfo? currentNetwork,
        List<NetworkInfo> allNetworks,
        int defaultScoreImpact = 10)
        => CheckIoTPlacement(category, currentNetwork, allNetworks, defaultScoreImpact, null, null);

    /// <summary>
    /// Check if an IoT device is correctly placed on an IoT or Security VLAN,
    /// with optional device allowance settings.
    /// </summary>
    /// <param name="category">Device category from detection</param>
    /// <param name="currentNetwork">The network the device is currently on</param>
    /// <param name="allNetworks">All available networks</param>
    /// <param name="defaultScoreImpact">Default score impact if not low-risk</param>
    /// <param name="allowanceSettings">Settings for allowing devices on main network</param>
    /// <param name="vendorName">Vendor name for device-specific allowances</param>
    /// <returns>Placement result with recommendation</returns>
    public static PlacementResult CheckIoTPlacement(
        ClientDeviceCategory category,
        NetworkInfo? currentNetwork,
        List<NetworkInfo> allNetworks,
        int defaultScoreImpact,
        DeviceAllowanceSettings? allowanceSettings,
        string? vendorName)
    {
        // IoT devices can be on IoT or Security networks
        var isCorrectlyPlaced = currentNetwork != null &&
            (currentNetwork.Purpose == NetworkPurpose.IoT ||
             currentNetwork.Purpose == NetworkPurpose.Security);

        // Find the IoT network to recommend (prefer lower VLAN number)
        var iotNetwork = allNetworks
            .Where(n => n.Purpose == NetworkPurpose.IoT)
            .OrderBy(n => n.VlanId)
            .FirstOrDefault();

        var recommendedLabel = iotNetwork != null
            ? $"{iotNetwork.Name} ({iotNetwork.VlanId})"
            : "IoT VLAN";

        // Low-risk IoT devices get Recommended severity
        var isLowRisk = category.IsLowRiskIoT();
        var severity = isLowRisk ? AuditSeverity.Recommended : AuditSeverity.Critical;
        var scoreImpact = isLowRisk ? ScoreConstants.LowRiskIoTImpact : defaultScoreImpact;
        var isAllowedBySettings = false;

        // Check if device is explicitly allowed on main network
        if (allowanceSettings != null && isLowRisk)
        {
            var isAllowed = category switch
            {
                ClientDeviceCategory.StreamingDevice => allowanceSettings.IsStreamingDeviceAllowed(vendorName),
                ClientDeviceCategory.SmartTV => allowanceSettings.IsSmartTVAllowed(vendorName),
                ClientDeviceCategory.SmartSpeaker => allowanceSettings.IsSmartSpeakerAllowed(vendorName),
                ClientDeviceCategory.MediaPlayer => allowanceSettings.IsMediaPlayerAllowed(),
                _ => false
            };

            if (isAllowed)
            {
                severity = AuditSeverity.Informational;
                scoreImpact = 0; // User explicitly allows this - no score penalty
                isAllowedBySettings = true;
            }
        }

        return new PlacementResult(
            IsCorrectlyPlaced: isCorrectlyPlaced,
            IsLowRisk: isLowRisk,
            RecommendedNetwork: iotNetwork,
            RecommendedNetworkLabel: recommendedLabel,
            Severity: severity,
            ScoreImpact: scoreImpact,
            IsAllowedBySettings: isAllowedBySettings);
    }

    /// <summary>
    /// Check if a printer is correctly placed.
    /// If Printer VLAN exists, device is always flagged as incorrectly placed unless on Printer VLAN.
    /// AllowPrintersOnMainNetwork controls severity: true = Informational, false = Recommended.
    /// If no Printer VLAN exists, IoT or Security is acceptable.
    /// </summary>
    /// <param name="currentNetwork">The network the device is currently on</param>
    /// <param name="allNetworks">All available networks</param>
    /// <param name="defaultScoreImpact">Default score impact</param>
    /// <param name="allowanceSettings">Settings for allowing devices on main network</param>
    /// <returns>Placement result with recommendation</returns>
    public static PlacementResult CheckPrinterPlacement(
        NetworkInfo? currentNetwork,
        List<NetworkInfo> allNetworks,
        int defaultScoreImpact,
        DeviceAllowanceSettings? allowanceSettings)
    {
        // Check if user allows printers on main/IoT network (lenient mode)
        var isAllowed = allowanceSettings?.AllowPrintersOnMainNetwork ?? true;

        // Find the best network to recommend
        // Priority: Printer VLAN > IoT VLAN
        var printerNetwork = allNetworks
            .Where(n => n.Purpose == NetworkPurpose.Printer)
            .OrderBy(n => n.VlanId)
            .FirstOrDefault();

        var iotNetwork = allNetworks
            .Where(n => n.Purpose == NetworkPurpose.IoT)
            .OrderBy(n => n.VlanId)
            .FirstOrDefault();

        // Determine if correctly placed based on available networks and settings:
        // - Printer VLAN is always the ideal placement
        // - If Printer VLAN exists: always suggest it (Info if lenient, Recommended if strict)
        // - If no Printer VLAN: IoT or Security is acceptable
        bool isCorrectlyPlaced;
        if (currentNetwork?.Purpose == NetworkPurpose.Printer)
        {
            // Always correct on Printer VLAN
            isCorrectlyPlaced = true;
        }
        else if (printerNetwork != null)
        {
            // Printer VLAN exists but device not on it: always flag
            // Severity controlled by isAllowed (Info vs Recommended)
            isCorrectlyPlaced = false;
        }
        else
        {
            // No Printer VLAN: IoT or Security is acceptable
            isCorrectlyPlaced = currentNetwork != null &&
                (currentNetwork.Purpose == NetworkPurpose.IoT ||
                 currentNetwork.Purpose == NetworkPurpose.Security);
        }

        NetworkInfo? recommendedNetwork;
        string recommendedLabel;

        if (printerNetwork != null)
        {
            recommendedNetwork = printerNetwork;
            recommendedLabel = $"{printerNetwork.Name} ({printerNetwork.VlanId})";
        }
        else if (iotNetwork != null)
        {
            recommendedNetwork = iotNetwork;
            recommendedLabel = $"{iotNetwork.Name} ({iotNetwork.VlanId})";
        }
        else
        {
            recommendedNetwork = null;
            recommendedLabel = "Printer or IoT VLAN";
        }

        // If allowed: Informational (no score impact, purely advisory)
        // If not allowed: Recommended severity with score impact
        var severity = isAllowed ? AuditSeverity.Informational : AuditSeverity.Recommended;
        var scoreImpact = isAllowed ? 0 : defaultScoreImpact;

        return new PlacementResult(
            IsCorrectlyPlaced: isCorrectlyPlaced,
            IsLowRisk: true, // Printers are always low-risk
            RecommendedNetwork: recommendedNetwork,
            RecommendedNetworkLabel: recommendedLabel,
            Severity: severity,
            ScoreImpact: scoreImpact,
            IsAllowedBySettings: isAllowed);
    }

    /// <summary>
    /// Check if a camera/surveillance device is correctly placed on a Security VLAN.
    /// </summary>
    /// <param name="currentNetwork">The network the device is currently on</param>
    /// <param name="allNetworks">All available networks</param>
    /// <param name="defaultScoreImpact">Default score impact (cameras are always high-risk)</param>
    /// <returns>Placement result with recommendation</returns>
    public static PlacementResult CheckCameraPlacement(
        NetworkInfo? currentNetwork,
        List<NetworkInfo> allNetworks,
        int defaultScoreImpact = 8)
    {
        // Cameras should only be on Security networks
        var isCorrectlyPlaced = currentNetwork?.Purpose == NetworkPurpose.Security;

        // Find the Security network to recommend (prefer lower VLAN number)
        var securityNetwork = allNetworks
            .Where(n => n.Purpose == NetworkPurpose.Security)
            .OrderBy(n => n.VlanId)
            .FirstOrDefault();

        var recommendedLabel = securityNetwork != null
            ? $"{securityNetwork.Name} ({securityNetwork.VlanId})"
            : "Security VLAN";

        // Cameras are always high-risk - always Critical severity
        return new PlacementResult(
            IsCorrectlyPlaced: isCorrectlyPlaced,
            IsLowRisk: false,
            RecommendedNetwork: securityNetwork,
            RecommendedNetworkLabel: recommendedLabel,
            Severity: AuditSeverity.Critical,
            ScoreImpact: defaultScoreImpact);
    }

    /// <summary>
    /// Build common metadata for VLAN placement issues
    /// </summary>
    public static Dictionary<string, object> BuildMetadata(
        DeviceDetectionResult detection,
        NetworkInfo? currentNetwork,
        bool? isLowRisk = null)
    {
        var metadata = new Dictionary<string, object>
        {
            { "device_type", detection.CategoryName },
            { "device_category", detection.Category.ToString() },
            { "detection_source", detection.Source.ToString() },
            { "detection_confidence", detection.ConfidenceScore },
            { "vendor", detection.VendorName ?? "Unknown" },
            { "current_network_purpose", currentNetwork?.Purpose.ToString() ?? "Unknown" }
        };

        if (isLowRisk.HasValue)
        {
            metadata["is_low_risk_device"] = isLowRisk.Value;
        }

        // Add configurable settings link for device types that have user-configurable allowances
        var settingsKey = detection.Category switch
        {
            ClientDeviceCategory.StreamingDevice => "streaming-devices",
            ClientDeviceCategory.SmartTV => "smart-tvs",
            ClientDeviceCategory.SmartSpeaker => "streaming-devices", // Apple HomePod uses same setting
            ClientDeviceCategory.MediaPlayer => "media-players",
            ClientDeviceCategory.Printer => "printers",
            _ => null
        };

        if (settingsKey != null)
        {
            metadata["configurable_setting"] = settingsKey;
        }

        return metadata;
    }
}
