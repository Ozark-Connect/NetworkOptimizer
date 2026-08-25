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
    private readonly ILogger<ApAgentClientLiveService> _logger;

    /// <summary>Creates the live service.</summary>
    public ApAgentClientLiveService(IApAgentClientReader reader, ILogger<ApAgentClientLiveService> logger)
    {
        _reader = reader;
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
            if (found != null || !follower.IsSearching) return found;
        }

        return follower.IsSearching
            ? await SearchAsync(siteSlug, clientMac, follower, aps, now, ct)
            : null;
    }

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

    /// <summary>
    /// Polls the access point the client is believed to be on. Its answer that the client is not
    /// there is the roam signal, and is what starts the search in the same tick.
    /// </summary>
    private async Task<ApAgentLiveClient?> AttachedPollAsync(
        string siteSlug, string clientMac, string apMac, ApAgentRoamFollower follower, DateTime now, CancellationToken ct)
    {
        var lookup = await _reader.ReadClientAsync(siteSlug, apMac, clientMac, ct);

        switch (lookup.Status)
        {
            case ApAgentClientLookupStatus.Found when lookup.Client != null:
                follower.Seen(apMac);
                return new ApAgentLiveClient(lookup.Client, apMac);

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
