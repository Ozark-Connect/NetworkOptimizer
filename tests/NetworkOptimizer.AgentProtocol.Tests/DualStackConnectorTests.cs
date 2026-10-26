using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using Xunit;

namespace NetworkOptimizer.AgentProtocol.Tests;

public class DualStackConnectorTests
{
    private static readonly IPAddress V6a = IPAddress.Parse("2001:db8::1");
    private static readonly IPAddress V6b = IPAddress.Parse("2001:db8::2");
    private static readonly IPAddress V4a = IPAddress.Parse("192.0.2.1");
    private static readonly IPAddress V4b = IPAddress.Parse("192.0.2.2");

    [Fact]
    public void Interleave_AlternatesFamilies_StartingWithResolverFirst()
    {
        DualStackConnector.Interleave([V6a, V6b, V4a, V4b]).Should().Equal(V6a, V4a, V6b, V4b);
        DualStackConnector.Interleave([V4a, V6a, V6b]).Should().Equal(V4a, V6a, V6b);
    }

    [Fact]
    public void Interleave_SingleFamily_KeepsOrder()
    {
        DualStackConnector.Interleave([V4a, V4b]).Should().Equal(V4a, V4b);
        DualStackConnector.Interleave([]).Should().BeEmpty();
    }

    [Fact]
    public async Task ConnectAsync_UnansweredFirstAddress_FallsBackToNext()
    {
        // 192.0.2.1 (TEST-NET-1) never answers: the SYN either black-holes or fails as unroutable.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var sw = Stopwatch.StartNew();
        using var socket = await DualStackConnector.ConnectAsync(
            [V4a, IPAddress.Loopback], port, TimeSpan.FromMilliseconds(100), CancellationToken.None);

        ((IPEndPoint)socket.RemoteEndPoint!).Address.Should().Be(IPAddress.Loopback);
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ConnectAsync_FailedAttempt_StartsNextWithoutWaitingForDelay()
    {
        // Listening on IPv4 loopback only, so ::1 is refused at once.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var sw = Stopwatch.StartNew();
        using var socket = await DualStackConnector.ConnectAsync(
            [IPAddress.IPv6Loopback, IPAddress.Loopback], port, TimeSpan.FromSeconds(30), CancellationToken.None);

        ((IPEndPoint)socket.RemoteEndPoint!).Address.Should().Be(IPAddress.Loopback);
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ConnectAsync_EveryAddressRefused_ThrowsSocketException()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var act = () => DualStackConnector.ConnectAsync(
            [IPAddress.Loopback], port, TimeSpan.FromMilliseconds(100), CancellationToken.None);

        await act.Should().ThrowAsync<SocketException>();
    }

    [Fact]
    public async Task ConnectAsync_NoAddresses_ThrowsSocketException()
    {
        var act = () => DualStackConnector.ConnectAsync(
            [], 443, TimeSpan.FromMilliseconds(100), CancellationToken.None);

        await act.Should().ThrowAsync<SocketException>();
    }
}
