using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace NetworkOptimizer.Web.Services.ApAgent;

/// <summary>
/// Asks a client to move to a different access point, using 802.11v BSS Transition Management.
///
/// This is the one thing the AP Agent does that changes the network rather than observing it. Three
/// properties of the mechanism shape everything here:
///
/// It is a request. The client decides. The ubus backend uses disassociation-imminent and a
/// departure guard; the limited native backend sends a voluntary request with neither. A native
/// client can decline and stay connected.
///
/// The candidate list steers by SIZE, not order. A phone repeatedly ignored a 12-entry list and
/// followed a 3-entry one exactly, so each intent sends only the candidates that serve it. The ubus
/// backend keeps the current AP last: an evicted client must have somewhere valid to land. Native
/// requests only offer other APs, with abridged=0 and no disassociation timer so staying is allowed.
///
/// Success here means the frame was sent. Where the client actually went arrives separately, as a
/// roam event through the agent's event stream.
/// </summary>
public sealed class ApAgentRoamService : IApAgentRoamService
{
    private static readonly TimeSpan NeighborTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan TransitionTimeout = TimeSpan.FromSeconds(8);
    private const long MaxNeighborBytes = 256 * 1024;
    private const long MaxTransitionBytes = 32 * 1024;

    /// <summary>
    /// Disassociation timer in beacon intervals, about ten seconds at a 100 TU beacon. Long enough
    /// for a client to move of its own accord before it is pushed.
    /// </summary>
    private const int DurationTbtt = 100;

    /// <summary>
    /// How long a client is kept off the access point it just left. Five seconds was not enough: a
    /// phone sat out the ban on its new access point and went straight back the instant it lifted.
    /// The agent applies it only to a client that left before the disassociation timer, so a client
    /// that could use no candidate is never locked out. On a network running 802.11r the agent
    /// re-steers a bounced client for this window instead of banning it - a hostapd ban refuses
    /// fast-transition auths and poisons the client against the AP.
    /// </summary>
    private const int BounceGuardMs = 20000;

    /// <summary>
    /// Idle ceiling for steering. Far below the ten minutes presence uses: this disassociates
    /// something, so it wants the client demonstrably in use rather than merely associated.
    /// </summary>
    private const long MaxIdleSecondsToSteer = 60;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ApAgentHttpTransport _transport;
    private readonly ApAgentTargetDirectory _directory;
    private readonly IApAgentClientReader _reader;
    private readonly NetworkOptimizer.Storage.Services.SiteDbContextFactory _siteDbFactory;
    private readonly ILogger<ApAgentRoamService> _logger;
    private readonly string _siteSlug;

    public ApAgentRoamService(
        ApAgentHttpTransport transport,
        ApAgentTargetDirectory directory,
        IApAgentClientReader reader,
        NetworkOptimizer.Storage.Services.SiteDbContextFactory siteDbFactory,
        ILogger<ApAgentRoamService> logger,
        string siteSlug = SiteManagementService.DefaultSiteSlug)
    {
        _transport = transport;
        _directory = directory;
        _reader = reader;
        _siteDbFactory = siteDbFactory;
        _logger = logger;
        _siteSlug = string.IsNullOrEmpty(siteSlug) ? SiteManagementService.DefaultSiteSlug : siteSlug;
    }

