using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Core.Models;

using AuditSeverity = NetworkOptimizer.Audit.Models.AuditSeverity;

namespace NetworkOptimizer.Audit.Rules;

/// <summary>
/// Detects self-hosted security cameras not on a dedicated security VLAN.
/// Uses enhanced detection: fingerprint > MAC OUI > port name patterns.
/// Note: Cloud cameras (Ring, Nest, Wyze, Blink, Arlo) are handled by IoT VLAN rules instead.
/// </summary>
public class CameraVlanRule : AuditRuleBase
{
    public override string RuleId => "CAMERA-VLAN-001";
    public override string RuleName => "Camera VLAN Placement";
    public override string Description => "Self-hosted security cameras should be on dedicated security/camera VLANs";
    public override AuditSeverity Severity => AuditSeverity.Critical;
    public override int ScoreImpact => 8;

    public override AuditIssue? Evaluate(PortInfo port, List<NetworkInfo> networks, List<NetworkInfo>? allNetworks = null)
    {
        // Check if a known Protect camera is on this port (bypasses ForwardMode gate).
        // Protect cameras don't appear in stat/sta so they have no ConnectedClient with
        // ForwardMode="native". We detect them by matching port MACs against the Protect API.
        var protectCamera = FindProtectCameraOnPort(port);
        if (protectCamera != null)
            return EvaluateProtectCamera(protectCamera, port, networks);

        // Skip uplinks, WAN ports, and non-access ports
        if (port.ForwardMode != "native" || port.IsUplink || port.IsWan)
            return null;

        DeviceDetectionResult detection;
        bool isOfflineDevice = false;

        if (port.IsUp && port.ConnectedClient != null)
        {
            // Active port with connected client: use full detection
            detection = DetectDeviceType(port);
        }
        else if (port.IsUp && port.ConnectedClient == null && HasOfflineDeviceData(port))
        {
            // Port is UP (link active) but no client connected (e.g., TV in standby)
            // Use LastConnectionMac or MAC restrictions for detection
            var offlineDetection = DetectDeviceTypeForDownPort(port);
            if (offlineDetection == null)
                return null;
            detection = offlineDetection;
            isOfflineDevice = true;
        }
        else if (!port.IsUp && IsAuditableDownPort(port))
        {
            // Down port: detect from last connection MAC or MAC restrictions
            var downPortDetection = DetectDeviceTypeForDownPort(port);
            if (downPortDetection == null)
                return null;
            detection = downPortDetection;
            isOfflineDevice = true;
        }
        else
        {
            // No connected client and no MAC data: skip
            return null;
        }

        // Check if this is a surveillance/security device (but not cloud-based ones)
        // Cloud surveillance (Ring, Nest, Wyze, Blink, Arlo, SimpliSafe) are handled by IoT VLAN rules
        if (!detection.Category.IsSurveillance())
            return null;

        // Skip cloud surveillance devices - they need internet so should go on IoT VLAN, not Security VLAN
        if (detection.Category.IsCloudSurveillance())
            return null;

        // Get the network this port is on
        var network = GetNetwork(port.NativeNetworkId, networks);
        if (network == null)
            return null;

        // Check if this is an NVR (allowed on Management VLAN)
        var isNvr = detection.Metadata?.ContainsKey("is_nvr") == true;

        // Check placement using shared logic
        var placement = VlanPlacementChecker.CheckCameraPlacement(network, networks, ScoreImpact, isNvr: isNvr);

        if (placement.IsCorrectlyPlaced)
            return null;

        // Determine severity and score based on recency for offline devices
        // Online devices: full score impact
        // Offline devices seen within 2 weeks: full score impact
        // Offline devices older than 2 weeks: Informational only (no score impact)
        var severity = placement.Severity;
        var scoreImpact = placement.ScoreImpact;

        if (isOfflineDevice)
        {
            var twoWeeksAgo = DateTimeOffset.UtcNow.AddDays(-14).ToUnixTimeSeconds();
            var isRecentlyActive = port.LastConnectionSeen.HasValue && port.LastConnectionSeen.Value >= twoWeeksAgo;

            if (!isRecentlyActive)
            {
                severity = AuditSeverity.Informational;
                scoreImpact = 0;
            }
        }

        // Build device name based on port state
        string deviceName;
        if (isOfflineDevice)
        {
            // Offline device: prefer historical client name, then custom port name, then detected category
            var historicalName = port.HistoricalClient?.DisplayName
                ?? port.HistoricalClient?.Name
                ?? port.HistoricalClient?.Hostname;

            if (!string.IsNullOrEmpty(historicalName))
            {
                deviceName = $"{historicalName} on {port.Switch.Name}";
            }
            else
            {
                // Fall back to custom port name if set, otherwise detected category
                var hasCustomPortName = PortNameHelper.IsCustomPortName(port.Name);
                deviceName = hasCustomPortName
                    ? $"{port.Name} on {port.Switch.Name}"
                    : $"{detection.CategoryName} on {port.Switch.Name}";
            }
        }
        else
        {
            // Active port: use connected client name if available
            var clientName = port.ConnectedClient?.Name ?? port.ConnectedClient?.Hostname;
            if (!string.IsNullOrEmpty(clientName))
            {
                deviceName = $"{clientName} on {port.Switch.Name}";
            }
            else if (!string.IsNullOrEmpty(detection.ProductName))
            {
                // Use specific product name from detection (e.g., "G6 Pro Bullet" from Protect API)
                deviceName = $"{detection.ProductName} on {port.Switch.Name}";
            }
            else
            {
                // Fall back to OUI (manufacturer) with MAC suffix, or detection vendor, or just MAC
                var oui = port.ConnectedClient?.Oui;
                var mac = port.ConnectedClient?.Mac;
                var macSuffix = !string.IsNullOrEmpty(mac) && mac.Length >= 8
                    ? mac.Substring(mac.Length - 5).ToUpperInvariant()
                    : null;

                if (!string.IsNullOrEmpty(oui) && !string.IsNullOrEmpty(macSuffix))
                {
                    deviceName = $"{oui} ({macSuffix}) on {port.Switch.Name}";
                }
                else if (!string.IsNullOrEmpty(detection.VendorName) && !string.IsNullOrEmpty(macSuffix))
                {
                    deviceName = $"{detection.VendorName} ({macSuffix}) on {port.Switch.Name}";
                }
                else if (!string.IsNullOrEmpty(mac))
                {
                    deviceName = $"{mac} on {port.Switch.Name}";
                }
                else
                {
                    deviceName = $"{detection.CategoryName} on {port.Switch.Name}";
                }
            }
        }

        var message = isNvr
            ? $"NVR on {network.Name} VLAN - should be on management or security VLAN"
            : $"{detection.CategoryName} on {network.Name} VLAN - should be on security VLAN";

        return new AuditIssue
        {
            Type = RuleId,
            Severity = severity,
            Message = message,
            DeviceName = deviceName,
            DeviceMac = port.Switch.MacAddress,
            Port = port.PortIndex.ToString(),
            PortName = port.Name,
            CurrentNetwork = network.Name,
            CurrentVlan = network.VlanId,
            RecommendedNetwork = placement.RecommendedNetwork?.Name,
            RecommendedVlan = placement.RecommendedNetwork?.VlanId,
            RecommendedAction = VlanPlacementChecker.GetMoveRecommendation(placement.RecommendedNetworkLabel),
            Metadata = VlanPlacementChecker.BuildMetadata(detection, network),
            RuleId = RuleId,
            ScoreImpact = scoreImpact
        };
    }

