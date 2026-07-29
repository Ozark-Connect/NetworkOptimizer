using ApexCharts;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Audit;
using NetworkOptimizer.Audit.Analyzers;
using NetworkOptimizer.Audit.Services;
using NetworkOptimizer.Core.Helpers;
using NetworkOptimizer.Monitoring.Providers;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.Web;
using NetworkOptimizer.Web.Endpoints;
using NetworkOptimizer.Web.Services;
using NetworkOptimizer.Web.Services.Authorization;
using NetworkOptimizer.Web.Services.Gates;
using NetworkOptimizer.Web.Services.Identity;
using NetworkOptimizer.Web.Services.CableModemProviders;
using NetworkOptimizer.Web.Services.Licensing;
using NetworkOptimizer.Web.Services.OntProviders;
using NetworkOptimizer.Web.Services.Ssh;
using NetworkOptimizer.WiFi.Models;
using Serilog;
using Serilog.Events;

namespace NetworkOptimizer.Web.Endpoints;

/// <summary>
/// Export, validate, and restore the instance configuration archive.
/// </summary>
public static class ConfigTransferEndpoints
{
    public static void Map(WebApplication app)
    {
        // Gate 2 (design doc 06): the whole group carries authorization metadata, which is what
        // architecture test A1 checks. The policy short-circuits when the install has
        // authentication disabled (GlobalRoleHandler).
        var group = app.MapGroup("").RequireAuthorization(Policies.RequireAdmin);

        // --- Config Backup/Restore API ---

        group.MapGet("/api/config/backups", async (string type, IConfigTransferService service) =>
        {
            var exportType = type?.Equals("settings", StringComparison.OrdinalIgnoreCase) == true
                ? ExportType.SettingsOnly
                : ExportType.Full;

            var bytes = await service.ExportAsync(exportType);
            var label = exportType == ExportType.Full ? "full" : "settings";
            var fileName = $"NetworkOptimizer-{label}-{DateTime.UtcNow:yyyyMMdd}.nopt";
            return Results.File(bytes, "application/octet-stream", fileName);
        });

        group.MapPost("/api/config/backups", async (HttpContext context, IConfigTransferService service) =>
        {
            var form = await context.Request.ReadFormAsync();
            var file = form.Files.GetFile("file");
            if (file == null || file.Length == 0)
                return Results.BadRequest(new { error = "No file provided" });

            try
            {
                using var stream = file.OpenReadStream();
                var preview = await service.ValidateImportAsync(stream);
                return Results.Ok(preview);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = $"Invalid file: {ex.Message}" });
            }
        });

        group.MapPut("/api/config", async (IConfigTransferService service) =>
        {
            try
            {
                await service.ApplyImportAsync();
                return Results.Ok(new { message = "Config restored. Restarting..." });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapDelete("/api/config/backups/pending", async (IConfigTransferService service) =>
        {
            await service.CancelPendingImportAsync();
            return Results.Ok(new { message = "Pending backup cancelled" });
        });
    }
}
