using System.Text.Json;
using NetworkOptimizer.Core.Models;
using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.Audit.Models;

/// <summary>
/// Request parameters for running a security audit.
/// Consolidates all optional inputs into a single parameter object.
/// </summary>
public class AuditRequest
{
    /// <summary>
    /// Required: JSON string containing UniFi device data
    /// </summary>
    public required string DeviceDataJson { get; init; }

    /// <summary>
    /// Optional: List of currently connected clients
    /// </summary>
    public List<UniFiClientResponse>? Clients { get; init; }

    /// <summary>
    /// Optional: Historical client data for offline device analysis
    /// </summary>
    public List<UniFiClientHistoryResponse>? ClientHistory { get; init; }

    /// <summary>
    /// Optional: UniFi fingerprint database for device detection
    /// </summary>
    public UniFiFingerprintDatabase? FingerprintDb { get; init; }

    /// <summary>
    /// Optional: UniFi controller settings data
    /// </summary>
    public JsonElement? SettingsData { get; init; }

    /// <summary>
    /// Optional: Firewall policies data from UniFi API
    /// </summary>
    public JsonElement? FirewallPoliciesData { get; init; }

    /// <summary>
    /// Optional: User-defined device allowance settings
    /// </summary>
    public DeviceAllowanceSettings? AllowanceSettings { get; init; }

    /// <summary>
    /// Optional: UniFi Protect camera collection
    /// </summary>
    public ProtectCameraCollection? ProtectCameras { get; init; }

    /// <summary>
    /// Optional: Client name for display purposes
    /// </summary>
    public string? ClientName { get; init; }
}
