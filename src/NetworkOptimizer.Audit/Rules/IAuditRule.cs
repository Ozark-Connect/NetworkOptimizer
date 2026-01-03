using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.Audit.Services;
using NetworkOptimizer.Core.Enums;

using AuditSeverity = NetworkOptimizer.Audit.Models.AuditSeverity;

namespace NetworkOptimizer.Audit.Rules;

/// <summary>
/// Interface for audit rules that analyze network configuration
/// </summary>
public interface IAuditRule
{
    /// <summary>
    /// Unique identifier for this rule
    /// </summary>
    string RuleId { get; }

    /// <summary>
    /// Human-readable name of the rule
    /// </summary>
    string RuleName { get; }

    /// <summary>
    /// Description of what this rule checks
    /// </summary>
    string Description { get; }

    /// <summary>
    /// Severity level if this rule fails
    /// </summary>
    AuditSeverity Severity { get; }

    /// <summary>
    /// Score impact when this rule fails
    /// </summary>
    int ScoreImpact { get; }

    /// <summary>
    /// Whether this rule is enabled
    /// </summary>
    bool Enabled { get; }

    /// <summary>
    /// Evaluate this rule against a port configuration
    /// </summary>
    AuditIssue? Evaluate(PortInfo port, List<NetworkInfo> networks);
}

/// <summary>
/// Base class for audit rules with common functionality
/// </summary>
public abstract class AuditRuleBase : IAuditRule
{
    public abstract string RuleId { get; }
    public abstract string RuleName { get; }
    public abstract string Description { get; }
    public abstract AuditSeverity Severity { get; }
    public virtual int ScoreImpact { get; } = 5;
    public virtual bool Enabled { get; set; } = true;

    /// <summary>
    /// Device type detection service for enhanced detection
    /// </summary>
    protected DeviceTypeDetectionService? DetectionService { get; private set; }

    /// <summary>
    /// Device allowance settings for allowing certain devices on main network
    /// </summary>
    protected DeviceAllowanceSettings AllowanceSettings { get; private set; } = DeviceAllowanceSettings.Default;

    /// <summary>
    /// Set the detection service for enhanced device type detection
    /// </summary>
    public void SetDetectionService(DeviceTypeDetectionService service)
    {
        DetectionService = service;
    }

    /// <summary>
    /// Set the allowance settings for device placement rules
    /// </summary>
    public void SetAllowanceSettings(DeviceAllowanceSettings settings)
    {
        AllowanceSettings = settings;
    }

    public abstract AuditIssue? Evaluate(PortInfo port, List<NetworkInfo> networks);

    /// <summary>
    /// Detect device type using all available signals.
    /// Uses client data (fingerprint, MAC OUI) if available, otherwise falls back to port name patterns.
    /// </summary>
    protected DeviceDetectionResult DetectDeviceType(PortInfo port)
    {
        if (DetectionService != null)
        {
            // Use full detection with client data if available (fingerprint, MAC OUI, UniFi OUI)
            // Falls back to port name pattern matching if no client connected
            return DetectionService.DetectDeviceType(
                client: port.ConnectedClient,
                portName: port.Name
            );
        }

        // Fallback to legacy pattern matching when detection service not configured
        if (IsCameraDeviceName(port.Name))
        {
            return new DeviceDetectionResult
            {
                Category = ClientDeviceCategory.Camera,
                Source = DetectionSource.PortName,
                ConfidenceScore = 70,
                RecommendedNetwork = NetworkPurpose.Security
            };
        }

        if (IsIoTDeviceName(port.Name))
        {
            return new DeviceDetectionResult
            {
                Category = ClientDeviceCategory.IoTGeneric,
                Source = DetectionSource.PortName,
                ConfidenceScore = 60,
                RecommendedNetwork = NetworkPurpose.IoT
            };
        }

        return DeviceDetectionResult.Unknown;
    }

