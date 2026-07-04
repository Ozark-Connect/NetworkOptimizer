using Grpc.Core;
using NetworkOptimizer.AgentProtocol;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Whether the agent tunnel listener is bound and on which port. Resolved once
/// at startup (the listener needs a dedicated HTTP/2 Kestrel endpoint, so it
/// cannot be toggled without a restart).
/// </summary>
public record AgentTunnelOptions(bool Enabled, int Port);

/// <summary>
/// Server side of the persistent agent tunnel (spec D3). Agents dial out and
/// hold one bidirectional gRPC stream: the first inbound message must be an
/// AgentHello carrying the agent key; after authentication the stream carries
/// agent heartbeats and probe result batches inbound, and probe configuration
/// pushes outbound via <see cref="AgentTunnelRegistry"/>.
/// </summary>
public class AgentTunnelService : AgentTunnel.AgentTunnelBase
{
    /// <summary>Heartbeat cadence handed to agents in the ServerHello.</summary>
    public const int HeartbeatIntervalSeconds = 30;

    private readonly AgentEnrollmentService _enrollment;
    private readonly AgentTunnelRegistry _registry;
    private readonly AgentProbeResultSink _probeResultSink;
    private readonly AgentTunnelProxyService _proxy;
    private readonly AgentIperf3Service _iperf3;
    private readonly AgentUwnService _uwn;
    private readonly AgentProbeService _probe;
    private readonly AgentSnmpQueryService _snmpQuery;
    private readonly ILogger<AgentTunnelService> _logger;

    public AgentTunnelService(
        AgentEnrollmentService enrollment,
        AgentTunnelRegistry registry,
        AgentProbeResultSink probeResultSink,
        AgentTunnelProxyService proxy,
        AgentIperf3Service iperf3,
        AgentUwnService uwn,
        AgentProbeService probe,
        AgentSnmpQueryService snmpQuery,
        ILogger<AgentTunnelService> logger)
    {
        _enrollment = enrollment;
        _registry = registry;
        _probeResultSink = probeResultSink;
        _proxy = proxy;
        _iperf3 = iperf3;
        _uwn = uwn;
        _probe = probe;
        _snmpQuery = snmpQuery;
        _logger = logger;
    }

    public override async Task Connect(
        IAsyncStreamReader<AgentMessage> requestStream,
        IServerStreamWriter<ServerMessage> responseStream,
        ServerCallContext context)
    {
        var ct = context.CancellationToken;

        if (!await requestStream.MoveNext(ct) || requestStream.Current.Hello is not { } hello)
            throw new RpcException(new Status(StatusCode.Unauthenticated, "First message must be a hello"));

        var auth = await _enrollment.AuthenticateByKeyAsync(hello.AgentKey);
        if (auth == null)
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Invalid agent key"));

        var (agent, siteSlug) = auth.Value;
        await _enrollment.HeartbeatAsync(hello.AgentKey, hello.Version, hello.LanIp);

        var connection = _registry.Register(agent.Id, siteSlug, agent.Name);
        _logger.LogInformation("Agent {Name} (id {Id}) opened tunnel for site {Slug}", agent.Name, agent.Id, siteSlug);

        // The pump and refresh loops must stop when the read loop ends for any
        // reason, including a graceful agent close (which does not cancel the
        // call's own token).
        using var streamCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task? pumpTask = null;
        Task? refreshTask = null;
        try
        {
            await responseStream.WriteAsync(new ServerMessage
            {
                Hello = new ServerHello
                {
                    SiteSlug = siteSlug,
                    AgentName = agent.Name,
                    HeartbeatIntervalSeconds = HeartbeatIntervalSeconds,
                }
            }, ct);

            // The response stream allows one writer at a time, so all outbound
            // traffic funnels through the connection's channel and this pump.
            pumpTask = PumpOutboundAsync(connection, responseStream, streamCts.Token);
            refreshTask = RefreshProbeConfigLoopAsync(connection, streamCts.Token);

            await _probeResultSink.OnAgentConnectedAsync(connection, ct);

            while (await requestStream.MoveNext(ct))
            {
                connection.LastMessageAt = DateTime.UtcNow;
                var message = requestStream.Current;
                switch (message.PayloadCase)
                {
                    case AgentMessage.PayloadOneofCase.Heartbeat:
                        await _enrollment.HeartbeatAsync(hello.AgentKey, hello.Version, hello.LanIp);
                        break;
                    case AgentMessage.PayloadOneofCase.ProbeResults:
                        await _probeResultSink.RecordBatchAsync(connection, message.ProbeResults, ct);
                        break;
                    case AgentMessage.PayloadOneofCase.ProxyOpenResult:
                        _proxy.OnProxyOpenResult(message.ProxyOpenResult);
                        break;
                    case AgentMessage.PayloadOneofCase.ProxyData:
                        await _proxy.OnProxyDataAsync(message.ProxyData, ct);
                        break;
                    case AgentMessage.PayloadOneofCase.ProxyClose:
                        _proxy.OnProxyClose(message.ProxyClose);
                        break;
                    case AgentMessage.PayloadOneofCase.SnmpResults:
                        await _probeResultSink.RecordSnmpBatchAsync(connection, message.SnmpResults, ct);
                        break;
                    case AgentMessage.PayloadOneofCase.Iperf3Result:
                        _iperf3.OnResult(message.Iperf3Result);
                        break;
                    case AgentMessage.PayloadOneofCase.UwnResult:
                        _uwn.OnResult(message.UwnResult);
                        break;
                    case AgentMessage.PayloadOneofCase.ProbeResponse:
                        _probe.OnResult(message.ProbeResponse);
                        break;
                    case AgentMessage.PayloadOneofCase.SnmpOidResult:
                        _snmpQuery.OnResult(message.SnmpOidResult);
                        break;
                    default:
                        _logger.LogDebug("Agent {Id} sent unexpected {Payload} mid-stream", agent.Id, message.PayloadCase);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Agent went away or server is shutting down - normal teardown.
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Agent {Id} tunnel dropped", agent.Id);
        }
        finally
        {
            _registry.Unregister(connection);
            _proxy.OnAgentDisconnected(connection);
            _iperf3.OnAgentDisconnected(connection);
            _uwn.OnAgentDisconnected(connection);
            _probe.OnAgentDisconnected(connection);
            _snmpQuery.OnAgentDisconnected(connection);
            streamCts.Cancel();
            await AwaitQuietlyAsync(pumpTask);
            await AwaitQuietlyAsync(refreshTask);
            _logger.LogInformation("Agent {Name} (id {Id}) tunnel closed", agent.Name, agent.Id);
        }
    }

    private static async Task PumpOutboundAsync(
        AgentTunnelConnection connection,
        IServerStreamWriter<ServerMessage> responseStream,
        CancellationToken ct)
    {
        await foreach (var message in connection.Outbound.ReadAllAsync(ct))
        {
            await responseStream.WriteAsync(message, ct);
        }
    }

    /// <summary>
    /// Re-pushes the site's probe and SNMP configs every few minutes so
    /// monitoring edits (targets, credentials, new devices) reach connected
    /// agents without waiting for a reconnect.
    /// </summary>
    private async Task RefreshProbeConfigLoopAsync(AgentTunnelConnection connection, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(60), ct);
            await _probeResultSink.OnAgentConnectedAsync(connection, ct);
        }
    }

    private static async Task AwaitQuietlyAsync(Task? task)
    {
        if (task == null) return;
        try { await task; }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }
}
