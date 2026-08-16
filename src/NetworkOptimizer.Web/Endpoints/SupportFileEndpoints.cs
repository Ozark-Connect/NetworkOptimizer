using NetworkOptimizer.Web.Services;
using NetworkOptimizer.Web.Services.Authorization;

namespace NetworkOptimizer.Web.Endpoints;

public static class SupportFileEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("").RequireAuthorization(Policies.RequireAdmin);

        group.MapPost("/api/support-file/generate", async (ISupportFileService supportFile, CancellationToken ct) =>
        {
            var result = await supportFile.GenerateAndDownloadAsync(ct);
            if (!result.Success)
                return Results.Json(new { error = result.Error }, statusCode: 400);

            return Results.File(
                result.Stream!,
                "application/gzip",
                result.Filename ?? "support-file.tgz");
        });
    }
}
