using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Gates;

namespace NetworkOptimizer.Web.Services.ApAgent;

/// <summary>
/// Asks a client to move to a different access point over 802.11v.
///
/// Operator rather than Admin: this operates the network on a permitted site, it changes no
/// configuration, and it is reversible by the client itself. Deploying or removing the AP Agent is
/// a Settings action and stays Admin.
/// </summary>
[MutatingService(SiteScoped = true)]
public interface IApAgentRoamService
{
    /// <summary>
    /// Asks the client to leave whichever BSSID currently holds it. Returns once the request is
    /// sent: 802.11v is a request, so where the client lands arrives afterwards as a roam event.
    ///
    /// The candidate list is what steers, and <paramref name="intent"/> decides it. Where the client
    /// already is always goes last, never absent, so a client that can use nothing offered still has
    /// somewhere valid to land.
    /// </summary>
    /// <param name="clientMac">Client MAC, or the MLD MAC for a Wi-Fi 7 client.</param>
    /// <param name="ssid">Restricts candidates to this SSID so a client is never steered onto a
    /// different network. Null offers every SSID the neighbors report.</param>
    /// <param name="intent">Whether to offer other access points or other bands on this one.</param>
    /// <param name="ct">Cancellation token.</param>
    [RequireRole(Roles.Operator)]
    [AuditAction(AuditActions.ApAgentClientSteered, TargetType = "client")]
    Task<ApAgentRoamResult> RequestRoamAsync(
        string clientMac, string? ssid = null,
        ApAgentRoamIntent intent = ApAgentRoamIntent.AccessPoint, CancellationToken ct = default);

    /// <summary>
    /// Whether steering is available: the feature is on, at least two access points are running the
    /// AP Agent, and - when a client is named - that client has roamed before.
    ///
    /// The client check is a safety gate, not a nicety. hostapd exposes only
    /// <c>wnm_disassoc_imminent</c>, so every steer is an eviction with a timer rather than a
    /// suggestion, and nothing reports whether a client supports BSS Transition: mca-dump carries
    /// is_11r but no 11v equivalent. A device that cannot act on the request is disassociated
    /// anyway, and one was observed never rejoining any SSID afterwards. A prior roam is the only
    /// evidence we have that a client survives being moved.
    /// </summary>
    /// <param name="clientMac">Client to check, or null for the site-level answer alone.</param>
    [RequireRole(Roles.Viewer)]
    Task<bool> IsAvailableAsync(string? clientMac = null, CancellationToken ct = default);

    /// <summary>
    /// Whether a band move is worth offering: we have seen this client on a band better than the one
    /// it is on now. A client already at its best band declines every candidate and returns to where
    /// it started, so asking costs a disconnection and achieves nothing.
    ///
    /// Observed bands undercount capability - a 6 GHz-capable client that has never had reason to
    /// use it reads as 5 GHz-only. That is accepted: it will associate on 6 GHz by itself eventually
    /// and be recorded then.
    /// </summary>
    /// <param name="clientMac">Client to check.</param>
    /// <param name="currentBand">Band it is on now, in either the agent's ("5") or UniFi's ("na") spelling.</param>
    [RequireRole(Roles.Viewer)]
    Task<bool> CanChangeBandAsync(string clientMac, string? currentBand, CancellationToken ct = default);
}
