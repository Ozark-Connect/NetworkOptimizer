using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.Web.Services.Gates;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Custom SNMP OIDs a site polls on top of the built-in set.
///
/// Adding one is Site Operator and removing one is Site Admin, matching the card's own gates:
/// adding widens what we collect, while removing discards a series other views may already be
/// built on.
/// </summary>
[MutatingService(SiteScoped = true)]
public interface ICustomOidService
{
    /// <summary>Adds an OID to poll on a device.</summary>
    [RequireRole(Roles.Operator)]
    [AuditAction(AuditActions.MonitoringSetupChanged, TargetType = "custom_oid")]
    Task<CustomOidConfiguration> AddAsync(
        string deviceMac, string oid, string fieldName, CustomOidValueType valueType,
        CustomOidScope scope, string? description);

    /// <summary>Edits an OID's definition. Same tier as adding: it alters what we collect.</summary>
    [RequireRole(Roles.Operator)]
    [AuditAction(AuditActions.MonitoringSetupChanged, TargetType = "custom_oid")]
    Task<bool> UpdateAsync(
        int id, string oid, string fieldName, CustomOidValueType valueType,
        CustomOidScope scope, bool enabled, string? description);

    /// <summary>Stops polling an OID and forgets its configuration.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.MonitoringSetupChanged, TargetType = "custom_oid")]
    Task<bool> DeleteAsync(int id);
}

/// <inheritdoc cref="ICustomOidService" />
public class CustomOidService : ICustomOidService
{
    private readonly SiteDbContextFactory _siteDbFactory;
    private readonly SiteContextService _siteContext;

    /// <param name="siteDbFactory">Per-site database factory.</param>
    /// <param name="siteContext">The site this scope operates on.</param>
    public CustomOidService(SiteDbContextFactory siteDbFactory, SiteContextService siteContext)
    {
        _siteDbFactory = siteDbFactory;
        _siteContext = siteContext;
    }

    /// <inheritdoc />
    public async Task<CustomOidConfiguration> AddAsync(
        string deviceMac, string oid, string fieldName, CustomOidValueType valueType,
        CustomOidScope scope, string? description)
    {
        var now = DateTime.UtcNow;
        var entry = new CustomOidConfiguration
        {
            DeviceMac = deviceMac,
            Oid = oid.Trim(),
            FieldName = fieldName.Trim(),
            ValueType = valueType,
            Scope = scope,
            Enabled = true,
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            CreatedAt = now,
            UpdatedAt = now,
        };

        using var db = _siteDbFactory.CreateForSite(_siteContext.Slug, _siteContext.IsDefault);
        db.CustomOidConfigurations.Add(entry);
        await db.SaveChangesAsync();
        return entry;
    }

    /// <inheritdoc />
    public async Task<bool> UpdateAsync(
        int id, string oid, string fieldName, CustomOidValueType valueType,
        CustomOidScope scope, bool enabled, string? description)
    {
        using var db = _siteDbFactory.CreateForSite(_siteContext.Slug, _siteContext.IsDefault);
        var existing = await db.CustomOidConfigurations.FindAsync(id);
        if (existing == null) return false;

        existing.Oid = oid.Trim();
        existing.FieldName = fieldName.Trim();
        existing.ValueType = valueType;
        existing.Scope = scope;
        existing.Enabled = enabled;
        existing.Description = description?.Trim();
        existing.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(int id)
    {
        using var db = _siteDbFactory.CreateForSite(_siteContext.Slug, _siteContext.IsDefault);
        var entry = await db.CustomOidConfigurations.FindAsync(id);
        if (entry == null) return false;

        db.CustomOidConfigurations.Remove(entry);
        await db.SaveChangesAsync();
        return true;
    }
}
