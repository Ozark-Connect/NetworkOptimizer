using System.Collections.Concurrent;
using System.Text.Json;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.Web.Services.ClientIcons;

/// <summary>A cached icon file, ready to serve.</summary>
public sealed record ClientIconImage(string Path, string ContentType);

/// <summary>
/// Each site's client icons: which icon each client has, read from the console in the background, and
/// the images themselves, fetched once and kept on disk. Ubiquiti's catalog icons are shared by every
/// site (<c>client-icons/shared/</c> in the data folder); custom icons and the client map belong to their
/// site (<c>client-icons/</c> in the site's data folder). Nothing polls a site nobody is looking at: a
/// read that finds the map due starts the refresh. Callers on a user's behalf go through
/// <see cref="IClientIcons"/>.
/// </summary>
public sealed class ClientIconRegistry : ISiteScopedRegistry
{
    /// <summary>Online clients (<c>stat/sta</c>, which carries the fingerprinted id).</summary>
    private static readonly TimeSpan OnlineEvery = TimeSpan.FromMinutes(5);
    /// <summary>Every known client (<c>rest/user</c>), for offline clients' custom and chosen icons.</summary>
    private static readonly TimeSpan KnownEvery = TimeSpan.FromMinutes(30);
    /// <summary>A failed image fetch is tried again after this long.</summary>
    private static readonly TimeSpan FailedRetry = TimeSpan.FromHours(6);
    private static readonly TimeSpan FirstReadWait = TimeSpan.FromSeconds(3);
    private const int MaxImageBytes = 2 * 1024 * 1024;
    private const int ParallelFetches = 4;
    private const int MaxFailedEntries = 10_000;
    internal const string HttpClientName = "ClientIcons";

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly SiteConnectionRegistry _connections;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<ClientIconRegistry> _logger;
    private readonly string _dataDir;
    private readonly string _sitesRoot;
    private readonly ConcurrentDictionary<string, SiteState> _sites = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>One fetch per file at a time, across sites.</summary>
    private readonly ConcurrentDictionary<string, Lazy<Task<bool>>> _fetches = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTime> _failedAt = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _fetchSlots = new(ParallelFetches, ParallelFetches);
    private readonly string _webRoot;
    private readonly SemaphoreSlim _catalogLock = new(1, 1);
    private UbiquitiDeviceCatalog? _catalog;
    private DateTime _catalogDueAt;

    /// <summary>Ubiquiti's device catalog changes when products launch, so a week-old copy is fine.</summary>
    private static readonly TimeSpan CatalogEvery = TimeSpan.FromDays(7);
    private const int MaxCatalogBytes = 16 * 1024 * 1024;
    private readonly FingerprintDatabaseService _fingerprints;

    public ClientIconRegistry(
        SiteConnectionRegistry connections,
        IHttpClientFactory httpFactory,
        SiteDatabasePaths paths,
        IWebHostEnvironment environment,
        FingerprintDatabaseService fingerprints,
        ILogger<ClientIconRegistry> logger)
    {
        _fingerprints = fingerprints;
        _connections = connections;
        _httpFactory = httpFactory;
        _logger = logger;
        _dataDir = Path.GetDirectoryName(Path.GetFullPath(paths.MainDbPath))!;
        _sitesRoot = paths.SitesRoot;
        _webRoot = environment.WebRootPath;
    }

    private sealed class SiteState
    {
        public readonly SemaphoreSlim Refresh = new(1, 1);
        public volatile ClientIconMap Map = ClientIconMap.Empty;
        /// <summary>Icon per MAC, every client ever resolved; replaced whole, never mutated.</summary>
        public Dictionary<string, ClientIconRef> Clients = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Image URL per UniFi device IP; replaced whole, never mutated.</summary>
        public Dictionary<string, string> Devices = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Catalog icon URL per UniFi device MAC, whether or not a bundled image exists.</summary>
        public Dictionary<string, string> DeviceCatalog = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>UniFi's fingerprint database, which tells a vendor's device from a generic one.</summary>
        public UniFiFingerprintDatabase? Fingerprints;
        public bool Loaded;
        public DateTime OnlineAt, KnownAt;
        public int Refreshing;
        public Task? FirstRefresh;
        public volatile Task? CurrentRefresh;
    }

