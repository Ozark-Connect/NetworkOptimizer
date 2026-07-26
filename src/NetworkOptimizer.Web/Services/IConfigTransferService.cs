using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Gates;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Exports and imports the instance's configuration/database archive. Every operation here reads or
/// replaces the whole install, so the entire surface is Admin-only and audited (design doc 06, gate 9;
/// the import is the <c>db.restored</c> event from the doc-05 coverage list).
/// </summary>
[MutatingService]
public interface IConfigTransferService
{
    /// <summary>Builds the export archive for the selected scope.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.DbExported, Category = AuditCategories.Settings, TargetType = "config_archive")]
    Task<byte[]> ExportAsync(ExportType type);

    /// <summary>Validates an uploaded archive and stages it, returning what the import would change.</summary>
    [RequireRole(Roles.Admin)]
    Task<ImportPreview> ValidateImportAsync(Stream uploadStream);

    /// <summary>Applies the staged import and restarts the app.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.DbRestored, Category = AuditCategories.Settings, TargetType = "config_archive")]
    Task ApplyImportAsync();

    /// <summary>Discards a staged import without applying it.</summary>
    [RequireRole(Roles.Admin)]
    void CancelPendingImport();
}
