using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace NetworkOptimizer.Web.Services.ApAgent;

/// <summary>
/// Asks a client to move to a different access point, using 802.11v BSS Transition Management.
///
/// This is the one thing the AP Agent does that changes the network rather than observing it. Three
/// properties of the mechanism shape everything here:
///
/// It is a request. The client decides, and hostapd exposes only the disassoc-imminent variant, so a
/// client that declines is disassociated when the timer expires and reassociates wherever it likes,
/// possibly the same access point.
///
/// The candidate list is what steers, by order: other access points first, the current one last. It
/// used to be omitted entirely, which made staying put a refusal - and left a client that could not
/// use any candidate with nowhere valid to go. One was observed never rejoining any SSID.
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

        var (current, idleSeconds, currentBands) = await FindHoldingApAsync(targets, mac, ct);
        if (current == null)
            return ApAgentRoamResult.Fail("That client is not on an access point running the AP Agent.");

        // A sleeping client holds an association through standby but will not scan and authenticate
        // until it wakes, so evicting one leaves it off the network until somebody turns it on.
        if (idleSeconds is { } idle && idle > MaxIdleSecondsToSteer)
            return ApAgentRoamResult.Fail(
                $"That client has been idle for {idle}s and may be asleep. A sleeping client can be moved off but cannot rejoin on its own.");

        var own = await FetchNeighborsAsync(current, ssid, ct);
        var wanted = intent == ApAgentRoamIntent.Band
            ? OtherBandsOf(own, currentBands)
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
                string.Join(", ", candidates.Select(DescribeCandidate)));
        }

        var body = JsonSerializer.Serialize(new ApAgentTransitionRequest
        {
            Candidates = candidates,
            DurationTbtt = DurationTbtt,
            Abridged = true,
        }, JsonOptions);

        try
        {
            var (host, port) = await _transport.RouteAsync(_siteSlug, current.Host);
            var result = await _transport.SendAsync(
                host, port, current.Token, $"/clients/{mac}/bss-transitions",
                TransitionTimeout, MaxTransitionBytes, body, ct);

            if (!result.IsUsable)
            {
                _logger.LogWarning("BTM request for {Mac} on {Ap} answered {Status}",
                    mac, current.Name ?? current.Host, result.Status);
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
        return await HasRoamedBeforeAsync(clientMac.Trim().ToLowerInvariant(), ct);
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

    /// <summary>
    /// A neighbor report element as "bssid/band ch". Hex layout: BSSID(6), BSSID info(4), operating
    /// class(1), channel(1) - so the band is already in what we forward.
    /// </summary>
    private static string DescribeCandidate(string element)
    {
        if (element.Length < 24) return element;

        var bssid = string.Join(":", Enumerable.Range(0, 6).Select(i => element.Substring(i * 2, 2)));
        if (!int.TryParse(element.AsSpan(22, 2), System.Globalization.NumberStyles.HexNumber, null, out var channel))
            return bssid;

        return $"{bssid}/{BandOf(element) ?? "?"}GHz ch{channel}";
    }

    /// <summary>
    /// Band token of a neighbor report element, from its operating class. Per 802.11: 81-84 are
    /// 2.4 GHz, 115-130 are 5 GHz, 131-136 are 6 GHz.
    /// </summary>
    private static string? BandOf(string element)
    {
        if (element.Length < 22) return null;
        if (!int.TryParse(element.AsSpan(20, 2), System.Globalization.NumberStyles.HexNumber, null, out var opClass))
            return null;

        return opClass switch
        {
            >= 81 and <= 84 => "2.4",
            >= 115 and <= 130 => "5",
            >= 131 and <= 136 => "6",
            _ => null,
        };
    }

    /// <summary>
    /// This access point's other bands, best first, for a band move. An MLO client holds several
    /// bands at once, so every band it is already on is excluded rather than just the active one.
    /// </summary>
    private static List<string> OtherBandsOf(IEnumerable<string> own, IReadOnlyCollection<string> currentBands)
        => own.Select(e => (Element: e, Band: BandOf(e)))
            .Where(x => x.Band != null && !currentBands.Contains(x.Band))
            .OrderByDescending(x => x.Band switch { "6" => 3, "5" => 2, _ => 1 })
            .Select(x => x.Element)
            .ToList();

    /// <summary>One access point's own neighbor report elements, filtered to the client's SSID.</summary>
    private async Task<List<string>> FetchNeighborsAsync(ApAgentTarget target, string? ssid, CancellationToken ct)
    {
        var elements = new List<string>();
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

                elements.Add(n.Element);
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
    private async Task<(ApAgentTarget? Ap, long? IdleSeconds, IReadOnlyCollection<string> Bands)> FindHoldingApAsync(
        IReadOnlyList<ApAgentTarget> targets, string mac, CancellationToken ct)
    {
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

            return (target, idle, bands);
        }
        return (null, null, Array.Empty<string>());
    }

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
