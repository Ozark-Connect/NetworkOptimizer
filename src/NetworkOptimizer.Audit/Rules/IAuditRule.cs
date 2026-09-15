using Microsoft.Extensions.Logging;
using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.Audit.Services;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Core.Models;

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
    /// Whether this rule should evaluate LAG child ports.
    /// Most rules should skip LAG children since their config is controlled
    /// by the parent LAG port. Defaults to false.
    /// </summary>
    bool AppliesToLagChildPorts { get; }

    /// <summary>
    /// Evaluate this rule against a port configuration
    /// </summary>
    /// <param name="port">Port to evaluate</param>
    /// <param name="networks">Enabled networks only (for most rules)</param>
    /// <param name="allNetworks">All networks including disabled (for rules that check port config exposure)</param>
    AuditIssue? Evaluate(PortInfo port, List<NetworkInfo> networks, List<NetworkInfo>? allNetworks = null);
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
    public virtual bool AppliesToLagChildPorts => false;

    protected ILogger? Logger { get; private set; }

    public void SetLogger(ILogger logger)
    {
        Logger = logger;
    }

    /// <summary>
    /// Device type detection service for enhanced detection
    /// </summary>
    protected DeviceTypeDetectionService? DetectionService { get; private set; }

    /// <summary>
    /// Device allowance settings for allowing certain devices on main network
    /// </summary>
    protected DeviceAllowanceSettings AllowanceSettings { get; private set; } = DeviceAllowanceSettings.Default;

    /// <summary>
    /// UniFi Protect camera collection for direct camera detection on ports.
    /// Cameras detected via Protect API bypass the ForwardMode gate since they
    /// don't appear in stat/sta client data.
    /// </summary>
    protected ProtectCameraCollection? ProtectCameras { get; private set; }

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

    /// <summary>
    /// Set the Protect camera collection for direct camera detection on ports
    /// </summary>
    public void SetProtectCameras(ProtectCameraCollection cameras)
    {
        ProtectCameras = cameras;
    }

    /// <summary>
    /// UniFi Network application version (e.g., "10.6.106"). Null when unknown.
    /// </summary>
    protected string? NetworkApplicationVersion { get; private set; }

    /// <summary>
    /// Set the UniFi Network application version for version-gated recommendations
    /// </summary>
    public void SetNetworkApplicationVersion(string? version)
    {
        NetworkApplicationVersion = version;
    }

    /// <summary>
    /// Whether issue text names UniFi Network 10.6+ port settings (Port Profile, Port Security, MAC Address Filter)
    /// rather than the older ones (Ethernet Port Profile, Restricted, allowed list).
    /// </summary>
    protected bool UsesPortSecurityNames => PortLockSupport.UsesPortSecurityNames(NetworkApplicationVersion);

    /// <summary>
    /// Whether this port could ever take Lock Port to UniFi Device: it is on a UniFi switch and is not a LAG.
    /// Independent of versions, so copy can tell "upgrade to get it" from "never offered here".
    /// </summary>
    protected static bool CanPortEverLock(PortInfo port) =>
        PortLockSupport.SupportsDeviceType(port.Switch.Type) && !port.IsLagParent;

    /// <summary>
    /// Whether the port is on a UniFi switch and the application and switch firmware both meet the
    /// minimum versions for Lock Port to UniFi Device.
    /// </summary>
    protected bool IsPortLockSupported(PortInfo port) =>
        PortLockSupport.SupportsDeviceType(port.Switch.Type) &&
        PortLockSupport.IsAvailable(NetworkApplicationVersion, port.Switch.FirmwareVersion);

    /// <summary>
    /// Whether an assigned Port Profile stops this port from taking the lock.
    /// </summary>
    protected static bool IsPortLockBlockedByProfile(PortInfo port) =>
        !PortLockSupport.CanLockWithProfile(port.PortProfileId);

    /// <summary>
    /// Whether Lock Port to UniFi Device can be recommended for this port: versions are met,
    /// no profile stands in the way, and the port is not a LAG (UniFi does not offer the lock on one).
    /// </summary>
    protected bool IsPortLockAvailable(PortInfo port) =>
        IsPortLockSupported(port) && !IsPortLockBlockedByProfile(port) && !port.IsLagParent;

    /// <summary>
    /// Check if the device type is network fabric (gateway, AP, switch, bridge): it carries LAN traffic, so it
    /// legitimately needs trunk ports with multiple VLANs and shouldn't get MAC restriction recommendations.
    /// Modems, NVRs, Cloud Keys are endpoints and SHOULD get recommendations.
    /// </summary>
    protected static bool IsNetworkFabricDevice(string? deviceType)
    {
        if (string.IsNullOrEmpty(deviceType))
            return false;

        // Only network fabric devices - the ones that carry LAN traffic
        return deviceType.ToLowerInvariant() switch
        {
            "ugw" or "usg" or "udm" or "uxg" or "ucg" => true,  // Gateways
            "uap" => true,  // Access Points
            "usw" => true,  // Switches
            "ubb" => true,  // Building-to-Building Bridges
            _ => false
        };
    }

    /// <summary>
    /// Whether the port is an access port: native, or custom with a native network set.
    /// </summary>
    protected static bool IsAccessPort(PortInfo port) =>
        port.ForwardMode == "native" ||
        (port.ForwardMode == "custom" && !string.IsNullOrEmpty(port.NativeNetworkId));

    /// <summary>
    /// Describe the UniFi device on a port for issue copy: the client name plus its UniFi
    /// app when known ("AI Key (UniFi Protect)"), otherwise the uplink-table device's name and role
    /// ("[AP] Back Yard (UniFi Access Point)"), or the role alone when unnamed.
    /// </summary>
    protected static string DescribeUniFiDevice(PortInfo port)
    {
        var client = port.ConnectedClient;
        var clientName = client?.Name ?? client?.Hostname;
        if (!string.IsNullOrEmpty(clientName))
        {
            var app = client!.UniFiProductLine switch
            {
                "protect" => "UniFi Protect",
                "network" => "UniFi Network",
                "access" => "UniFi Access",
                "talk" => "UniFi Talk",
                "connect" => "UniFi Connect",
                "drive" => "UniFi Drive",
                "play" => "UniFi Play",
                _ => null
            };
            return app == null ? clientName : $"{clientName} ({app})";
        }

        var role = port.ConnectedDeviceType?.ToLowerInvariant() switch
        {
            "uap" => "Access Point",
            "usw" => "Switch",
            "ubb" => "Bridge",
            "ugw" or "usg" or "udm" or "uxg" or "ucg" => "Gateway",
            "umbb" => "Modem",
            "uck" or "uas" => "Cloud Key",
            "usp" => "Power Device",
            "unas" => "NAS",
            _ => "Device"
        };
        return string.IsNullOrEmpty(port.ConnectedDeviceName)
            ? $"the connected UniFi {role}"
            : $"{port.ConnectedDeviceName} (UniFi {role})";
    }

    public abstract AuditIssue? Evaluate(PortInfo port, List<NetworkInfo> networks, List<NetworkInfo>? allNetworks = null);

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
    /// Check if the port has an intentional unrestricted access profile assigned.
    /// This indicates the user has explicitly configured this as a multi-device port
    /// (like hotel RJ45 jacks that need to accept any device).
    /// </summary>
    protected static bool HasIntentionalUnrestrictedProfile(PortInfo port)
    {
        var profile = port.AssignedPortProfile;
        if (profile == null)
            return false;

        // Profile must be:
        // - Access port mode (forward=native)
        // - MAC restriction disabled (port_security_enabled=false)
        // - Tagged VLANs blocked (tagged_vlan_mgmt=block_all)
        return profile.Forward == "native"
            && !profile.PortSecurityEnabled
            && profile.TaggedVlanMgmt == "block_all";
    }

    /// <summary>
    /// Create an audit issue from this rule
    /// </summary>
    protected AuditIssue CreateIssue(
        string message,
        PortInfo port,
        Dictionary<string, object>? metadata = null,
        string? recommendedAction = null,
        AuditSeverity? overrideSeverity = null,
        int? overrideScoreImpact = null)
    {
        var deviceName = GetBestDeviceName(port);

        return new AuditIssue
        {
            Type = RuleId,
            Severity = overrideSeverity ?? Severity,
            Message = message,
            DeviceName = deviceName,
            DeviceMac = port.Switch.MacAddress,
            Port = port.PortIndex.ToString(),
            PortName = port.Name,
            Metadata = metadata,
            RecommendedAction = recommendedAction,
            RuleId = RuleId,
            ScoreImpact = overrideScoreImpact ?? ScoreImpact
        };
    }

    /// <summary>
    /// Get the best available device name for a port, checking multiple sources.
    /// Priority: ConnectedClient > ConnectedDeviceName > HistoricalClient > Detection ProductName > ModelName > Custom port name > Port number
    /// </summary>
    private string GetBestDeviceName(PortInfo port)
    {
        // 1. Try connected client name (prefer Name, fall back to Hostname)
        var clientName = GetFirstNonEmpty(
            port.ConnectedClient?.Name,
            port.ConnectedClient?.Hostname);
        if (!string.IsNullOrEmpty(clientName))
            return $"{clientName} on {port.Switch.Name}";

        // 1b. A UniFi device uplinked into this port (AP, switch) is not a client, so without this its port label wins
        if (!string.IsNullOrEmpty(port.ConnectedDeviceName))
            return $"{port.ConnectedDeviceName} on {port.Switch.Name}";

        // 2. Try historical client name (for devices that were connected before)
        var historicalName = GetFirstNonEmpty(
            port.HistoricalClient?.DisplayName,
            port.HistoricalClient?.Name,
            port.HistoricalClient?.Hostname);
        if (!string.IsNullOrEmpty(historicalName))
            return $"{historicalName} on {port.Switch.Name}";

        // 3. Try detection ProductName (Protect camera name, fingerprint product, etc.)
        var detection = DetectDeviceType(port);
        if (!string.IsNullOrEmpty(detection.ProductName))
            return $"{detection.ProductName} on {port.Switch.Name}";

        // 4. Try historical client model name (e.g., "g6-pro-bullet")
        if (!string.IsNullOrEmpty(port.HistoricalClient?.ModelName))
            return $"{port.HistoricalClient.ModelName} on {port.Switch.Name}";

        // 5. Try custom port name (not just "Port X" or a bare number)
        if (!string.IsNullOrWhiteSpace(port.Name) && IsCustomPortName(port.Name))
            return $"{port.Name} on {port.Switch.Name}";

        // 6. Fall back to port number
        return $"Port {port.PortIndex} on {port.Switch.Name}";
    }

    /// <summary>
    /// Get the first non-null, non-empty string from the provided values.
    /// </summary>
    private static string? GetFirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrEmpty(value))
                return value;
        }
        return null;
    }

    /// <summary>
    /// Check if a port name is a custom name (not a default port label).
    /// Delegates to PortNameHelper for consistent behavior across all rules.
    /// </summary>
    private static bool IsCustomPortName(string portName) => PortNameHelper.IsCustomPortName(portName);
}
