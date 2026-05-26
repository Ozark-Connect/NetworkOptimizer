using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Threats.Interfaces;
using NetworkOptimizer.Web.Services.Auditing;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Writes the threat collector's own noise-filter changes to the audit log as Network Optimizer.
/// Uses the same action and target as a user's filter change so both read together.
/// </summary>
public sealed class ThreatSystemAudit : IThreatSystemAudit
{
    private readonly IServiceProvider _services;

    public ThreatSystemAudit(IServiceProvider services) => _services = services;

    /// <inheritdoc />
    public void NoiseFilterChanged(string change, string sourceIp, string? siteSlug, string reason) =>
        _services.GetService<IAuditLogger>()?.Log(AuditEventBuilder.FromSystem(
            AuditCategories.Settings,
            AuditActions.SettingsChanged,
            targetType: "threat_noise_filter",
            targetName: sourceIp,
            siteSlug: siteSlug,
            details: new { change, reason }));
}
