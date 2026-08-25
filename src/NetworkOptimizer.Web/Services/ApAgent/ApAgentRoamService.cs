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

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ApAgentHttpTransport _transport;
    private readonly ApAgentTargetDirectory _directory;
    private readonly NetworkOptimizer.Storage.Services.SiteDbContextFactory _siteDbFactory;
    private readonly ILogger<ApAgentRoamService> _logger;
    private readonly string _siteSlug;

    public ApAgentRoamService(
        ApAgentHttpTransport transport,
        ApAgentTargetDirectory directory,
        NetworkOptimizer.Storage.Services.SiteDbContextFactory siteDbFactory,
        ILogger<ApAgentRoamService> logger,
        string siteSlug = SiteManagementService.DefaultSiteSlug)
    {
        _transport = transport;
        _directory = directory;
        _siteDbFactory = siteDbFactory;
        _logger = logger;
        _siteSlug = string.IsNullOrEmpty(siteSlug) ? SiteManagementService.DefaultSiteSlug : siteSlug;
    }

    /// <inheritdoc />
    public async Task<ApAgentRoamResult> RequestRoamAsync(
        string clientMac, string? ssid = null, CancellationToken ct = default)
    {
        var mac = (clientMac ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(mac)) return ApAgentRoamResult.Fail("No client given.");

        var targets = await _directory.GetTargetsAsync(_siteSlug, ct);
        if (targets.Count < 2)
            return ApAgentRoamResult.Fail("Steering needs at least two access points running the AP Agent.");

        if (!await HasRoamedBeforeAsync(mac, ct))
            return ApAgentRoamResult.Fail("This client has never been seen roaming, so it may not survive being moved.");

        var current = await FindHoldingApAsync(targets, mac, ct);
        if (current == null)
            return ApAgentRoamResult.Fail("That client is not on an access point running the AP Agent.");

        // Every OTHER access point's neighbor reports first: order is preference, so the client
        // tries those before anything after them.
        var candidates = await CollectCandidatesAsync(targets, current, ssid, ct);
        if (candidates.Count == 0)
            return ApAgentRoamResult.Fail("No other access point offered a candidate to move to.");

        // Then the current access point, last. The request is an eviction either way - hostapd
        // offers nothing but wnm_disassoc_imminent - so a client that cannot use any of the
        // candidates above is going to be disassociated regardless. Listing where it already is
        // gives it somewhere valid to land instead of nowhere, which is how a client ends up on no
        // SSID at all. It stays a steer because this entry is last.
        candidates.AddRange(await FetchNeighborsAsync(current, ssid, ct));

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
    /// Whether this client has ever been recorded roaming. The only evidence available that it can
    /// survive a transition: hostapd sends evictions rather than suggestions, and nothing reports a
    /// client's BSS Transition support - mca-dump carries is_11r with no 11v equivalent.
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
                if (!string.IsNullOrEmpty(ssid) && !string.Equals(n.Ssid, ssid, StringComparison.Ordinal)) continue;
                elements.Add(n.Element);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read neighbors from {Host}", target.Host);
        }
        return elements;
    }

    /// <summary>Finds which access point currently holds the client.</summary>
    private async Task<ApAgentTarget?> FindHoldingApAsync(
        IReadOnlyList<ApAgentTarget> targets, string mac, CancellationToken ct)
    {
        foreach (var target in targets)
        {
            try
            {
                var (host, port) = await _transport.RouteAsync(_siteSlug, target.Host);
                var result = await _transport.SendAsync(
                    host, port, target.Token, $"/clients/{mac}", NeighborTimeout, MaxTransitionBytes, ct);

                if (result.IsUsable) return target;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // An access point that cannot be reached simply is not the one holding the client.
            }
        }
        return null;
    }

    /// <summary>Gathers neighbor reports from every access point except the one to move off.</summary>
    private async Task<List<string>> CollectCandidatesAsync(
        IReadOnlyList<ApAgentTarget> targets, ApAgentTarget current, string? ssid, CancellationToken ct)
    {
        var candidates = new List<string>();

        foreach (var target in targets)
        {
            if (string.Equals(target.Mac, current.Mac, StringComparison.OrdinalIgnoreCase)) continue;

            try
            {
                var (host, port) = await _transport.RouteAsync(_siteSlug, target.Host);
                var result = await _transport.SendAsync(
                    host, port, target.Token, "/neighbors", NeighborTimeout, MaxNeighborBytes, ct);

                if (!result.IsUsable) continue;

                var payload = JsonSerializer.Deserialize<ApAgentNeighborsPayload>(result.Body, JsonOptions);
                if (payload?.Neighbors == null) continue;

                foreach (var n in payload.Neighbors)
                {
                    if (string.IsNullOrEmpty(n.Element)) continue;

                    // Mesh backhaul VAPs advertise themselves too. Steering a client onto one would
                    // move it to a network it is not a member of.
                    if (n.Ssid.StartsWith("vwire-", StringComparison.OrdinalIgnoreCase)) continue;
                    if (ssid != null && !string.Equals(n.Ssid, ssid, StringComparison.Ordinal)) continue;

                    candidates.Add(n.Element);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // One unreachable access point costs a candidate, not the whole request.
            }
        }

        return candidates;
    }
}
