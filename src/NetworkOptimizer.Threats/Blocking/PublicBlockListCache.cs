using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Threats.Interfaces;

namespace NetworkOptimizer.Threats.Blocking;

/// <summary>One list's last fetch, for the Settings card.</summary>
public sealed record PublicBlockListStatus(string Id, int Count, DateTime? FetchedAt, string? Error);

/// <summary>
/// Downloads the public block lists and Cloudflare's ranges once per instance, each no more often
/// than its source allows, and only once something asks for it. The last good copy is kept in the
/// main database, so a restart or a source outage never empties a list: a failed or implausibly
/// small download keeps the copy before it.
/// </summary>
public sealed class PublicBlockListCache(IHttpClientFactory httpClientFactory, ILogger<PublicBlockListCache> logger)
{
    public const string CloudflareId = "cloudflare-ranges";
    private const string CloudflareUrl = "https://api.cloudflare.com/client/v4/ips";
    private static readonly TimeSpan CloudflareRefresh = TimeSpan.FromDays(1);
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(30);
    private const string CacheKeyPrefix = "blocking.feeds.cache.";

    private sealed record Entry(IReadOnlyList<string> Items, DateTime? FetchedAt, DateTime LastAttempt, string? Error);
    private sealed record Persisted(List<string> Items, DateTime FetchedAt);

    private readonly ConcurrentDictionary<string, Entry> _entries = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>The list's entries: fetched when due, else the last good copy, else empty.</summary>
    public async Task<IReadOnlyList<string>> GetAsync(string id, IThreatSettingsAccessor settings, DateTime now, CancellationToken ct)
    {
        if (PublicBlockLists.Find(id) is not { } list) return [];
        return await GetOrRefreshAsync(id, list.Refresh, settings, now, () => FetchListAsync(list, ct), ct);
    }

    /// <summary>Cloudflare's published ranges, refreshed daily; the built-in copy until the first fetch.</summary>
    public async Task<IReadOnlyList<string>> GetCloudflareRangesAsync(IThreatSettingsAccessor settings, DateTime now, CancellationToken ct)
    {
        var items = await GetOrRefreshAsync(CloudflareId, CloudflareRefresh, settings, now, () => FetchCloudflareAsync(ct), ct);
        return items.Count > 0 ? items : BlockAllowlist.CloudflareRanges;
    }

    /// <summary>What each list last fetched, for lists something has asked for since startup.</summary>
    public PublicBlockListStatus Status(string id) =>
        _entries.TryGetValue(id, out var e) ? new PublicBlockListStatus(id, e.Items.Count, e.FetchedAt, e.Error) : new PublicBlockListStatus(id, 0, null, null);

    private async Task<IReadOnlyList<string>> GetOrRefreshAsync(string id, TimeSpan refresh, IThreatSettingsAccessor settings,
        DateTime now, Func<Task<List<string>>> fetch, CancellationToken ct)
    {
        if (_entries.TryGetValue(id, out var cached) && !Due(cached, refresh, now)) return cached.Items;

        await _lock.WaitAsync(ct);
        try
        {
            if (!_entries.TryGetValue(id, out cached))
            {
                cached = await LoadAsync(id, settings, ct);
                _entries[id] = cached;
            }
            if (!Due(cached, refresh, now)) return cached.Items;

            try
            {
                var items = await fetch();
                // A list that shrinks to under a tenth of its last size is a broken download, not news.
                if (items.Count == 0 || (cached.Items.Count >= 50 && items.Count < cached.Items.Count / 10))
                    throw new InvalidDataException($"got {items.Count} entries, had {cached.Items.Count}");
                cached = new Entry(items, now, now, null);
                await settings.SaveSettingAsync(CacheKeyPrefix + id, JsonSerializer.Serialize(new Persisted(items, now)), ct);
                logger.LogInformation("Public block list {List}: {Count} entries", id, items.Count);
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or JsonException or TaskCanceledException
                                       && !ct.IsCancellationRequested)
            {
                cached = cached with { LastAttempt = now, Error = ex.Message };
                logger.LogWarning("Public block list {List} not refreshed, keeping {Count} entries: {Error}", id, cached.Items.Count, ex.Message);
            }
            _entries[id] = cached;
            return cached.Items;
        }
        finally
        {
            _lock.Release();
        }
    }

    private static bool Due(Entry e, TimeSpan refresh, DateTime now) =>
        (e.FetchedAt == null || now - e.FetchedAt >= refresh) && now - e.LastAttempt >= (e.Error == null ? refresh : RetryAfterFailure);

    private static async Task<Entry> LoadAsync(string id, IThreatSettingsAccessor settings, CancellationToken ct)
    {
        var json = await settings.GetSettingAsync(CacheKeyPrefix + id, ct);
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                if (JsonSerializer.Deserialize<Persisted>(json) is { } p)
                    return new Entry(p.Items, p.FetchedAt, p.FetchedAt, null);
            }
            catch (JsonException)
            {
            }
        }
        return new Entry([], null, DateTime.MinValue, null);
    }

    private async Task<List<string>> FetchListAsync(PublicBlockList list, CancellationToken ct)
    {
        var http = httpClientFactory.CreateClient("PublicBlockLists");
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var url in list.Urls)
        {
            using var response = await http.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
            foreach (var entry in list.Parse(await response.Content.ReadAsStringAsync(ct)))
                result.Add(entry);
        }
        return result.ToList();
    }

    private async Task<List<string>> FetchCloudflareAsync(CancellationToken ct)
    {
        var http = httpClientFactory.CreateClient("PublicBlockLists");
        using var response = await http.GetAsync(CloudflareUrl, ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        if (!root.TryGetProperty("success", out var ok) || !ok.GetBoolean() || !root.TryGetProperty("result", out var r))
            throw new InvalidDataException("Cloudflare returned no ranges");
        var ranges = new List<string>();
        foreach (var name in new[] { "ipv4_cidrs", "ipv6_cidrs" })
        {
            if (!r.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array) continue;
            ranges.AddRange(arr.EnumerateArray().Select(e => PublicBlockLists.Normalize(e.GetString())).OfType<string>());
        }
        // Cloudflare's own ranges only grow; a short answer is a bad one.
        if (ranges.Count < BlockAllowlist.CloudflareRanges.Count / 2)
            throw new InvalidDataException($"Cloudflare returned {ranges.Count} ranges");
        return ranges;
    }
}
