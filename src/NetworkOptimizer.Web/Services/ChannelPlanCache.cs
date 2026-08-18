using System.Collections.Concurrent;
using NetworkOptimizer.WiFi.Models;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Process-wide cache for computed channel plans and the client-rate history they read.
///
/// The analysis is expensive enough to be user-visible: it fans out across UniFi topology, scan
/// results, propagation, persisted outcome memory and an InfluxDB history query, then runs a
/// combinatorial search. Recomputing it on every page interaction is what made the page feel slow.
///
/// Lifetime: singleton, partitioned by site slug so one site's plan can never be served to another.
/// </summary>
public class ChannelPlanCache
{
    /// <summary>
    /// How long a computed plan stays fresh. The inputs (neighbor environment, persisted outcome
    /// memory, client history) move on the order of hours, not seconds - and Refresh always
    /// bypasses this, so the user is never stuck with a stale answer they can see is stale.
    /// </summary>
    public static readonly TimeSpan PlanTtl = TimeSpan.FromHours(1);

    /// <summary>
    /// Client-rate history TTL, deliberately longer than <see cref="PlanTtl"/> and NOT cleared by
    /// Refresh. It is a 90-day aggregate that measured ~33s to run and shifts negligibly within a
    /// few hours, so tying it to Refresh would make the button feel broken for no gain in accuracy.
    /// </summary>
    public static readonly TimeSpan ClientRatesTtl = TimeSpan.FromHours(6);

    private sealed class Entry<T>
    {
        public T? Value;
        public bool Populated;
        public DateTime AtUtc = DateTime.MinValue;
        public readonly SemaphoreSlim Lock = new(1, 1);
    }

    private readonly ConcurrentDictionary<string, Entry<Dictionary<RadioBand, ChannelPlan>>> _plans = new();
    private readonly ConcurrentDictionary<string, Entry<Dictionary<RadioBand, Dictionary<string, IReadOnlyList<ClientRateSample>>>>> _clientRates = new();

    /// <summary>
    /// Returns the cached plan for this site and option set, or builds one. Concurrent callers
    /// coalesce onto a single build rather than each starting their own multi-second run.
    /// </summary>
    /// <param name="key">Site slug plus a fingerprint of the options the plan was built for</param>
    /// <param name="forceRefresh">Bypass the cached value and rebuild (the Refresh button)</param>
    public async Task<Dictionary<RadioBand, ChannelPlan>> GetOrBuildPlanAsync(
        string key,
        bool forceRefresh,
        Func<Task<Dictionary<RadioBand, ChannelPlan>>> build)
    {
        Func<Task<Dictionary<RadioBand, ChannelPlan>?>> nullable = async () => await build();
        var plan = await GetOrBuildAsync(_plans, key, forceRefresh, PlanTtl, nullable);
        return plan ?? new Dictionary<RadioBand, ChannelPlan>();
    }

    /// <summary>
    /// Client-rate history for a site. Never force-refreshed - see <see cref="ClientRatesTtl"/>.
    /// A failed lookup caches its null result too, so an unreachable InfluxDB costs one timeout
    /// per TTL instead of one per plan build.
    /// </summary>
    public Task<Dictionary<RadioBand, Dictionary<string, IReadOnlyList<ClientRateSample>>>?> GetOrBuildClientRatesAsync(
        string siteSlug,
        Func<Task<Dictionary<RadioBand, Dictionary<string, IReadOnlyList<ClientRateSample>>>?>> build)
        => GetOrBuildAsync(_clientRates, siteSlug, forceRefresh: false, ClientRatesTtl, build);

    /// <summary>Drops every cached plan for a site (e.g. its console connection changed).</summary>
    public void InvalidateSite(string siteSlug)
    {
        foreach (var key in _plans.Keys.Where(k => k.StartsWith(siteSlug + "|", StringComparison.Ordinal)).ToList())
            _plans.TryRemove(key, out _);
        _clientRates.TryRemove(siteSlug, out _);
    }

    private static async Task<T?> GetOrBuildAsync<T>(
        ConcurrentDictionary<string, Entry<T>> store,
        string key,
        bool forceRefresh,
        TimeSpan ttl,
        Func<Task<T?>> build)
    {
        var entry = store.GetOrAdd(key, _ => new Entry<T>());

        if (!forceRefresh && IsFresh(entry, ttl)) return entry.Value;

        // Timestamp seen before queuing: if it moves while we wait, someone else rebuilt.
        var seenAt = entry.AtUtc;
        await entry.Lock.WaitAsync();
        try
        {
            // A rebuild that finished while we were queued is newer than this request, so it
            // satisfies a forced refresh too - building again would just duplicate the work.
            if (entry.AtUtc > seenAt && entry.Populated) return entry.Value;
            if (!forceRefresh && IsFresh(entry, ttl)) return entry.Value;

            entry.Value = await build();
            entry.Populated = true;
            entry.AtUtc = DateTime.UtcNow;
            return entry.Value;
        }
        finally
        {
            entry.Lock.Release();
        }
    }

    private static bool IsFresh<T>(Entry<T> entry, TimeSpan ttl) =>
        entry.Populated && DateTime.UtcNow - entry.AtUtc <= ttl;
}
