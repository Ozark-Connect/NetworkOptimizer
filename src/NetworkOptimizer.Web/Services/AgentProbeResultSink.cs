using NetworkOptimizer.AgentProtocol;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Receives probe results streamed back from on-site agents and pushes probe
/// configuration to agents when they connect. Split out of the tunnel handler
/// so the transport stays free of storage concerns.
/// </summary>
public class AgentProbeResultSink
{
    private readonly ILogger<AgentProbeResultSink> _logger;

    public AgentProbeResultSink(ILogger<AgentProbeResultSink> logger)
    {
        _logger = logger;
    }

    /// <summary>Called once per connection after the hello exchange.</summary>
    public Task OnAgentConnectedAsync(AgentTunnelConnection connection, CancellationToken ct)
    {
        // Probe configuration push lands with the agent probing milestone.
        return Task.CompletedTask;
    }

    /// <summary>Records a batch of probe results from an agent.</summary>
    public Task RecordBatchAsync(AgentTunnelConnection connection, ProbeResultBatch batch, CancellationToken ct)
    {
        _logger.LogDebug("Agent {Id} (site {Slug}) sent {Count} probe results - persistence not wired yet",
            connection.AgentId, connection.SiteSlug, batch.Results.Count);
        return Task.CompletedTask;
    }
}
