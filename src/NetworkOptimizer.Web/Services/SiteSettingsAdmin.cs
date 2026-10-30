using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Gates;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// The gated write path for settings that belong to one site, decided by the caller's role on the site
/// in context. <see cref="ISystemSettingsAdmin"/> needs the global Admin role, which is right for
/// instance-wide settings and wrong for these: a Site Admin administers their own site's.
/// </summary>
[MutatingService(SiteScoped = true)]
public interface ISiteSettingsAdmin
{
    /// <summary>
    /// Saves the Security Audit options in the current site's database as one change. Only
    /// <c>audit:</c> keys are accepted: on the main site that database also holds instance-wide settings.
    /// </summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.SettingsChanged, Category = AuditCategories.Settings, TargetType = "audit_settings")]
    Task SaveAuditOptionsAsync(IReadOnlyDictionary<string, string?> values);

    /// <summary>Turns threat event collection on or off for the current site.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.SettingsChanged, Category = AuditCategories.Settings, TargetType = "threat_collection")]
    Task SetThreatCollectionEnabledAsync(bool enabled);
}

/// <inheritdoc />
public sealed class SiteSettingsAdmin(ISystemSettingsService settings, SiteContextService site) : ISiteSettingsAdmin
{
    /// <summary>Prefix every Security Audit option key carries.</summary>
    public const string AuditKeyPrefix = "audit:";

    /// <summary>
    /// The site's threat collection key, as <c>ThreatCollectionService</c> reads it: the bare key on the
    /// default site, <c>threats.enabled:{slug}</c> elsewhere. Stored in the main database.
    /// </summary>
    public static string ThreatCollectionKey(SiteContextService site) =>
        site.IsDefault ? "threats.enabled" : $"threats.enabled:{site.Slug}";

    /// <inheritdoc />
    public async Task SaveAuditOptionsAsync(IReadOnlyDictionary<string, string?> values)
    {
        if (values.Keys.FirstOrDefault(k => !k.StartsWith(AuditKeyPrefix, StringComparison.Ordinal)) is { } bad)
            throw new ArgumentException($"Not a Security Audit option: {bad}", nameof(values));
        foreach (var (key, value) in values)
            await settings.SetAsync(site.Slug, key, value);
    }

    /// <inheritdoc />
    public Task SetThreatCollectionEnabledAsync(bool enabled) =>
        settings.SetGlobalAsync(ThreatCollectionKey(site), enabled ? "true" : "false");
}
