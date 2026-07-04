using System.Collections.Concurrent;
using NetworkOptimizer.AgentProtocol;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Runs the UWN WAN speed test at a secondary site through its agent tunnel. The
/// central server's own WAN is the wrong link to measure for another site, so it
/// asks the site's agent to run the uwnspeedtest binary locally and return the raw
/// JSON stdout; the server then parses that JSON and stores the result under the
/// site, exactly as it does for a local run. Requests are correlated by id,
/// mirroring <see cref="AgentIperf3Service"/>.
/// </summary>
public class AgentUwnService
{
    // Cover the test plus the agent's own (timeout + 30s) process guard, with
    // headroom, before we give up waiting for a result.
    private const int ResultTimeoutOverheadSeconds = 45;

    private readonly AgentTunnelRegistry _registry;
    private readonly ILogger<AgentUwnService> _logger;

    private readonly ConcurrentDictionary<long, PendingRun> _pending = new();
    private long _nextRequestId;

    public AgentUwnService(AgentTunnelRegistry registry, ILogger<AgentUwnService> logger)
    {
        _registry = registry;
        _logger = logger;
    }

    /// <summary>
    /// Asks the site's agent to run the UWN WAN speed test and returns the raw
    /// result, matching the (success, output) contract of a local run: the
    /// uwnspeedtest JSON stdout on success, or an error message otherwise.
    /// </summary>
    public async Task<(bool success, string output)> RunAsync(
        string siteSlug, int streams, int servers, int durationSeconds, int timeoutSeconds, CancellationToken ct)
    {
        var agent = _registry.GetForSite(siteSlug).FirstOrDefault();
        if (agent == null)
            return (false, $"No agent is online for site '{siteSlug}' to run the WAN speed test");

        var id = Interlocked.Increment(ref _nextRequestId);
        var pending = new PendingRun(agent.AgentId);
        _pending[id] = pending;
        try
        {
            var request = new UwnRequest
            {
                RequestId = id,
                Streams = streams,
                Servers = servers,
                DurationSeconds = durationSeconds,
                TimeoutSeconds = timeoutSeconds,
            };

            _logger.LogDebug("Requesting agent UWN WAN test {Id} for site {Slug} ({Streams} streams, {Servers} servers)",
                id, siteSlug, streams, servers);

            var sent = await agent.SendAsync(new ServerMessage { UwnRequest = request }, ct);
            if (!sent)
                return (false, "The site's agent tunnel closed before the WAN speed test request could be sent");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds + ResultTimeoutOverheadSeconds));
            try
            {
                var result = await pending.Completion.Task.WaitAsync(timeout.Token);
                return (result.Success, result.Output);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return (false, "Timed out waiting for the site's agent to return the WAN speed test result");
            }
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>Completes the matching pending run when an agent returns a result.</summary>
    public void OnResult(UwnResult result)
    {
        if (_pending.TryGetValue(result.RequestId, out var pending))
            pending.Completion.TrySetResult(result);
    }

    /// <summary>Fails any runs waiting on an agent whose tunnel just dropped.</summary>
    public void OnAgentDisconnected(AgentTunnelConnection agent)
    {
        foreach (var (_, pending) in _pending.Where(p => p.Value.AgentId == agent.AgentId))
        {
            pending.Completion.TrySetResult(new UwnResult
            {
                Success = false,
                Output = "The site's agent tunnel dropped during the WAN speed test",
            });
        }
    }

    private sealed class PendingRun
    {
        public PendingRun(int agentId) => AgentId = agentId;

        public int AgentId { get; }

        public TaskCompletionSource<UwnResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
