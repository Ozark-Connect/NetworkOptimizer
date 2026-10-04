using System.Net.Sockets;

namespace NetworkOptimizer.Monitoring.Providers;

/// <summary>
/// Opens connections inside a device's own network: directly when this server is on that network,
/// or through the site's agent when it is not. Providers address devices by their real address and
/// leave the path to this, so an address a device hands back (a redirect) is reached the same way.
/// </summary>
public interface IDeviceDialer
{
    /// <summary>A TCP connection to <paramref name="host"/>:<paramref name="port"/> in the device's network.</summary>
    ValueTask<Stream> DialAsync(string host, int port, CancellationToken cancellationToken);

    /// <summary>
    /// An endpoint for a library that opens its own sockets (SSH.NET): the address itself, or a
    /// local endpoint that leads to it.
    /// </summary>
    Task<(string Host, int Port)> ResolveAsync(string host, int port);
}

/// <summary>Dials from this server's own network. The default for a context built without a site.</summary>
public sealed class DirectDeviceDialer : IDeviceDialer
{
    /// <summary>The shared instance.</summary>
    public static readonly DirectDeviceDialer Instance = new();

    private DirectDeviceDialer()
    {
    }

    /// <inheritdoc/>
    public ValueTask<Stream> DialAsync(string host, int port, CancellationToken cancellationToken)
        => ConnectAsync(host, port, cancellationToken);

    /// <inheritdoc/>
    public Task<(string Host, int Port)> ResolveAsync(string host, int port) => Task.FromResult((host, port));

    /// <summary>A plain TCP connection from this server, as SocketsHttpHandler opens one by default.</summary>
    public static async ValueTask<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(host, port, cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
