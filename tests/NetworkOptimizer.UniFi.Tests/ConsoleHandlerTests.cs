using System.Net;
using FluentAssertions;
using NetworkOptimizer.UniFi;
using Xunit;

namespace NetworkOptimizer.UniFi.Tests;

/// <summary>
/// A console reached through an agent tunnel is dialled at a loopback proxy while still being ASKED
/// FOR by its own name, because the URL decides the Host header and the TLS SNI - point the URL at
/// 127.0.0.1 and a console behind a name-routing reverse proxy answers 404 whatever the credentials.
///
/// The tunnelled path runs on a different handler to do that, which is the risk these cover: UniFi's
/// password sign-in is cookie-based, so a handler that forgets to carry a cookie container
/// authenticates and immediately loses the session. Every site in our own fleet uses an API key, so
/// nothing there would notice.
/// </summary>
public class ConsoleHandlerTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(41000)]
    public void BothPathsCarryCookies(int? agentProxyPort)
    {
        var cookies = new CookieContainer();

        using var handler = UniFiApiClient.CreateHandler(cookies, ignoreSslErrors: true, agentProxyPort);

        switch (handler)
        {
            case SocketsHttpHandler sockets:
                sockets.UseCookies.Should().BeTrue();
                sockets.CookieContainer.Should().BeSameAs(cookies);
                sockets.ConnectCallback.Should().NotBeNull("the tunnelled path dials the loopback proxy itself");
                break;
            case HttpClientHandler direct:
                direct.UseCookies.Should().BeTrue();
                direct.CookieContainer.Should().BeSameAs(cookies);
                break;
            default:
                Assert.Fail($"Unexpected handler type {handler.GetType().Name}");
                break;
        }
    }

    [Fact]
    public void OnlyTheTunnelledPathRedirectsTheConnection()
    {
        using var direct = UniFiApiClient.CreateHandler(new CookieContainer(), true, agentProxyPort: null);
        using var tunnelled = UniFiApiClient.CreateHandler(new CookieContainer(), true, agentProxyPort: 41000);

        direct.Should().BeOfType<HttpClientHandler>(
            "a direct console must keep the handler it has always used - nothing about it changed");
        tunnelled.Should().BeOfType<SocketsHttpHandler>();
    }

    [Fact]
    public void CertificateValidationIsBypassedOnBothPathsWhenAsked()
    {
        using var direct = (HttpClientHandler)UniFiApiClient.CreateHandler(new CookieContainer(), true, null);
        using var tunnelled = (SocketsHttpHandler)UniFiApiClient.CreateHandler(new CookieContainer(), true, 41000);

        direct.ServerCertificateCustomValidationCallback.Should().NotBeNull();
        tunnelled.SslOptions.RemoteCertificateValidationCallback.Should()
            .NotBeNull("consoles use self-signed certificates whichever way they are reached");
    }
}
