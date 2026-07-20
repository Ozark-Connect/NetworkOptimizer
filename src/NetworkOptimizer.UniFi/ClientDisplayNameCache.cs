using System.Runtime.CompilerServices;

namespace NetworkOptimizer.UniFi;

/// <summary>
/// Per-connection (per-site) cache of the UniFi v2 active-clients <c>display_name</c> - the
/// system-selected friendly device name the console shows (e.g. "[IoT] Tiny Home - Plug", or a
/// fingerprint-derived name like "Apple TV" for a device the user never named). Exposed as a
/// lower-cased-MAC -> name lookup and refreshed at most once every 5 minutes.
///
/// This deliberately lives OUTSIDE <see cref="UniFiApiClient"/>: the real-time
/// <see cref="UniFiApiClient.GetActiveClientsAsync"/> stays uncached, and only this label-only
/// projection is cached - so callers that need live client state are never served stale data.
/// Label-only consumers (the 2D/3D LAN flow maps and Client Performance) use this instead of
/// hitting the v2 endpoint on every request. Entries are keyed weakly by <see cref="UniFiApiClient"/>
/// instance, so a site's cache is evicted when its connection is torn down.
/// </summary>
public static class ClientDisplayNameCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    private sealed class Entry
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public IReadOnlyDictionary<string, string> Map =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public DateTime FetchedUtc = DateTime.MinValue;
    }

    private static readonly ConditionalWeakTable<UniFiApiClient, Entry> Cache = new();

    /// <summary>
    /// Returns a lower-cased-MAC -> <c>display_name</c> lookup for the given connection, refreshing
    /// from the v2 active-clients endpoint at most once per 5 minutes. Clients without a display name
    /// are omitted, so callers keep their own downstream fallback (name > hostname > MAC) for those.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, string>> GetAsync(
        UniFiApiClient client, CancellationToken cancellationToken = default)
    {
        var entry = Cache.GetOrCreateValue(client);
        if (DateTime.UtcNow - entry.FetchedUtc < Ttl)
            return entry.Map;

        await entry.Gate.WaitAsync(cancellationToken);
        try
        {
            // Re-check under the lock: another caller may have refreshed while we waited.
            if (DateTime.UtcNow - entry.FetchedUtc < Ttl)
                return entry.Map;

            var active = await client.GetActiveClientsAsync(cancellationToken);
            entry.Map = active
                .Where(c => !string.IsNullOrEmpty(c.DisplayName) && !string.IsNullOrEmpty(c.Mac))
                .GroupBy(c => c.Mac.ToLowerInvariant())
                .ToDictionary(g => g.Key, g => g.First().DisplayName!, StringComparer.OrdinalIgnoreCase);
            entry.FetchedUtc = DateTime.UtcNow;
            return entry.Map;
        }
        finally
        {
            entry.Gate.Release();
        }
    }
}
