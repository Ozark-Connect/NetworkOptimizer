using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetworkOptimizer.Web.Services.Firmware;

/// <summary>
/// One build in Ubiquiti's public release feed.
/// </summary>
/// <param name="Id">Feed entry id; also the path segment of the data and changelog links.</param>
/// <param name="Platform">Model code the build applies to, e.g. "U6M", "linux-x64".</param>
/// <param name="Product">Product family, e.g. "unifi-firmware", "unifi-os-server".</param>
/// <param name="Channel">Feed channel, e.g. "release", "beta-public".</param>
/// <param name="Version">Feed version string, e.g. "v6.8.2+15592".</param>
/// <param name="Created">Publish date - the input to the ripeness gate.</param>
/// <param name="DownloadUrl">Direct image URL on fw-download.ubnt.com.</param>
/// <param name="Md5">MD5 of the image.</param>
/// <param name="Sha256">SHA256 of the image.</param>
/// <param name="ChangelogUrl">Changelog endpoint. Present on most entries, but it 404s when no changelog was uploaded.</param>
/// <param name="FileSizeBytes">Image size in bytes.</param>
public sealed record UbiquitiFirmwareRelease(
    string? Id,
    string? Platform,
    string? Product,
    string? Channel,
    string? Version,
    DateTime? Created,
    string? DownloadUrl,
    string? Md5,
    string? Sha256,
    string? ChangelogUrl,
    long? FileSizeBytes);

/// <summary>
/// Read-only client for Ubiquiti's public release feed at https://fw-update.ui.com.
/// <para>
/// It exists because the console's own catalog (cmd/firmware list-available) carries the LATEST
/// build per model on the current channel only. The feed adds publish dates (ripeness), changelog
/// links, and - the reason rollback needs it - the URL of any PRIOR version.
/// </para>
/// <para>
/// Anonymous access only serves GA. Filtering by channel "release" returns builds; "release-candidate"
/// and "beta" come back empty, and EA needs authentication we deliberately do not carry. Callers
/// resolving an RC or EA build must use the console catalog instead.
/// </para>
/// </summary>
public class UbiquitiReleaseFeedClient
{
    /// <summary>Named <see cref="IHttpClientFactory"/> client for this feed.</summary>
    public const string HttpClientName = "UbiquitiReleaseFeed";

    /// <summary>Public feed base URL.</summary>
    public const string FeedBaseUrl = "https://fw-update.ui.com";

    /// <summary>The only channel the anonymous feed populates.</summary>
    public const string GaChannel = "release";

    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<UbiquitiReleaseFeedClient> _logger;
    private readonly TimeProvider _timeProvider;

    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();

    private sealed record CacheEntry(DateTimeOffset FetchedAt, IReadOnlyList<UbiquitiFirmwareRelease> Releases);

    public UbiquitiReleaseFeedClient(
        IHttpClientFactory httpClientFactory,
        ILogger<UbiquitiReleaseFeedClient> logger,
        TimeProvider timeProvider)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// The newest build for a model on a channel, from /api/firmware-latest. Returns null when the
    /// feed knows no such combination (which includes every non-GA channel - see the class summary).
    /// </summary>
    /// <param name="platform">Model code as the console reports it, e.g. "U6M".</param>
    /// <param name="channel">Feed channel; GA by default.</param>
    /// <param name="product">Optional product family filter, e.g. "unifi-firmware".</param>
    public async Task<UbiquitiFirmwareRelease?> GetLatestAsync(
        string platform,
        string channel = GaChannel,
        string? product = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(platform);

        var filters = new List<string> { Filter("platform", platform) };
        if (!string.IsNullOrWhiteSpace(channel))
            filters.Add(Filter("channel", channel));
        if (!string.IsNullOrWhiteSpace(product))
            filters.Add(Filter("product", product));

        var releases = await FetchAsync(BuildUrl("firmware-latest", filters), cancellationToken);
        return releases.FirstOrDefault();
    }

    /// <summary>
    /// A specific build for a model, from /api/firmware. Accepts either the feed's own version
    /// string ("v6.8.2+15592") or the console's dotted form ("6.8.2.15592"); the raw string is tried
    /// as well when the two differ, so a version the feed spells some other way still resolves.
    /// </summary>
    public async Task<UbiquitiFirmwareRelease?> GetByVersionAsync(
        string platform,
        string version,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(platform);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        var feedVersion = ToFeedVersion(version);

        var releases = await FetchAsync(
            BuildUrl("firmware", [Filter("platform", platform), Filter("version", feedVersion)]),
            cancellationToken);

        if (releases.Count == 0 && !string.Equals(feedVersion, version.Trim(), StringComparison.Ordinal))
        {
            releases = await FetchAsync(
                BuildUrl("firmware", [Filter("platform", platform), Filter("version", version.Trim())]),
                cancellationToken);
        }

        return releases.FirstOrDefault();
    }