    /// <summary>On-disk form of a site's client map.</summary>
    /// <summary>
    /// On-disk form of a site's maps, so a restart serves icons before the first console read.
    /// Device maps are absent from a file an older build wrote.
    /// </summary>
    private sealed record StoredMap(Dictionary<string, StoredIcon> Clients,
        Dictionary<string, string>? Devices = null, Dictionary<string, string>? DeviceCatalog = null);

    private sealed record StoredIcon(string Key, string Url, bool SiteOwned);

    private SiteState State(string slug) => _sites.GetOrAdd(slug, _ => new SiteState());

    /// <summary>The site's icons, waiting briefly for the first refresh so a page's first load has them.</summary>
    public async Task<ClientIconMap> GetReadyAsync(string slug, CancellationToken ct = default)
    {
        var state = State(slug);
        EnsureLoaded(slug, state);
        StartRefreshIfDue(slug, state);
        if (state.FirstRefresh is { IsCompleted: false } first)
            await Task.WhenAny(first, Task.Delay(FirstReadWait, ct));
        return state.Map;
    }

    /// <summary>
    /// The site's icons once the refresh in progress lands, for a page that rendered with
    /// <see cref="GetReadyAsync"/>'s answer and should update when a slow first read finishes.
    /// The map as it is when no refresh is running.
    /// </summary>
    public async Task<ClientIconMap> GetRefreshedAsync(string slug, CancellationToken ct = default)
    {
        var state = State(slug);
        EnsureLoaded(slug, state);
        StartRefreshIfDue(slug, state);
        if (state.CurrentRefresh is { IsCompleted: false } running)
            await running.WaitAsync(ct);
        return state.Map;
    }

    /// <summary>
    /// A cached icon's file, fetching it first when it is not on disk yet. A custom icon is served only
    /// for the site that has it.
    /// </summary>
    public async Task<ClientIconImage?> GetImageAsync(string slug, string key, CancellationToken ct = default)
    {
        if (!ClientIconSource.IsValidKey(key))
            return null;

        string? url;
        string path;
        if (ClientIconSource.CatalogUrl(key) is { } catalogUrl)
        {
            url = catalogUrl;
            path = SharedPath(key);
        }
        else
        {
            var state = State(slug);
            EnsureLoaded(slug, state);
            url = state.Clients.Values.FirstOrDefault(i => i.SiteOwned && i.Key == key)?.SourceUrl;
            if (url == null)
                return null;
            path = SitePath(slug, key);
        }

        if (!File.Exists(path) && !await FetchAsync(url, path, ct))
            return null;
        return new ClientIconImage(path, "image/png");
    }

    /// <inheritdoc />
    /// <remarks>The files stay: the site's data folder goes with the site, and the shared icons serve every site.</remarks>
    public Func<ValueTask>? EvictSite(string slug)
    {
        _sites.TryRemove(slug, out _);
        return null;
    }

    private void StartRefreshIfDue(string slug, SiteState state)
    {
        var now = DateTime.UtcNow;
        if (now - state.OnlineAt < OnlineEvery || Interlocked.Exchange(ref state.Refreshing, 1) != 0)
            return;
        var task = Task.Run(async () =>
        {
            try { await RefreshAsync(slug, state, CancellationToken.None); }
            catch (Exception ex) { _logger.LogWarning(ex, "Client icons: refresh failed for site {Site}", slug); }
            finally { Interlocked.Exchange(ref state.Refreshing, 0); }
        });
        state.FirstRefresh ??= task;
        state.CurrentRefresh = task;
    }

