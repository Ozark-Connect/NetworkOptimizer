using NetworkOptimizer.Web.Services.Authorization;
using NetworkOptimizer.Web.Services.ClientIcons;

namespace NetworkOptimizer.Web.Endpoints;

public static class ClientIconEndpoints
{
    public static void Map(WebApplication app)
    {
        var read = app.MapGroup("").RequireAuthorization(Policies.RequireViewer);

        // A key names one image for good (a changed icon gets a new key), so the browser keeps it.
        read.MapGet("/api/client-icons/{key}", async (string key, IClientIcons icons, HttpContext context, CancellationToken ct) =>
        {
            var image = await icons.GetImageAsync(key, ct);
            if (image == null)
                return Results.NotFound();
            context.Response.Headers.CacheControl = "private, max-age=2592000, immutable";
            return Results.File(image.Path, image.ContentType);
        });
    }
}
