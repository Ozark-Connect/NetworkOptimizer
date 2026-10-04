using NetworkOptimizer.Storage.Interfaces;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services.Gates;

namespace NetworkOptimizer.Web.Services.Firmware;

/// <inheritdoc />
public class SharedFirmwareCatalogService : ISharedFirmwareCatalogService
{
    /// <summary>The channel a hand-added build is filed under: only Early Access sites take it.</summary>
    public const string AddedChannel = FirmwareChannels.Beta;

    private readonly ISharedFirmwareCatalogRepository _catalog;
    private readonly IAuditContext _audit;
    private readonly TimeProvider _time;
    private readonly ILogger<SharedFirmwareCatalogService> _logger;

    public SharedFirmwareCatalogService(
        ISharedFirmwareCatalogRepository catalog,
        IAuditContext audit,
        TimeProvider time,
        ILogger<SharedFirmwareCatalogService> logger)
    {
        _catalog = catalog;
        _audit = audit;
        _time = time;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<FirmwareUrlAddResult> AddFirmwareUrlAsync(string url, CancellationToken cancellationToken = default)
    {
        var parsed = FirmwareUrlParser.Parse(url, out var error);
        if (parsed == null)
        {
            _audit.SuppressNoChange();
            return new FirmwareUrlAddResult(error);
        }

        var now = _time.GetUtcNow().UtcDateTime;
        FirmwareUrlAddResult result;

        if (parsed.Kind == FirmwareUrlKind.NetworkApp)
        {
            await _catalog.UpsertNetworkAppBuildAsync(AddedChannel, parsed.Version, parsed.Url, cancellationToken);
            result = new FirmwareUrlAddResult(null, FirmwareUrlKind.NetworkApp, null, parsed.Version, Url: parsed.Url);
        }
        else
        {
            var (kind, target, matched) = await ResolveTargetAsync(parsed, cancellationToken);
            if (kind == FirmwareUrlKind.Unknown || string.IsNullOrWhiteSpace(target))
            {
                _audit.SuppressNoChange();
                return new FirmwareUrlAddResult(
                    "This link is not a known firmware layout, and no build already in the catalog shares its path.");
            }

            if (kind == FirmwareUrlKind.UniFiOs)
            {
                // No publish date comes with a URL. The add time keeps the release-age gate meaningful.
                await _catalog.UpsertUniFiOsBuildsAsync(
                    [new SharedUniFiOsBuild { Platform = target, Channel = AddedChannel, Version = parsed.Version, Url = parsed.Url, PublishedUtc = now }],
                    cancellationToken);
                if (!(await _catalog.ListUniFiOsBuildsAsync(cancellationToken)).Any(b =>
                        b.Platform == target && b.Channel == AddedChannel && b.Version == parsed.Version))
                    return SaveFailed();
            }
            else
            {
                await _catalog.UpsertDeviceBuildsAsync(
                    [new SharedFirmwareBuild { Model = target, Channel = AddedChannel, Version = parsed.Version, Url = parsed.Url }],
                    cancellationToken);
                if (!(await _catalog.ListDeviceBuildsAsync(cancellationToken)).Any(b =>
                        b.Model == target && b.Channel == AddedChannel && b.Version == parsed.Version))
                    return SaveFailed();
            }

            result = new FirmwareUrlAddResult(null, kind, target, parsed.Version, matched, parsed.Url);
        }

        _audit.SetTarget(result.Target ?? "unifi-network", $"{result.Kind} {result.Version}");
        _audit.SetDetails(new
        {
            kind = result.Kind.ToString(),
            target = result.Target,
            version = result.Version,
            channel = AddedChannel,
            url = parsed.Url,
            matchedExisting = result.MatchedExisting,
        });
        _logger.LogInformation(
            "Added {Kind} {Version} for {Target} to the shared firmware catalog on {Channel} ({Url})",
            result.Kind, result.Version, result.Target ?? "UniFi Network", AddedChannel, parsed.Url);
        return result;
    }

    /// <summary>
    /// The model or platform a firmware image is for. A build already in the catalog with the same
    /// path and file token wins over the token itself: the file name does not always carry the
    /// console's code (UXGPRO ships as UXGPROV2), and only a matching code is ever offered.
    /// </summary>
    private async Task<(FirmwareUrlKind Kind, string? Target, bool Matched)> ResolveTargetAsync(
        ParsedFirmwareUrl parsed, CancellationToken cancellationToken)
    {
        var token = parsed.Token!;

        if (parsed.Kind != FirmwareUrlKind.UniFiOs)
        {
            var device = (await _catalog.ListDeviceBuildsAsync(cancellationToken))
                .FirstOrDefault(b => string.Equals(FirmwareUrlParser.TokenIn(b.Url, parsed.Directory), token, StringComparison.OrdinalIgnoreCase));
            if (device != null) return (FirmwareUrlKind.Device, device.Model, true);
        }

        if (parsed.Kind != FirmwareUrlKind.Device)
        {
            var os = (await _catalog.ListUniFiOsBuildsAsync(cancellationToken))
                .FirstOrDefault(b => string.Equals(FirmwareUrlParser.TokenIn(b.Url, parsed.Directory), token, StringComparison.OrdinalIgnoreCase));
            if (os != null) return (FirmwareUrlKind.UniFiOs, os.Platform, true);
        }

        return (parsed.Kind, parsed.Kind == FirmwareUrlKind.Unknown ? null : token, false);
    }

    private FirmwareUrlAddResult SaveFailed()
    {
        _audit.SuppressNoChange();
        return new FirmwareUrlAddResult("The build could not be saved. See the logs for details.");
    }
}