    /// <inheritdoc />
    public async Task<ApAgentRoamResult> RequestRoamAsync(
        string clientMac, string? ssid = null,
        ApAgentRoamIntent intent = ApAgentRoamIntent.AccessPoint, CancellationToken ct = default)
    {
        var mac = (clientMac ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(mac)) return ApAgentRoamResult.Fail("No client given.");

        var targets = await _directory.GetTargetsAsync(_siteSlug, ct);
        if (targets.Count == 0)
            return ApAgentRoamResult.Fail("No access points on this site are running the AP Agent.");
        if (intent == ApAgentRoamIntent.AccessPoint && targets.Count < 2)
            return ApAgentRoamResult.Fail("Moving to another access point needs at least two running the AP Agent.");

        if (!await HasRoamedBeforeAsync(mac, ct))
            return ApAgentRoamResult.Fail("This client has never been seen roaming, so it may not survive being moved.");

        var (current, idleSeconds, currentBands, sourceClient) = await FindHoldingApAsync(targets, mac, ct);
        if (current == null)
            return ApAgentRoamResult.Fail("That client is not on an access point running the AP Agent.");

        // A sleeping client holds its association but will not scan until it wakes, so moving it
        // off leaves it off.
        if (idleSeconds is { } idle && idle > MaxIdleSecondsToSteer)
            return ApAgentRoamResult.Fail("Unable to roam, client appears to be idle, so we won't want to strand it.");

        // Revalidate immediately before every mutation. A cached UI capability cannot authorize
        // a request after an agent restart, firmware change or client move.
        var steeringHealth = await FetchSteeringHealthAsync(current, ct);
        if (!FreshSteeringHealth(steeringHealth))
            return ApAgentRoamResult.Fail("The access point's steering capabilities are unavailable or stale.");

        if (steeringHealth!.Unavailable.Contains(SteeringProbe, StringComparer.OrdinalIgnoreCase))
        {
            if (intent != ApAgentRoamIntent.AccessPoint)
                return ApAgentRoamResult.Fail("This access point supports only voluntary moves to another access point.");
            var nativeVaps = ApAgentNativeSteering.SupportedVaps(steeringHealth);
            if (sourceClient == null || !ApAgentNativeSteering.EligibleClient(sourceClient, nativeVaps))
                return ApAgentRoamResult.Fail("Native steering needs an authorized non-MLO client on a supported 5 GHz network.");
            var sourceSsid = sourceClient.Links[0].Ssid!;
            if (!string.IsNullOrEmpty(ssid) && ssid != sourceSsid)
                return ApAgentRoamResult.Fail("The client's network changed before the request.");
            var reports = new List<ApAgentNeighborReport>();
            foreach (var target in targets.Where(t => !t.Mac.Equals(current.Mac, StringComparison.OrdinalIgnoreCase)))
                reports.AddRange(await FetchNeighborReportsAsync(target, sourceSsid, ct));
            var nativeCandidates = ApAgentNativeSteering.Candidates(reports, sourceSsid);
            if (nativeCandidates.Count == 0)
                return ApAgentRoamResult.Fail("No compatible 5 GHz destination supplied a verified neighbor report.");
            return await SendNativeAsync(current, sourceClient, nativeCandidates, ct);
        }

        var own = await FetchNeighborsAsync(current, ssid, ct);
        var wanted = intent == ApAgentRoamIntent.Band
            ? ApAgentRoamCandidates.OtherBands(own, currentBands)
            : await CollectCandidatesAsync(targets, current, ssid, ct);

        if (wanted.Count == 0)
            return ApAgentRoamResult.Fail(intent == ApAgentRoamIntent.Band
                ? "That access point offers no other band on this network."
                : "No other access point offered a candidate to move to.");

        // Where the client already is, last. The request evicts either way, so a client that can use
        // none of the above needs somewhere valid to land or it ends up on no SSID at all.
        var candidates = wanted;
        candidates.AddRange(own.Except(wanted));

        // What was offered, in order. The access point does not record the list, so without this
        // there is no way to ask afterwards why a client landed where it did.
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("BTM candidates for {Mac} leaving {Ap} ({Intent}, on {Bands}): {Candidates}",
                mac, current.Name ?? current.Host, intent,
                currentBands.Count > 0 ? string.Join("+", currentBands) : "unknown",
                string.Join(", ", candidates.Select(ApAgentRoamCandidates.Describe)));
        }

        var body = JsonSerializer.Serialize(new ApAgentTransitionRequest
        {
            Candidates = candidates,
            DurationTbtt = DurationTbtt,
            Abridged = true,

            // Only for an access point move. A band move lands on the same access point, so banning
            // there would block the destination.
            BanMs = intent == ApAgentRoamIntent.AccessPoint ? BounceGuardMs : 0,
        }, JsonOptions);