    private async Task RefreshAsync(string slug, SiteState state, CancellationToken ct)
    {
        await state.Refresh.WaitAsync(ct);
        try
        {
            var api = _connections.GetFor(slug).Client;
            if (api == null)
                return;

            var now = DateTime.UtcNow;
            var clients = new Dictionary<string, ClientIconRef>(state.Clients, StringComparer.OrdinalIgnoreCase);
            var readKnown = now - state.KnownAt >= KnownEvery;

            // Known clients first so the online read, which also has the fingerprinted id, wins.
            if (readKnown)
            {
                Apply(clients, await api.GetAllKnownClientsAsync(ct), offline: true);
                state.KnownAt = now;
            }
            Apply(clients, await api.GetClientsAsync(ct), offline: false);
            state.OnlineAt = now;

            var changed = !SameIcons(clients, state.Clients);
            state.Clients = clients;

            await FetchMissingAsync(slug, clients.Values, ct);
            var (devices, deviceCatalog) = await ResolveDevicesAsync(slug, ct);
            // An empty device list is a connection not ready yet, not a site with no devices: keep the last one.
            if (devices.Count == 0 && state.Devices.Count > 0)
                (devices, deviceCatalog) = (state.Devices, state.DeviceCatalog);
            changed |= !SameUrls(devices, state.Devices) || !SameUrls(deviceCatalog, state.DeviceCatalog);
            (state.Devices, state.DeviceCatalog) = (devices, deviceCatalog);
            if (changed)
                Save(slug, state);
            state.Fingerprints = await _fingerprints.GetDatabaseAsync(_connections.GetFor(slug), ct) ?? state.Fingerprints;
            state.Map = BuildMap(slug, state);
            if (readKnown)
                PruneCustomIcons(slug, clients.Values);
        }
        finally
        {
            state.Refresh.Release();
        }
    }

    /// <summary>
    /// Each UniFi device's image by IP: the product image bundled with the app, else the icon UniFi
    /// Network shows for its model, from Ubiquiti's device catalog.
    /// </summary>
    private async Task<(Dictionary<string, string> ByIp, Dictionary<string, string> CatalogByMac)> ResolveDevicesAsync(
        string slug, CancellationToken ct)
    {
        var byIp = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var catalogByMac = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var catalog = await GetCatalogAsync(ct);
        foreach (var device in await _connections.GetFor(slug).GetDiscoveredDevicesAsync(ct))
        {
            string? catalogUrl = null;
            if (ClientIconSource.UniFiDeviceKey(catalog.IconIdFor(device.Model, device.Shortname)
                    ?? catalog.IconIdForModel(device.FriendlyModelName)) is { } key)
            {
                var path = SharedPath(key);
                if (File.Exists(path) || await FetchAsync(ClientIconSource.CatalogUrl(key)!, path, ct))
                    catalogUrl = ClientIconMap.Url(slug, key);
            }
            if (catalogUrl != null && !string.IsNullOrWhiteSpace(device.Mac))
                catalogByMac[ClientIconMap.NormalizeMac(device.Mac)] = catalogUrl;
            if (!string.IsNullOrWhiteSpace(device.IpAddress) && (BundledImage(device.FriendlyModelName) ?? catalogUrl) is { } url)
                byIp[device.IpAddress] = url;
        }
        return (byIp, catalogByMac);
    }

    /// <summary>
    /// A UniFi device's image by its friendly model name: the bundled product image, else the icon
    /// UniFi Network shows for the model (served, and fetched on first request). Null when neither is
    /// known; before the catalog's first read it starts that read and answers with the bundled image only.
    /// </summary>
    /// <param name="model">The friendly model name, as <c>DeviceIcon</c> receives it.</param>
    /// <param name="slug">The site in context, which the served URL carries like every icon URL.</param>
    public string? DeviceImageUrl(string? model, string slug)
    {
        if (BundledImage(model) is { } bundled)
            return bundled;
        if (_catalog is not { } catalog)
        {
            if (Interlocked.Exchange(ref _catalogLoading, 1) == 0)
                _ = Task.Run(async () =>
                {
                    try { await GetCatalogAsync(CancellationToken.None); }
                    finally { Interlocked.Exchange(ref _catalogLoading, 0); }
                });
            return null;
        }
        return ClientIconSource.UniFiDeviceKey(catalog.IconIdForModel(model)) is { } key ? ClientIconMap.Url(slug, key) : null;
    }

