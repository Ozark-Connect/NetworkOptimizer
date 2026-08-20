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
/// Placement of access points on the floor plan (position, mount, orientation).
/// </summary>
public static class ApLocationEndpoints
{
    public static void Map(WebApplication app)
    {
        // Gate 2 (design doc 06): every endpoint is mapped onto a group that carries its
        // authorization policy, which is what architecture test A1 checks. Reads are any
        // authenticated user, running a test is Operator, and changes are Admin.
        var read = app.MapGroup("").RequireAuthorization(Policies.RequireViewer);
        // Metadata only - IApMapAdminService gates these on the site in context.
        var admin = app.MapGroup("").RequireAuthorization(Policies.RequireViewer);

        // AP Location API endpoints
        read.MapGet("/api/ap-locations", async (NetworkOptimizer.Storage.Services.SiteDbContextFactory siteDbFactory, SiteContextService siteContext) =>
        {
            // The site's own database, matching where the writes below land.
            using var db = siteDbFactory.CreateForSite(siteContext.Slug, siteContext.IsDefault);
            var locations = await db.ApLocations.ToListAsync();
            return Results.Ok(locations);
        });

        // Through the gated service, not the DbContext: it carries the Site Operator gate the
        // Signal Map's own editing uses, and it resolves the SITE's database - injecting the
        // context here wrote a managed site's placements into the main install's.
        admin.MapPut("/api/ap-locations/{mac}", async (string mac, HttpContext context, IApMapAdminService apMap) =>
        {
            var request = await context.Request.ReadFromJsonAsync<ApLocationRequest>();
            if (request == null)
            {
                return Results.BadRequest(new { error = "Request body is required" });
            }

            await apMap.SaveApLocationAsync(mac, request.Latitude, request.Longitude, request.Floor ?? 1);
            return Results.Ok(new { success = true });
        });

        admin.MapDelete("/api/ap-locations/{mac}", async (string mac, IApMapAdminService apMap) =>
            await apMap.DeleteApLocationAsync(mac) ? Results.NoContent() : Results.NotFound());
    }
}