    /// <summary>
    /// Check if a known Protect camera is connected to this port.
    /// Checks ConnectedClient MAC, LastConnectionMac, and HistoricalClient MAC.
    /// </summary>
    private ProtectCamera? FindProtectCameraOnPort(PortInfo port)
    {
        if (ProtectCameras == null || ProtectCameras.Count == 0)
            return null;

        // Check connected client MAC
        if (ProtectCameras.TryGet(port.ConnectedClient?.Mac, out var camera) && camera != null)
            return camera;

        // Check last connection MAC (for down/offline ports)
        if (ProtectCameras.TryGet(port.LastConnectionMac, out camera) && camera != null)
            return camera;

        // Check historical client MAC
        if (ProtectCameras.TryGet(port.HistoricalClient?.Mac, out camera) && camera != null)
            return camera;

        return null;
    }

    /// <summary>
    /// Evaluate a Protect camera's VLAN placement using the Protect API's ConnectionNetworkId.
    /// This bypasses the normal detection pipeline since Protect gives us 100% confidence.
    /// </summary>
    private AuditIssue? EvaluateProtectCamera(ProtectCamera camera, PortInfo port, List<NetworkInfo> networks)
    {
        // Use Protect API's ConnectionNetworkId for authoritative network placement
        var network = GetNetwork(camera.ConnectionNetworkId, networks);
        if (network == null)
            return null;

        var placement = VlanPlacementChecker.CheckCameraPlacement(network, networks, ScoreImpact, isNvr: camera.IsNvr);
        if (placement.IsCorrectlyPlaced)
            return null;

        var deviceName = $"{camera.Name} on {port.Switch.Name}";
        var message = camera.IsNvr
            ? $"NVR on {network.Name} VLAN - should be on management or security VLAN"
            : $"Camera on {network.Name} VLAN - should be on security VLAN";

        return new AuditIssue
        {
            Type = RuleId,
            Severity = placement.Severity,
            Message = message,
            DeviceName = deviceName,
            DeviceMac = port.Switch.MacAddress,
            Port = port.PortIndex.ToString(),
            PortName = port.Name,
            CurrentNetwork = network.Name,
            CurrentVlan = network.VlanId,
            RecommendedNetwork = placement.RecommendedNetwork?.Name,
            RecommendedVlan = placement.RecommendedNetwork?.VlanId,
            RecommendedAction = VlanPlacementChecker.GetMoveRecommendation(placement.RecommendedNetworkLabel),
            Metadata = new Dictionary<string, object>
            {
                ["category"] = camera.IsNvr ? "NVR" : "Camera",
                ["confidence"] = 100,
                ["source"] = "ProtectAPI",
                ["camera_name"] = camera.Name,
                ["camera_mac"] = camera.Mac
            },
            RuleId = RuleId,
            ScoreImpact = placement.ScoreImpact
        };
    }
}