    private int _catalogLoading;

    /// <summary>Whether the app ships an image for a model, cached: the files change only with a new build.</summary>
    private readonly ConcurrentDictionary<string, string?> _bundled = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The app's own product image for a model, when it ships one (the path <c>DeviceIcon</c> uses).</summary>
    private string? BundledImage(string? model)
    {
        if (Components.Shared.DeviceIcon.GetIconPath(model) is not { } url)
            return null;
        return _bundled.GetOrAdd(url, u => File.Exists(Path.Combine(_webRoot, "images", "devices", Path.GetFileName(u))) ? u : null);
    }

    /// <summary>Ubiquiti's device catalog: kept on disk, read again weekly. A stale copy beats none.</summary>
    private async Task<UbiquitiDeviceCatalog> GetCatalogAsync(CancellationToken ct)
    {
        if (_catalog != null && DateTime.UtcNow < _catalogDueAt)
            return _catalog;
        await _catalogLock.WaitAsync(ct);
        try
        {
            if (_catalog != null && DateTime.UtcNow < _catalogDueAt)
                return _catalog;

            var file = Path.Combine(SharedDir, "public.json");
            var fresh = File.Exists(file) && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < CatalogEvery;
            var json = fresh ? null : await DownloadCatalogAsync(file);
            if (json == null && File.Exists(file))
                json = await File.ReadAllTextAsync(file, ct);

            try
            {
                _catalog = json != null ? UbiquitiDeviceCatalog.Parse(json) : UbiquitiDeviceCatalog.Empty;
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Client icons: Ubiquiti's device catalog could not be read");
                _catalog = UbiquitiDeviceCatalog.Empty;
            }
            _catalogDueAt = DateTime.UtcNow + (_catalog.Count > 0 ? CatalogEvery : FailedRetry);
            return _catalog;
        }
        finally
        {
            _catalogLock.Release();
        }
    }

    /// <summary>Downloads the catalog to its file and returns it, or null when it could not be fetched.</summary>
    private async Task<string?> DownloadCatalogAsync(string file)
    {
        try
        {
            using var http = _httpFactory.CreateClient(HttpClientName);
            using var response = await http.GetAsync(UbiquitiDeviceCatalog.SourceUrl, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxCatalogBytes)
            {
                _logger.LogDebug("Client icons: device catalog answered {Status}", (int)response.StatusCode);
                return null;
            }
            var json = await response.Content.ReadAsStringAsync();
            UbiquitiDeviceCatalog.Parse(json); // Never replace a good copy with one that does not parse
            Directory.CreateDirectory(SharedDir);
            await File.WriteAllTextAsync(file + ".tmp", json);
            File.Move(file + ".tmp", file, overwrite: true);
            return json;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or JsonException)
        {
            _logger.LogDebug(ex, "Client icons: could not fetch the device catalog");
            return null;
        }
    }

