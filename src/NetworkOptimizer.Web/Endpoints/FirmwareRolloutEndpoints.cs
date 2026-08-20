using NetworkOptimizer.Web.Services;
using NetworkOptimizer.Web.Services.Authorization;
using NetworkOptimizer.Web.Services.Firmware;

namespace NetworkOptimizer.Web.Endpoints;

/// <summary>
/// The Firmware Rollout page's file downloads. The page itself reads the gated service directly;
/// this exists for the report PDF, which the browser has to fetch as a file.
/// </summary>
public static class FirmwareRolloutEndpoints
{
    public static void Map(WebApplication app)
    {
        // Gate 2 (design doc 06): the group carries authorization metadata, which architecture test
        // A1 checks. Reading a report is a Viewer act; the rollout service gates it again on the
        // site in context, which is why the report is read through the interface and not the store.
        var group = app.MapGroup("").RequireAuthorization(Policies.RequireViewer);

        group.MapGet("/api/firmware-rollout/reports/{planId:int}/pdf", async (
            int planId,
            IFirmwareRolloutService rollout,
            SiteContextService siteContext,
            SiteManagementService siteManagement,
            CancellationToken ct) =>
        {
            var view = await rollout.GetReportAsync(planId, ct);
            var report = RolloutReport.Parse(view?.ReportJson);
            if (report == null)
                return Results.NotFound(new { error = "That rollout has no report yet" });

            // Only a secondary site has a name worth putting in the title; the default site is
            // just "this install" to its operator.
            string? siteName = null;
            if (!siteContext.IsDefault)
            {
                var sites = await siteManagement.GetSitesAsync();
                siteName = sites.FirstOrDefault(s => s.Slug == siteContext.Slug)?.Name ?? siteContext.Slug;
            }

            var pdfBytes = new RolloutReportPdfGenerator().GenerateReportBytes(report, siteName);
            var sitePart = siteContext.IsDefault ? "" : $"_{siteContext.Slug}";
            var stamp = (report.CompletedAt ?? report.GeneratedAt).ToLocalTime();
            var fileName = $"FirmwareRollout{sitePart}_{planId}_{stamp:yyyyMMdd-HHmm}.pdf";
            return Results.File(pdfBytes, "application/pdf", fileName);
        });
    }
}
