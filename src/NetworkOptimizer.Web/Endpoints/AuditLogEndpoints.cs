using System.Text;
using NetworkOptimizer.Web.Services.Auditing;
using NetworkOptimizer.Web.Services.Authorization;

namespace NetworkOptimizer.Web.Endpoints;

/// <summary>
/// Audit-log export endpoints (design doc 05): CSV/JSON export of the current filter, Admin-only. The
/// interactive Audit Log page reads via <see cref="IAuditQueryService"/> directly over its circuit;
/// these endpoints exist because a file download must come from an HTTP response, not a circuit.
/// </summary>
public static class AuditLogEndpoints
{
    public static void MapAuditLogEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/audit").RequireAuthorization(Policies.RequireAdmin);

        group.MapGet("/export.json", async (IAuditQueryService query, HttpContext ctx) =>
        {
            var json = await query.ExportJsonAsync(ReadFilter(ctx));
            return Results.File(Encoding.UTF8.GetBytes(json), "application/json", "audit-log.json");
        });

        group.MapGet("/export.csv", async (IAuditQueryService query, HttpContext ctx) =>
        {
            var csv = await query.ExportCsvAsync(ReadFilter(ctx));
            return Results.File(Encoding.UTF8.GetBytes(csv), "text/csv", "audit-log.csv");
        });
    }

    private static AuditFilter ReadFilter(HttpContext ctx)
    {
        var q = ctx.Request.Query;
        return new AuditFilter
        {
            FromUtc = DateTime.TryParse(q["from"], out var f) ? f.ToUniversalTime() : null,
            ToUtc = DateTime.TryParse(q["to"], out var t) ? t.ToUniversalTime() : null,
            Category = NullIfEmpty(q["category"]),
            Actor = NullIfEmpty(q["actor"]),
            // NOT "site": that name belongs to the app's per-tab site pin, and site-context.js
            // stamps it onto every /api/ anchor at click time. Reading it here turned Export into
            // "export the current site only", silently dropping every event that has no site of its
            // own - which is most of them, so the file came back as just the newest handful.
            SiteSlug = NullIfEmpty(q["siteSlug"]),
            Outcome = NullIfEmpty(q["outcome"]),
        };
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
