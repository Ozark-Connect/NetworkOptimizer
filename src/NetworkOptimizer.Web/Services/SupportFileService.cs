using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Gates;

namespace NetworkOptimizer.Web.Services;

[MutatingService(SiteScoped = true)]
public interface ISupportFileService
{
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.SupportFileGenerated, TargetType = "console")]
    Task<SupportFileResult> GenerateAndDownloadAsync(CancellationToken ct = default);

    [RequireRole(Roles.Viewer)]
    Task<bool> GetIsAvailableAsync();
}

/// <summary>
/// Generates and downloads a full UniFi OS console support file (FD21 bundle) for the site.
/// The endpoints live on the UniFi OS layer, not the Network API, so they require a
/// username/password session - API key connections cannot authenticate at all.
///
/// The flow is fire-and-forget: POST /generate returns immediately, and the caller polls
/// HEAD /download until it stops returning 423 Locked. On a UOS Server with Network only,
/// generation takes ~6 seconds; a Cloud Gateway with Protect can take minutes.
/// </summary>
public class SupportFileService : ISupportFileService
{
    private readonly UniFiConnectionService _connection;
    private readonly ILogger<SupportFileService> _logger;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(5);

    public SupportFileService(
        UniFiConnectionService connection,
        ILogger<SupportFileService> logger)
    {
        _connection = connection;
        _logger = logger;
    }

    /// <summary>Whether the connection can generate a support file (connected, username/password).</summary>
    public bool IsAvailable => _connection.IsConnected && !_connection.IsApiKeyAuth;

    /// <summary>Whether the current connection uses an API key.</summary>
    public bool IsApiKeyConnection => _connection.IsApiKeyAuth;

    public Task<bool> GetIsAvailableAsync() => Task.FromResult(IsAvailable);

    public async Task<SupportFileResult> GenerateAndDownloadAsync(CancellationToken ct = default)
    {
        if (!_connection.IsConnected)
            return SupportFileResult.Fail("Not connected to a UniFi Console.");

        if (_connection.IsApiKeyAuth)
            return SupportFileResult.Fail(
                "Support file generation requires a username and password connection. " +
                "API key connections don't have the UniFi OS session needed for this operation. " +
                "You can switch to username/password in Settings - Connection.");

        var client = _connection.Client;
        if (client == null)
            return SupportFileResult.Fail("Console connection is not ready.");

        _logger.LogInformation("Starting support file generation");
        if (!await client.GenerateSupportFileAsync(recreate: true, ct))
            return SupportFileResult.Fail("Failed to start support file generation. The console may have denied the request.");

        var deadline = DateTime.UtcNow + MaxWait;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(PollInterval, ct);

            if (await client.IsSupportFileReadyAsync(ct))
            {
                _logger.LogInformation("Support file ready, downloading");
                var download = await client.DownloadSupportFileAsync(ct);
                if (download == null)
                    return SupportFileResult.Fail("Support file was ready but download failed.");

                return SupportFileResult.Ok(download.Value.stream, download.Value.filename);
            }
        }

        return SupportFileResult.Fail($"Support file generation timed out after {MaxWait.TotalMinutes:0} minutes.");
    }
}

public class SupportFileResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public Stream? Stream { get; init; }
    public string? Filename { get; init; }

    public static SupportFileResult Ok(Stream stream, string filename) =>
        new() { Success = true, Stream = stream, Filename = filename };

    public static SupportFileResult Fail(string error) =>
        new() { Success = false, Error = error };
}
