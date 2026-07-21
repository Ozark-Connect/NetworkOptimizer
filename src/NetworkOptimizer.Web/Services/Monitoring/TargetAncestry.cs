using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Monitoring.Probes;
using NetworkOptimizer.Storage;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>
/// Shared trace-and-persist for a monitored target's upstream hop ancestry. Used by both the
/// on-save fast path (LatencyTargetsCard) and the periodic backfill (UpstreamTracerService via the
/// re-discovery tick). Custom/Internet targets aren't part of the discovery sweep, so this is what
/// gives them the ancestry that lets ISP Health use them as routes-through witnesses.
/// </summary>
public static class TargetAncestry
{
    /// <summary>
    /// Traces <paramref name="address"/> over the given site vantage executor (server or on-site
    /// agent) and upserts the ordered responding hops before the destination as the target's
    /// UpstreamDiscovery ancestry. Returns true when ancestry was persisted. Best-effort: swallows
    /// failures (the caller retries on the next cycle) and returns false when no hops respond.
    /// </summary>
    public static async Task<bool> TraceAndPersistAsync(
        IProbeExecutor executor,
        NetworkOptimizerDbContext db,
        int targetId,
        string address,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        try
        {
            var result = await executor.TracerouteAsync(
                new ProbeTarget(address, ProbeMode.Icmp),
                maxHops: 30,
                perHopTimeout: TimeSpan.FromSeconds(2),
                totalDeadline: TimeSpan.FromSeconds(20),
                ct: ct);

            // Ordered responding hops before the destination = the ancestry it routes through.
            var ancestors = result.Hops
                .Where(h => h.Responded && !string.Equals(h.Address, address, StringComparison.OrdinalIgnoreCase))
                .Select(h => h.Address!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (ancestors.Count == 0) return false;

            var existing = await db.UpstreamDiscoveries.FirstOrDefaultAsync(d => d.MonitoringTargetId == targetId, ct);
            if (existing == null)
            {
                db.UpstreamDiscoveries.Add(new UpstreamDiscovery
                {
                    MonitoringTargetId = targetId,
                    HopIp = address,
                    HopNumber = ancestors.Count + 1,
                    AncestorHopIps = string.Join(" ", ancestors),
                    Role = UpstreamRole.PathProxy,
                    IsActive = true,
                    LastValidated = DateTime.UtcNow,
                    LastTracerouteAt = DateTime.UtcNow
                });
            }
            else
            {
                existing.AncestorHopIps = string.Join(" ", ancestors);
                existing.HopNumber = ancestors.Count + 1;
                existing.LastTracerouteAt = DateTime.UtcNow;
                existing.LastValidated = DateTime.UtcNow;
                existing.IsActive = true;
            }
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "Ancestry trace failed for target {TargetId} ({Address})", targetId, address);
            return false;
        }
    }
}
