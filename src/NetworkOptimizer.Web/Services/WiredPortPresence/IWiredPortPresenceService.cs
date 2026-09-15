using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Gates;

namespace NetworkOptimizer.Web.Services.WiredPortPresence;

/// <summary>
/// Which wired clients are on a switch port by the port's link, on the site in context. Read by
/// the surfaces that draw clients (Client Performance, its picker, the maps) so a client the
/// console dropped while its port stayed up is shown online like any other. Viewer, because
/// every reader is open to any role.
/// </summary>
[MutatingService(SiteScoped = true)]
public interface IWiredPortPresenceService
{
    /// <summary>The port one client is present on, or null when the console lists it or nothing vouches for it.</summary>
    [RequireRole(Roles.Viewer)]
    Task<WiredPortPresence?> ResolveAsync(string clientMac);

    /// <summary>Every client present by port on this site.</summary>
    [RequireRole(Roles.Viewer)]
    Task<IReadOnlyList<WiredPortPresence>> ListAsync();

    /// <summary>
    /// Whether the port the console lists this wired client on is down by the switch's own last
    /// SNMP sample. The console keeps a wired client listed for minutes after its link drops; a
    /// down port is nobody's, so the client is offline. False when nothing monitors the port.
    /// </summary>
    [RequireRole(Roles.Viewer)]
    Task<bool> IsLinkDownAsync(string clientMac);
}