        try
        {
            var (host, port) = await _transport.RouteAsync(_siteSlug, current.Host);
            var result = await _transport.SendAsync(
                host, port, current.Token, $"/clients/{mac}/bss-transitions",
                TransitionTimeout, MaxTransitionBytes, body, ct);

            if (!result.IsUsable)
            {
                // The body carries the access point's own reason; without it a 500 says only that
                // something went wrong, on the one surface that cannot be reproduced on demand.
                _logger.LogWarning("BTM request for {Mac} on {Ap} answered {Status}: {Body}",
                    mac, current.Name ?? current.Host, result.Status, result.Body);
                // The access point distinguishes a refusal from a failure, so say which. A client
                // that moved between choosing this access point and the request arriving is the
                // common case, and reporting it as a server error made it look like a defect.
                return ApAgentRoamResult.Fail(result.Status switch
                {
                    404 => "That client is no longer on that access point - it may have already moved.",
                    400 => "The access point could not use the request.",
                    _ => $"The access point refused the request ({result.Status}).",
                });
            }

            _logger.LogInformation("BTM request sent for {Mac} from {Ap} with {Count} candidate(s) on site {Site}",
                mac, current.Name ?? current.Host, candidates.Count, _siteSlug);

            return new ApAgentRoamResult(true, "Asked the client to move.", current.Name, candidates.Count);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "BTM request failed for {Mac} on site {Site}", mac, _siteSlug);
            return ApAgentRoamResult.Fail("Could not reach the access point the client is on.");
        }
    }

    /// <inheritdoc />
    public async Task<bool> IsAvailableAsync(string? clientMac = null, CancellationToken ct = default)
    {
        if (!await _directory.IsSiteEnabledAsync(_siteSlug, ct)) return false;
        var targets = await _directory.GetTargetsAsync(_siteSlug, ct);
        if (targets.Count < 2) return false;

        if (string.IsNullOrWhiteSpace(clientMac)) return true;
        var mac = clientMac.Trim().ToLowerInvariant();
        if (!await HasRoamedBeforeAsync(mac, ct)) return false;
        var (holding, _, _, client) = await FindHoldingApAsync(targets, mac, ct);
        if (holding == null) return false;
        var health = await FetchSteeringHealthAsync(holding, ct);
        if (!FreshSteeringHealth(health)) return false;
        if (!health!.Unavailable.Contains(SteeringProbe, StringComparer.OrdinalIgnoreCase)) return true;
        var nativeVaps = ApAgentNativeSteering.SupportedVaps(health);
        if (client == null || !ApAgentNativeSteering.EligibleClient(client, nativeVaps)) return false;
        var reports = new List<ApAgentNeighborReport>();
        foreach (var target in targets.Where(t => !t.Mac.Equals(holding.Mac, StringComparison.OrdinalIgnoreCase)))
            reports.AddRange(await FetchNeighborReportsAsync(target, client.Links[0].Ssid, ct));
        return ApAgentNativeSteering.Candidates(reports, client.Links[0].Ssid!).Count > 0;
    }

    /// <summary>
    /// The agent probe that steering depends on: hostapd's control objects on ubus. An agent that
    /// gains another steering path must report it, and this gate must follow.
    /// </summary>
    public const string SteeringProbe = "ubus";

    /// <inheritdoc />
    public Task<IReadOnlyCollection<string>> GetApsWithoutSteeringAsync(CancellationToken ct = default)
        => Task.FromResult(_directory.ApsWithoutSteering(_siteSlug));

    /// <inheritdoc />
    public async Task<bool> CanChangeBandAsync(string clientMac, string? currentBand, CancellationToken ct = default)
    {
        var targets = await _directory.GetTargetsAsync(_siteSlug, ct);
        var (holding, _, _, _) = await FindHoldingApAsync(targets, clientMac, ct);
        if (holding == null) return false;
        var health = await FetchSteeringHealthAsync(holding, ct);
        if (!FreshSteeringHealth(health) || health!.Unavailable.Contains(SteeringProbe, StringComparer.OrdinalIgnoreCase)) return false;

        var rank = ApAgentRoamCandidates.BandRank(currentBand);
        if (rank == 0) return true;

        var mac = (clientMac ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(mac)) return false;

        try
        {
            using var db = _siteDbFactory.CreateForSite(_siteSlug, _siteSlug == SiteManagementService.DefaultSiteSlug);
            var seen = await db.ApRoamRecords.AsNoTracking()
                .Where(r => r.ClientMac == mac)
                .Select(r => new { r.Band, r.FromBand })
                .ToListAsync(ct);

            return seen.Any(s => ApAgentRoamCandidates.BandRank(s.Band) > rank
                || ApAgentRoamCandidates.BandRank(s.FromBand) > rank);
        }
        catch (Exception ex)
        {
            // Offer it rather than withhold on a read failure: the worst case is one refused steer.
            _logger.LogDebug(ex, "Could not read observed bands for {Mac} on {Site}", mac, _siteSlug);
            return true;
        }
    }

    /// <summary>
    /// Whether this client has ever been recorded roaming - the only evidence that it survives a
    /// transition, since nothing reports BSS Transition support.
    /// </summary>
    private async Task<bool> HasRoamedBeforeAsync(string mac, CancellationToken ct)
    {
        try
        {
            using var db = _siteDbFactory.CreateForSite(_siteSlug, _siteSlug == SiteManagementService.DefaultSiteSlug);
            return await db.ApRoamRecords.AsNoTracking()
                .AnyAsync(r => r.ClientMac == mac, ct);
        }
        catch (Exception ex)
        {
            // No history is the safe answer: it withholds the control rather than offering one that
            // can strand a device.
            _logger.LogDebug(ex, "Could not read roam history for {Mac} on {Site}", mac, _siteSlug);
            return false;
        }
    }

    /// <summary>One access point's own neighbor report elements, filtered to the client's SSID.</summary>
    private async Task<List<string>> FetchNeighborsAsync(ApAgentTarget target, string? ssid, CancellationToken ct)
        => (await FetchNeighborReportsAsync(target, ssid, ct)).Select(n => n.Element).ToList();

    private async Task<List<ApAgentNeighborReport>> FetchNeighborReportsAsync(ApAgentTarget target, string? ssid, CancellationToken ct)
    {
        var elements = new List<ApAgentNeighborReport>();
        try
        {
            var (host, port) = await _transport.RouteAsync(_siteSlug, target.Host);
            var result = await _transport.SendAsync(
                host, port, target.Token, "/neighbors", NeighborTimeout, MaxNeighborBytes, ct);
            if (!result.IsUsable) return elements;

            var payload = JsonSerializer.Deserialize<ApAgentNeighborsPayload>(result.Body, JsonOptions);
            if (payload?.Neighbors == null) return elements;

            foreach (var n in payload.Neighbors)
            {
                if (string.IsNullOrEmpty(n.Element)) continue;

                // Mesh backhaul VAPs advertise themselves too. Steering a client onto one would move
                // it to a network it is not a member of.
                if (n.Ssid.StartsWith("vwire-", StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.IsNullOrEmpty(ssid) && !string.Equals(n.Ssid, ssid, StringComparison.Ordinal)) continue;

                elements.Add(n);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // One unreachable access point costs a candidate, not the whole request.
            _logger.LogDebug(ex, "Could not read neighbors from {Host}", target.Host);
        }
        return elements;
    }

    /// <summary>Finds which access point currently holds the client, and on which bands.</summary>
    private async Task<(ApAgentTarget? Ap, long? IdleSeconds, IReadOnlyCollection<string> Bands, ApAgentClient? Client)> FindHoldingApAsync(
        IReadOnlyList<ApAgentTarget> targets, string mac, CancellationToken ct)
    {
        (ApAgentTarget? Target, long? Idle, IReadOnlyCollection<string> Bands, ApAgentClient? Client) best =
            (null, null, Array.Empty<string>(), null);

        foreach (var target in targets)
        {
            // Through the reader rather than a second hand-rolled fetch: it unwraps the reply, and
            // parsing it here as a bare client silently produced an empty one for months.
            var lookup = await _reader.ReadClientAsync(_siteSlug, target.Mac, mac, ct);
            if (lookup.Status != ApAgentClientLookupStatus.Found || lookup.Client is not { } client) continue;

            // The same payload carries how long since the access point last heard from this client,
            // which is what decides whether it is awake enough to be moved.
            long? idle = null;
            var bands = new HashSet<string>(StringComparer.Ordinal);
            if (client.Links is { Count: > 0 })
            {
                idle = NetworkOptimizer.Core.Helpers.ClientPresence.LowestIdle(client.Links.Select(l => l.IdleSeconds));
                foreach (var link in client.Links.Where(l => !string.IsNullOrEmpty(l.Band)))
                    bands.Add(link.Band!);
            }
            else if (!string.IsNullOrEmpty(client.Band))
            {
                bands.Add(client.Band);
            }

            // Keep looking. More than one access point can answer Found - one of them holding a
            // station the client left - and taking the first refused a live client as asleep on
            // another access point's hour-old entry. Least idle is the one actually serving it.
            if (best.Target == null || Fresher(idle, best.Idle))
                best = (target, idle, bands, client);
        }
        return best;
    }

    private static bool FreshSteeringHealth(ApAgentHealthPayload? health)
        => health != null && health.LastProbeRun != default && health.CollectedAt != default
            && health.CollectedAt >= health.LastProbeRun
            && health.CollectedAt - health.LastProbeRun <= TimeSpan.FromMinutes(10);

    private async Task<ApAgentHealthPayload?> FetchSteeringHealthAsync(ApAgentTarget target, CancellationToken ct)
    {
        try
        {
            var (host, port) = await _transport.RouteAsync(_siteSlug, target.Host);
            var result = await _transport.SendAsync(host, port, target.Token, "/health", NeighborTimeout, MaxNeighborBytes, ct);
            return result.IsUsable ? ApAgentHealthClient.ParseHealth(result.Body) : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not verify steering health for {Ap}", target.Name);
            return null;
        }
    }

    internal async Task<ApAgentRoamResult> SendNativeAsync(ApAgentTarget target, ApAgentClient client,
        List<ApAgentNativeCandidate> candidates, CancellationToken ct)
    {
        // This body has no legacy duration or guard fields. Never fall back to the eviction route
        // on any failure: a timeout can mean the single voluntary frame was already sent.
        var body = JsonSerializer.Serialize(new ApAgentNativeTransitionRequest { Candidates = candidates }, JsonOptions);
        try
        {
            var (host, port) = await _transport.RouteAsync(_siteSlug, target.Host);
            var result = await _transport.SendAsync(host, port, target.Token,
                $"/clients/{client.Mac}/{ApAgentNativeSteering.Route}", TransitionTimeout, MaxTransitionBytes, body, ct);
            if (!result.IsUsable)
                return ApAgentRoamResult.Fail(result.Status == 404
                    ? "The client moved or the agent no longer supports this voluntary request."
                    : "The access point could not confirm the voluntary request. It will not be retried.");
            var sent = JsonSerializer.Deserialize<ApAgentTransitionResult>(result.Body, JsonOptions);
            if (sent == null || !sent.Mac.Equals(client.Mac, StringComparison.OrdinalIgnoreCase)
                || sent.Vap != client.Links[0].Vap || sent.Candidates != candidates.Count)
                return ApAgentRoamResult.Fail("The access point returned an unexpected acknowledgment. It will not be retried.");
            return new(true, "Asked the client to move voluntarily. It may stay connected here.", target.Name, candidates.Count);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not confirm the voluntary request for {Mac}", client.Mac);
            return ApAgentRoamResult.Fail("The voluntary request could not be confirmed. It will not be retried.");
        }
    }

    /// <summary>Whether one station reading is fresher. A known idle beats an unknown one.</summary>
    private static bool Fresher(long? candidate, long? held) => (candidate, held) switch
    {
        (null, _) => false,
        (_, null) => true,
        var (c, h) => c < h,
    };

    /// <summary>Gathers neighbor reports from every access point except the one to move off.</summary>
    private async Task<List<string>> CollectCandidatesAsync(
        IReadOnlyList<ApAgentTarget> targets, ApAgentTarget current, string? ssid, CancellationToken ct)
    {
        var candidates = new List<string>();

        foreach (var target in targets)
        {
            if (string.Equals(target.Mac, current.Mac, StringComparison.OrdinalIgnoreCase)) continue;
            candidates.AddRange(await FetchNeighborsAsync(target, ssid, ct));
        }

        return candidates;
    }
}
