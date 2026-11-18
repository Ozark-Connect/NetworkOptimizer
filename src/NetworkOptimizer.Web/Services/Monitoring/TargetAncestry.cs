using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Core.Helpers;
using NetworkOptimizer.Monitoring.Probes;
using NetworkOptimizer.Storage;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services.Monitoring.PathLocalization;
using NetworkOptimizer.Web.Services.Monitoring.PathMap;

namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>What one ancestry trace stored.</summary>
/// <param name="Persisted">Whether ancestry was written.</param>
/// <param name="Route">The route the trace recorded; null when nothing answered.</param>
/// <param name="Shift">The path shift the trace confirmed, when it confirmed one.</param>
public sealed record AncestryTrace(bool Persisted, StoredRoute? Route, RouteShift? Shift);

/// <summary>
/// Shared trace-and-persist for a monitored target's upstream hop ancestry and its route. Every writer
/// goes through <see cref="PathRouteRecorder"/>, which traces from the vantage and bind that measure
/// the target: a route from anywhere else describes another path. Custom/Internet targets aren't part
/// of the discovery sweep, so this is what gives them the ancestry that lets ISP Health use them as
/// routes-through witnesses, and the route the Path Map draws.
/// </summary>
public static class TargetAncestry
{
    /// <summary>
    /// Traces <paramref name="address"/> over <paramref name="executor"/>, bound as <paramref name="vantage"/>
    /// says, and upserts the target's UpstreamDiscovery row: the ordered responding hops before the
    /// destination as its ancestry, and the full route with its path-shift state. Best-effort: swallows
    /// failures (the caller retries on the next cycle).
    /// </summary>
    /// <param name="siteSlug">The site the trace runs for: its traces use its own cities set by hand.</param>
    /// <param name="placer">Places the route for its path-shift signature; without one, no shift is detected.</param>
    /// <param name="witness">An Internet/Custom target, whose ancestry this writes. An ISP or Transit target
    /// keeps the ancestry discovery gave it, and gets its route only when discovery made it a row.</param>
    public static async Task<AncestryTrace> TraceAsync(
        IProbeExecutor executor,
        NetworkOptimizerDbContext db,
        string siteSlug,
        int targetId,
        string address,
        PathVantage vantage,
        RoutePlacer? placer,
        bool witness,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        try
        {
            var result = await executor.TracerouteAsync(
                new ProbeTarget(address, ProbeMode.Icmp, null, vantage.SourceInterface),
                maxHops: 30,
                perHopTimeout: TimeSpan.FromSeconds(2),
                totalDeadline: TimeSpan.FromSeconds(20),
                probesPerHop: 3,
                ct: ct);

            // A hostname target's destination is the address its name resolved to for this trace.
            var destination = System.Net.IPAddress.TryParse(address, out _) ? address : result.ResolvedAddress;
            bool IsDestination(string? a) => a != null
                && (string.Equals(a, address, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(a, destination, StringComparison.OrdinalIgnoreCase));

            // Ordered responding hops before the destination = the ancestry it routes through.
            var ancestors = result.Hops
                .Where(h => h.Responded && !IsDestination(h.Address))
                .Select(h => h.Address!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            // A router answering at hop 1 (the site's own upstream gateway) has no ancestry but still has a route:
            // without one the Path Map's trace loop waited on it forever.
            var reached = result.Hops.Any(h => h.Responded && IsDestination(h.Address));
            if (ancestors.Count == 0 && !reached) return new AncestryTrace(false, null, null);

            var route = await RecordRouteAsync(result, vantage, destination, siteSlug, placer, ct);

            var existing = await db.UpstreamDiscoveries.FirstOrDefaultAsync(d => d.MonitoringTargetId == targetId, ct);
            if (existing == null && ancestors.Count == 0) return new AncestryTrace(false, route, null);
            if (existing == null)
            {
                // Only a witness (Internet/Custom) target gets a row of its own: a discovery row
                // carries the hop distances the localizer orders by.
                if (!witness) return new AncestryTrace(false, route, null);
                existing = new UpstreamDiscovery
                {
                    MonitoringTargetId = targetId,
                    HopIp = address,
                    Role = UpstreamRole.PathProxy,
                };
                db.UpstreamDiscoveries.Add(existing);
            }
            // A discovery hop's ancestry lists the monitored hops before it, as discovery saw them:
            // its route is stored beside it, and the ancestry is left alone.
            if (existing.Role == UpstreamRole.PathProxy && ancestors.Count > 0)
            {
                existing.AncestorHopIps = string.Join(" ", ancestors);
                existing.HopNumber = ancestors.Count + 1;
                existing.LastTracerouteAt = DateTime.UtcNow;
                existing.LastValidated = DateTime.UtcNow;
                existing.IsActive = true;
            }

            var (state, shift) = StoredRoutes.Advance(StoredRoutes.ReadState(existing.RouteJson), route);
            // The signatures carry each trace's own placement: a hop placed better since is not a route change.
            if (shift != null && placer != null)
            {
                var overrides = await placer.OverridesAsync(siteSlug, ct);
                if (!RouteChanges.IsReal(await placer.PlaceAsync(shift.From, overrides, ct), await placer.PlaceAsync(shift.To, overrides, ct)))
                    shift = null;
            }
            existing.RouteJson = StoredRoutes.Write(state);
            if (shift != null)
                existing.RouteShiftsJson = StoredRoutes.Write(StoredRoutes.Append(StoredRoutes.ReadShifts(existing.RouteShiftsJson), shift));

            await db.SaveChangesAsync(ct);
            return new AncestryTrace(true, route, shift);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger?.LogDebug(ex, "Ancestry trace failed for target {TargetId} ({Address})", targetId, address);
            return new AncestryTrace(false, null, null);
        }
    }

    /// <summary>
    /// The trace as a stored route: hostnames the vantage did not name are looked up from this server,
    /// whose resolver can hold a name the vantage's does not, then the route is placed for its signature.
    /// </summary>
    private static async Task<StoredRoute> RecordRouteAsync(TracerouteResult result, PathVantage vantage, string? destination,
        string siteSlug, RoutePlacer? placer, CancellationToken ct)
    {
        var ptrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hop in result.Hops.Where(h => h.Responded && string.IsNullOrEmpty(h.Hostname)
                     && NetworkUtilities.IsPublicIpAddress(h.Address!)).Select(h => h.Address!).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if ((await ReverseDnsCache.ResolveAsync(hop, ct)).Hostname is { } name) ptrs[hop] = name;
        }

        var route = StoredRoutes.FromTrace(result, vantage.Label, vantage.SourceInterface, destination, ptrs, signature: null);
        if (placer == null) return route;
        var placed = await placer.PlaceAsync(route, await placer.OverridesAsync(siteSlug, ct), ct);
        return route with { Signature = RoutePlacer.Signature(placed) };
    }
}
