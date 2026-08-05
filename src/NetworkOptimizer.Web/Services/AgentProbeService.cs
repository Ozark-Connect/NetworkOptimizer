using System.Collections.Concurrent;
using NetworkOptimizer.AgentProtocol;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Runs an on-demand ping/traceroute from a secondary site's agent host over its
/// tunnel. Network Tools ("agent" vantage) and Upstream Discovery both need a probe
/// origin ON the site's network - the on-site equivalent of the NO Server on the home
/// site - which the central server can't provide for a remote site. The server sends a
/// <see cref="ProbeRequest"/> and the agent returns the SAME LocalProbeExecutor result
/// serialized to JSON. Requests are correlated by id, mirroring
/// <see cref="AgentIperf3Service"/>.
/// </summary>
public class AgentProbeService
{
    private readonly AgentTunnelRegistry _registry;
    private readonly ILogger<AgentProbeService> _logger;

    private readonly ConcurrentDictionary<long, PendingProbe> _pending = new();
    private long _nextRequestId;

    public AgentProbeService(AgentTunnelRegistry registry, ILogger<AgentProbeService> logger)
    {
        _registry = registry;
        _logger = logger;
    }

    /// <summary>Whether an online agent exists for the site to run on-demand probes.</summary>
    public bool HasAgentForSite(string siteSlug) => _registry.GetForSite(siteSlug).Any();

    /// <summary>
    /// Asks the site's agent to run a probe and returns the response, or null if no
    /// agent is online. On tunnel/timeout failure the response carries Success=false.
    /// </summary>
    /// <param name="siteSlug">Site whose agents may run the probe.</param>
    /// <param name="request">The probe to run; its SourceIp carries any WAN context bind.</param>
    /// <param name="timeout">How long to wait for the agent's response.</param>
    /// <param name="ct">Cancellation.</param>
    /// <param name="agentId">
    /// Which of the site's agents should run it. Null keeps the original behavior - the site's
    /// first connected agent - which is what every caller that has no reason to care wants. A
    /// caller that does care is asking for one WAN's vantage, and another agent sits behind a
    /// different WAN, so an unavailable one is reported rather than quietly substituted.
    /// </param>
    public async Task<ProbeResponse?> RunAsync(
        string siteSlug, ProbeRequest request, TimeSpan timeout, CancellationToken ct, int? agentId = null)
    {
        var agent = SelectAgent(_registry.GetForSite(siteSlug), agentId);
        if (agent == null)
        {
            // No agent at all is null, which callers word as "no on-site agent". A NAMED agent
            // that is not connected is a different thing to say, and substituting another one
            // would silently measure a different WAN.
            if (agentId != null)
                return new ProbeResponse { Success = false, Error = "The agent this probe was aimed at isn't connected right now" };
            return null;
        }

        var id = Interlocked.Increment(ref _nextRequestId);
        request.RequestId = id;
        var pending = new PendingProbe(agent.AgentId);
        _pending[id] = pending;
        try
        {
            var sent = await agent.SendAsync(new ServerMessage { ProbeRequest = request }, ct);
            if (!sent)
                return new ProbeResponse { RequestId = id, Success = false, Error = "The site's agent tunnel closed before the probe could be sent" };

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(timeout);
            try
            {
                return await pending.Completion.Task.WaitAsync(deadline.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return new ProbeResponse { RequestId = id, Success = false, Error = "Timed out waiting for the site's agent to return the probe result" };
            }
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>
    /// Which connected agent runs a probe: the one asked for, or - when nothing asked - the
    /// site's first, exactly as before. Never falls back from a named agent to another one:
    /// the whole point of naming it is that it sits behind a particular WAN.
    /// </summary>
    /// <param name="connections">The site's live tunnel connections.</param>
    /// <param name="agentId">Agent the caller wants, or null for "any".</param>
    internal static AgentTunnelConnection? SelectAgent(IReadOnlyList<AgentTunnelConnection> connections, int? agentId)
        => agentId is int wanted
            ? connections.FirstOrDefault(c => c.AgentId == wanted)
            : connections.FirstOrDefault();

    /// <summary>Completes the matching pending probe when an agent returns a response.</summary>
    public void OnResult(ProbeResponse response)
    {
        if (_pending.TryGetValue(response.RequestId, out var pending))
            pending.Completion.TrySetResult(response);
    }

    /// <summary>Fails any probes waiting on an agent whose tunnel just dropped.</summary>
    public void OnAgentDisconnected(AgentTunnelConnection agent)
    {
        foreach (var (_, pending) in _pending.Where(p => p.Value.AgentId == agent.AgentId))
        {
            pending.Completion.TrySetResult(new ProbeResponse
            {
                Success = false,
                Error = "The site's agent tunnel dropped during the probe",
            });
        }
    }

    private sealed class PendingProbe
    {
        public PendingProbe(int agentId) => AgentId = agentId;

        public int AgentId { get; }

        public TaskCompletionSource<ProbeResponse> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
