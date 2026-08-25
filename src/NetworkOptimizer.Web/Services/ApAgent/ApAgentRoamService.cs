using System.Text.Json;

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
/// The candidate list is what steers. Omitting the current access point's own BSSIDs is what makes
/// staying put a refusal rather than a valid choice.
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
    private readonly ILogger<ApAgentRoamService> _logger;
    private readonly string _siteSlug;

    public ApAgentRoamService(
        ApAgentHttpTransport transport,
        ApAgentTargetDirectory directory,
        ILogger<ApAgentRoamService> logger,
        string siteSlug = SiteManagementService.DefaultSiteSlug)
    {
        _transport = transport;
        _directory = directory;
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

        var current = await FindHoldingApAsync(targets, mac, ct);
        if (current == null)
            return ApAgentRoamResult.Fail("That client is not on an access point running the AP Agent.");

        // Candidates are every OTHER access point's neighbor reports. Excluding the current one is
        // the whole steering mechanism: with its own BSSIDs absent, staying is a refusal.
        var candidates = await CollectCandidatesAsync(targets, current, ssid, ct);
        if (candidates.Count == 0)
            return ApAgentRoamResult.Fail("No other access point offered a candidate to move to.");

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
                return ApAgentRoamResult.Fail($"The access point refused the request ({result.Status}).");
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
    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        if (!await _directory.IsSiteEnabledAsync(_siteSlug, ct)) return false;
        var targets = await _directory.GetTargetsAsync(_siteSlug, ct);
        return targets.Count >= 2;
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
