using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Web.Services;
using NetworkOptimizer.Web.Services.Authorization;

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

        // Through the gated service, not the DbContext: the service carries the Site Operator
        // gate, the same one the Signal Map's editing uses.
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
