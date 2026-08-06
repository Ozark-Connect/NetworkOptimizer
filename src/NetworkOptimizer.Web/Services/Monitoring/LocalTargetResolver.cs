using System.Net;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Core.Helpers;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>
/// Settles whether a target sits on the local network, once, and writes the answer down.
/// <para>
/// A literal address answers itself. A hostname does not - "cloudkey.local" and
/// "speedtest.example.net" are the same shape - so it is resolved to the address it actually
/// reaches and judged on that. Kept because it almost never changes and because the alternative is
/// a DNS lookup on every read of a column three different features consult.
/// </para>
/// </summary>
public static class LocalTargetResolver
{
    /// <summary>How many unresolved targets one sweep will look up, so a site with a long list of
    /// dead hostnames cannot spend a whole tick on DNS.</summary>
    private const int SweepBatchSize = 25;

    /// <summary>
    /// Whether a target is on the local network: what was resolved if anything was, otherwise what
    /// the address itself says. Fabric is local by what it is, whatever address it wears.
    /// </summary>
    public static bool IsLocal(MonitoringTarget target) =>
        IsLocal(target.TargetType, target.Address, target.IsLocal);

    /// <summary>
    /// The same question from loose fields, for callers projecting columns rather than loading
    /// entities.
    /// </summary>
    /// <param name="targetType">The target's type; Fabric is local whatever its address.</param>
    /// <param name="address">The configured address.</param>
    /// <param name="isLocal">The resolved answer, or null when nothing has settled it.</param>
    public static bool IsLocal(MonitoringTargetType targetType, string? address, bool? isLocal) =>
        targetType == MonitoringTargetType.Fabric
        || (isLocal ?? NetworkUtilities.IsPrivateIpAddress(address ?? string.Empty));

    /// <summary>
    /// Resolves one address to a local/not-local answer and the address it answered on, or nulls
    /// when DNS cannot say. Never throws: an unanswerable name leaves the target unresolved rather
    /// than wrongly settled. The resolved IP comes back so callers can say WHY in a log - a wrong
    /// verdict is almost always a surprising IP rather than a bad rule.
    /// </summary>
    /// <param name="address">The target's address, a literal or a hostname.</param>
    /// <param name="logger">Optional, for reporting a lookup that failed outright.</param>
    /// <param name="ct">Cancellation.</param>
    public static async Task<(bool? IsLocal, string? ResolvedIp)> ResolveAsync(
        string? address, ILogger? logger = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(address)) return (null, null);
        var name = address.Trim();
        try
        {
            // A literal answers itself; only a name needs asking.
            if (!IPAddress.TryParse(name, out var literal))
            {
                var answers = await Dns.GetHostAddressesAsync(name, ct);
                // IPv4 first. Locality is judged on the RFC1918 ranges, so an AAAA answer taken
                // just because DNS listed it first would be judged by a rule that cannot see it.
                literal = answers.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                    ?? answers.FirstOrDefault();
            }

            // "::" and "0.0.0.0" are what a resolver hands back when the name expanded to
            // something with no address of its own - a search-domain suffix that exists but
            // answers nothing. They parse as addresses and are not any host, so judging them
            // would quietly file a LAN device as reached over a WAN.
            if (literal is null || literal.Equals(IPAddress.Any) || literal.Equals(IPAddress.IPv6Any))
            {
                logger?.LogDebug(
                    "Local check: {Address} did not resolve to a usable address ({Answer}), so whether it "
                    + "is local is still unknown", address, literal?.ToString() ?? "no answer");
                return (null, null);
            }

            var ip = literal.ToString();
            return (NetworkUtilities.IsPrivateIpAddress(literal), ip);
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "Local check: looking up {Address} failed; leaving it unresolved", address);
            return (null, null);
        }
    }

    /// <summary>
    /// Resolves targets that have no answer yet. Returns how many were settled. Only touches nulls,
    /// so it costs nothing once a site is resolved and needs no run-once bookkeeping. Caller saves.
    /// </summary>
    /// <param name="db">The site's database.</param>
    /// <param name="ct">Cancellation.</param>
    public static async Task<int> SweepUnresolvedAsync(
        NetworkOptimizerDbContext db, ILogger? logger = null, CancellationToken ct = default)
    {
        var pending = await db.MonitoringTargets
            .Where(t => t.IsLocal == null)
            .OrderBy(t => t.Id)
            .Take(SweepBatchSize)
            .ToListAsync(ct);
        if (pending.Count == 0) return 0;

        var settled = 0;
        foreach (var target in pending)
        {
            if (ct.IsCancellationRequested) break;
            var (answer, ip) = await ResolveAsync(target.Address, logger, ct);
            if (answer == null) continue;
            target.IsLocal = answer;
            settled++;
            // Per target, because this is the moment a target changes which side of the LAN/WAN
            // line it falls on - and that decides where it appears, whether a vantage may adopt
            // it, and whether a metered WAN may slow it. A count alone cannot answer "why is this
            // one over there".
            logger?.LogInformation(
                "Local check: {Name} ({Address}) resolved to {Ip} - {Verdict}",
                target.Name, target.Address, ip, answer.Value ? "on this network" : "reached over a WAN");
        }
        return settled;
    }
}