    /// <summary>
    /// Detect device type for a down port using available signals.
    /// Priority: LastConnectionMac > AllowedMacAddresses > PortName
    /// </summary>
    protected DeviceDetectionResult? DetectDeviceTypeForDownPort(PortInfo port)
    {
        if (DetectionService == null)
            return null;

        DeviceDetectionResult? bestResult = null;

        // Priority 1: Last connected device MAC (most reliable for down ports)
        if (!string.IsNullOrEmpty(port.LastConnectionMac))
        {
            var result = DetectionService.DetectFromMac(port.LastConnectionMac);
            if (result.Category != ClientDeviceCategory.Unknown)
            {
                bestResult = result;
            }
        }

        // Priority 2: MAC restrictions (if configured)
        var macs = port.AllowedMacAddresses;
        if (macs != null && macs.Count > 0)
        {
            foreach (var mac in macs)
            {
                var result = DetectionService.DetectFromMac(mac);
                if (result.Category != ClientDeviceCategory.Unknown)
                {
                    // Take the highest confidence detection
                    if (bestResult == null || result.ConfidenceScore > bestResult.ConfidenceScore)
                    {
                        bestResult = result;
                    }
                }
            }
        }

        // Priority 3: Port name patterns
        if (!string.IsNullOrEmpty(port.Name))
        {
            var nameResult = DetectionService.DetectFromPortName(port.Name);
            if (nameResult.Category != ClientDeviceCategory.Unknown)
            {
                if (bestResult == null || nameResult.ConfidenceScore > bestResult.ConfidenceScore)
                {
                    bestResult = nameResult;
                }
            }
        }

        return bestResult;
    }

    /// <summary>
    /// Check if a down port has enough information to audit.
    /// Returns true if the port is down, is an access port, and has either
    /// a last connection MAC or MAC restrictions configured.
    /// </summary>
    protected bool IsAuditableDownPort(PortInfo port)
    {
        return !port.IsUp
            && port.ForwardMode == "native"
            && !port.IsUplink
            && !port.IsWan
            && HasOfflineDeviceData(port);
    }

    /// <summary>
    /// Check if a port has offline device data (last connection MAC or MAC restrictions).
    /// Used for detecting devices that are offline but have historical MAC data.
    /// </summary>
    protected bool HasOfflineDeviceData(PortInfo port)
    {
        return !string.IsNullOrEmpty(port.LastConnectionMac) || port.AllowedMacAddresses?.Count > 0;
    }

    /// <summary>
    /// Helper to get network info by ID
    /// </summary>
    protected NetworkInfo? GetNetwork(string? networkId, List<NetworkInfo> networks)
    {
        if (string.IsNullOrEmpty(networkId))
            return null;

        return networks.FirstOrDefault(n => n.Id == networkId);
    }

    /// <summary>
    /// Helper to get network name by ID
    /// </summary>
    protected string? GetNetworkName(string? networkId, List<NetworkInfo> networks)
    {
        return GetNetwork(networkId, networks)?.Name;
    }

    /// <summary>
    /// Helper to check if a port name suggests an IoT device
    /// </summary>
    protected bool IsIoTDeviceName(string? portName) => DeviceNameHints.IsIoTDeviceName(portName);

    /// <summary>
    /// Helper to check if a port name suggests a security camera
    /// </summary>
    protected bool IsCameraDeviceName(string? portName) => DeviceNameHints.IsCameraDeviceName(portName);

    /// <summary>
    /// Helper to check if a port name suggests an access point
    /// </summary>
    protected bool IsAccessPointName(string? portName) => DeviceNameHints.IsAccessPointName(portName);

    /// <summary>
    /// Create an audit issue from this rule
    /// </summary>
    protected AuditIssue CreateIssue(string message, PortInfo port, Dictionary<string, object>? metadata = null)
    {
        // Use connected client name if available, otherwise port name
        var clientName = port.ConnectedClient?.Name ?? port.ConnectedClient?.Hostname ?? port.Name;
        // Include switch context: "ClientName on SwitchName"
        var deviceName = clientName != null && clientName != port.Name
            ? $"{clientName} on {port.Switch.Name}"
            : $"{port.Name ?? $"Port {port.PortIndex}"} on {port.Switch.Name}";

        return new AuditIssue
        {
            Type = RuleId,
            Severity = Severity,
            Message = message,
            DeviceName = deviceName,
            Port = port.PortIndex.ToString(),
            PortName = port.Name,
            Metadata = metadata,
            RuleId = RuleId,
            ScoreImpact = ScoreImpact
        };
    }
}
