using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.Audit.Services;
using NetworkOptimizer.Core.Enums;

using AuditSeverity = NetworkOptimizer.Audit.Models.AuditSeverity;

namespace NetworkOptimizer.Audit.Rules;

/// <summary>
/// Interface for audit rules that analyze wireless client configurations
/// </summary>
public interface IWirelessAuditRule
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
    /// Evaluate this rule against a wireless client
    /// </summary>
    AuditIssue? Evaluate(WirelessClientInfo client, List<NetworkInfo> networks);
}

/// <summary>
/// Base class for wireless audit rules with common functionality
/// </summary>
public abstract class WirelessAuditRuleBase : IWirelessAuditRule
{
    public abstract string RuleId { get; }
    public abstract string RuleName { get; }
    public abstract string Description { get; }
    public abstract AuditSeverity Severity { get; }
    public virtual int ScoreImpact { get; } = 5;
    public virtual bool Enabled { get; set; } = true;

    /// <summary>
    /// Device allowance settings for allowing certain devices on main network
    /// </summary>
    protected DeviceAllowanceSettings AllowanceSettings { get; private set; } = DeviceAllowanceSettings.Default;

    /// <summary>
    /// Set the allowance settings for device placement rules
    /// </summary>
    public void SetAllowanceSettings(DeviceAllowanceSettings settings)
    {
        AllowanceSettings = settings;
    }

    public abstract AuditIssue? Evaluate(WirelessClientInfo client, List<NetworkInfo> networks);

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
    /// Create an audit issue for a wireless client
    /// </summary>
    protected AuditIssue CreateIssue(
        string message,
        WirelessClientInfo client,
        AuditSeverity? severityOverride = null,
        int? scoreImpactOverride = null,
        string? recommendedNetwork = null,
        int? recommendedVlan = null,
        string? recommendedAction = null,
        Dictionary<string, object>? metadata = null)
    {
        // Include AP context: "ClientName on APName (Band)"
        string deviceName;
        if (!string.IsNullOrEmpty(client.AccessPointName))
        {
            var bandSuffix = !string.IsNullOrEmpty(client.WifiBand) ? $" ({client.WifiBand})" : "";
            deviceName = $"{client.DisplayName} on {client.AccessPointName}{bandSuffix}";
        }
        else
        {
            deviceName = client.DisplayName;
        }

        return new AuditIssue
        {
            Type = RuleId,
            Severity = severityOverride ?? Severity,
            Message = message,
            DeviceName = deviceName,
            Port = null, // No port for wireless
            PortName = null,
            CurrentNetwork = client.Network?.Name,
            CurrentVlan = client.Network?.VlanId,
            RecommendedNetwork = recommendedNetwork,
            RecommendedVlan = recommendedVlan,
            RecommendedAction = recommendedAction,
            ClientMac = client.Mac,
            ClientName = client.DisplayName,
            AccessPoint = client.AccessPointName,
            WifiBand = client.WifiBand,
            IsWireless = true,
            Metadata = metadata,
            RuleId = RuleId,
            ScoreImpact = scoreImpactOverride ?? ScoreImpact
        };
    }
}
