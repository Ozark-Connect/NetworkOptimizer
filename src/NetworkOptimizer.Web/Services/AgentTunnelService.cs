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

    // A healthy agent is never silent longer than one heartbeat interval, so
    // three missed heartbeats means the tunnel is dead even though TCP hasn't
    // noticed (a WAN outage black-holes the connection rather than resetting
    // it - the read loop would sit in MoveNext for the full TCP timeout,
    // ~15 min). Dropping promptly matters beyond hygiene: the registry entry
    // keeps IsAgentOnline() true, which blocks the console's awaiting-agent
    // fail-fast flip - every page of the site (including a site switch) then
    // hangs on proxy opens into the dead tunnel until they time out.
    private static readonly TimeSpan LivenessTimeout = TimeSpan.FromSeconds(90);

    private readonly AgentEnrollmentService _enrollment;
    private readonly AgentTunnelRegistry _registry;
    private readonly AgentProbeResultSink _probeResultSink;
    private readonly AgentTunnelProxyService _proxy;
    private readonly AgentIperf3Service _iperf3;
    private readonly AgentUwnService _uwn;
    private readonly AgentProbeService _probe;
    private readonly AgentSnmpQueryService _snmpQuery;
    private readonly Licensing.LicenseStateService _licenseState;
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
        Licensing.LicenseStateService licenseState,
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
        _licenseState = licenseState;
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

        // License enforcement: a restricted site's agent stays paused. The agent's
        // own dial-out backoff keeps retrying, so re-licensing resumes it without
        // any agent-side changes.
        if (!_licenseState.IsSiteOperational(siteSlug))
        {
            _logger.LogDebug("Agent {Name} rejected: site {Slug} is license-restricted", agent.Name, siteSlug);
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Site license restricted"));
        }

        await _enrollment.HeartbeatAsync(hello.AgentKey, hello.Version, hello.LanIp);

        var connection = _registry.Register(agent.Id, siteSlug, agent.Name);
        _logger.LogInformation("Agent {Name} (id {Id}) opened tunnel for site {Slug}", agent.Name, agent.Id, siteSlug);

        // The pump and refresh loops must stop when the read loop ends for any
        // reason, including a graceful agent close (which does not cancel the
        // call's own token). The connection's drop token lets the server force
        // the whole call down (license enforcement).
        using var streamCts = CancellationTokenSource.CreateLinkedTokenSource(ct, connection.DropToken);
        ct = streamCts.Token;
        Task? pumpTask = null;
        Task? refreshTask = null;
        Task? livenessTask = null;
        try
        {
            await responseStream.WriteAsync(new ServerMessage
            {
                Hello = new ServerHello
                {
                    SiteSlug = siteSlug,
                    AgentName = agent.Name,
                    HeartbeatIntervalSeconds = HeartbeatIntervalSeconds,
                    SupportsResultAck = true,
                }
            }, ct);

            // The response stream allows one writer at a time, so all outbound
            // traffic funnels through the connection's channel and this pump.
            pumpTask = PumpOutboundAsync(connection, responseStream, streamCts.Token);
            refreshTask = RefreshProbeConfigLoopAsync(connection, streamCts.Token);
            livenessTask = WatchLivenessAsync(connection, streamCts.Token);

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
                        // Ack only after the batch is persisted, so the agent keeps it
                        // buffered until we confirm it landed. Cumulative: sequence N
                        // acks everything <= N. Sequence 0 = an older agent with no acking.
                        if (message.Sequence > 0)
                            connection.TrySend(new ServerMessage { ResultAck = new ResultAck { Sequence = message.Sequence } });
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
                        if (message.Sequence > 0)
                            connection.TrySend(new ServerMessage { ResultAck = new ResultAck { Sequence = message.Sequence } });
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
            _probeResultSink.OnAgentDisconnected(connection);
            streamCts.Cancel();
            await AwaitQuietlyAsync(pumpTask);
            await AwaitQuietlyAsync(refreshTask);
            await AwaitQuietlyAsync(livenessTask);
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

    /// <summary>
    /// Drops the connection when the agent has been silent past
    /// <see cref="LivenessTimeout"/>. Cancelling the drop token aborts the
    /// read loop, which runs the normal teardown: unregister, proxy/console
    /// awaiting-agent flip, and the agent's own reconnect gets a clean slate.
    /// Before that, the moment silence crosses the stale threshold, the site's
    /// console is flipped to awaiting-agent proactively - without this, a site
    /// nobody dialed during the outage stayed stale-green until first contact,
    /// and that first switch paid a dial-and-retry on every console call for
    /// the rest of the 90s window.
    /// </summary>
    private async Task WatchLivenessAsync(AgentTunnelConnection connection, CancellationToken ct)
    {
        var flaggedStale = false;
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(15), ct);
            var silent = DateTime.UtcNow - connection.LastMessageAt;
            if (!flaggedStale && silent > AgentTunnelConnection.StaleThreshold)
            {
                flaggedStale = true;
                _logger.LogInformation(
                    "Agent {Name} (id {Id}, site {Slug}) silent for {Silent:0}s; treating the tunnel as black-holed",
                    connection.AgentName, connection.AgentId, connection.SiteSlug, silent.TotalSeconds);
                _probeResultSink.OnTunnelStale(connection);
            }
            if (silent <= LivenessTimeout) continue;
            _logger.LogWarning(
                "Agent {Name} (id {Id}, site {Slug}) silent for {Silent:0}s (heartbeat is {Interval}s); dropping dead tunnel",
                connection.AgentName, connection.AgentId, connection.SiteSlug,
                silent.TotalSeconds, HeartbeatIntervalSeconds);
            connection.Drop();
            return;
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
