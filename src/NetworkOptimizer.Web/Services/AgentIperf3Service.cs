using System.Collections.Concurrent;
using NetworkOptimizer.AgentProtocol;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Runs a LAN iperf3 client test at a secondary site through its agent tunnel.
/// The central server can't reach an agent-backed site's LAN devices directly,
/// so it asks the site's agent to run <c>iperf3 -c ...</c> locally against the
/// target and return the raw <c>-J</c> output; throughput then reflects the site
/// LAN rather than the tunnel. Requests are correlated by id, mirroring the
/// connection-id pattern in <see cref="AgentTunnelProxyService"/>. The server
/// keeps orchestrating the test (server management over the tunnelled SSH path,
/// parsing, path analysis, storage); only the client run moves to the agent.
/// </summary>
public class AgentIperf3Service
{
    // Cover connect + both directions plus the agent's own (duration + 15s)
    // client timeout, with headroom, before we give up waiting for a result.
    private const int ResultTimeoutOverheadSeconds = 30;

    private readonly AgentTunnelRegistry _registry;
    private readonly ILogger<AgentIperf3Service> _logger;

    private readonly ConcurrentDictionary<long, PendingRun> _pending = new();
    private long _nextRequestId;

    public AgentIperf3Service(AgentTunnelRegistry registry, ILogger<AgentIperf3Service> logger)
    {
        _registry = registry;
        _logger = logger;
    }

    /// <summary>
    /// Asks the site's agent to run an iperf3 client test and returns the raw
    /// result, matching the (success, output) contract of a local run: the
    /// <c>iperf3 -J</c> JSON on success, or an error message otherwise.
    /// </summary>
    public async Task<(bool success, string output)> RunClientAsync(
        string siteSlug, string host, int port, int durationSeconds, int parallelStreams, bool reverse, CancellationToken ct)
    {
        var agent = _registry.GetForSite(siteSlug).FirstOrDefault();
        if (agent == null)
            return (false, $"No agent is online for site '{siteSlug}' to run the LAN iperf3 test");

        var id = Interlocked.Increment(ref _nextRequestId);
        var pending = new PendingRun(agent.AgentId);
        _pending[id] = pending;
        try
        {
            var request = new Iperf3ClientRequest
            {
                RequestId = id,
                Host = host,
                Port = port,
                DurationSeconds = durationSeconds,
                ParallelStreams = parallelStreams,
                Reverse = reverse,
            };

            _logger.LogDebug("Requesting agent iperf3 client run {Id} for site {Slug} -> {Host}:{Port} (reverse={Reverse})",
                id, siteSlug, host, port, reverse);

            var sent = await agent.SendAsync(new ServerMessage { Iperf3Request = request }, ct);
            if (!sent)
                return (false, "The site's agent tunnel closed before the iperf3 request could be sent");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(durationSeconds + ResultTimeoutOverheadSeconds));
            try
            {
                var result = await pending.Completion.Task.WaitAsync(timeout.Token);
                return (result.Success, result.Output);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return (false, "Timed out waiting for the site's agent to return the iperf3 result");
            }
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>Completes the matching pending run when an agent returns a result.</summary>
    public void OnResult(Iperf3ClientResult result)
    {
        if (_pending.TryGetValue(result.RequestId, out var pending))
            pending.Completion.TrySetResult(result);
    }

    /// <summary>Fails any runs waiting on an agent whose tunnel just dropped.</summary>
    public void OnAgentDisconnected(AgentTunnelConnection agent)
    {
        foreach (var (_, pending) in _pending.Where(p => p.Value.AgentId == agent.AgentId))
        {
            pending.Completion.TrySetResult(new Iperf3ClientResult
            {
                Success = false,
                Output = "The site's agent tunnel dropped during the iperf3 test",
            });
        }
    }

    private sealed class PendingRun
    {
        public PendingRun(int agentId) => AgentId = agentId;

        public int AgentId { get; }

        public TaskCompletionSource<Iperf3ClientResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
