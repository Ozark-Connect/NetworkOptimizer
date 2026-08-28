namespace NetworkOptimizer.Web.Services.ApAgent;

/// <summary>One live reading of a client from the access point it is actually on.</summary>
/// <param name="Client">The client as its access point resolved it.</param>
/// <param name="ApMac">The access point that answered, which is the authority on holding it.</param>
public sealed record ApAgentLiveClient(ApAgentClient Client, string ApMac);

/// <summary>
/// Live client polling from AP Agents, with the roam follow on top.
///
/// This is an accelerator, never a requirement: every path that cannot answer returns null, and the
/// caller then polls the console exactly as it does on a site with no AP Agents at all.
/// </summary>
public sealed class ApAgentClientLiveService
{
    /// <summary>
    /// How far back the roam hint reads events. Bounds the replay the access point has to return
    /// and matches the window the search itself runs for.
    /// </summary>
    private static readonly TimeSpan HintLookback = ApAgentRoamFollower.SearchWindow;

    private readonly IApAgentClientReader _reader;
    private readonly MonitoringLiveStatsRegistry _liveStats;
    private readonly ILogger<ApAgentClientLiveService> _logger;

    /// <summary>Creates the live service.</summary>
    public ApAgentClientLiveService(
        IApAgentClientReader reader,
        MonitoringLiveStatsRegistry liveStats,
        ILogger<ApAgentClientLiveService> logger)
    {
        _reader = reader;
        _liveStats = liveStats;
        _logger = logger;
    }

