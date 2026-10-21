using NetworkOptimizer.Web.Services;
using NetworkOptimizer.Web.Services.Authorization;

namespace NetworkOptimizer.Web.Endpoints;

/// <summary>
/// The site's managed WAF exclusions. IWafConnectionAdmin gates each call on the site in context
/// (Viewer to list, Site Admin to add or remove) and audits the changes; the group carries the metadata.
/// </summary>
public static class WafEndpoints
{
    /// <summary>The body of POST /api/waf/exclusions.</summary>
    public sealed record ExclusionRequest(long RuleId, string? Host, string? Method, string? Path, string? Note);

    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("").RequireAuthorization(Policies.RequireViewer);

        group.MapGet("/api/waf/exclusions", async (IWafConnectionAdmin waf, CancellationToken ct) =>
            Results.Ok(await waf.GetExclusionsAsync(ct)));

        group.MapPost("/api/waf/exclusions", async (ExclusionRequest body, IWafConnectionAdmin waf, CancellationToken ct) =>
        {
            try
            {
                var added = await waf.AddExclusionAsync(body.RuleId, body.Host, body.Method, body.Path, body.Note, ct);
                return Results.Created($"/api/waf/exclusions/{added.Id}", added);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapDelete("/api/waf/exclusions/{id}", async (string id, IWafConnectionAdmin waf, CancellationToken ct) =>
        {
            try
            {
                return await waf.RemoveExclusionAsync(id, ct) ? Results.NoContent() : Results.NotFound();
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
    }
}
