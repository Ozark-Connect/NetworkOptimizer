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
/// Downloads for the pre-generated Security Audit report PDFs.
/// </summary>
public static class ReportEndpoints
{
    public static void Map(WebApplication app)
    {
        // Gate 2 (design doc 06): the whole group carries authorization metadata, which is what
        // architecture test A1 checks. The policy short-circuits when the install has
        // authentication disabled (GlobalRoleHandler).
        var group = app.MapGroup("").RequireAuthorization(Policies.RequireViewer);

        // Audit Report PDF download endpoints (serves pre-generated PDFs)
        // Auth handled by middleware for all /api/* paths
        // Uses strongly-typed int to prevent path traversal attacks
        group.MapGet("/api/reports/{auditId:int}/pdf", async (int auditId, AuditService auditService) =>
        {
            var (pdfBytes, fileName) = await auditService.GetAuditPdfAsync(auditId);
            return pdfBytes != null ? Results.File(pdfBytes, "application/pdf", fileName) : Results.NotFound(new { error = "PDF not found" });
        });

        // Get the latest audit report PDF (works across restarts since it queries database)
        group.MapGet("/api/reports/latest/pdf", async (AuditService auditService) =>
        {
            var (pdfBytes, fileName) = await auditService.GetLatestAuditPdfAsync();
            return pdfBytes != null ? Results.File(pdfBytes, "application/pdf", fileName) : Results.NotFound(new { error = "PDF not found" });
        });
    }
}
