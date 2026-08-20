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
/// Data the Client Performance pages poll for the signed-in client: signal, trace, and speed history.
/// </summary>
public static class ClientDashboardEndpoints
{
    public static void Map(WebApplication app)
    {
        // Gate 2 (design doc 06): every endpoint is mapped onto a group that carries its
        // authorization policy, which is what architecture test A1 checks. Reads are any
        // authenticated user, running a test is Operator, and changes are Admin.
        var read = app.MapGroup("").RequireAuthorization(Policies.RequireViewer);
        var operate = app.MapGroup("").RequireAuthorization(Policies.RequireOperator);

        // Demo mode masking endpoint (returns mappings from DEMO_MODE_MAPPINGS env var)
        // --- Client Dashboard API ---

        // The page cannot read its own viewer's address: the app does not prerender, so the
        // component only ever runs in the circuit, where there is no HttpContext. The browser asks
        // for it here instead, where the request carries the real address.
        read.MapGet("/api/client-dashboard/address", (HttpContext context) =>
            Results.Ok(new { address = EndpointHelpers.GetClientIp(context) }));

        read.MapGet("/api/client-dashboard/client", async (HttpContext context, ClientDashboardService service) =>
        {
            var clientIp = EndpointHelpers.GetClientIp(context);
            var identity = await service.IdentifyClientAsync(clientIp);
            return identity != null ? Results.Ok(identity) : Results.NotFound(new { error = "Client not found" });
        });

        read.MapGet("/api/client-dashboard/signal-detail", async (HttpContext context, ClientDashboardService service,
            double? lat = null, double? lng = null, int? acc = null) =>
        {
            var clientIp = EndpointHelpers.GetClientIp(context);
            var result = await service.PollSignalAsync(clientIp, lat, lng, acc);
            return result != null ? Results.Ok(result) : Results.NotFound(new { error = "Client not found" });
        });

        operate.MapPost("/api/client-dashboard/gps-locations", async (HttpContext context, ClientDashboardService service) =>
        {
            var request = await context.Request.ReadFromJsonAsync<NetworkOptimizer.Web.Models.GpsUpdateRequest>();
            if (request == null)
                return Results.BadRequest(new { error = "Request body is required" });

            // Identify client by IP to get MAC
            var clientIp = EndpointHelpers.GetClientIp(context);
            var identity = await service.IdentifyClientAsync(clientIp);
            if (identity == null)
                return Results.NotFound(new { error = "Client not found" });

            await service.SubmitGpsAsync(identity.Mac, request.Latitude, request.Longitude, request.AccuracyMeters);
            return Results.Ok(new { success = true });
        });

        read.MapGet("/api/client-dashboard/signal-history", async (ClientDashboardService service,
            string mac, DateTime? from = null, DateTime? to = null, int? skip = null, int? take = null) =>
        {
            var fromDate = from ?? DateTime.UtcNow.AddHours(-24);
            var toDate = to ?? DateTime.UtcNow;
            var history = await service.GetSignalHistoryAsync(mac, fromDate, toDate, skip ?? 0, take ?? 500);
            return Results.Ok(history);
        });

        read.MapGet("/api/client-dashboard/trace-history", async (ClientDashboardService service,
            string mac, DateTime? from = null, DateTime? to = null) =>
        {
            var fromDate = from ?? DateTime.UtcNow.AddHours(-24);
            var toDate = to ?? DateTime.UtcNow;
            var history = await service.GetTraceHistoryAsync(mac, fromDate, toDate);
            return Results.Ok(history);
        });

        read.MapGet("/api/client-dashboard/speed-results", async (ClientDashboardService service,
            string mac, DateTime? from = null, DateTime? to = null) =>
        {
            var fromDate = from ?? DateTime.UtcNow.AddHours(-24);
            var toDate = to ?? DateTime.UtcNow;
            var results = await service.GetSpeedResultsAsync(mac, fromDate, toDate);
            return Results.Ok(results);
        });
    }
}
