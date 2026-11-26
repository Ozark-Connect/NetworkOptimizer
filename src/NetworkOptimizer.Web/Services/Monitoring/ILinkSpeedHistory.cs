using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Gates;

namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>
/// Whether a client's switch port has linked faster than it does now while that client was on it: the evidence a
/// 10/100 Mbps note needs to be a fault rather than the device's own speed. Viewer: it decides what the path notes
/// say on Client Performance and speed test results, both open to any role.
/// </summary>
[MutatingService(SiteScoped = true)]
public interface ILinkSpeedHistory
{
    /// <summary>
    /// True when, within the last 30 days, the port linked faster than <paramref name="mbps"/> in an hour this client
    /// was on it. False with no such hour, and with no record to say (InfluxDB off, or the speed not yet recorded).
    /// </summary>
    [RequireRole(Roles.Viewer)]
    Task<bool> RanFasterWithClientAsync(string switchMac, int port, string clientMac, int mbps, CancellationToken ct = default);
}
