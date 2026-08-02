using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Gates;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// The site's monitoring settings as the Monitoring - Setup forms edit them: the master enable and
/// the device temperature, SFP DDM and ONT alert thresholds. These were direct DbContext writes in
/// the page, so turning monitoring off or moving an alert threshold left no audit trail at all.
///
/// Site-scoped, Operator: the same reach the forms' own SiteOperatorOnly gate grants, now enforced
/// where the write happens rather than by whether a button was rendered.
/// </summary>
[MutatingService(SiteScoped = true)]
public interface IMonitoringSettingsService
{
    /// <summary>Turns the site's monitoring collection on or off.</summary>
    /// <remarks>Admin, matching the SiteAdminOnly wrapper the Enable/Disable buttons have always
    /// had: the gate is the boundary, so granting Operator here would widen it past what the UI
    /// reserved for Site Admins.</remarks>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.MonitoringSetupChanged, TargetType = "monitoring_settings")]
    Task<MonitoringSettings> SetEnabledAsync(bool enabled, CancellationToken ct = default);

    /// <summary>
    /// Clears the site's InfluxDB connection and disables collection, returning Monitoring to its
    /// unconfigured state. Admin: it discards the stored token and endpoint, matching the role the
    /// rest of the InfluxDB provisioning surface requires.
    /// </summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.MonitoringSetupChanged, TargetType = "monitoring_settings")]
    Task ResetInfluxSetupAsync(CancellationToken ct = default);

    /// <summary>Saves the switch and gateway temperature alert thresholds.</summary>
    [RequireRole(Roles.Operator)]
    [AuditAction(AuditActions.MonitoringSetupChanged, TargetType = "monitoring_settings")]
    Task<MonitoringSettings> SaveTempThresholdsAsync(double? switchHighC, double? gatewayHighC,
        CancellationToken ct = default);

    /// <summary>Saves the full SFP DDM alert threshold set (PON, active ethernet, and generic).</summary>
    [RequireRole(Roles.Operator)]
    [AuditAction(AuditActions.MonitoringSetupChanged, TargetType = "monitoring_settings")]
    Task<MonitoringSettings> SaveSfpThresholdsAsync(SfpThresholdEdit edit, CancellationToken ct = default);

    /// <summary>
    /// Saves the ONT alert thresholds. These are the shared PON optical thresholds, so this also
    /// drives the PON row under SFP Alert Thresholds and the gateway SFP DDM alerts.
    /// </summary>
    [RequireRole(Roles.Operator)]
    [AuditAction(AuditActions.MonitoringSetupChanged, TargetType = "monitoring_settings")]
    Task<MonitoringSettings> SaveOntThresholdsAsync(double? ponTempHighC, double? ponRxPowerLowDbm,
        CancellationToken ct = default);
}

/// <summary>
/// The SFP Alert Thresholds form's values. Temperatures are normalized (a non-positive entry means
/// "use the default"); power thresholds are not, since a negative RX low is legitimate and only a
/// blank field means default.
/// </summary>
public sealed record SfpThresholdEdit
{
    public double? PonTempHighC { get; init; }
    public double? PonRxPowerLowDbm { get; init; }
    public double? PonTxPowerHighDbm { get; init; }
    public double? AeTempHighC { get; init; }
    public double? AeRxPowerLowDbm { get; init; }
    public double? AeTxPowerHighDbm { get; init; }
    public double? SfpTempHighGenericC { get; init; }
}
