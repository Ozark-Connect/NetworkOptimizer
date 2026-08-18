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
    /// Client-rate history TTL. Deliberately the same as <see cref="PlanTtl"/> so there is one rule
    /// to reason about: everything is an hour old at most, and Refresh rebuilds all of it.
    ///
    /// It earns a cache at all for two reasons rather than cost - the query runs in well under a
    /// second: several option sets building plans inside the same hour share one fetch, and a null
    /// result is cached too, so an unreachable InfluxDB costs one timeout per hour instead of one
    /// per plan build.
    /// </summary>
    public static readonly TimeSpan ClientRatesTtl = PlanTtl;

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
        Func<Task<Dictionary<RadioBand, ChannelPlan>>> build,
        Func<Dictionary<RadioBand, ChannelPlan>?, bool>? shouldCache = null)
    {
        Func<Task<Dictionary<RadioBand, ChannelPlan>?>> nullable = async () => await build();
        // An empty result means the console was unreachable or the build threw, NOT that this site
        // has no plan. Caching it pinned "channel analysis unavailable" for the full hour even once
        // the console came back - a six-second window after a restart was enough to do it.
        var plan = await GetOrBuildAsync(
            _plans, key, forceRefresh, PlanTtl, nullable,
            shouldCache: shouldCache ?? (p => p is { Count: > 0 }));

        // Hand out a copy, never the cached instance. Callers own what they are given and do mutate
        // it - Channel Analysis clears the dictionary when switching back to Show Current Channels,
        // which emptied the shared entry in place and made the next Recommend Best Channels look
        // like it did nothing. Before caching, every call got a fresh dictionary and that was safe.
        return plan == null
            ? new Dictionary<RadioBand, ChannelPlan>()
            : new Dictionary<RadioBand, ChannelPlan>(plan);
    }

    /// <summary>
    /// Client-rate history for a site. A failed lookup caches its null result too, so an
    /// unreachable InfluxDB costs one timeout per TTL instead of one per plan build.
    /// </summary>
    public async Task<Dictionary<RadioBand, Dictionary<string, IReadOnlyList<ClientRateSample>>>?> GetOrBuildClientRatesAsync(
        string siteSlug,
        bool forceRefresh,
        Func<Task<Dictionary<RadioBand, Dictionary<string, IReadOnlyList<ClientRateSample>>>?>> build)
    {
        var rates = await GetOrBuildAsync(_clientRates, siteSlug, forceRefresh, ClientRatesTtl, build);
        // Copied for the same reason as plans; the inner lists are IReadOnlyList and stay shared.
        return rates == null
            ? null
            : new Dictionary<RadioBand, Dictionary<string, IReadOnlyList<ClientRateSample>>>(rates);
    }

    /// <summary>Drops every cached plan for a site (e.g. its console connection changed).</summary>
    public void InvalidateSite(string siteSlug)
    {
        foreach (var key in _plans.Keys.Where(k => k.StartsWith(siteSlug + "|", StringComparison.Ordinal)).ToList())
            _plans.TryRemove(key, out _);
        _clientRates.TryRemove(siteSlug, out _);
    }

    /// <param name="shouldCache">
    /// Whether a freshly built value is worth keeping. Defaults to caching everything, including
    /// nulls - deliberate for client-rate history, where a null is a bounded timeout we do not want
    /// to repeat on every build. Results that represent a transient failure must opt out.
    /// </param>
    private static async Task<T?> GetOrBuildAsync<T>(
        ConcurrentDictionary<string, Entry<T>> store,
        string key,
        bool forceRefresh,
        TimeSpan ttl,
        Func<Task<T?>> build,
        Func<T?, bool>? shouldCache = null)
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

            var built = await build();
            if (shouldCache != null && !shouldCache(built))
                return built;

            entry.Value = built;
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
