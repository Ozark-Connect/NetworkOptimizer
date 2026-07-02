using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.AgentProtocol;
using NetworkOptimizer.Storage.Services;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Bridges agent probing to the monitoring pipeline: pushes the site's enabled
/// monitoring targets to an agent when it connects (and on periodic refresh),
/// and persists the probe results the agent streams back - Influx latency
/// points, live stats, and target LastVerified, mirroring the server's own
/// latency tier. Split out of the tunnel handler so the transport stays free
/// of storage concerns.
/// </summary>
public class AgentProbeResultSink
{
    private readonly SiteDbContextFactory _siteDbFactory;
    private readonly MonitoringInfluxRegistry _influxRegistry;
    private readonly MonitoringLiveStats _liveStats;
    private readonly ILogger<AgentProbeResultSink> _logger;

    public AgentProbeResultSink(
        SiteDbContextFactory siteDbFactory,
        MonitoringInfluxRegistry influxRegistry,
        MonitoringLiveStats liveStats,
        ILogger<AgentProbeResultSink> logger)
    {
        _siteDbFactory = siteDbFactory;
        _influxRegistry = influxRegistry;
        _liveStats = liveStats;
        _logger = logger;
    }

    /// <summary>Called once per connection after the hello exchange.</summary>
    public Task OnAgentConnectedAsync(AgentTunnelConnection connection, CancellationToken ct) =>
        PushProbeConfigAsync(connection, ct);

    /// <summary>
    /// Sends the site's enabled monitoring targets to the agent as a full
    /// replacement set. Also invoked periodically by the tunnel handler so
    /// target edits reach connected agents without a reconnect.
    /// </summary>
    public async Task PushProbeConfigAsync(AgentTunnelConnection connection, CancellationToken ct)
    {
        try
        {
            var isDefault = connection.SiteSlug == SiteManagementService.DefaultSiteSlug;
            await using var db = _siteDbFactory.CreateForSite(connection.SiteSlug, isDefault);
            var targets = await db.MonitoringTargets
                .AsNoTracking()
                .Where(t => t.Enabled)
                .ToListAsync(ct);

            var config = new ProbeConfig();
            foreach (var target in targets)
            {
                config.Targets.Add(new ProbeTargetSpec
                {
                    TargetId = target.TargetId,
                    Address = target.Address,
                    ProbeMode = target.ProbeMode.ToString().ToLowerInvariant(),
                    Port = target.Port ?? 0,
                    PollIntervalSeconds = target.PollIntervalSeconds,
                    PingCount = target.PingCount,
                    TargetType = target.TargetType.ToString().ToLowerInvariant(),
                });
            }

            connection.TrySend(new ServerMessage { ProbeConfig = config });
            _logger.LogInformation("Pushed {Count} probe target(s) to agent {Id} (site {Slug})",
                config.Targets.Count, connection.AgentId, connection.SiteSlug);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to push probe config to agent {Id} (site {Slug})",
                connection.AgentId, connection.SiteSlug);
        }
    }

    /// <summary>Records a batch of probe results from an agent.</summary>
    public async Task RecordBatchAsync(AgentTunnelConnection connection, ProbeResultBatch batch, CancellationToken ct)
    {
        if (batch.Results.Count == 0) return;

        var isDefault = connection.SiteSlug == SiteManagementService.DefaultSiteSlug;
        await using var db = _siteDbFactory.CreateForSite(connection.SiteSlug, isDefault);
        var ids = batch.Results.Select(r => r.TargetId).Distinct().ToList();
        var targets = await db.MonitoringTargets
            .Where(t => ids.Contains(t.TargetId))
            .ToDictionaryAsync(t => t.TargetId, ct);

        // Distinguishes agent probes from the server's own "server" vantage in
        // the latency measurement; stable across agent renames.
        var vantage = $"agent-{connection.AgentId}";

        // Each site writes to its own buckets (decision D1); the site's client
        // configures itself from that site's MonitoringSettings on first use.
        var influx = _influxRegistry.GetFor(connection.SiteSlug);
        if (!influx.IsConfigured) await influx.ReconfigureAsync(ct);

        foreach (var result in batch.Results)
        {
            if (!targets.TryGetValue(result.TargetId, out var target))
            {
                _logger.LogDebug("Agent {Id} sent result for unknown target {Target}", connection.AgentId, result.TargetId);
                continue;
            }

            var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(result.TimestampUnixMs).UtcDateTime;

            await influx.WriteLatencyAsync(
                targetId: target.TargetId,
                vantagePoint: vantage,
                targetType: target.TargetType,
                probeMode: target.ProbeMode,
                rttMinMs: result.HasRttMinMs ? result.RttMinMs : null,
                rttAvgMs: result.HasRttAvgMs ? result.RttAvgMs : null,
                rttMaxMs: result.HasRttMaxMs ? result.RttMaxMs : null,
                jitterMs: result.HasJitterMs ? result.JitterMs : null,
                lossPercent: result.LossPercent,
                success: result.Success,
                sent: result.Sent,
                received: result.Received,
                timestamp: timestamp);

            // Live stats back the default site's dashboards only; per-site live
            // views come with the background fan-out work.
            if (isDefault)
            {
                _liveStats.RecordTargetProbe(
                    target.TargetId,
                    result.HasRttAvgMs ? result.RttAvgMs : null,
                    result.LossPercent,
                    result.Success,
                    timestamp);
            }

            if (result.Success)
                target.LastVerified = timestamp;
        }

        await db.SaveChangesAsync(ct);
    }
}
