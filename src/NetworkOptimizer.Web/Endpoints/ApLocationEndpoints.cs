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
        var admin = app.MapGroup("").RequireAuthorization(Policies.RequireAdmin);

        // AP Location API endpoints
        read.MapGet("/api/ap-locations", async (NetworkOptimizerDbContext db) =>
        {
            var locations = await db.ApLocations.ToListAsync();
            return Results.Ok(locations);
        });

        admin.MapPut("/api/ap-locations/{mac}", async (string mac, HttpContext context, NetworkOptimizerDbContext db) =>
        {
            var request = await context.Request.ReadFromJsonAsync<ApLocationRequest>();
            if (request == null)
            {
                return Results.BadRequest(new { error = "Request body is required" });
            }

            // Normalize MAC to lowercase for consistent matching
            var normalizedMac = mac.ToLowerInvariant();

            var existing = await db.ApLocations.FirstOrDefaultAsync(a => a.ApMac == normalizedMac);
            if (existing != null)
            {
                existing.Latitude = request.Latitude;
                existing.Longitude = request.Longitude;
                existing.Floor = request.Floor ?? 1;
                existing.UpdatedAt = DateTime.UtcNow;
            }
            else
            {
                var location = new ApLocation
                {
                    ApMac = normalizedMac,
                    Latitude = request.Latitude,
                    Longitude = request.Longitude,
                    Floor = request.Floor ?? 1,
                    UpdatedAt = DateTime.UtcNow
                };
                db.ApLocations.Add(location);
            }

            await db.SaveChangesAsync();
            return Results.Ok(new { success = true });
        });

        admin.MapDelete("/api/ap-locations/{mac}", async (string mac, NetworkOptimizerDbContext db) =>
        {
            var normalizedMac = mac.ToLowerInvariant();
            var existing = await db.ApLocations.FirstOrDefaultAsync(a => a.ApMac == normalizedMac);
            if (existing == null)
            {
                return Results.NotFound();
            }

            db.ApLocations.Remove(existing);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }
}
