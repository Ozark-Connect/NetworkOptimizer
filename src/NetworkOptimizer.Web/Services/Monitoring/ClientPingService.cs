using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Monitoring.Probes;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>
/// <see cref="IClientPingService"/> over the latency probes' own executors. The vantage is
/// <see cref="ProbeExecutorFactory.GetServer"/>'s: this server where it probes the site, else the site's collector
/// On-Site Agent, which is run by id so the result and its label name the same agent.
/// </summary>
public sealed class ClientPingService : IClientPingService
{
    private const string ServerName = "Network Optimizer server";
    private const string AgentKind = "On-Site Agent";

    private readonly ProbeExecutorFactory _executors;
    private readonly AgentTunnelRegistry _tunnels;
    private readonly AgentProbeResultSink _probeSink;
    private readonly IDbContextFactory<NetworkOptimizerDbContext> _mainDb;
    private readonly SiteContextService _site;
    private readonly NetworkOptimizer.Storage.Services.MonitoringInfluxClient _influx;
    private readonly ILogger<ClientPingService> _logger;

    // Kept while pings run (the page reloads on a site switch); dropped whenever one could not run.
    private (IProbeExecutor Executor, string Vantage)? _vantage;

    public ClientPingService(
        ProbeExecutorFactory executors,
        AgentTunnelRegistry tunnels,
        AgentProbeResultSink probeSink,
        IDbContextFactory<NetworkOptimizerDbContext> mainDb,
        SiteContextService site,
        NetworkOptimizer.Storage.Services.MonitoringInfluxClient influx,
        ILogger<ClientPingService> logger)
    {
        _executors = executors;
        _tunnels = tunnels;
        _probeSink = probeSink;
        _mainDb = mainDb;
        _site = site;
        _influx = influx;
        _logger = logger;
    }

    public async Task<ClientPingResult> PingAsync(string clientIp, ClientPingSeries? series = null, CancellationToken ct = default)
    {
        var (executor, vantage) = _vantage ??= await ResolveVantageAsync(ct);
        try
        {
            // No source bind: a bound source would leave by a WAN rather than reach the LAN Client.
            var result = await executor.PingAsync(new ProbeTarget(clientIp, ProbeMode.Icmp),
                count: 1, perPingTimeout: TimeSpan.FromSeconds(1), ct: ct);
            var ping = new ClientPingResult(result.Sent > 0, result.Received > 0 ? result.RttAvgMs : null, vantage);
            // Nothing could send it (the agent is offline, or another took over collecting): look the vantage up
            // again next time, so the ping and its name follow whichever agent comes back.
            if (!ping.Ran) _vantage = null;
            // A ping nothing could send says nothing about the Client, so only one that ran is recorded.
            if (ping.Ran && series != null)
                await _influx.WriteClientPingAsync(series.ClientMac, series.Wired, series.ParentMac, series.Band,
                    series.Port, ping.RttMs, vantage, DateTime.UtcNow);
            return ping;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Client ping to {Ip} on site {Site} could not run", clientIp, _site.Slug);
            _vantage = null;
            return new ClientPingResult(false, null, vantage);
        }
    }

    private async Task<(IProbeExecutor, string)> ResolveVantageAsync(CancellationToken ct)
    {
        if (!_executors.ServerVantageIsAgent) return (_executors.GetServer(), ServerName);

        var slug = _site.Slug;
        var connections = _tunnels.GetForSite(slug);
        var agentId = await _probeSink.GetCollectorAgentIdAsync(slug, ct)
            ?? connections.OrderBy(c => c.AgentId).Select(c => (int?)c.AgentId).FirstOrDefault();
        if (agentId is not int id) return (_executors.GetServer(), AgentKind);

        string? name = null;
        try
        {
            await using var db = await _mainDb.CreateDbContextAsync(ct);
            name = await db.SiteAgents.Where(a => a.Id == id).Select(a => a.Name).FirstOrDefaultAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Agent name unavailable for agent {AgentId}", id);
        }
        if (string.IsNullOrWhiteSpace(name)) name = connections.FirstOrDefault(c => c.AgentId == id)?.AgentName;
        return (_executors.ForAgent(id), string.IsNullOrWhiteSpace(name) ? AgentKind : $"{name} ({AgentKind})");
    }
}
