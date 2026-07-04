using System.Threading.Channels;
using Grpc.Core;
using Grpc.Net.Client;
using NetworkOptimizer.AgentProtocol;

namespace NetworkOptimizer.Agent;

/// <summary>
/// The agent side of the persistent tunnel: dials out to the central server's
/// HTTP/2 tunnel port, authenticates with the agent key, then holds one
/// bidirectional stream carrying heartbeats and probe results out and probe
/// configuration in. All outbound writes funnel through a channel so multiple
/// producers (heartbeat loop, probe runner) never race on the stream.
/// </summary>
public sealed class TunnelClient
{
    // Wait (not drop) when full: proxied byte streams ride this channel and a
    // dropped frame would corrupt them.
    private readonly Channel<AgentMessage> _outbound = Channel.CreateBounded<AgentMessage>(
        new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.Wait });

    /// <summary>Invoked whenever the server pushes a new probe configuration.</summary>
    public Action<ProbeConfig>? OnProbeConfig { get; set; }

    /// <summary>Invoked whenever the server pushes a new SNMP monitoring configuration.</summary>
    public Action<SnmpConfig>? OnSnmpConfig { get; set; }

    /// <summary>Server asks us to dial a site-local TCP endpoint.</summary>
    public Func<ProxyOpen, CancellationToken, Task>? OnProxyOpen { get; set; }

    /// <summary>Server-to-site bytes for an open proxied connection.</summary>
    public Func<ProxyData, CancellationToken, Task>? OnProxyData { get; set; }

    /// <summary>Server closed a proxied connection.</summary>
    public Action<ProxyClose>? OnProxyClose { get; set; }

    /// <summary>Server asks us to run an iperf3 client test against a site-local target.</summary>
    public Func<Iperf3ClientRequest, CancellationToken, Task>? OnIperf3Request { get; set; }

    /// <summary>Server asks us to run the UWN WAN speed test at the site.</summary>
    public Func<UwnRequest, CancellationToken, Task>? OnUwnRequest { get; set; }

    /// <summary>Server asks us to run an on-demand ping/traceroute from this host.</summary>
    public Func<ProbeRequest, CancellationToken, Task>? OnProbeRequest { get; set; }

    /// <summary>Queues a message for the stream pump. Safe from any thread.</summary>
    public bool TrySend(AgentMessage message) => _outbound.Writer.TryWrite(message);

    /// <summary>Queues with backpressure. False once the tunnel is torn down.</summary>
    public async ValueTask<bool> SendAsync(AgentMessage message, CancellationToken ct)
    {
        try
        {
            await _outbound.Writer.WriteAsync(message, ct);
            return true;
        }
        catch (ChannelClosedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Connects and runs the tunnel until it drops or <paramref name="ct"/> is
    /// cancelled. Throws on connection failure so the caller can back off and retry.
    /// </summary>
    public async Task RunAsync(string tunnelUrl, string agentKey, string version, string? lanIp, bool ignoreSslErrors, CancellationToken ct)
    {
        // Belt-and-braces with the startup config validation: the tunnel carries
        // SNMP credentials and proxied console traffic, so cleartext is never OK.
        if (!Uri.TryCreate(tunnelUrl, UriKind.Absolute, out var tunnelUri)
            || tunnelUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException(
                $"Refusing non-HTTPS tunnel address '{tunnelUrl}' - publish the tunnel through the TLS reverse proxy.");
        }

        var handler = new SocketsHttpHandler
        {
            // No HTTP/2 keepalive pings: reverse proxies in front of the server do
            // not reliably ACK client pings, so the ping timeout tore down a healthy
            // tunnel every ~90s (KeepAlivePingDelay 60s + KeepAlivePingTimeout 30s).
            // The application heartbeat (~30s, agent -> server) keeps NAT/firewall
            // state warm instead.
            EnableMultipleHttp2Connections = true,
        };
        if (ignoreSslErrors)
        {
            handler.SslOptions = new System.Net.Security.SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = delegate { return true; }
            };
        }

        using var grpcChannel = GrpcChannel.ForAddress(tunnelUrl, new GrpcChannelOptions { HttpHandler = handler });
        var client = new AgentTunnel.AgentTunnelClient(grpcChannel);
        using var call = client.Connect(cancellationToken: ct);

        await call.RequestStream.WriteAsync(new AgentMessage
        {
            Hello = new AgentHello { AgentKey = agentKey, Version = version, LanIp = lanIp ?? "" }
        }, ct);

        if (!await call.ResponseStream.MoveNext(ct) || call.ResponseStream.Current.Hello is not { } hello)
            throw new IOException("Tunnel closed before server hello");

        var heartbeatInterval = TimeSpan.FromSeconds(Math.Max(5, hello.HeartbeatIntervalSeconds));
        Console.WriteLine($"Tunnel open for site '{hello.SiteSlug}' (heartbeat every {heartbeatInterval.TotalSeconds:0}s)");

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pumpTask = PumpOutboundAsync(call.RequestStream, linked.Token);
        var heartbeatTask = HeartbeatLoopAsync(heartbeatInterval, linked.Token);

        try
        {
            while (await call.ResponseStream.MoveNext(ct))
            {
                var message = call.ResponseStream.Current;
                switch (message.PayloadCase)
                {
                    case ServerMessage.PayloadOneofCase.ProbeConfig:
                        Console.WriteLine($"Received probe config: {message.ProbeConfig.Targets.Count} target(s)");
                        OnProbeConfig?.Invoke(message.ProbeConfig);
                        break;
                    case ServerMessage.PayloadOneofCase.SnmpConfig:
                        OnSnmpConfig?.Invoke(message.SnmpConfig);
                        break;
                    case ServerMessage.PayloadOneofCase.ProxyOpen:
                        // Fire and forget: the dial can take seconds and the
                        // server won't send data for this id until we answer.
                        if (OnProxyOpen is { } openHandler)
                            _ = openHandler(message.ProxyOpen, linked.Token);
                        break;
                    case ServerMessage.PayloadOneofCase.ProxyData:
                        if (OnProxyData is { } dataHandler)
                            await dataHandler(message.ProxyData, linked.Token);
                        break;
                    case ServerMessage.PayloadOneofCase.ProxyClose:
                        OnProxyClose?.Invoke(message.ProxyClose);
                        break;
                    case ServerMessage.PayloadOneofCase.Iperf3Request:
                        // Fire and forget: the test runs for its full duration and
                        // must not block the read loop (heartbeats, other messages).
                        if (OnIperf3Request is { } iperf3Handler)
                            _ = iperf3Handler(message.Iperf3Request, linked.Token);
                        break;
                    case ServerMessage.PayloadOneofCase.UwnRequest:
                        // Fire and forget for the same reason as the iperf3 test:
                        // the WAN speed test runs for many seconds.
                        if (OnUwnRequest is { } uwnHandler)
                            _ = uwnHandler(message.UwnRequest, linked.Token);
                        break;
                    case ServerMessage.PayloadOneofCase.ProbeRequest:
                        // Fire and forget: a traceroute can take up to ~10s and must
                        // not block the read loop (heartbeats, proxy, other messages).
                        if (OnProbeRequest is { } probeHandler)
                            _ = probeHandler(message.ProbeRequest, linked.Token);
                        break;
                }
            }
        }
        finally
        {
            linked.Cancel();
            _outbound.Writer.TryComplete();
            try { await Task.WhenAll(pumpTask, heartbeatTask); } catch (OperationCanceledException) { }
        }
    }

    private async Task PumpOutboundAsync(IClientStreamWriter<AgentMessage> requestStream, CancellationToken ct)
    {
        await foreach (var message in _outbound.Reader.ReadAllAsync(ct))
        {
            await requestStream.WriteAsync(message, ct);
        }
    }

    private async Task HeartbeatLoopAsync(TimeSpan interval, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(interval, ct);
            TrySend(new AgentMessage
            {
                Heartbeat = new AgentHeartbeat { TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }
            });
        }
    }
}