    /// <summary>
    /// Every build the feed carries for a model, newest first - the rollback candidate list.
    /// </summary>
    /// <param name="platform">Model code as the console reports it, e.g. "U6M".</param>
    /// <param name="channel">Feed channel, or null for all channels.</param>
    /// <param name="limit">Maximum entries to request; the feed defaults to 25 without one.</param>
    public async Task<IReadOnlyList<UbiquitiFirmwareRelease>> ListVersionsAsync(
        string platform,
        string? channel = GaChannel,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(platform);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        var filters = new List<string> { Filter("platform", platform) };
        if (!string.IsNullOrWhiteSpace(channel))
            filters.Add(Filter("channel", channel));

        var url = BuildUrl("firmware", filters, $"sort=-version&limit={limit}");
        return await FetchAsync(url, cancellationToken);
    }

    /// <summary>
    /// Maps the console's dotted build string ("6.8.2.15592") onto the feed's form
    /// ("v6.8.2+15592"). Anything already carrying a "+" is left alone bar the "v" prefix.
    /// </summary>
    internal static string ToFeedVersion(string version)
    {
        var trimmed = version.Trim();
        if (trimmed.Length == 0)
            return trimmed;

        var withoutPrefix = trimmed.StartsWith('v') ? trimmed[1..] : trimmed;

        if (withoutPrefix.Contains('+'))
            return "v" + withoutPrefix;

        var parts = withoutPrefix.Split('.');
        if (parts.Length < 4)
            return "v" + withoutPrefix;

        return $"v{parts[0]}.{parts[1]}.{parts[2]}+{string.Join('.', parts[3..])}";
    }

    private static string Filter(string field, string value) =>
        $"filter=eq~~{field}~~{Uri.EscapeDataString(value)}";

    private static string BuildUrl(string resource, IEnumerable<string> filters, string? extraQuery = null)
    {
        var query = string.Join('&', filters);
        if (!string.IsNullOrEmpty(extraQuery))
            query = query.Length == 0 ? extraQuery : $"{query}&{extraQuery}";

        return query.Length == 0
            ? $"{FeedBaseUrl}/api/{resource}"
            : $"{FeedBaseUrl}/api/{resource}?{query}";
    }

    private async Task<IReadOnlyList<UbiquitiFirmwareRelease>> FetchAsync(
        string url,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();

        if (_cache.TryGetValue(url, out var cached) && now - cached.FetchedAt < CacheTtl)
            return cached.Releases;

        try
        {
            var http = _httpClientFactory.CreateClient(HttpClientName);
            var response = await http.GetAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Release feed returned {StatusCode} for {Url}", response.StatusCode, url);
                return [];
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var payload = JsonSerializer.Deserialize<FeedResponse>(json);

            var releases = (payload?.Embedded?.Firmware ?? [])
                .Select(Map)
                .ToList();

            _cache[url] = new CacheEntry(now, releases);
            return releases;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not read the Ubiquiti release feed at {Url}", url);
            return [];
        }
    }

    private static UbiquitiFirmwareRelease Map(FeedFirmware entry) => new(
        entry.Id,
        entry.Platform,
        entry.Product,
        entry.Channel,
        entry.Version,
        entry.Created,
        entry.Links?.Data?.Href,
        entry.Md5,
        entry.Sha256Checksum,
        entry.Links?.Upload?
            .FirstOrDefault(u => string.Equals(u.Name, "changelog", StringComparison.OrdinalIgnoreCase))?.Href,
        entry.FileSize);

    private sealed class FeedResponse
    {
        [JsonPropertyName("_embedded")]
        public FeedEmbedded? Embedded { get; set; }
    }

    private sealed class FeedEmbedded
    {
        [JsonPropertyName("firmware")]
        public List<FeedFirmware> Firmware { get; set; } = new();
    }

    private sealed class FeedFirmware
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("platform")]
        public string? Platform { get; set; }

        [JsonPropertyName("product")]
        public string? Product { get; set; }

        [JsonPropertyName("channel")]
        public string? Channel { get; set; }

        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("created")]
        public DateTime? Created { get; set; }

        [JsonPropertyName("md5")]
        public string? Md5 { get; set; }

        [JsonPropertyName("sha256_checksum")]
        public string? Sha256Checksum { get; set; }

        [JsonPropertyName("file_size")]
        public long? FileSize { get; set; }

        [JsonPropertyName("_links")]
        public FeedLinks? Links { get; set; }
    }

    private sealed class FeedLinks
    {
        [JsonPropertyName("data")]
        public FeedLink? Data { get; set; }

        [JsonPropertyName("upload")]
        public List<FeedLink> Upload { get; set; } = new();
    }

    private sealed class FeedLink
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("href")]
        public string? Href { get; set; }
    }
}
