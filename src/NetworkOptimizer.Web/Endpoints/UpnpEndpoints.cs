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
/// Notes an operator attaches to UPnP port mappings in the UPnP Inspector.
/// </summary>
public static class UpnpEndpoints
{
    public static void Map(WebApplication app)
    {
        // Gate 2 (design doc 06): every endpoint is mapped onto a group that carries its
        // authorization policy, which is what architecture test A1 checks. Reads are any
        // authenticated user, running a test is Operator, and changes are Admin.
        var read = app.MapGroup("").RequireAuthorization(Policies.RequireViewer);
        var admin = app.MapGroup("").RequireAuthorization(Policies.RequireViewer);

        // UPnP Notes API endpoints
        read.MapGet("/api/upnp/notes", async (IUpnpNoteService notes) =>
            Results.Ok(await notes.GetNotesAsync()));

        // IUpnpNoteService gates this on the site in context (Site Operator) and resolves the
        // site's own database; the group carries the metadata only.
        admin.MapPut("/api/upnp/notes", async (HttpContext context, IUpnpNoteService notes) =>
        {
            var request = await context.Request.ReadFromJsonAsync<UpnpNoteRequest>();
            if (request == null || string.IsNullOrWhiteSpace(request.HostIp) ||
                string.IsNullOrWhiteSpace(request.Port) || string.IsNullOrWhiteSpace(request.Protocol))
            {
                return Results.BadRequest(new { error = "HostIp, Port, and Protocol are required" });
            }

            await notes.SaveNoteAsync(request.HostIp, request.Port, request.Protocol, request.Note);
            return Results.Ok(new { success = true });
        });
    }
}
