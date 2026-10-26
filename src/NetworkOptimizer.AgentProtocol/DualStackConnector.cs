using System.Net;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;

namespace NetworkOptimizer.AgentProtocol;

/// <summary>
/// Happy Eyeballs (RFC 8305) connect for the agent's connections to the central server.
/// .NET's default connect tries the resolved addresses one at a time, so a site whose IPv6
/// path black-holes SYNs spends the whole request timeout on the AAAA address and never
/// reaches IPv4. This races the address families instead: the next address starts after
/// <see cref="AttemptDelay"/>, or at once when an attempt fails, and the first connect wins.
/// </summary>
public static class DualStackConnector
{
    /// <summary>RFC 8305's recommended Connection Attempt Delay.</summary>
    public static readonly TimeSpan AttemptDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>Drop-in <see cref="SocketsHttpHandler.ConnectCallback"/>.</summary>
    public static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
        var socket = await ConnectAsync(addresses, context.DnsEndPoint.Port, AttemptDelay, ct);
        return new NetworkStream(socket, ownsSocket: true);
    }

    /// <summary>
    /// Connects to the first address that answers. Throws the last connect error when every
    /// address fails, and <see cref="OperationCanceledException"/> when <paramref name="ct"/> fires.
    /// </summary>
    public static async Task<Socket> ConnectAsync(
        IReadOnlyList<IPAddress> addresses, int port, TimeSpan attemptDelay, CancellationToken ct)
    {
        var ordered = Interleave(addresses);
        if (ordered.Count == 0)
            throw new SocketException((int)SocketError.HostNotFound);

        using var attemptsCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pending = new List<Task<Socket>>();
        Exception? lastError = null;
        var next = 0;
        try
        {
            while (true)
            {
                if (next < ordered.Count)
                    pending.Add(AttemptAsync(ordered[next++], port, attemptsCts.Token));
                if (pending.Count == 0)
                    break;

                // Wake on the first attempt to finish, or when the next address is due to start.
                var waitOn = pending.Cast<Task>().ToList();
                if (next < ordered.Count)
                    waitOn.Add(Task.Delay(attemptDelay, attemptsCts.Token));
                var finished = await Task.WhenAny(waitOn);
                ct.ThrowIfCancellationRequested();

                if (finished is not Task<Socket> attempt)
                    continue;
                pending.Remove(attempt);
                if (attempt.IsCompletedSuccessfully)
                    return attempt.Result;
                lastError = attempt.Exception?.InnerException ?? lastError;
            }
        }
        finally
        {
            // Losers: cancel them, and close any that connected after the winner.
            attemptsCts.Cancel();
            foreach (var loser in pending)
            {
                _ = loser.ContinueWith(t =>
                {
                    if (t.IsCompletedSuccessfully)
                        t.Result.Dispose();
                    else
                        _ = t.Exception;
                }, TaskScheduler.Default);
            }
        }

        ExceptionDispatchInfo.Throw(lastError ?? new SocketException((int)SocketError.HostUnreachable));
        return null!;
    }

    /// <summary>
    /// Alternates address families, starting with the family the resolver listed first
    /// (RFC 8305 section 4), and keeps the resolver's order within each family.
    /// </summary>
    public static List<IPAddress> Interleave(IReadOnlyList<IPAddress> addresses)
    {
        if (addresses.Count == 0)
            return [];
        var firstFamily = addresses[0].AddressFamily;
        var first = addresses.Where(a => a.AddressFamily == firstFamily).ToList();
        var other = addresses.Where(a => a.AddressFamily != firstFamily).ToList();
        var result = new List<IPAddress>(addresses.Count);
        for (var i = 0; i < Math.Max(first.Count, other.Count); i++)
        {
            if (i < first.Count) result.Add(first[i]);
            if (i < other.Count) result.Add(other[i]);
        }
        return result;
    }

    private static async Task<Socket> AttemptAsync(IPAddress address, int port, CancellationToken ct)
    {
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, port), ct);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
