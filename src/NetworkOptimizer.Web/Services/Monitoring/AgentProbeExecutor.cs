using System.Text.Json;
using NetworkOptimizer.AgentProtocol;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Monitoring.Probes;

namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>
/// An <see cref="IProbeExecutor"/> that runs ping/traceroute FROM a secondary site's
/// agent host, over the tunnel. This is the on-site equivalent of the server-vantage
/// <see cref="LocalProbeExecutor"/> on the home site: the agent runs the identical
/// LocalProbeExecutor and returns the result as JSON, so Network Tools and Upstream
/// Discovery get a path that originates on the site's own network (first hop = the
/// site's gateway, filtered exactly as on home). Both sides use default JSON options
/// so the records round-trip.
/// </summary>
public sealed class AgentProbeExecutor : IProbeExecutor
{
    private readonly AgentProbeService _agentProbe;
    private readonly string _siteSlug;
    private readonly ILogger _logger;

    public AgentProbeExecutor(AgentProbeService agentProbe, string siteSlug, ILogger logger)
    {
        _agentProbe = agentProbe;
        _siteSlug = siteSlug;
        _logger = logger;
    }

    public ProbeVantage Vantage { get; } = new("agent", VantageKind.Server);

    public Task<ProbeCapability> GetCapabilityAsync(CancellationToken ct = default) =>
        Task.FromResult(new ProbeCapability
        {
            CanIcmpPing = true,
            CanIcmpTraceroute = true,
            CanUdpTraceroute = true,
            CanTcpProbe = true,
            IsBusyBoxPing = false,
            IsBusyBoxTraceroute = false,
        });

    public async Task<PingProbeResult> PingAsync(ProbeTarget target, int count = 10, TimeSpan? perPingTimeout = null, CancellationToken ct = default)
    {
        var request = BuildRequest(target, traceroute: false, count: count, maxHops: 0);
        var resp = await _agentProbe.RunAsync(_siteSlug, request, TimeSpan.FromSeconds(count * 3 + 15), ct);
        if (resp == null) return FailedPing(target, "No on-site agent is online to run the probe");
        if (!resp.Success || string.IsNullOrEmpty(resp.ResultJson))
            return FailedPing(target, string.IsNullOrEmpty(resp.Error) ? "Agent probe failed" : resp.Error);
        try
        {
            return JsonSerializer.Deserialize<PingProbeResult>(resp.ResultJson)
                   ?? FailedPing(target, "Agent returned an unreadable ping result");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to parse agent ping result for site {Slug}", _siteSlug);
            return FailedPing(target, $"Couldn't parse the agent ping result: {ex.Message}");
        }
    }

    public async Task<TracerouteResult> TracerouteAsync(ProbeTarget target, int maxHops = 30, TimeSpan? perHopTimeout = null, TimeSpan? totalDeadline = null, CancellationToken ct = default)
    {
        var request = BuildRequest(target, traceroute: true, count: 0, maxHops: maxHops);
        var resp = await _agentProbe.RunAsync(_siteSlug, request, TimeSpan.FromSeconds(30), ct);
        if (resp == null) return FailedTrace(target, "No on-site agent is online to run the traceroute");
        if (!resp.Success || string.IsNullOrEmpty(resp.ResultJson))
            return FailedTrace(target, string.IsNullOrEmpty(resp.Error) ? "Agent traceroute failed" : resp.Error);
        try
        {
            return JsonSerializer.Deserialize<TracerouteResult>(resp.ResultJson)
                   ?? FailedTrace(target, "Agent returned an unreadable traceroute result");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to parse agent traceroute result for site {Slug}", _siteSlug);
            return FailedTrace(target, $"Couldn't parse the agent traceroute result: {ex.Message}");
        }
    }

    public async Task<TcpProbeResult> TcpProbeAsync(ProbeTarget target, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        // A single TCP-connect probe via a 1-count TCP ping on the agent.
        var ping = await PingAsync(target with { Mode = ProbeMode.Tcp }, count: 1, ct: ct);
        return new TcpProbeResult
        {
            Target = target,
            Vantage = Vantage,
            Connected = ping.Received > 0,
            ConnectTimeMs = ping.RttAvgMs,
            Timestamp = ping.Timestamp,
            ErrorMessage = ping.ErrorMessage,
        };
    }

    private ProbeRequest BuildRequest(ProbeTarget target, bool traceroute, int count, int maxHops) => new()
    {
        Address = target.Address,
        Mode = target.Mode.ToString().ToLowerInvariant(),
        Port = target.Port ?? 0,
        SourceIp = target.SourceInterface ?? "",
        Traceroute = traceroute,
        Count = count,
        MaxHops = maxHops,
    };

    private PingProbeResult FailedPing(ProbeTarget target, string error) => new()
    {
        Target = target,
        Vantage = Vantage,
        Sent = 0,
        Received = 0,
        Timestamp = DateTime.UtcNow,
        ErrorMessage = error,
    };

    private TracerouteResult FailedTrace(ProbeTarget target, string error) => new()
    {
        Target = target,
        Vantage = Vantage,
        ModeUsed = target.Mode,
        Timestamp = DateTime.UtcNow,
        Hops = Array.Empty<TraceHop>(),
        ErrorMessage = error,
    };
}
