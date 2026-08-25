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
    /// Asks the client to leave whichever access point currently holds it, offering every other
    /// access point as a candidate. Returns once the request is sent: 802.11v is a request, so
    /// where the client lands arrives afterwards as a roam event.
    /// </summary>
    /// <param name="clientMac">Client MAC, or the MLD MAC for a Wi-Fi 7 client.</param>
    /// <param name="ssid">Restricts candidates to this SSID so a client is never steered onto a
    /// different network. Null offers every SSID the neighbors report.</param>
    /// <param name="ct">Cancellation token.</param>
    [RequireRole(Roles.Operator)]
    [AuditAction(AuditActions.ApAgentClientSteered, TargetType = "client")]
    Task<ApAgentRoamResult> RequestRoamAsync(string clientMac, string? ssid = null, CancellationToken ct = default);

    /// <summary>
    /// Whether steering is available on this site: the feature is on and at least two access points
    /// are running the AP Agent. Drives whether the control is offered at all.
    /// </summary>
    [RequireRole(Roles.Viewer)]
    Task<bool> IsAvailableAsync(CancellationToken ct = default);
}
