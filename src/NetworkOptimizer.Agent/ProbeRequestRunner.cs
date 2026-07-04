using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkOptimizer.AgentProtocol;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Monitoring.Probes;

namespace NetworkOptimizer.Agent;

/// <summary>
/// Runs an on-demand ping or traceroute from the agent host on the server's behalf.
/// Network Tools ("agent" vantage) and Upstream Discovery both need a probe origin ON
/// the site's network - the on-site equivalent of running from the NO Server on the
/// home site. The central server sends a <see cref="ProbeRequest"/> over the tunnel;
/// this runner executes the SAME <see cref="LocalProbeExecutor"/> the server uses and
/// returns the result serialized to JSON. Correlated by request_id, mirroring
/// <see cref="Iperf3ClientRunner"/>.
/// </summary>
public sealed class ProbeRequestRunner
{
    private readonly TunnelClient _tunnel;
    private readonly LocalProbeExecutor _executor = new(NullLogger<LocalProbeExecutor>.Instance);

    public ProbeRequestRunner(TunnelClient tunnel)
    {
        _tunnel = tunnel;
    }

    /// <summary>Runs the requested probe and returns the JSON-serialized result over the tunnel.</summary>
    public async Task HandleAsync(ProbeRequest request, CancellationToken ct)
    {
        ProbeResponse response;
        try
        {
            var mode = Enum.TryParse<ProbeMode>(request.Mode, ignoreCase: true, out var m) ? m : ProbeMode.Icmp;
            var target = new ProbeTarget(
                request.Address,
                mode,
                request.Port > 0 ? request.Port : null,
                string.IsNullOrEmpty(request.SourceIp) ? null : request.SourceIp);

            string json;
            if (request.Traceroute)
            {
                var result = await _executor.TracerouteAsync(
                    target,
                    maxHops: request.MaxHops > 0 ? request.MaxHops : 30,
                    perHopTimeout: TimeSpan.FromSeconds(1),
                    totalDeadline: TimeSpan.FromSeconds(10),
                    ct: ct);
                json = JsonSerializer.Serialize(result);
            }
            else
            {
                var result = await _executor.PingAsync(
                    target,
                    count: request.Count > 0 ? request.Count : 5,
                    perPingTimeout: TimeSpan.FromSeconds(2),
                    ct: ct);
                json = JsonSerializer.Serialize(result);
            }

            response = new ProbeResponse { RequestId = request.RequestId, Success = true, ResultJson = json };
        }
        catch (Exception ex)
        {
            response = new ProbeResponse { RequestId = request.RequestId, Success = false, Error = ex.Message };
        }

        await _tunnel.SendAsync(new AgentMessage { ProbeResponse = response }, ct);
    }
}
