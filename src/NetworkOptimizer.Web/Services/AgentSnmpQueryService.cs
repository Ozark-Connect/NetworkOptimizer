using System.Collections.Concurrent;
using NetworkOptimizer.AgentProtocol;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Runs an on-demand single SNMP GET from a secondary site's agent over its tunnel, backing
/// the Monitoring "Test OID" button for agent-covered sites (the central server can't reach a
/// remote site's device directly). The server sends a <see cref="SnmpOidQuery"/> and the agent
/// returns the raw value in a <see cref="SnmpOidResult"/>. Requests are correlated by id,
/// mirroring <see cref="AgentProbeService"/>. Sites without an online agent are handled by the
/// caller's direct-poll path, so the main-site experience is unchanged.
/// </summary>
public class AgentSnmpQueryService
{
    private readonly AgentTunnelRegistry _registry;
    private readonly ILogger<AgentSnmpQueryService> _logger;

    private readonly ConcurrentDictionary<long, PendingQuery> _pending = new();
    private long _nextRequestId;

    public AgentSnmpQueryService(AgentTunnelRegistry registry, ILogger<AgentSnmpQueryService> logger)
    {
        _registry = registry;
        _logger = logger;
    }

    /// <summary>Whether an online agent exists for the site to service an OID test.</summary>
    public bool HasAgentForSite(string siteSlug) => _registry.GetForSite(siteSlug).Any();

    /// <summary>
    /// Asks the site's agent to GET a single OID and returns the result, or null if no agent is
    /// online. On tunnel/timeout failure the result carries Success=false with an Error.
    /// </summary>
    public async Task<SnmpOidResult?> QueryAsync(string siteSlug, string deviceIp, string oid, TimeSpan timeout, CancellationToken ct)
    {
        var agent = _registry.GetForSite(siteSlug).FirstOrDefault();
        if (agent == null)
        {
            _logger.LogDebug("OID query for site {Slug}: no online agent", siteSlug);
            return null;
        }

        var id = Interlocked.Increment(ref _nextRequestId);
        var pending = new PendingQuery(agent.AgentId);
        _pending[id] = pending;
        try
        {
            var sent = await agent.SendAsync(new ServerMessage
            {
                SnmpOidQuery = new SnmpOidQuery { RequestId = id, DeviceIp = deviceIp, Oid = oid }
            }, ct);
            _logger.LogDebug("OID query {Id} to agent {AgentId} (site {Slug}, ip {Ip}, oid {Oid}): sent={Sent}",
                id, agent.AgentId, siteSlug, deviceIp, oid, sent);
            if (!sent)
                return new SnmpOidResult { RequestId = id, Success = false, Error = "The site's agent tunnel closed before the test could be sent." };

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(timeout);
            try
            {
                return await pending.Completion.Task.WaitAsync(deadline.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return new SnmpOidResult { RequestId = id, Success = false, Error = "Timed out waiting for the site's agent to return the OID value." };
            }
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>Completes the matching pending query when an agent returns a result.</summary>
    public void OnResult(SnmpOidResult result)
    {
        var matched = _pending.TryGetValue(result.RequestId, out var pending);
        _logger.LogDebug("OID query {Id} result received: matched={Matched} success={Success} error={Error}",
            result.RequestId, matched, result.Success, result.Error);
        if (matched)
            pending!.Completion.TrySetResult(result);
    }

    /// <summary>Fails any queries waiting on an agent whose tunnel just dropped.</summary>
    public void OnAgentDisconnected(AgentTunnelConnection agent)
    {
        foreach (var (_, pending) in _pending.Where(p => p.Value.AgentId == agent.AgentId))
        {
            pending.Completion.TrySetResult(new SnmpOidResult
            {
                Success = false,
                Error = "The site's agent tunnel dropped during the OID test.",
            });
        }
    }

    private sealed class PendingQuery
    {
        public PendingQuery(int agentId) => AgentId = agentId;

        public int AgentId { get; }

        public TaskCompletionSource<SnmpOidResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