    /// <summary>
    /// One live poll. Returns null whenever the AP Agent path cannot answer, which is the signal
    /// for the caller to use the console path for this tick.
    /// </summary>
    /// <param name="siteSlug">Site the client is on.</param>
    /// <param name="clientMac">Client's MAC. An MLO client's link MAC resolves to its MLD record.</param>
    /// <param name="consoleApMac">Where the console last said the client was, used to seed the follow.</param>
    /// <param name="follower">This client's follow state, owned by the caller.</param>
    /// <param name="now">Current time, so the search window is testable.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<ApAgentLiveClient?> PollAsync(
        string siteSlug,
        string clientMac,
        string? consoleApMac,
        ApAgentRoamFollower follower,
        DateTime now,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(clientMac)) return null;

        var aps = await _reader.ListAgentApsAsync(siteSlug, ct);
        if (aps.Count == 0)
        {
            follower.Stalled();
            return null;
        }

        Seed(follower, consoleApMac, aps, now);

        if (follower.State == ApAgentFollowState.Attached && follower.CurrentAp is { } current)
        {
            var found = await AttachedPollAsync(siteSlug, clientMac, current, follower, now, ct);
            if (found != null) Publish(siteSlug, found);
            if (found != null || !follower.IsSearching) return found;
        }

        if (!follower.IsSearching) return null;

        var searched = await SearchAsync(siteSlug, clientMac, follower, aps, now, ct);
        if (searched != null) Publish(siteSlug, searched);
        return searched;
    }

    /// <summary>
    /// Publishes the reading into the site's live cache. Client Performance is polling this client
    /// far faster than the collector does, and that freshness should be available to anything else
    /// asking what the client is doing right now rather than staying private to the page.
    /// </summary>
    private void Publish(string siteSlug, ApAgentLiveClient live)
    {
        try
        {
            var c = live.Client;

            // A client the access point has not heard from is not connected now, however often we
            // are asked about it. Watching one on Client Performance must not be what keeps a
            // departed device alive in the cache, and so on the maps.
            var idle = NetworkOptimizer.Core.Helpers.ClientPresence.LowestIdle(c.Links.Select(l => l.IdleSeconds));
            if (!NetworkOptimizer.Core.Helpers.ClientPresence.IsPresent(idle)) return;

            var live_ = _liveStats.GetFor(siteSlug);
            var clientKey = string.IsNullOrEmpty(c.MldMac) ? c.Mac : c.MldMac;

            // Throughput has to be carried explicitly. The cache reads a missing rate as zero, so
            // publishing without one overwrites the collector's measurement with "idle" twice a
            // second, which showed as a burst lasting about as long as the cache's single-zero
            // hold and flat zero either side.
            var (tx, rx) = ResolveThroughput(clientKey, c);
            var prior = tx == null ? live_.GetWifiClient(clientKey) : null;

            // The agent already resolves the active link into these scalars, so there is nothing to
            // pick here: taking them straight through is what keeps our value and the page's equal.
            live_.RecordWifiClient(new WifiClientLiveSnapshot
            {
                ClientMac = string.IsNullOrEmpty(c.MldMac) ? c.Mac : c.MldMac,
                ApMac = live.ApMac,
                Band = NormalizeBand(c.Band),
                Channel = c.Channel > 0 ? c.Channel : null,
                ChannelWidth = c.Bandwidth > 0 ? c.Bandwidth : null,
                SignalDbm = c.Signal,
                NoiseDbm = c.Noise,
                TxRateKbps = c.TxRateKbps > 0 ? c.TxRateKbps : null,
                RxRateKbps = c.RxRateKbps > 0 ? c.RxRateKbps : null,
                Rssi = c.Snr,
                IsMlo = c.IsMlo,
                IdleSeconds = idle,
                TxThroughputBps = tx ?? prior?.TxThroughputBps,
                RxThroughputBps = rx ?? prior?.RxThroughputBps,
                Source = WifiClientSource.ApAgent,
                LastUpdate = DateTime.UtcNow,
            });
        }
        catch (Exception ex)
        {
            // Publishing is a courtesy to other readers; it must never break the page's own poll.
            _logger.LogDebug(ex, "Could not publish AP Agent client reading to the live cache");
        }
    }

    /// <summary>
    /// Throughput for the watched client from its own byte counters, so the page's rate is measured
    /// at the rate the page polls rather than inherited from the collector's slower pass.
    ///
    /// Returns null far more often than not: this polls several times a second while the access
    /// point refreshes the counters on its own short tier, so most calls span no new reading at all
    /// and the caller holds what was last measured.
    /// </summary>
    private (double? Tx, double? Rx) ResolveThroughput(string clientKey, ApAgentClient c)
    {
        var sample = ApAgentWifiFieldMapper.ToSample(c, "00:00:00:00:00:00");
        if (sample?.TxBytes is not { } tx || sample.RxBytes is not { } rx) return (null, null);

        var at = sample.BytesAt ?? DateTime.UtcNow;
        var resolved = _bytes.TryGetValue(clientKey, out var prev)
            ? ApAgentThroughput.FromCounters(tx, rx, at, prev.TxBytes, prev.RxBytes, prev.At)
            : (null, null);

        // Only advance the baseline on a genuinely new reading, or a run of polls sharing one
        // reading would keep resetting it and no gap would ever be long enough to measure.
        if (prev.At != at) _bytes[clientKey] = new Reading(at, tx, rx);
        return resolved;
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Reading> _bytes = new(StringComparer.OrdinalIgnoreCase);

    private readonly record struct Reading(DateTime At, long TxBytes, long RxBytes);

    /// <summary>The live cache keys band as "2.4ghz" / "5ghz" / "6ghz"; the agent reports "ng" / "na" / "6e".</summary>
    private static string NormalizeBand(string? band) => band switch
    {
        "ng" or "2.4" or "2.4ghz" => "2.4ghz",
        "na" or "5" or "5ghz" => "5ghz",
        "6e" or "6" or "6ghz" => "6ghz",
        _ => band ?? "",
    };

    /// <summary>
    /// Points the follow at the console's access point when it has nowhere else to go. An access
    /// point without an AP Agent is not adopted, so that client stays on the console path.
    /// </summary>
    private static void Seed(ApAgentRoamFollower follower, string? consoleApMac, IReadOnlyList<string> aps, DateTime now)
    {
        if (!follower.ShouldSeed(consoleApMac, now)) return;

        var mac = ApAgentWifiFieldMapper.NormalizeMac(consoleApMac);
        if (aps.Contains(mac, StringComparer.Ordinal)) follower.Seen(mac);
    }

    /// <summary>Idle past which a station is a leftover, not the association serving the client.</summary>
    private const long StaleStationSeconds = 45;

    /// <summary>
    /// Polls the access point the client is believed to be on. Its answer that the client is not
    /// there is the roam signal, and is what starts the search in the same tick.
    /// </summary>
    private async Task<ApAgentLiveClient?> AttachedPollAsync(
        string siteSlug, string clientMac, string apMac, ApAgentRoamFollower follower, DateTime now, CancellationToken ct)
    {
        var lookup = await _reader.ReadClientAsync(siteSlug, apMac, clientMac, ct);

        // An access point can keep a station long after the client left, and it answers Found for it
        // like any other. Only a departure it noticed starts the search, so a quiet station pins the
        // page to the wrong access point for as long as the stale entry survives.
        var quiet = lookup.Status == ApAgentClientLookupStatus.Found && lookup.Client != null
            && NetworkOptimizer.Core.Helpers.ClientPresence.LowestIdle(
                lookup.Client.Links.Select(l => l.IdleSeconds)) is { } idle
            && idle > StaleStationSeconds;

        switch (lookup.Status)
        {
            case ApAgentClientLookupStatus.Found when lookup.Client != null && !quiet:
                follower.Seen(apMac);
                return new ApAgentLiveClient(lookup.Client, apMac);

            case ApAgentClientLookupStatus.Found:
            case ApAgentClientLookupStatus.NotOnAp:
                var hint = await _reader.ReadPeerHintAsync(siteSlug, apMac, clientMac, now - HintLookback, ct);
                _logger.LogDebug("Client {Mac} left {Ap}; following the roam (peer hint {Hint})",
                    clientMac, apMac, hint ?? "none");
                follower.Left(now, hint);
                return null;

            default:
                follower.Stalled();
                return null;
        }
    }

    /// <summary>
    /// Probes a bounded slice of the fleet for a client that just left. Stops at the first access
    /// point that has it; the follower closes the window when it never turns up.
    /// </summary>
    private async Task<ApAgentLiveClient?> SearchAsync(
        string siteSlug, string clientMac, ApAgentRoamFollower follower,
        IReadOnlyList<string> aps, DateTime now, CancellationToken ct)
    {
        var probes = follower.NextProbes(aps, now);
        if (probes.Count == 0) return null;

        var lookups = await Task.WhenAll(probes.Select(ap => _reader.ReadClientAsync(siteSlug, ap, clientMac, ct)));

        for (var i = 0; i < probes.Count; i++)
        {
            if (lookups[i].Status != ApAgentClientLookupStatus.Found || lookups[i].Client is not { } client) continue;

            follower.Seen(probes[i]);
            _logger.LogDebug("Client {Mac} reappeared on {Ap}", clientMac, probes[i]);
            return new ApAgentLiveClient(client, probes[i]);
        }

        return null;
    }
}
