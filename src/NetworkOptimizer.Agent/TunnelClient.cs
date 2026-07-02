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

    /// <summary>Server asks us to dial a site-local TCP endpoint.</summary>
    public Func<ProxyOpen, CancellationToken, Task>? OnProxyOpen { get; set; }

    /// <summary>Server-to-site bytes for an open proxied connection.</summary>
    public Func<ProxyData, CancellationToken, Task>? OnProxyData { get; set; }

    /// <summary>Server closed a proxied connection.</summary>
    public Action<ProxyClose>? OnProxyClose { get; set; }

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
    public async Task RunAsync(string tunnelUrl, string agentKey, string version, bool ignoreSslErrors, CancellationToken ct)
    {
        var handler = new SocketsHttpHandler
        {
            // Keep the long-lived stream alive across NAT/firewall idle timeouts.
            KeepAlivePingDelay = TimeSpan.FromSeconds(60),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(30),
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always,
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
            Hello = new AgentHello { AgentKey = agentKey, Version = version }
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
