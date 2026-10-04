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
    private readonly IHttpClientFactory _http;
    private readonly ILogger<SharedFirmwareCatalogService> _logger;

    public SharedFirmwareCatalogService(
        ISharedFirmwareCatalogRepository catalog,
        IAuditContext audit,
        TimeProvider time,
        IHttpClientFactory http,
        ILogger<SharedFirmwareCatalogService> logger)
    {
        _catalog = catalog;
        _audit = audit;
        _time = time;
        _http = http;
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
        FirmwareUrlAddResult? result = null;

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
                var family = await ResolveFamilyAsync(parsed, target, cancellationToken);
                if (family == null)
                {
                    _audit.SuppressNoChange();
                    return new FirmwareUrlAddResult(
                        $"This image does not match any build a Console has been offered, and its folder ({parsed.Token}) is not a model code. Paste the per-model link from fw-download.ubnt.com instead.");
                }

                await _catalog.UpsertDeviceBuildsAsync(
                    family.Models.Select(m => new SharedFirmwareBuild
                    {
                        Model = m, Channel = AddedChannel, Version = family.Version, Url = family.UrlFor(m, parsed.Url), Md5Sum = family.Md5,
                    }).ToList(),
                    cancellationToken);
                if (!(await _catalog.ListDeviceBuildsAsync(cancellationToken)).Any(b =>
                        b.Model == family.Models[0] && b.Channel == AddedChannel && b.Version == family.Version))
                    return SaveFailed();

                var lead = family.Models.FirstOrDefault(m => string.Equals(m, target, StringComparison.OrdinalIgnoreCase))
                    ?? family.Models[0];
                result = new FirmwareUrlAddResult(null, kind, lead, family.Version, matched || family.Matched, parsed.Url, family.Models,
                    family.Models.ToDictionary(m => m, m => family.UrlFor(m, parsed.Url), StringComparer.OrdinalIgnoreCase));
            }

            result ??= new FirmwareUrlAddResult(null, kind, target, parsed.Version, matched, parsed.Url);
        }

        _audit.SetTarget(result.Target ?? "unifi-network", result.DisplayName);
        _audit.SetDetails(new
        {
            kind = result.Kind.ToString(),
            target = result.Target,
            models = result.Models,
            modelUrls = result.ModelUrls,
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

    /// <summary>
    /// The device models one image serves, the version to file it under, its md5, and each model's
    /// own catalog URL for the same bytes where it has one.
    /// </summary>
    private sealed record ImageFamily(
        IReadOnlyList<string> Models, string Version, string? Md5, bool Matched,
        IReadOnlyDictionary<string, string>? CatalogUrls = null)
    {
        public string UrlFor(string model, string pasted) =>
            CatalogUrls != null && CatalogUrls.TryGetValue(model, out var url) ? url : pasted;
    }

    /// <summary>The host the Consoles' own catalogs serve device images from, and which networks already allow.</summary>
    private const string CatalogImageHost = "fw-download.ubnt.com";

    /// <summary>
    /// Every device model an image is for. One image often serves a family (dl.ui.com's U7PRO folder
    /// covers fourteen model codes), and its folder or file name need not be a model code at all, so
    /// the family is whatever the Consoles' catalog lists with the same md5. When no Console has been
    /// offered the build yet, a folder that is a model code stands in with that model's newest known
    /// family. Null when neither can place the image.
    /// </summary>
    private async Task<ImageFamily?> ResolveFamilyAsync(ParsedFirmwareUrl parsed, string target, CancellationToken cancellationToken)
    {
        var rows = await _catalog.ListDeviceBuildsAsync(cancellationToken);
        var md5 = rows.FirstOrDefault(r => r.Url == parsed.Url && !string.IsNullOrWhiteSpace(r.Md5Sum))?.Md5Sum
            ?? await FetchMd5Async(parsed.Url, cancellationToken);

        if (md5 != null)
        {
            var same = rows.Where(r => string.Equals(r.Md5Sum, md5, StringComparison.OrdinalIgnoreCase)).ToList();
            if (same.Count > 0)
            {
                // The catalog's version carries the build number a file name may leave off. Each model
                // installs from its own catalog URL for these bytes: that host is the one networks allow.
                var version = same.Select(r => r.Version).FirstOrDefault(v => v.Count(c => c == '.') >= 3) ?? same[0].Version;
                var urls = same
                    .GroupBy(r => r.Model, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        g => g.Key,
                        g => (g.FirstOrDefault(r => Uri.TryCreate(r.Url, UriKind.Absolute, out var u)
                                  && u.Host.Equals(CatalogImageHost, StringComparison.OrdinalIgnoreCase)) ?? g.First()).Url,
                        StringComparer.OrdinalIgnoreCase);
                return new ImageFamily(urls.Keys.ToList(), version, md5, true, urls);
            }
        }

        var known = rows.Where(r => string.Equals(r.Model, target, StringComparison.OrdinalIgnoreCase)).ToList();
        if (known.Count == 0)
        {
            // A fw-download file name carries the model code; a dl.ui.com folder may not.
            return parsed.Directory.StartsWith("/unifi/firmware", StringComparison.OrdinalIgnoreCase)
                ? null
                : new ImageFamily([target], parsed.Version, md5, false);
        }

        var anchor = known.Where(r => !string.IsNullOrWhiteSpace(r.Md5Sum))
            .Aggregate((SharedFirmwareBuild?)null, (best, r) =>
                best == null || NetworkOptimizer.Core.Helpers.FirmwareVersionFormat.IsNewer(r.Version, best.Version) ? r : best);
        var models = anchor == null
            ? [target]
            : rows.Where(r => r.Version == anchor.Version && string.Equals(r.Md5Sum, anchor.Md5Sum, StringComparison.OrdinalIgnoreCase))
                .Select(r => r.Model).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return new ImageFamily(models, parsed.Version, md5, false);
    }

    /// <summary>
    /// The image's md5: the published <c>.md5sum</c> beside it where there is one (dl.ui.com), else
    /// the hash of the image itself, capped so a mistaken link cannot pull an unbounded download.
    /// Null when neither can be read.
    /// </summary>
    private async Task<string?> FetchMd5Async(string url, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        var client = _http.CreateClient(HttpClientName);

        try
        {
            using var sum = await client.GetAsync(url + ".md5sum", timeout.Token);
            if (sum.IsSuccessStatusCode)
            {
                var text = await sum.Content.ReadAsStringAsync(timeout.Token);
                var match = System.Text.RegularExpressions.Regex.Match(text, @"\b[0-9a-fA-F]{32}\b");
                if (match.Success) return match.Value.ToLowerInvariant();
            }

            using var image = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!image.IsSuccessStatusCode || image.Content.Headers.ContentLength is > MaxImageBytes)
                return null;
            await using var stream = await image.Content.ReadAsStreamAsync(timeout.Token);
            var hash = await System.Security.Cryptography.MD5.HashDataAsync(stream, timeout.Token);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException
                                   && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Could not read the md5 of {Url}", url);
            return null;
        }
    }

    /// <summary>Named HTTP client for reading firmware images and their checksums.</summary>
    public const string HttpClientName = "FirmwareImages";

    /// <summary>Largest image the fallback hash will download. Device images are well under this.</summary>
    private const long MaxImageBytes = 512L * 1024 * 1024;

    private FirmwareUrlAddResult SaveFailed()
    {
        _audit.SuppressNoChange();
        return new FirmwareUrlAddResult("The build could not be saved. See the logs for details.");
    }
}
