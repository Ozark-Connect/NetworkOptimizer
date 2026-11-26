using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Gates;

namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>One ping of a Client, and where it was sent from.</summary>
/// <param name="Ran">False when nothing could send it (no On-Site Agent online): not the same as no reply.</param>
/// <param name="RttMs">The round trip, or null with no reply.</param>
/// <param name="Vantage">Where the ping came from, as a reader names it ("Network Optimizer server").</param>
public sealed record ClientPingResult(bool Ran, double? RttMs, string Vantage);

/// <summary>Where a Client's ping is recorded: its own Wi-Fi or wired series.</summary>
/// <param name="ClientMac">The Client.</param>
/// <param name="Wired">Wired (switch and port) or Wi-Fi (access point and band).</param>
/// <param name="ParentMac">The access point or switch it is on.</param>
/// <param name="Band">The Wi-Fi band tag ("5ghz"), for a Wi-Fi Client.</param>
/// <param name="Port">The switch port, for a wired Client.</param>
public sealed record ClientPingSeries(string ClientMac, bool Wired, string ParentMac, string? Band, int? Port);

/// <summary>
/// Pings a Client from the vantage the site's scheduled LAN probes use, for Client Performance's live round
/// trip, and records each reading on the Client's own series. Viewer: Client Performance is open to any role,
/// and a ping changes nothing.
/// </summary>
[MutatingService(SiteScoped = true)]
public interface IClientPingService
{
    /// <summary>
    /// One echo to the Client at this address, with a 1 second timeout. With <paramref name="series"/>, a ping that
    /// ran is recorded there (round trip, or no reply).
    /// </summary>
    [RequireRole(Roles.Viewer)]
    Task<ClientPingResult> PingAsync(string clientIp, ClientPingSeries? series = null, CancellationToken ct = default);
}
