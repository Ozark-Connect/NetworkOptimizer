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
        int ScoreImpact);

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

        // Check if device is explicitly allowed on main network
        if (allowanceSettings != null && isLowRisk)
        {
            var isAllowed = category switch
            {
                ClientDeviceCategory.StreamingDevice => allowanceSettings.IsStreamingDeviceAllowed(vendorName),
                ClientDeviceCategory.SmartTV => allowanceSettings.IsSmartTVAllowed(vendorName),
                _ => false
            };

            if (isAllowed)
            {
                severity = AuditSeverity.Informational;
                scoreImpact = ScoreConstants.InformationalImpact;
            }
        }

        return new PlacementResult(
            IsCorrectlyPlaced: isCorrectlyPlaced,
            IsLowRisk: isLowRisk,
            RecommendedNetwork: iotNetwork,
            RecommendedNetworkLabel: recommendedLabel,
            Severity: severity,
            ScoreImpact: scoreImpact);
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

        return metadata;
    }
}
