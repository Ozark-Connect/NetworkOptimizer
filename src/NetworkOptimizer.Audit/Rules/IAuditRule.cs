using NetworkOptimizer.Audit.Models;

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

    public abstract AuditIssue? Evaluate(PortInfo port, List<NetworkInfo> networks);

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
    protected bool IsIoTDeviceName(string? portName)
    {
        if (string.IsNullOrEmpty(portName))
            return false;

        var nameLower = portName.ToLowerInvariant();
        var iotHints = new[] { "ikea", "hue", "smart", "iot", "alexa", "echo", "nest", "ring", "sonos", "philips" };
        return iotHints.Any(hint => nameLower.Contains(hint));
    }

    /// <summary>
    /// Helper to check if a port name suggests a security camera
    /// </summary>
    protected bool IsCameraDeviceName(string? portName)
    {
        if (string.IsNullOrEmpty(portName))
            return false;

        var nameLower = portName.ToLowerInvariant();
        var cameraHints = new[] { "cam", "camera", "ptz", "nvr", "protect" };
        return cameraHints.Any(hint => nameLower.Contains(hint));
    }

    /// <summary>
    /// Helper to check if a port name suggests an access point
    /// </summary>
    protected bool IsAccessPointName(string? portName)
    {
        if (string.IsNullOrEmpty(portName))
            return false;

        var nameLower = portName.ToLowerInvariant();
        return nameLower.Contains("ap") || nameLower.Contains("access point") || nameLower.Contains("wifi");
    }

    /// <summary>
    /// Create an audit issue from this rule
    /// </summary>
    protected AuditIssue CreateIssue(string message, PortInfo port, Dictionary<string, object>? metadata = null)
    {
        return new AuditIssue
        {
            Type = RuleId,
            Severity = Severity,
            Message = message,
            DeviceName = port.Switch.Name,
            Port = port.PortIndex.ToString(),
            PortName = port.Name,
            Metadata = metadata,
            RuleId = RuleId,
            ScoreImpact = ScoreImpact
        };
    }
}
