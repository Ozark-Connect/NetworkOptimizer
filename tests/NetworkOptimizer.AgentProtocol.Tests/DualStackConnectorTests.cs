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

    [Theory]
    [InlineData("127.0.0.1", "127.0.0.1")]
    [InlineData("[::1]", "::1")]
    [InlineData("localhost", "127.0.0.1")]
    public async Task HttpClient_ThroughConnectCallback_ReachesServer(string urlHost, string listenOn)
    {
        // The handler hands the callback a host as written in the URL: a name, or an IP
        // literal (IPv6 included). Each has to resolve and connect.
        using var listener = new TcpListener(IPAddress.Parse(listenOn), 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serve = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            var buffer = new byte[4096];
            var received = "";
            while (!received.Contains("\r\n\r\n"))
                received += System.Text.Encoding.ASCII.GetString(buffer, 0, await stream.ReadAsync(buffer));
            await stream.WriteAsync("HTTP/1.1 204 No Content\r\nConnection: close\r\n\r\n"u8.ToArray());
        });

        using var http = new HttpClient(new SocketsHttpHandler { ConnectCallback = DualStackConnector.ConnectAsync })
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
        using var response = await http.GetAsync($"http://{urlHost}:{port}/");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        await serve;
    }

    [Fact]
    public async Task ConnectAsync_Cancelled_ThrowsOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => DualStackConnector.ConnectAsync(
            [IPAddress.Loopback], 443, TimeSpan.FromMilliseconds(100), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ConnectAsync_NoAddresses_ThrowsSocketException()
    {
        var act = () => DualStackConnector.ConnectAsync(
            [], 443, TimeSpan.FromMilliseconds(100), CancellationToken.None);

        await act.Should().ThrowAsync<SocketException>();
    }
}
