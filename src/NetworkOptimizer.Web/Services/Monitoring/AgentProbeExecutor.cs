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
    private readonly int? _agentId;

    /// <summary>
    /// Builds an executor for a site's agent vantage.
    /// </summary>
    /// <param name="agentProbe">Tunnel probe service.</param>
    /// <param name="siteSlug">Site whose agent runs the probes.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="agentId">
    /// Which of the site's agents to run on. Null means the site's agent in the singular - the
    /// original behavior, and what every path that just wants an on-site origin needs. A named
    /// agent is a specific vantage (a WAN context's), so it is never quietly swapped for another.
    /// </param>
    public AgentProbeExecutor(AgentProbeService agentProbe, string siteSlug, ILogger logger, int? agentId = null)
    {
        _agentProbe = agentProbe;
        _siteSlug = siteSlug;
        _logger = logger;
        _agentId = agentId;
        Vantage = agentId is int id ? new($"agent:{id}", VantageKind.Server) : new("agent", VantageKind.Server);
    }

    public ProbeVantage Vantage { get; }

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
        var resp = await _agentProbe.RunAsync(_siteSlug, request, TimeSpan.FromSeconds(count * 3 + 15), ct, _agentId);
        if (resp == null) return FailedPing(target, "No on-site agent is online to run the probe");
        if (!resp.Success || string.IsNullOrEmpty(resp.ResultJson))
            return FailedPing(target, string.IsNullOrEmpty(resp.Error) ? "Agent probe failed" : resp.Error);
        try
        {
            var parsed = JsonSerializer.Deserialize<PingProbeResult>(resp.ResultJson);
            return parsed == null ? FailedPing(target, "Agent returned an unreadable ping result") : Attribute(parsed);
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
        var resp = await _agentProbe.RunAsync(_siteSlug, request, TimeSpan.FromSeconds(30), ct, _agentId);
        if (resp == null) return FailedTrace(target, "No on-site agent is online to run the traceroute");
        if (!resp.Success || string.IsNullOrEmpty(resp.ResultJson))
            return FailedTrace(target, string.IsNullOrEmpty(resp.Error) ? "Agent traceroute failed" : resp.Error);
        try
        {
            var parsed = JsonSerializer.Deserialize<TracerouteResult>(resp.ResultJson);
            return parsed == null ? FailedTrace(target, "Agent returned an unreadable traceroute result") : Attribute(parsed);
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

    /// <summary>
    /// Names the vantage a NAMED agent's result came from. The agent runs the same
    /// LocalProbeExecutor the server does, so its result arrives calling itself the "server"
    /// vantage - which reads as this server on a site where the server also probes, and a probe
    /// picked out by agent has to say which agent ran it. Left untouched for the unnamed
    /// executor, where "server" is exactly what the site's single agent vantage has always
    /// reported.
    /// </summary>
    private PingProbeResult Attribute(PingProbeResult result) =>
        _agentId == null ? result : result with { Vantage = Vantage };

    /// <inheritdoc cref="Attribute(PingProbeResult)" />
    private TracerouteResult Attribute(TracerouteResult result) =>
        _agentId == null ? result : result with { Vantage = Vantage };

    /// <inheritdoc/>
    /// <remarks>
    /// An agent predating the dns verb ignores Kind and runs a ping, so its result arrives with
    /// no Kind marker. That is reported as an agent that needs updating - showing the ping's
    /// empty address list as a DNS answer would read as "this name resolves to nothing."
    /// </remarks>
    public async Task<DnsLookupResult> LookupAsync(
        ProbeTarget target,
        bool reverse = false,
        CancellationToken ct = default)
    {
        var request = BuildRequest(target, traceroute: false, count: 0, maxHops: 0);
        request.Kind = "dns";
        request.Reverse = reverse;

        var resp = await _agentProbe.RunAsync(_siteSlug, request, TimeSpan.FromSeconds(30), ct, _agentId);
        if (resp == null) return FailedLookup(target, "No on-site agent is online to run the lookup");
        if (!resp.Success || string.IsNullOrEmpty(resp.ResultJson))
            return FailedLookup(target, string.IsNullOrEmpty(resp.Error) ? "Agent lookup failed" : resp.Error);

        try
        {
            var parsed = JsonSerializer.Deserialize<DnsLookupResult>(resp.ResultJson);
            if (parsed == null)
                return FailedLookup(target, "Agent returned an unreadable lookup result");
            if (string.IsNullOrEmpty(parsed.Kind))
                return FailedLookup(target, "This site's agent is too old to run DNS lookups. Update the agent to use this from its vantage.");

            return parsed with { Vantage = Vantage };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to parse agent lookup result for site {Slug}", _siteSlug);
            return FailedLookup(target, $"Couldn't parse the agent lookup result: {ex.Message}");
        }
    }

    private DnsLookupResult FailedLookup(ProbeTarget target, string error) => new()
    {
        Kind = NslookupOutputParser.ResultKind,
        Target = target,
        Vantage = Vantage,
        Timestamp = DateTime.UtcNow,
        ErrorMessage = error,
    };

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
