using System.Collections.Concurrent;
using NetworkOptimizer.Web.Services.Authorization;

namespace NetworkOptimizer.Web.Endpoints;

public static class SupportFileEndpoints
{
    private static readonly ConcurrentDictionary<string, PendingDownload> _pending = new();

    public static string Stage(string tempFilePath, string filename)
    {
        var token = Guid.NewGuid().ToString("N");
        _pending[token] = new PendingDownload(tempFilePath, filename, DateTime.UtcNow);
        return token;
    }

    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("").RequireAuthorization(Policies.RequireAdmin);

        group.MapGet("/api/support-file/download/{token}", (string token) =>
        {
            if (!_pending.TryRemove(token, out var download))
                return Results.NotFound();

            if (DateTime.UtcNow - download.Created > TimeSpan.FromMinutes(10))
            {
                try { File.Delete(download.Path); } catch { }
                return Results.NotFound();
            }

            if (!File.Exists(download.Path))
                return Results.NotFound();

            var stream = File.OpenRead(download.Path);
            _ = Task.Delay(TimeSpan.FromMinutes(5)).ContinueWith(_ =>
            {
                try { File.Delete(download.Path); } catch { }
            });

            return Results.File(stream, "application/gzip", download.Filename);
        });
    }

    private sealed record PendingDownload(string Path, string Filename, DateTime Created);
}
