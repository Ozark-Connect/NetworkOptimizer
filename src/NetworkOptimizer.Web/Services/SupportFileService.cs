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

    [RequireRole(Roles.Viewer)]
    Task<bool> GetIsApiKeyConnectionAsync();
}

/// <summary>
/// Generates and downloads a full UniFi OS console support file (FD21 bundle) for the site.
/// The endpoints live on the UniFi OS layer (direct /api/ path, not under /proxy/network/),
/// so they require a username/password session - API key connections cannot authenticate.
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

    public Task<bool> GetIsAvailableAsync() =>
        Task.FromResult(_connection.IsConnected && !_connection.IsApiKeyAuth);

    public Task<bool> GetIsApiKeyConnectionAsync() =>
        Task.FromResult(_connection.IsApiKeyAuth);

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
            return SupportFileResult.Fail(
                "The console denied the request. Support file generation requires the console user " +
                "to have the Super Admin role, or the Control Plane - Full Admin permission.");

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

                using var stream = download.Value.stream;
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms, ct);

                return SupportFileResult.Ok(ms.ToArray(), download.Value.filename);
            }
        }

        return SupportFileResult.Fail($"Support file generation timed out after {MaxWait.TotalMinutes:0} minutes.");
    }
}

public class SupportFileResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public byte[]? Data { get; init; }
    public string? Filename { get; init; }

    public static SupportFileResult Ok(byte[] data, string filename) =>
        new() { Success = true, Data = data, Filename = filename };

    public static SupportFileResult Fail(string error) =>
        new() { Success = false, Error = error };
}
