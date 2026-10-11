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

    /// <inheritdoc />
    public void SourceBlocked(string? siteSlug, string sourceIp, string reason, DateTime expiresAt, IReadOnlyList<string> targets) =>
        _services.GetService<IAuditLogger>()?.Log(AuditEventBuilder.FromSystem(
            AuditCategories.Settings,
            AuditActions.ThreatSourceBlocked,
            targetType: "threat_block",
            targetName: sourceIp,
            siteSlug: siteSlug,
            details: new { reason, expiresAt, targets = string.Join(",", targets) }));

    /// <inheritdoc />
    public void BlockExpired(string? siteSlug, string sourceIp) =>
        _services.GetService<IAuditLogger>()?.Log(AuditEventBuilder.FromSystem(
            AuditCategories.Settings,
            AuditActions.ThreatSourceUnblocked,
            targetType: "threat_block",
            targetName: sourceIp,
            siteSlug: siteSlug,
            details: new { reason = "expired" }));

    /// <inheritdoc />
    public void TrustedAddressesChanged(string? siteSlug, IReadOnlyList<string> added, IReadOnlyList<string> dropped) =>
        _services.GetService<IAuditLogger>()?.Log(AuditEventBuilder.FromSystem(
            AuditCategories.Settings,
            AuditActions.SettingsChanged,
            targetType: "trusted_sources",
            targetName: siteSlug ?? "default",
            siteSlug: siteSlug,
            details: new { added = string.Join(",", added), dropped = string.Join(",", dropped) }));
}
