using NetworkOptimizer.Audit.Models;

namespace NetworkOptimizer.Audit.Rules;

/// <summary>
/// Checks if security-sensitive devices have port isolation enabled
/// Port isolation prevents lateral movement within the same VLAN
/// </summary>
public class PortIsolationRule : AuditRuleBase
{
    public override string RuleId => "PORT-ISOLATION-001";
    public override string RuleName => "Port Isolation for Sensitive Devices";
    public override string Description => "Security cameras and IoT devices should have port isolation enabled";
    public override AuditSeverity Severity => AuditSeverity.Recommended;
    public override int ScoreImpact => 4;

    public override AuditIssue? Evaluate(PortInfo port, List<NetworkInfo> networks)
    {
        // Only check active access ports
        if (!port.IsUp || port.ForwardMode != "native" || port.IsUplink || port.IsWan)
            return null;

        // Check if switch supports isolation
        if (!port.Switch.Capabilities.SupportsIsolation)
            return null;

        // Only check for cameras and IoT devices on their respective networks
        var isCameraDevice = IsCameraDeviceName(port.Name);
        var isIotDevice = IsIoTDeviceName(port.Name);

        if (!isCameraDevice && !isIotDevice)
            return null;

        // Get the network
        var network = GetNetwork(port.NativeNetworkId, networks);
        if (network == null)
            return null;

        // Only check if device is on appropriate network
        if (isCameraDevice && network.Purpose != NetworkPurpose.Security)
            return null; // CameraVlanRule will catch this

        if (isIotDevice && network.Purpose != NetworkPurpose.IoT)
            return null; // IotVlanRule will catch this

        // Check if isolation is enabled
        if (port.IsolationEnabled)
            return null; // Correctly configured

        var deviceType = isCameraDevice ? "Camera" : "IoT device";

        return CreateIssue(
            $"{deviceType} without port isolation - consider enabling for enhanced security",
            port,
            new Dictionary<string, object>
            {
                { "device_type", deviceType },
                { "network", network.Name },
                { "recommendation", "Enable port isolation to prevent lateral movement" }
            });
    }
}