    /// <summary>
    /// Resolves each client's icon into the map. A known-client row has no fingerprinted id, so it
    /// never replaces an icon an online read resolved with no custom or chosen icon to go on.
    /// </summary>
    private static void Apply(Dictionary<string, ClientIconRef> clients, IEnumerable<UniFiClientResponse> rows, bool offline)
    {
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.Mac))
                continue;
            var mac = ClientIconMap.NormalizeMac(row.Mac);
            var icon = ClientIconSource.Resolve(row);
            if (icon != null)
                clients[mac] = icon;
            else if (!offline)
                clients.Remove(mac);
        }
    }

    private static bool SameIcons(Dictionary<string, ClientIconRef> a, Dictionary<string, ClientIconRef> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var other) && other == kv.Value);

    private static bool SameUrls(Dictionary<string, string> a, Dictionary<string, string> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var other) && other == kv.Value);

    /// <summary>A device image URL this registry produces: a bundled image or a served icon.</summary>
    private static bool IsDeviceImageUrl(string url) =>
        url.StartsWith("/images/devices/", StringComparison.Ordinal) || url.StartsWith("/api/client-icons/", StringComparison.Ordinal);

    private static Dictionary<string, string> LoadUrls(Dictionary<string, string>? stored) =>
        (stored ?? []).Where(kv => IsDeviceImageUrl(kv.Value)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

    private ClientIconMap BuildMap(string slug, SiteState state)
    {
        var cached = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var ranks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (mac, icon) in state.Clients)
        {
            if (!File.Exists(PathFor(slug, icon)))
                continue;
            cached[mac] = icon.Key;
            ranks[mac] = Rank(icon, state.Fingerprints);
        }
        return new ClientIconMap(slug, cached, state.Devices, state.DeviceCatalog, ranks);
    }

    /// <summary>
    /// How specific an icon is (<see cref="ClientIconMap.RankFor"/>). A fingerprint is a vendor's device
    /// when UniFi's database names a vendor for it; generic ones (Linux, Docker, a server) have none.
    /// Before the database loads, every fingerprint ranks as generic.
    /// </summary>
    [NetworkOptimizer.Core.VendorSpecific("UniFi", "fingerprint dev_ids entries: vendor_id set for a vendor's device")]
    internal static int Rank(ClientIconRef icon, UniFiFingerprintDatabase? fingerprints)
    {
        if (icon.SiteOwned)
            return 3;
        if (icon.Key.StartsWith("ud-", StringComparison.Ordinal))
            return 2;
        return fingerprints != null
            && fingerprints.DevIds.TryGetValue(icon.Key[3..], out var entry)
            && !string.IsNullOrEmpty(entry.VendorId) && entry.VendorId != "0"
            ? 2 : 1;
    }

    private async Task FetchMissingAsync(string slug, IEnumerable<ClientIconRef> icons, CancellationToken ct)
    {
        var missing = icons.DistinctBy(i => i.Key).Where(i => !File.Exists(PathFor(slug, i))).ToList();
        await Task.WhenAll(missing.Select(i => FetchAsync(i.SourceUrl, PathFor(slug, i), ct)));
    }

    /// <summary>Downloads an icon to its file. False when it could not be fetched or is not a PNG.</summary>
    private async Task<bool> FetchAsync(string url, string path, CancellationToken ct)
    {
        if (_failedAt.TryGetValue(path, out var failedAt) && DateTime.UtcNow - failedAt < FailedRetry)
            return false;

        var fetch = _fetches.GetOrAdd(path, _ => new Lazy<Task<bool>>(() => DownloadAsync(url, path)));
        try
        {
            return await fetch.Value.WaitAsync(ct);
        }
        finally
        {
            if (fetch.Value.IsCompleted)
                _fetches.TryRemove(new KeyValuePair<string, Lazy<Task<bool>>>(path, fetch));
        }
    }

    private async Task<bool> DownloadAsync(string url, string path)
    {
        if (!ClientIconSource.IsAllowedSource(url))
            return Fail(path, "host not allowed", url);

        await _fetchSlots.WaitAsync();
        try
        {
            using var http = _httpFactory.CreateClient(HttpClientName);
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            // An unknown fingerprint id answers with a redirect to a web page, not a 404.
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType != "image/png")
                return Fail(path, $"{(int)response.StatusCode} {response.Content.Headers.ContentType?.MediaType}", url);
            if (response.Content.Headers.ContentLength > MaxImageBytes)
                return Fail(path, "too large", url);

            var bytes = await response.Content.ReadAsByteArrayAsync();
            if (bytes.Length > MaxImageBytes || !bytes.AsSpan().StartsWith(PngSignature))
                return Fail(path, "not a PNG", url);

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            await File.WriteAllBytesAsync(temp, bytes);
            File.Move(temp, path, overwrite: true);
            _failedAt.TryRemove(path, out _);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return Fail(path, ex.Message, url);
        }
        finally
        {
            _fetchSlots.Release();
        }
    }

    private bool Fail(string path, string reason, string url)
    {
        // Any Viewer can ask for any well-formed key, so the table must not grow without limit.
        if (_failedAt.Count >= MaxFailedEntries)
            _failedAt.Clear();
        _failedAt[path] = DateTime.UtcNow;
        _logger.LogDebug("Client icons: could not fetch {Url}: {Reason}", url, reason);
        return false;
    }

    /// <summary>Deletes custom icons no client has any more (an icon removed or regenerated in UniFi Network).</summary>
    private void PruneCustomIcons(string slug, IEnumerable<ClientIconRef> icons)
    {
        var dir = CustomDir(slug);
        if (!Directory.Exists(dir))
            return;
        var keep = icons.Where(i => i.SiteOwned).Select(i => i.Key + ".png").ToHashSet(StringComparer.Ordinal);
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "ai-*.png"))
                if (!keep.Contains(Path.GetFileName(file)))
                    File.Delete(file);
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Client icons: could not prune custom icons for site {Site}", slug);
        }
    }

    private void EnsureLoaded(string slug, SiteState state)
    {
        if (state.Loaded)
            return;
        lock (state)
        {
            if (state.Loaded)
                return;
            try
            {
                var file = MapFile(slug);
                if (File.Exists(file) && JsonSerializer.Deserialize<StoredMap>(File.ReadAllText(file), JsonOptions) is { } stored)
                {
                    state.Clients = stored.Clients
                        .Where(kv => ClientIconSource.IsValidKey(kv.Value.Key) && ClientIconSource.IsAllowedSource(kv.Value.Url))
                        .ToDictionary(kv => kv.Key, kv => new ClientIconRef(kv.Value.Key, kv.Value.Url, kv.Value.SiteOwned),
                            StringComparer.OrdinalIgnoreCase);
                    state.Devices = LoadUrls(stored.Devices);
                    state.DeviceCatalog = LoadUrls(stored.DeviceCatalog);
                    state.Map = BuildMap(slug, state);
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Client icons: could not read the client map for site {Site}", slug);
            }
            state.Loaded = true;
        }
    }

    private void Save(string slug, SiteState state)
    {
        try
        {
            var file = MapFile(slug);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var stored = new StoredMap(
                state.Clients.ToDictionary(kv => kv.Key, kv => new StoredIcon(kv.Value.Key, kv.Value.SourceUrl, kv.Value.SiteOwned)),
                state.Devices, state.DeviceCatalog);
            var temp = file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(stored, JsonOptions));
            File.Move(temp, file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Client icons: could not save the client map for site {Site}", slug);
        }
    }

    private string SiteDir(string slug) =>
        string.Equals(slug, SiteDatabasePaths.DefaultSiteSlug, StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(_dataDir, "client-icons")
            : Path.Combine(_sitesRoot, slug, "client-icons");

    private string MapFile(string slug) => Path.Combine(SiteDir(slug), "clients.json");
    private string CustomDir(string slug) => Path.Combine(SiteDir(slug), "custom");
    private string SharedDir => Path.Combine(_dataDir, "client-icons", "shared");
    private string SharedPath(string key) => Path.Combine(SharedDir, key + ".png");
    private string SitePath(string slug, string key) => Path.Combine(CustomDir(slug), key + ".png");
    private string PathFor(string slug, ClientIconRef icon) => icon.SiteOwned ? SitePath(slug, icon.Key) : SharedPath(icon.Key);
}
