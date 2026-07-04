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
    public async Task<ProbeResponse?> RunAsync(string siteSlug, ProbeRequest request, TimeSpan timeout, CancellationToken ct)
    {
        var agent = _registry.GetForSite(siteSlug).FirstOrDefault();
        if (agent == null)
            return null;

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
