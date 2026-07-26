using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Gates;

namespace NetworkOptimizer.Web.Services.Licensing;

/// <summary>
/// Activates, refreshes, removes, and assigns licence keys. Gated at the service layer (design doc
/// 06, gate 9): the key and assignment lists are readable by any authenticated user, every change to
/// what this install is licensed for is Admin-only and audited as <c>license.changed</c>.
/// </summary>
[MutatingService]
public interface ILicenseActivationService
{
    /// <summary>Licence keys held by this install. Admin-only: keys carry the org they were issued to.</summary>
    [RequireRole(GlobalRoles.Admin)]
    Task<List<LicenseKeyRecord>> GetKeysAsync();

    /// <summary>Which key is assigned to which site. Admin-only, like the keys themselves.</summary>
    [RequireRole(GlobalRoles.Admin)]
    Task<List<SiteLicenseAssignment>> GetAssignmentsAsync();

    /// <summary>Activates a licence key against the licence server.</summary>
    [RequireRole(GlobalRoles.Admin)]
    [AuditAction(AuditActions.LicenseChanged, Category = AuditCategories.License, TargetType = "license_key")]
    Task<string?> ActivateAsync(string enteredKey);

    /// <summary>Re-checks a key with the licence server and refreshes its stored entitlement.</summary>
    [RequireRole(GlobalRoles.Admin)]
    [AuditAction(AuditActions.LicenseChanged, Category = AuditCategories.License, TargetType = "license_key")]
    Task<string?> RefreshKeyAsync(string canonicalKey);

    /// <summary>Removes a licence key from this install.</summary>
    [RequireRole(GlobalRoles.Admin)]
    [AuditAction(AuditActions.LicenseChanged, Category = AuditCategories.License, TargetType = "license_key")]
    Task RemoveAsync(int licenseKeyRecordId);

    /// <summary>Assigns (or clears) the licence key covering a site.</summary>
    [RequireRole(GlobalRoles.Admin)]
    [AuditAction(AuditActions.LicenseChanged, Category = AuditCategories.License, TargetType = "site")]
    Task<string?> AssignAsync(int siteId, int? licenseKeyRecordId);

    /// <summary>This install's stable installation id (shown in the licensing card). Admin-only.</summary>
    [RequireRole(GlobalRoles.Admin)]
    Task<Guid> GetOrCreateInstallationIdAsync();
}
