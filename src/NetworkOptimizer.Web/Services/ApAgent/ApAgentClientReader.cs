namespace NetworkOptimizer.Web.Services.ApAgent;

/// <summary>
/// One access point's live view of one client, as the roam follow needs it.
///
/// An interface because the follow loop's whole value is in what it does across several access
/// points over time, which is only testable without a fleet of real ones.
/// </summary>
public interface IApAgentClientReader
{
    /// <summary>Access points on the site whose AP Agent this server may use. Empty means no agent path.</summary>
    Task<IReadOnlyList<string>> ListAgentApsAsync(string siteSlug, CancellationToken ct = default);

    /// <summary>Asks one access point about one client.</summary>
    Task<ApAgentClientLookup> ReadClientAsync(
        string siteSlug, string apMac, string clientMac, CancellationToken ct = default);

    /// <summary>
    /// The BSSID one access point announced this client was moving to, or null when it announced
    /// nothing. A hint for probe ordering, never an answer.
    /// </summary>
    Task<string?> ReadPeerHintAsync(
        string siteSlug, string apMac, string clientMac, DateTime sinceUtc, CancellationToken ct = default);
}

/// <summary>
/// Reads live client state from AP Agents over the shared transport. Every failure is an absent
/// answer rather than an exception, so the caller's fallback is the only error path.
/// </summary>
public sealed class ApAgentClientReader : IApAgentClientReader
{
    /// <summary>An access point serves this from an in-memory table, so a slow one is a broken one.</summary>
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(2);

    private readonly ApAgentTargetDirectory _directory;
    private readonly ApAgentTelemetryClient _telemetry;

    /// <summary>Creates the reader.</summary>
    public ApAgentClientReader(ApAgentTargetDirectory directory, ApAgentTelemetryClient telemetry)
    {
        _directory = directory;
        _telemetry = telemetry;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ListAgentApsAsync(string siteSlug, CancellationToken ct = default)
    {
        if (!await _directory.IsSiteEnabledAsync(siteSlug, ct)) return Array.Empty<string>();
        var targets = await _directory.GetTargetsAsync(siteSlug, ct);
        return targets.Select(t => t.Mac).ToList();
    }

    /// <inheritdoc />
    public async Task<ApAgentClientLookup> ReadClientAsync(
        string siteSlug, string apMac, string clientMac, CancellationToken ct = default)
    {
        var target = await ResolveAsync(siteSlug, apMac, ct);
        if (target == null) return new ApAgentClientLookup(ApAgentClientLookupStatus.Unreachable, null);

        return await _telemetry.GetClientAsync(siteSlug, target.Host, target.Token, clientMac, ReadTimeout, ct);
    }

    /// <inheritdoc />
    public async Task<string?> ReadPeerHintAsync(
        string siteSlug, string apMac, string clientMac, DateTime sinceUtc, CancellationToken ct = default)
    {
        var target = await ResolveAsync(siteSlug, apMac, ct);
        if (target == null) return null;

        var payload = await _telemetry.GetEventsAsync(siteSlug, target.Host, target.Token, sinceUtc, ReadTimeout, ct);
        return payload == null ? null : PeerHint(payload.Events, clientMac);
    }

    /// <summary>
    /// The newest peer BSSID this access point announced for the client. Only the two roam kinds
    /// carry one; listener and association events are about something else.
    /// </summary>
    public static string? PeerHint(IReadOnlyList<ApAgentEvent> events, string clientMac)
    {
        var wanted = ApAgentWifiFieldMapper.NormalizeMac(clientMac);
        string? hint = null;

        foreach (var e in events)
        {
            if (e.Type is not (ApAgentEventTypes.RoamBroadcast or ApAgentEventTypes.RoamToPeer)) continue;
            if (string.IsNullOrEmpty(e.PeerBssid)) continue;
            if (ApAgentWifiFieldMapper.NormalizeMac(e.Mac) != wanted) continue;
            hint = ApAgentWifiFieldMapper.NormalizeMac(e.PeerBssid);
        }

        return hint;
    }

    private async Task<ApAgentTarget?> ResolveAsync(string siteSlug, string apMac, CancellationToken ct)
        => await _directory.IsSiteEnabledAsync(siteSlug, ct)
            ? await _directory.FindAsync(siteSlug, apMac, ct)
            : null;
}
