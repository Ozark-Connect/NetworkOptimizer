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
/// producers (heartbeat loop, result-buffer drain, proxy handler) never race
/// on the stream. Monitoring results reach the stream only through
/// <see cref="DrainResultsAsync"/> from the process-wide
/// <see cref="ResultBuffer"/>, which is what carries them across tunnel
/// outages.
/// </summary>
public sealed class TunnelClient
{
    // Wait (not drop) when full: proxied byte streams ride this channel and a
    // dropped frame would corrupt them. Result frames also ride it, but they
    // stay in the ResultBuffer until the server acks them, so a frame still on
    // this channel when the tunnel tears down is simply re-sent on the next
    // connection - nothing to salvage.
    private readonly Channel<AgentMessage> _outbound = Channel.CreateBounded<AgentMessage>(
        new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.Wait });

    // Backlog-flush tuning for DrainResultsAsync: coalescing consecutive
    // batches slashes the server's per-batch overhead (DB round-trips, console
    // cache lookups) when replaying hours of buffered data, and the headroom
    // check keeps this flood from saturating the outbound channel so
    // heartbeats and proxied console traffic still get through mid-flush.
    private const int CoalesceMaxSamples = 500;
    private const int OutboundHeadroomSlots = 64;

    // The server is never silent on a healthy tunnel - it re-pushes the site's
    // configs every 60 s - so 2.5 missed refresh cycles means the link is
    // black-holed. A real WAN outage drops packets rather than resetting
    // connections: without this watchdog the read loop would hang in MoveNext
    // for the full TCP timeout (~15 min) before the reconnect loop - and the
    // buffered-backlog flush - could run. Written by the read loop, watched by
    // WatchInboundSilenceAsync.
    private const long InboundSilenceTimeoutMs = 150_000;
    private long _lastInboundTicks;

    // Bound the post-connect handshake: the inbound-silence watchdog only arms
    // once the server hello arrives, so without this a link black-holed between
    // connect and hello would hang the read on dead TCP for the full OS timeout
    // (~15 min) before the reconnect loop could retry.
    private static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(20);

    // Set from the server hello. An older server that doesn't ack result frames
    // (SupportsResultAck = false) means we can't rely on acks, so the drain trims
    // each frame on send - the pre-ack behavior - instead of retaining the whole
    // buffer and re-flushing it on every reconnect. A fresh TunnelClient per
    // connection defaults this to false (keep until proven), which is the safe
    // side: worst case a frame is re-sent, never dropped.
    private volatile bool _ackless;

    /// <summary>Invoked whenever the server pushes a new probe configuration.</summary>
    public Action<ProbeConfig>? OnProbeConfig { get; set; }

    /// <summary>Invoked whenever the server pushes a new SNMP monitoring configuration.</summary>
    public Action<SnmpConfig>? OnSnmpConfig { get; set; }

    /// <summary>Server pushes the WAN speed-test server list for the /wan/ redirect router.</summary>
    public Action<WanSpeedTestConfig>? OnWanSpeedTestConfig { get; set; }

    /// <summary>Server asks us to GET a single OID once (the "Test OID" button).</summary>
    public Func<SnmpOidQuery, CancellationToken, Task>? OnSnmpOidQuery { get; set; }

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

    /// <summary>
    /// Server has persisted result frames up to (and including) this sequence.
    /// Wired to <see cref="ResultBuffer.MarkAcked"/> so acked frames leave the
    /// buffer; anything unacked replays on the next connection.
    /// </summary>
    public Action<long>? OnResultAck { get; set; }

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
            // Without a cap, dialing a powered-off server host rides the OS's TCP
            // SYN retries for ~2 minutes per attempt, stretching the reconnect
            // cadence from ~30s to minutes while the server is coming back up.
            ConnectTimeout = TimeSpan.FromSeconds(15),
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
        // The linked source scopes the whole call (not just the pump/heartbeat)
        // so the inbound-silence watchdog can abort a black-holed read loop.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var call = client.Connect(cancellationToken: linked.Token);

        // The connection is up but the server hello has not arrived yet, and the
        // inbound-silence watchdog below only arms once it has. Bound the handshake
        // so a link black-holed in this window fails fast into a retry instead of
        // hanging the read on dead TCP for the full OS timeout.
        TimeSpan heartbeatInterval;
        string siteSlug;
        try
        {
            using var helloCts = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
            helloCts.CancelAfter(HelloTimeout);

            await call.RequestStream.WriteAsync(new AgentMessage
            {
                Hello = new AgentHello { AgentKey = agentKey, Version = version, LanIp = lanIp ?? "" }
            }, helloCts.Token);

            if (!await call.ResponseStream.MoveNext(helloCts.Token) || call.ResponseStream.Current.Hello is not { } hello)
                throw new IOException("Tunnel closed before server hello");

            heartbeatInterval = TimeSpan.FromSeconds(Math.Max(5, hello.HeartbeatIntervalSeconds));
            siteSlug = hello.SiteSlug;
            _ackless = !hello.SupportsResultAck;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IOException($"No server hello within {HelloTimeout.TotalSeconds:0}s of connecting; assuming dead link");
        }

        Console.WriteLine($"Tunnel open for site '{siteSlug}' (heartbeat every {heartbeatInterval.TotalSeconds:0}s)");

        Volatile.Write(ref _lastInboundTicks, Environment.TickCount64);
        var pumpTask = PumpOutboundAsync(call.RequestStream, linked.Token);
        var heartbeatTask = HeartbeatLoopAsync(heartbeatInterval, linked.Token);
        var watchdogTask = WatchInboundSilenceAsync(linked, linked.Token);

        try
        {
            while (await call.ResponseStream.MoveNext(linked.Token))
            {
                Volatile.Write(ref _lastInboundTicks, Environment.TickCount64);
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
                    case ServerMessage.PayloadOneofCase.WanSpeedtestConfig:
                        OnWanSpeedTestConfig?.Invoke(message.WanSpeedtestConfig);
                        break;
                    case ServerMessage.PayloadOneofCase.SnmpOidQuery:
                        // Fire and forget: a single GET can take a couple seconds and
                        // must not block the read loop.
                        if (OnSnmpOidQuery is { } oidHandler)
                            _ = oidHandler(message.SnmpOidQuery, linked.Token);
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
                    case ServerMessage.PayloadOneofCase.ResultAck:
                        OnResultAck?.Invoke((long)message.ResultAck.Sequence);
                        break;
                }
            }
        }
        finally
        {
            linked.Cancel();
            _outbound.Writer.TryComplete();
            try { await Task.WhenAll(pumpTask, heartbeatTask, watchdogTask); } catch (OperationCanceledException) { }
        }
    }

    /// <summary>
    /// Forces a reconnect when nothing has arrived from the server past the
    /// silence timeout. Cancelling the connection's source aborts the read loop;
    /// the buffer keeps every unacked frame, so the reconnect replays them and a
    /// forced reconnect never loses data.
    /// </summary>
    private async Task WatchInboundSilenceAsync(CancellationTokenSource connectionCts, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(15), ct);
            var silentMs = Environment.TickCount64 - Volatile.Read(ref _lastInboundTicks);
            if (silentMs <= InboundSilenceTimeoutMs) continue;
            Console.Error.WriteLine(
                $"Tunnel silent for {silentMs / 1000}s (server config refresh is 60s); assuming dead link, reconnecting - results keep buffering");
            connectionCts.Cancel();
            return;
        }
    }

    /// <summary>
    /// Feeds this connection from the store-and-forward buffer until the tunnel
    /// tears down. Frames are only PEEKED - each stays in the buffer until the
    /// server acks it (<see cref="OnResultAck"/>), so a teardown mid-flush loses
    /// nothing: every unacked frame replays on the next connection. FIFO order is
    /// preserved end to end (the server's interface rate calculator needs
    /// per-interface samples in chronological order): a fresh connection replays
    /// from sequence 0, i.e. the oldest unacked frame.
    /// </summary>
    public async Task DrainResultsAsync(ResultBuffer buffer, CancellationToken ct)
    {
        long sentThroughSeq = 0;
        while (!ct.IsCancellationRequested)
        {
            var frame = await buffer.TakeUnsentBatchAsync(sentThroughSeq, CoalesceMaxSamples, ct);

            // Leave headroom so heartbeats and proxied console traffic still get
            // through while a large backlog flushes.
            while (_outbound.Reader.Count > OutboundHeadroomSlots)
                await Task.Delay(20, ct);

            // Tag the frame so the server can ack it; the buffer keeps the
            // originals until that ack lands, so a black-holed write is not a loss.
            frame.Message.Sequence = (ulong)frame.ThroughSeq;
            if (!await SendAsync(frame.Message, ct))
                return; // tunnel torn down; the buffer still holds every unacked frame
            sentThroughSeq = frame.ThroughSeq;

            // Against a server that can't ack (older build), no ResultAck will
            // ever arrive, so trim on send to avoid an ever-growing buffer that
            // re-flushes on every reconnect. Cumulative, so it also clears any
            // frames sent before the hello revealed the server was ack-less.
            if (_ackless)
                buffer.MarkAcked(frame.ThroughSeq);
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
