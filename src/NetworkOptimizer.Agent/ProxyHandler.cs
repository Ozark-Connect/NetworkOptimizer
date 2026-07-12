using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Google.Protobuf;
using NetworkOptimizer.AgentProtocol;
using NetworkOptimizer.Core.Helpers;

namespace NetworkOptimizer.Agent;

/// <summary>
/// The site side of tunnel TCP proxying: the server asks for a connection to
/// a site-local host:port (SSH, the UniFi Console), this handler dials it and
/// pumps bytes both ways under the server-assigned connection id. Lives and
/// dies with its tunnel connection, like the probe runner.
/// </summary>
/// <remarks>
/// Security model: dial targets are gated by a <see cref="ProxyDialPolicy"/> the
/// agent owns - site-local addresses only by default, or the operator's pinned
/// CIDR list from agent.json ("proxyAllowedCidrs"). Nothing on the tunnel can
/// widen it, so a compromised central server cannot use this site as a relay to
/// the internet, and an operator pin caps its LAN reach through this path to
/// exactly the addresses listed. Every dial is journaled locally (allow and
/// deny), an audit trail the server cannot suppress. What this deliberately does
/// NOT claim: containment of a compromised server that holds gateway SSH
/// credentials - the gateway is the LAN router, so that reach is LAN-wide
/// regardless; protecting the central server remains the primary defense (see
/// the agent README "Security and hardening" section and TODO.md). Hostnames are
/// resolved once, every resolved address is validated, and the dial uses the
/// validated addresses - a name that resolves to any out-of-scope address is
/// refused outright rather than filtered, so DNS games can't split the check
/// from the connect.
/// </remarks>
public sealed class ProxyHandler
{
    private const int FrameBytes = 32 * 1024;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    private readonly TunnelClient _tunnel;
    private readonly ProxyDialPolicy _policy;
    private readonly ConcurrentDictionary<long, TcpClient> _connections = new();

    public ProxyHandler(TunnelClient tunnel, ProxyDialPolicy policy)
    {
        _tunnel = tunnel;
        _policy = policy;
    }

    public async Task HandleOpenAsync(ProxyOpen open, CancellationToken ct)
    {
        IPAddress[] addresses;
        if (IPAddress.TryParse(open.Host, out var literal))
        {
            addresses = new[] { literal };
        }
        else
        {
            try
            {
                addresses = await Dns.GetHostAddressesAsync(open.Host, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await SendOpenFailureAsync(open.ConnectionId, $"could not resolve '{open.Host}': {ex.Message}", ct);
                return;
            }
            if (addresses.Length == 0)
            {
                await SendOpenFailureAsync(open.ConnectionId, $"'{open.Host}' resolved to no addresses", ct);
                return;
            }
        }

        // All-or-nothing: one out-of-scope address in the resolution refuses the
        // whole dial instead of narrowing to the in-scope subset.
        var offender = addresses.FirstOrDefault(a => !_policy.IsAllowed(a));
        if (offender != null)
        {
            var scope = _policy.IsDefault ? "site-local scope" : "the operator-pinned proxy scope";
            Console.Error.WriteLine($"Proxy dial DENIED: {open.Host}:{open.Port} (conn {open.ConnectionId}) - {offender} is outside {scope}");
            await SendOpenFailureAsync(open.ConnectionId,
                $"proxy target denied by agent policy: {offender} is outside {scope}", ct);
            return;
        }

        Console.WriteLine($"Proxy dial: {open.Host}:{open.Port} (conn {open.ConnectionId})");

        var client = new TcpClient();
        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(ConnectTimeout);
            // Dial the validated addresses, never the name - re-resolving here
            // would let a changed DNS answer bypass the policy check above.
            await client.ConnectAsync(addresses, open.Port, connectCts.Token);
        }
        catch (Exception ex)
        {
            client.Dispose();
            var reason = ex is OperationCanceledException ? "connect timed out" : ex.Message;
            Console.Error.WriteLine($"Proxy dial failed: {open.Host}:{open.Port} (conn {open.ConnectionId}) - {reason}");
            await SendOpenFailureAsync(open.ConnectionId, reason, ct);
            return;
        }

        _connections[open.ConnectionId] = client;
        await _tunnel.SendAsync(new AgentMessage
        {
            ProxyOpenResult = new ProxyOpenResult { ConnectionId = open.ConnectionId, Ok = true }
        }, ct);

        // Site-to-server pump; the server-to-site direction arrives via
        // HandleDataAsync from the tunnel read loop.
        var buffer = new byte[FrameBytes];
        try
        {
            var stream = client.GetStream();
            while (!ct.IsCancellationRequested)
            {
                int read;
                try { read = await stream.ReadAsync(buffer, ct); }
                catch { break; }
                if (read <= 0) break;
                var ok = await _tunnel.SendAsync(new AgentMessage
                {
                    ProxyData = new ProxyData { ConnectionId = open.ConnectionId, Data = ByteString.CopyFrom(buffer, 0, read) }
                }, ct);
                if (!ok) break;
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (_connections.TryRemove(open.ConnectionId, out var removed))
            {
                _tunnel.TrySend(new AgentMessage
                {
                    ProxyClose = new ProxyClose { ConnectionId = open.ConnectionId }
                });
                try { removed.Dispose(); } catch { }
            }
        }
    }

    private async Task SendOpenFailureAsync(long connectionId, string error, CancellationToken ct) =>
        await _tunnel.SendAsync(new AgentMessage
        {
            ProxyOpenResult = new ProxyOpenResult
            {
                ConnectionId = connectionId,
                Ok = false,
                Error = error,
            }
        }, ct);

    public async Task HandleDataAsync(ProxyData data, CancellationToken ct)
    {
        if (!_connections.TryGetValue(data.ConnectionId, out var client)) return;
        try
        {
            await client.GetStream().WriteAsync(data.Data.Memory, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            HandleClose(new ProxyClose { ConnectionId = data.ConnectionId });
        }
    }

    public void HandleClose(ProxyClose close)
    {
        if (_connections.TryRemove(close.ConnectionId, out var client))
        {
            try { client.Dispose(); } catch { }
        }
    }
}
