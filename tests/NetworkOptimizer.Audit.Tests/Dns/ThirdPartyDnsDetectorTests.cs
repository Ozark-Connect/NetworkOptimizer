using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using NetworkOptimizer.Audit.Dns;
using NetworkOptimizer.Audit.Models;
using Xunit;

namespace NetworkOptimizer.Audit.Tests.Dns;

public class ThirdPartyDnsDetectorTests : IDisposable
{
    private readonly Mock<ILogger<ThirdPartyDnsDetector>> _loggerMock;

    public ThirdPartyDnsDetectorTests()
    {
        // Mock DNS resolver to avoid real network calls and timeouts
        DohProviderRegistry.DnsResolver = _ => Task.FromResult<string?>(null);
        _loggerMock = new Mock<ILogger<ThirdPartyDnsDetector>>();
    }

    public void Dispose()
    {
        DohProviderRegistry.ResetDnsResolver();
    }

    private ThirdPartyDnsDetector CreateDetector(HttpClient? httpClient = null)
    {
        httpClient ??= new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        return new ThirdPartyDnsDetector(_loggerMock.Object, httpClient);
    }

    private static HttpClient CreateMockHttpClient(HttpStatusCode statusCode, string content = "")
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = statusCode,
                Content = new StringContent(content)
            });

        return new HttpClient(handlerMock.Object) { Timeout = TimeSpan.FromSeconds(3) };
    }

    private static HttpClient CreateTimeoutHttpClient()
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new TaskCanceledException("Request timed out"));

        return new HttpClient(handlerMock.Object) { Timeout = TimeSpan.FromSeconds(3) };
    }

    /// <summary>
    /// Creates a mock HTTP client that returns different responses based on URL path
    /// </summary>
    private static HttpClient CreateUrlAwareMockHttpClient(Dictionary<string, (HttpStatusCode Status, string Content)> responses)
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage request, CancellationToken _) =>
            {
                var path = request.RequestUri?.AbsolutePath ?? "";

                foreach (var kvp in responses)
                {
                    if (path.Contains(kvp.Key))
                    {
                        return new HttpResponseMessage
                        {
                            StatusCode = kvp.Value.Status,
                            Content = new StringContent(kvp.Value.Content)
                        };
                    }
                }

                return new HttpResponseMessage { StatusCode = HttpStatusCode.NotFound };
            });

        return new HttpClient(handlerMock.Object) { Timeout = TimeSpan.FromSeconds(3) };
    }

    /// <summary>
    /// Creates a mock HTTP client that returns different responses based on port and path
    /// </summary>
    private static HttpClient CreatePortAwareMockHttpClient(Dictionary<(int Port, string Path), (HttpStatusCode Status, string Content)> responses)
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage request, CancellationToken _) =>
            {
                var port = request.RequestUri?.Port ?? 0;
                var path = request.RequestUri?.AbsolutePath ?? "";

                foreach (var kvp in responses)
                {
                    if (kvp.Key.Port == port && path.Contains(kvp.Key.Path))
                    {
                        return new HttpResponseMessage
                        {
                            StatusCode = kvp.Value.Status,
                            Content = new StringContent(kvp.Value.Content)
                        };
                    }
                }

                return new HttpResponseMessage { StatusCode = HttpStatusCode.NotFound };
            });

        return new HttpClient(handlerMock.Object) { Timeout = TimeSpan.FromSeconds(3) };
    }

    /// <summary>
    /// Creates a mock HTTP client that returns different responses based on IP and path
    /// </summary>
    private static HttpClient CreateIpAwareMockHttpClient(Dictionary<(string Ip, string Path), (HttpStatusCode Status, string Content)> responses)
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage request, CancellationToken _) =>
            {
                var host = request.RequestUri?.Host ?? "";
                var path = request.RequestUri?.AbsolutePath ?? "";

                foreach (var kvp in responses)
                {
                    if (kvp.Key.Ip == host && path.Contains(kvp.Key.Path))
                    {
                        return new HttpResponseMessage
                        {
                            StatusCode = kvp.Value.Status,
                            Content = new StringContent(kvp.Value.Content)
                        };
                    }
                }

                return new HttpResponseMessage { StatusCode = HttpStatusCode.NotFound };
            });

        return new HttpClient(handlerMock.Object) { Timeout = TimeSpan.FromSeconds(3) };
    }

    #region IsRfc1918Address Tests

    [Theory]
    [InlineData("10.0.0.1", true)]
    [InlineData("10.255.255.255", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.255", true)]
    [InlineData("172.20.1.1", true)]
    [InlineData("192.168.0.1", true)]
    [InlineData("192.168.255.255", true)]
    [InlineData("192.168.1.100", true)]
    public void IsRfc1918Address_PrivateIp_ReturnsTrue(string ip, bool expected)
    {
        var result = ThirdPartyDnsDetector.IsRfc1918Address(ip);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("172.15.0.1")]  // Just below 172.16.x.x range
    [InlineData("172.32.0.1")]  // Just above 172.31.x.x range
    [InlineData("192.167.1.1")] // Not 192.168.x.x
    [InlineData("11.0.0.1")]    // Not 10.x.x.x
    [InlineData("0.0.0.0")]
    [InlineData("255.255.255.255")]
    public void IsRfc1918Address_PublicIp_ReturnsFalse(string ip)
    {
        var result = ThirdPartyDnsDetector.IsRfc1918Address(ip);
        result.Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("invalid")]
    [InlineData("192.168.1.1.1")]
    [InlineData("abc.def.ghi.jkl")]
    public void IsRfc1918Address_InvalidIp_ReturnsFalse(string ip)
    {
        var result = ThirdPartyDnsDetector.IsRfc1918Address(ip);
        result.Should().BeFalse();
    }

    [Fact]
    public void IsRfc1918Address_Ipv6_ReturnsFalse()
    {
        var result = ThirdPartyDnsDetector.IsRfc1918Address("::1");
        result.Should().BeFalse();
    }

    #endregion

    #region DetectThirdPartyDnsAsync - Basic Tests

    [Fact]
    public async Task DetectThirdPartyDnsAsync_EmptyNetworks_ReturnsEmptyList()
    {
        var detector = CreateDetector();
        var networks = new List<NetworkInfo>();

        var result = await detector.DetectThirdPartyDnsAsync(networks);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task DetectThirdPartyDnsAsync_NetworkWithoutDhcp_ReturnsEmptyList()
    {
        var detector = CreateDetector();
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "Corporate",
                VlanId = 10,
                DhcpEnabled = false,
                Gateway = "192.168.1.1",
                DnsServers = new List<string> { "192.168.1.5" }
            }
        };

        var result = await detector.DetectThirdPartyDnsAsync(networks);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task DetectThirdPartyDnsAsync_NetworkWithNoDnsServers_ReturnsEmptyList()
    {
        var detector = CreateDetector();
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "Corporate",
                VlanId = 10,
                DhcpEnabled = true,
                Gateway = "192.168.1.1",
                DnsServers = null
            }
        };

        var result = await detector.DetectThirdPartyDnsAsync(networks);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task DetectThirdPartyDnsAsync_NetworkWithEmptyDnsServers_ReturnsEmptyList()
    {
        var detector = CreateDetector();
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "Corporate",
                VlanId = 10,
                DhcpEnabled = true,
                Gateway = "192.168.1.1",
                DnsServers = new List<string>()
            }
        };

        var result = await detector.DetectThirdPartyDnsAsync(networks);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task DetectThirdPartyDnsAsync_DnsMatchesGateway_ReturnsEmptyList()
    {
        var detector = CreateDetector();
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "Corporate",
                VlanId = 10,
                DhcpEnabled = true,
                Gateway = "192.168.1.1",
                DnsServers = new List<string> { "192.168.1.1" }
            }
        };

        var result = await detector.DetectThirdPartyDnsAsync(networks);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task DetectThirdPartyDnsAsync_PublicDns_ReturnsEmptyList()
    {
        var detector = CreateDetector();
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "Corporate",
                VlanId = 10,
                DhcpEnabled = true,
                Gateway = "192.168.1.1",
                DnsServers = new List<string> { "8.8.8.8", "1.1.1.1" }
            }
        };

        var result = await detector.DetectThirdPartyDnsAsync(networks);

        result.Should().BeEmpty();
    }

    #endregion

    #region DetectThirdPartyDnsAsync - Third-Party Detection Tests

    [Fact]
    public async Task DetectThirdPartyDnsAsync_ThirdPartyLanDns_DetectsCorrectly()
    {
        var httpClient = CreateTimeoutHttpClient(); // Pi-hole probe fails
        var detector = CreateDetector(httpClient);
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "Corporate",
                VlanId = 10,
                DhcpEnabled = true,
                Gateway = "192.168.1.1",
                DnsServers = new List<string> { "192.168.1.5" }
            }
        };

        var result = await detector.DetectThirdPartyDnsAsync(networks);

        result.Should().HaveCount(1);
        result[0].DnsServerIp.Should().Be("192.168.1.5");
        result[0].NetworkName.Should().Be("Corporate");
        result[0].NetworkVlanId.Should().Be(10);
        result[0].IsLanIp.Should().BeTrue();
        result[0].IsPihole.Should().BeFalse();
        result[0].DnsProviderName.Should().Be("Third-Party LAN DNS");
    }

    [Fact]
    public async Task DetectThirdPartyDnsAsync_MultipleDnsServers_DetectsAll()
    {
        var httpClient = CreateTimeoutHttpClient();
        var detector = CreateDetector(httpClient);
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "Corporate",
                VlanId = 10,
                DhcpEnabled = true,
                Gateway = "192.168.1.1",
                DnsServers = new List<string> { "192.168.1.5", "192.168.1.6" }
            }
        };

        var result = await detector.DetectThirdPartyDnsAsync(networks);

        result.Should().HaveCount(2);
        result.Should().Contain(r => r.DnsServerIp == "192.168.1.5");
        result.Should().Contain(r => r.DnsServerIp == "192.168.1.6");
    }

    [Fact]
    public async Task DetectThirdPartyDnsAsync_MultipleNetworks_DetectsAll()
    {
        var httpClient = CreateTimeoutHttpClient();
        var detector = CreateDetector(httpClient);
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "Corporate",
                VlanId = 10,
                DhcpEnabled = true,
                Gateway = "192.168.1.1",
                DnsServers = new List<string> { "192.168.1.5" }
            },
            new NetworkInfo
            {
                Id = "net2",
                Name = "IoT",
                VlanId = 20,
                DhcpEnabled = true,
                Gateway = "192.168.2.1",
                DnsServers = new List<string> { "192.168.1.5" } // Same DNS server
            }
        };

        var result = await detector.DetectThirdPartyDnsAsync(networks);

        result.Should().HaveCount(2);
        result.Should().Contain(r => r.NetworkName == "Corporate");
        result.Should().Contain(r => r.NetworkName == "IoT");
    }

    [Fact]
    public async Task DetectThirdPartyDnsAsync_MixedDnsServers_DetectsOnlyThirdParty()
    {
        var httpClient = CreateTimeoutHttpClient();
        var detector = CreateDetector(httpClient);
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "Corporate",
                VlanId = 10,
                DhcpEnabled = true,
                Gateway = "192.168.1.1",
                DnsServers = new List<string> { "192.168.1.1", "192.168.1.5", "8.8.8.8" }
            }
        };

        var result = await detector.DetectThirdPartyDnsAsync(networks);

        result.Should().HaveCount(1);
        result[0].DnsServerIp.Should().Be("192.168.1.5");
    }

    #endregion

    #region Pi-hole Detection Tests

    [Fact]
    public async Task DetectThirdPartyDnsAsync_PiholeDetected_SetsIsPiholeTrue()
    {
        // Pi-hole v6+ /api/info/login response format
        var piholeResponse = @"{""dns"":true,""https_port"":0,""took"":0.00001}";
        var httpClient = CreateMockHttpClient(HttpStatusCode.OK, piholeResponse);
        var detector = CreateDetector(httpClient);
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "Corporate",
                VlanId = 10,
                DhcpEnabled = true,
                Gateway = "192.168.1.1",
                DnsServers = new List<string> { "192.168.1.5" }
            }
        };

        var result = await detector.DetectThirdPartyDnsAsync(networks);

        result.Should().HaveCount(1);
        result[0].IsPihole.Should().BeTrue();
        result[0].DnsProviderName.Should().Be("Pi-hole");
    }

    [Fact]
    public async Task DetectThirdPartyDnsAsync_PiholeWithDnsTrue_DetectsAsPihole()
    {
        // Pi-hole v6+ /api/info/login response with dns:true indicates Pi-hole
        var piholeResponse = @"{""dns"":true,""https_port"":443,""took"":0.001}";
        var httpClient = CreateMockHttpClient(HttpStatusCode.OK, piholeResponse);
        var detector = CreateDetector(httpClient);
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "Corporate",
                VlanId = 10,
                DhcpEnabled = true,
                Gateway = "192.168.1.1",
                DnsServers = new List<string> { "192.168.1.5" }
            }
        };

        var result = await detector.DetectThirdPartyDnsAsync(networks);

        result.Should().HaveCount(1);
        result[0].IsPihole.Should().BeTrue();
        result[0].PiholeVersion.Should().Be("detected");
    }

    [Fact]
    public async Task DetectThirdPartyDnsAsync_ResponseWithoutDnsProperty_NotDetectedAsPihole()
    {
        // Response that doesn't contain "dns" property should not be detected as Pi-hole
        var notPiholeResponse = @"{""https_port"":443,""took"":0.001}";
        var httpClient = CreateMockHttpClient(HttpStatusCode.OK, notPiholeResponse);
        var detector = CreateDetector(httpClient);
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "Corporate",
                VlanId = 10,
                DhcpEnabled = true,
                Gateway = "192.168.1.1",
                DnsServers = new List<string> { "192.168.1.5" }
            }
        };

        var result = await detector.DetectThirdPartyDnsAsync(networks);

        result.Should().HaveCount(1);
        result[0].IsPihole.Should().BeFalse();
        result[0].DnsProviderName.Should().Be("Third-Party LAN DNS");
    }

    [Fact]
    public async Task DetectThirdPartyDnsAsync_HttpError_TreatsAsNonPihole()
    {
        var httpClient = CreateMockHttpClient(HttpStatusCode.NotFound);
        var detector = CreateDetector(httpClient);
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "Corporate",
                VlanId = 10,
                DhcpEnabled = true,
                Gateway = "192.168.1.1",
                DnsServers = new List<string> { "192.168.1.5" }
            }
        };

        var result = await detector.DetectThirdPartyDnsAsync(networks);

        result.Should().HaveCount(1);
        result[0].IsPihole.Should().BeFalse();
        result[0].DnsProviderName.Should().Be("Third-Party LAN DNS");
    }

    [Fact]
    public async Task DetectThirdPartyDnsAsync_NonPiholeJsonResponse_TreatsAsNonPihole()
    {
        var nonPiholeResponse = @"{""message"":""Hello World""}";
        var httpClient = CreateMockHttpClient(HttpStatusCode.OK, nonPiholeResponse);
        var detector = CreateDetector(httpClient);
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "Corporate",
                VlanId = 10,
                DhcpEnabled = true,
                Gateway = "192.168.1.1",
                DnsServers = new List<string> { "192.168.1.5" }
            }
        };

        var result = await detector.DetectThirdPartyDnsAsync(networks);

        result.Should().HaveCount(1);
        result[0].IsPihole.Should().BeFalse();
    }

    [Fact]
    public async Task DetectThirdPartyDnsAsync_Timeout_TreatsAsNonPihole()
    {
        var httpClient = CreateTimeoutHttpClient();
        var detector = CreateDetector(httpClient);
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "Corporate",
                VlanId = 10,
                DhcpEnabled = true,
                Gateway = "192.168.1.1",
                DnsServers = new List<string> { "192.168.1.5" }
            }
        };

        var result = await detector.DetectThirdPartyDnsAsync(networks);

        result.Should().HaveCount(1);
        result[0].IsPihole.Should().BeFalse();
        result[0].DnsProviderName.Should().Be("Third-Party LAN DNS");
    }

    #endregion

    #region AdGuard Home Detection Tests

    [Fact]
    public async Task DetectThirdPartyDnsAsync_AdGuardHomeDetected_SetsIsAdGuardHomeTrue()
    {
        // AdGuard Home login.html with JS reference
        var loginHtml = @"<html><head><script src=""login.abc123.js""></script></head></html>";
        var jsContent = @"var app = { name: 'AdGuard Home' };";

        var httpClient = CreateUrlAwareMockHttpClient(new Dictionary<string, (HttpStatusCode, string)>
        {
            { "/api/info/login", (HttpStatusCode.NotFound, "") },
            { "/login.html", (HttpStatusCode.OK, loginHtml) },
            { "/login.abc123.js", (HttpStatusCode.OK, jsContent) }
        });

        var detector = CreateDetector(httpClient);
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "Corporate",
                VlanId = 10,
                DhcpEnabled = true,
                Gateway = "192.168.1.1",
                DnsServers = new List<string> { "192.168.1.5" }
            }
        };

        var result = await detector.DetectThirdPartyDnsAsync(networks);

        result.Should().HaveCount(1);
        result[0].IsAdGuardHome.Should().BeTrue();
        result[0].IsPihole.Should().BeFalse();
        result[0].DnsProviderName.Should().Be("AdGuard Home");
    }

    [Fact]
    public async Task DetectThirdPartyDnsAsync_AdGuardHomeWithVersion_DetectsVersion()
    {
        var loginHtml = @"<html><head><script src=""login.v0.107.js""></script></head></html>";
        var jsContent = @"AdGuard Home v0.107.0";

        var httpClient = CreateUrlAwareMockHttpClient(new Dictionary<string, (HttpStatusCode, string)>
        {
            { "/api/info/login", (HttpStatusCode.NotFound, "") },
            { "/login.html", (HttpStatusCode.OK, loginHtml) },
            { "/login.v0.107.js", (HttpStatusCode.OK, jsContent) }
        });

        var detector = CreateDetector(httpClient);
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "Test Network",
                VlanId = 20,
                DhcpEnabled = true,
                Gateway = "192.168.1.1",
                DnsServers = new List<string> { "192.168.1.10" }
            }
        };

        var result = await detector.DetectThirdPartyDnsAsync(networks);

        result.Should().HaveCount(1);
        result[0].IsAdGuardHome.Should().BeTrue();
        result[0].AdGuardHomeVersion.Should().Be("detected");
    }

    [Fact]
    public async Task DetectThirdPartyDnsAsync_AdGuardHomeNoJsMatch_NotDetected()
    {
        // login.html without proper JS reference
        var loginHtml = @"<html><head><script src=""other.js""></script></head></html>";

        var httpClient = CreateUrlAwareMockHttpClient(new Dictionary<string, (HttpStatusCode, string)>
        {
            { "/api/info/login", (HttpStatusCode.NotFound, "") },
            { "/login.html", (HttpStatusCode.OK, loginHtml) }
        });

        var detector = CreateDetector(httpClient);
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "Corporate",
                VlanId = 10,
                DhcpEnabled = true,
                Gateway = "192.168.1.1",
                DnsServers = new List<string> { "192.168.1.5" }
            }
        };

        var result = await detector.DetectThirdPartyDnsAsync(networks);

        result.Should().HaveCount(1);
        result[0].IsAdGuardHome.Should().BeFalse();
        result[0].IsPihole.Should().BeFalse();
        result[0].DnsProviderName.Should().Be("Third-Party LAN DNS");
    }

    [Fact]
    public async Task DetectThirdPartyDnsAsync_AdGuardHomeJsWithoutAdGuardString_NotDetected()
    {
        var loginHtml = @"<html><head><script src=""login.abc.js""></script></head></html>";
        var jsContent = @"var app = { name: 'Some Other App' };";

        var httpClient = CreateUrlAwareMockHttpClient(new Dictionary<string, (HttpStatusCode, string)>
        {
            { "/api/info/login", (HttpStatusCode.NotFound, "") },
            { "/login.html", (HttpStatusCode.OK, loginHtml) },
            { "/login.abc.js", (HttpStatusCode.OK, jsContent) }
        });

        var detector = CreateDetector(httpClient);
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "Corporate",
                VlanId = 10,
                DhcpEnabled = true,
                Gateway = "192.168.1.1",
                DnsServers = new List<string> { "192.168.1.5" }
            }
        };

        var result = await detector.DetectThirdPartyDnsAsync(networks);

        result.Should().HaveCount(1);
        result[0].IsAdGuardHome.Should().BeFalse();
        result[0].DnsProviderName.Should().Be("Third-Party LAN DNS");
    }

    [Fact]
    public async Task DetectThirdPartyDnsAsync_AdGuardHomeLoginPageNotFound_NotDetected()
    {
        var httpClient = CreateUrlAwareMockHttpClient(new Dictionary<string, (HttpStatusCode, string)>
        {
            { "/api/info/login", (HttpStatusCode.NotFound, "") },
            { "/login.html", (HttpStatusCode.NotFound, "") }
        });

        var detector = CreateDetector(httpClient);
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "Corporate",
                VlanId = 10,
                DhcpEnabled = true,
                Gateway = "192.168.1.1",
                DnsServers = new List<string> { "192.168.1.5" }
            }
        };

        var result = await detector.DetectThirdPartyDnsAsync(networks);

        result.Should().HaveCount(1);
        result[0].IsAdGuardHome.Should().BeFalse();
        result[0].IsPihole.Should().BeFalse();
    }

    [Fact]
    public async Task DetectThirdPartyDnsAsync_AdGuardHomeJsFileFails_NotDetected()
    {
        var loginHtml = @"<html><head><script src=""login.abc.js""></script></head></html>";

        var httpClient = CreateUrlAwareMockHttpClient(new Dictionary<string, (HttpStatusCode, string)>
        {
            { "/api/info/login", (HttpStatusCode.NotFound, "") },
            { "/login.html", (HttpStatusCode.OK, loginHtml) },
            { "/login.abc.js", (HttpStatusCode.InternalServerError, "") }
        });

        var detector = CreateDetector(httpClient);
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "Corporate",
                VlanId = 10,
                DhcpEnabled = true,
                Gateway = "192.168.1.1",
                DnsServers = new List<string> { "192.168.1.5" }
            }
        };

        var result = await detector.DetectThirdPartyDnsAsync(networks);

        result.Should().HaveCount(1);
        result[0].IsAdGuardHome.Should().BeFalse();
    }

    #endregion

    #region Detection Priority Tests (Pi-hole takes precedence)

    [Fact]
    public async Task DetectThirdPartyDnsAsync_PiholeTakesPrecedenceOverAdGuardHome()
    {
        // Both Pi-hole and AdGuard Home endpoints respond, but Pi-hole is checked first
        var piholeResponse = @"{""dns"":true,""https_port"":0,""took"":0.00001}";
        var loginHtml = @"<html><head><script src=""login.abc.js""></script></head></html>";
        var jsContent = @"AdGuard Home";

        var httpClient = CreateUrlAwareMockHttpClient(new Dictionary<string, (HttpStatusCode, string)>
        {
            { "/api/info/login", (HttpStatusCode.OK, piholeResponse) },
            { "/login.html", (HttpStatusCode.OK, loginHtml) },
            { "/login.abc.js", (HttpStatusCode.OK, jsContent) }
        });

        var detector = CreateDetector(httpClient);
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "Corporate",
                VlanId = 10,
                DhcpEnabled = true,
                Gateway = "192.168.1.1",
                DnsServers = new List<string> { "192.168.1.5" }
            }
        };

        var result = await detector.DetectThirdPartyDnsAsync(networks);

        result.Should().HaveCount(1);
        result[0].IsPihole.Should().BeTrue();
        result[0].IsAdGuardHome.Should().BeFalse();
        result[0].DnsProviderName.Should().Be("Pi-hole");
    }

    [Fact]
    public async Task DetectThirdPartyDnsAsync_PiholeFailsAdGuardHomeSucceeds()
    {
        // Pi-hole endpoint fails, AdGuard Home is tried next
        var loginHtml = @"<html><head><script src=""login.xyz.js""></script></head></html>";
        var jsContent = @"AdGuard Home Dashboard";

        var httpClient = CreateUrlAwareMockHttpClient(new Dictionary<string, (HttpStatusCode, string)>
        {
            { "/api/info/login", (HttpStatusCode.NotFound, "") },
            { "/login.html", (HttpStatusCode.OK, loginHtml) },
            { "/login.xyz.js", (HttpStatusCode.OK, jsContent) }
        });

        var detector = CreateDetector(httpClient);
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "Corporate",
                VlanId = 10,
                DhcpEnabled = true,
                Gateway = "192.168.1.1",
                DnsServers = new List<string> { "192.168.1.5" }
            }
        };

        var result = await detector.DetectThirdPartyDnsAsync(networks);

        result.Should().HaveCount(1);
        result[0].IsPihole.Should().BeFalse();
        result[0].IsAdGuardHome.Should().BeTrue();
        result[0].DnsProviderName.Should().Be("AdGuard Home");
    }

    [Fact]
    public async Task DetectThirdPartyDnsAsync_BothProbesFail_GenericThirdPartyDns()
    {
        var httpClient = CreateUrlAwareMockHttpClient(new Dictionary<string, (HttpStatusCode, string)>
        {
            { "/api/info/login", (HttpStatusCode.NotFound, "") },
            { "/login.html", (HttpStatusCode.NotFound, "") }
        });

        var detector = CreateDetector(httpClient);
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "Corporate",
                VlanId = 10,
                DhcpEnabled = true,
                Gateway = "192.168.1.1",
                DnsServers = new List<string> { "192.168.1.5" }
            }
        };

        var result = await detector.DetectThirdPartyDnsAsync(networks);

        result.Should().HaveCount(1);
        result[0].IsPihole.Should().BeFalse();
        result[0].IsAdGuardHome.Should().BeFalse();
        result[0].DnsProviderName.Should().Be("Third-Party LAN DNS");
    }

    #endregion

    #region Custom Port Tests

    [Fact]
    public async Task DetectThirdPartyDnsAsync_CustomPort_PiholeDetected()
    {
        var piholeResponse = @"{""dns"":true}";
        var httpClient = CreatePortAwareMockHttpClient(new Dictionary<(int, string), (HttpStatusCode, string)>
        {
            { (8888, "/api/info/login"), (HttpStatusCode.OK, piholeResponse) }
        });

        var detector = CreateDetector(httpClient);
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "Corporate",
                VlanId = 10,
                DhcpEnabled = true,
                Gateway = "192.168.1.1",
                DnsServers = new List<string> { "192.168.1.5" }
            }
        };

        var result = await detector.DetectThirdPartyDnsAsync(networks, customPort: 8888);

        result.Should().HaveCount(1);
        result[0].IsPihole.Should().BeTrue();
    }

    [Fact]
    public async Task DetectThirdPartyDnsAsync_CustomPort_AdGuardHomeDetected()
    {
        var loginHtml = @"<html><head><script src=""login.custom.js""></script></head></html>";
        var jsContent = @"AdGuard Home";

        var httpClient = CreatePortAwareMockHttpClient(new Dictionary<(int, string), (HttpStatusCode, string)>
        {
            { (3080, "/api/info/login"), (HttpStatusCode.NotFound, "") },
            { (3080, "/login.html"), (HttpStatusCode.OK, loginHtml) },
            { (3080, "/login.custom.js"), (HttpStatusCode.OK, jsContent) }
        });

        var detector = CreateDetector(httpClient);
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "Corporate",
                VlanId = 10,
                DhcpEnabled = true,
                Gateway = "192.168.1.1",
                DnsServers = new List<string> { "192.168.1.5" }
            }
        };

        var result = await detector.DetectThirdPartyDnsAsync(networks, customPort: 3080);

        result.Should().HaveCount(1);
        result[0].IsAdGuardHome.Should().BeTrue();
        result[0].DnsProviderName.Should().Be("AdGuard Home");
    }

    [Fact]
    public async Task DetectThirdPartyDnsAsync_CustomPortFails_FallsBackToDefaultPorts()
    {
        var piholeResponse = @"{""dns"":true}";
        var httpClient = CreatePortAwareMockHttpClient(new Dictionary<(int, string), (HttpStatusCode, string)>
        {
            { (9999, "/api/info/login"), (HttpStatusCode.NotFound, "") }, // Custom port fails
            { (80, "/api/info/login"), (HttpStatusCode.OK, piholeResponse) } // Default port succeeds
        });

        var detector = CreateDetector(httpClient);
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "Corporate",
                VlanId = 10,
                DhcpEnabled = true,
                Gateway = "192.168.1.1",
                DnsServers = new List<string> { "192.168.1.5" }
            }
        };

        var result = await detector.DetectThirdPartyDnsAsync(networks, customPort: 9999);

        result.Should().HaveCount(1);
        result[0].IsPihole.Should().BeTrue();
    }

    [Fact]
    public async Task DetectThirdPartyDnsAsync_ZeroCustomPort_IgnoredUsesDefaults()
    {
        var piholeResponse = @"{""dns"":true}";
        var httpClient = CreatePortAwareMockHttpClient(new Dictionary<(int, string), (HttpStatusCode, string)>
        {
            { (80, "/api/info/login"), (HttpStatusCode.OK, piholeResponse) }
        });

        var detector = CreateDetector(httpClient);
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "Corporate",
                VlanId = 10,
                DhcpEnabled = true,
                Gateway = "192.168.1.1",
                DnsServers = new List<string> { "192.168.1.5" }
            }
        };

        var result = await detector.DetectThirdPartyDnsAsync(networks, customPort: 0);

        result.Should().HaveCount(1);
        result[0].IsPihole.Should().BeTrue();
    }

    #endregion

    #region Multiple Networks and Providers Tests

    [Fact]
    public async Task DetectThirdPartyDnsAsync_DifferentProvidersOnDifferentNetworks()
    {
        var piholeResponse = @"{""dns"":true}";
        var loginHtml = @"<html><head><script src=""login.test.js""></script></head></html>";
        var jsContent = @"AdGuard Home";

        var httpClient = CreateIpAwareMockHttpClient(new Dictionary<(string, string), (HttpStatusCode, string)>
        {
            // Pi-hole at 192.168.1.5
            { ("192.168.1.5", "/api/info/login"), (HttpStatusCode.OK, piholeResponse) },
            // AdGuard Home at 192.168.1.10
            { ("192.168.1.10", "/api/info/login"), (HttpStatusCode.NotFound, "") },
            { ("192.168.1.10", "/login.html"), (HttpStatusCode.OK, loginHtml) },
            { ("192.168.1.10", "/login.test.js"), (HttpStatusCode.OK, jsContent) }
        });

        var detector = CreateDetector(httpClient);
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "Corporate",
                VlanId = 10,
                DhcpEnabled = true,
                Gateway = "192.168.1.1",
                DnsServers = new List<string> { "192.168.1.5" }
            },
            new NetworkInfo
            {
                Id = "net2",
                Name = "IoT",
                VlanId = 20,
                DhcpEnabled = true,
                Gateway = "192.168.2.1",
                DnsServers = new List<string> { "192.168.1.10" }
            }
        };

        var result = await detector.DetectThirdPartyDnsAsync(networks);

        result.Should().HaveCount(2);

        var piholeResult = result.First(r => r.NetworkName == "Corporate");
        piholeResult.IsPihole.Should().BeTrue();
        piholeResult.IsAdGuardHome.Should().BeFalse();
        piholeResult.DnsProviderName.Should().Be("Pi-hole");

        var adguardResult = result.First(r => r.NetworkName == "IoT");
        adguardResult.IsPihole.Should().BeFalse();
        adguardResult.IsAdGuardHome.Should().BeTrue();
        adguardResult.DnsProviderName.Should().Be("AdGuard Home");
    }

    [Fact]
    public async Task DetectThirdPartyDnsAsync_SameAdGuardHomeMultipleNetworks_ProbesOnce()
    {
        var loginHtml = @"<html><head><script src=""login.shared.js""></script></head></html>";
        var jsContent = @"AdGuard Home";

        var callCount = 0;
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage request, CancellationToken _) =>
            {
                callCount++;
                var path = request.RequestUri?.AbsolutePath ?? "";

                if (path.Contains("/api/info/login"))
                    return new HttpResponseMessage { StatusCode = HttpStatusCode.NotFound };

                if (path.Contains("/login.html"))
                    return new HttpResponseMessage { StatusCode = HttpStatusCode.OK, Content = new StringContent(loginHtml) };

                if (path.Contains("/login.shared.js"))
                    return new HttpResponseMessage { StatusCode = HttpStatusCode.OK, Content = new StringContent(jsContent) };

                return new HttpResponseMessage { StatusCode = HttpStatusCode.NotFound };
            });

        var httpClient = new HttpClient(handlerMock.Object) { Timeout = TimeSpan.FromSeconds(3) };
        var detector = CreateDetector(httpClient);
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "Network1",
                VlanId = 10,
                DhcpEnabled = true,
                Gateway = "192.168.1.1",
                DnsServers = new List<string> { "192.168.1.5" }
            },
            new NetworkInfo
            {
                Id = "net2",
                Name = "Network2",
                VlanId = 20,
                DhcpEnabled = true,
                Gateway = "192.168.2.1",
                DnsServers = new List<string> { "192.168.1.5" } // Same DNS
            }
        };

        var result = await detector.DetectThirdPartyDnsAsync(networks);

        result.Should().HaveCount(2);
        result.Should().AllSatisfy(r =>
        {
            r.IsAdGuardHome.Should().BeTrue();
            r.DnsProviderName.Should().Be("AdGuard Home");
        });

        // Should only probe once, not twice
        // Pi-hole: 3 ports tried, AdGuard: 1 port (found on first try = 2 requests: login.html + js)
        // Total should be ~5 requests max, not 10
        callCount.Should().BeLessThanOrEqualTo(6);
    }

    [Fact]
    public async Task DetectThirdPartyDnsAsync_NetworkWithBothProvidersDnsServers()
    {
        var piholeResponse = @"{""dns"":true}";
        var loginHtml = @"<html><head><script src=""login.test.js""></script></head></html>";
        var jsContent = @"AdGuard Home";

        var httpClient = CreateIpAwareMockHttpClient(new Dictionary<(string, string), (HttpStatusCode, string)>
        {
            // Pi-hole at 192.168.1.5
            { ("192.168.1.5", "/api/info/login"), (HttpStatusCode.OK, piholeResponse) },
            // AdGuard Home at 192.168.1.6
            { ("192.168.1.6", "/api/info/login"), (HttpStatusCode.NotFound, "") },
            { ("192.168.1.6", "/login.html"), (HttpStatusCode.OK, loginHtml) },
            { ("192.168.1.6", "/login.test.js"), (HttpStatusCode.OK, jsContent) }
        });

        var detector = CreateDetector(httpClient);
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "Corporate",
                VlanId = 10,
                DhcpEnabled = true,
                Gateway = "192.168.1.1",
                DnsServers = new List<string> { "192.168.1.5", "192.168.1.6" }
            }
        };

        var result = await detector.DetectThirdPartyDnsAsync(networks);

        result.Should().HaveCount(2);

        var piholeResult = result.First(r => r.DnsServerIp == "192.168.1.5");
        piholeResult.IsPihole.Should().BeTrue();
        piholeResult.DnsProviderName.Should().Be("Pi-hole");

        var adguardResult = result.First(r => r.DnsServerIp == "192.168.1.6");
        adguardResult.IsAdGuardHome.Should().BeTrue();
        adguardResult.DnsProviderName.Should().Be("AdGuard Home");
    }

    #endregion

    #region Edge Cases

    [Fact]
    public async Task DetectThirdPartyDnsAsync_NullDnsServerInList_SkipsNull()
    {
        var httpClient = CreateTimeoutHttpClient();
        var detector = CreateDetector(httpClient);
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "Corporate",
                VlanId = 10,
                DhcpEnabled = true,
                Gateway = "192.168.1.1",
                DnsServers = new List<string> { null!, "", "192.168.1.5" }
            }
        };

        var result = await detector.DetectThirdPartyDnsAsync(networks);

        result.Should().HaveCount(1);
        result[0].DnsServerIp.Should().Be("192.168.1.5");
    }

    [Fact]
    public async Task DetectThirdPartyDnsAsync_DifferentSubnets_DetectsAll()
    {
        var httpClient = CreateTimeoutHttpClient();
        var detector = CreateDetector(httpClient);
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "Corporate",
                VlanId = 10,
                DhcpEnabled = true,
                Gateway = "10.0.0.1",
                DnsServers = new List<string> { "192.168.1.5" } // Different subnet
            }
        };

        var result = await detector.DetectThirdPartyDnsAsync(networks);

        result.Should().HaveCount(1);
        result[0].DnsServerIp.Should().Be("192.168.1.5");
        result[0].IsLanIp.Should().BeTrue();
    }

    [Fact]
    public async Task DetectThirdPartyDnsAsync_SameDnsServerMultipleNetworks_ProbesOnce()
    {
        // Track how many times the HTTP handler is called
        var callCount = 0;
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() =>
            {
                callCount++;
                return new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.NotFound
                };
            });

        var httpClient = new HttpClient(handlerMock.Object) { Timeout = TimeSpan.FromSeconds(3) };
        var detector = CreateDetector(httpClient);
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "net1",
                Name = "Corporate",
                VlanId = 10,
                DhcpEnabled = true,
                Gateway = "192.168.1.1",
                DnsServers = new List<string> { "192.168.1.5" }
            },
            new NetworkInfo
            {
                Id = "net2",
                Name = "IoT",
                VlanId = 20,
                DhcpEnabled = true,
                Gateway = "192.168.2.1",
                DnsServers = new List<string> { "192.168.1.5" } // Same DNS server
            }
        };

        var result = await detector.DetectThirdPartyDnsAsync(networks);

        result.Should().HaveCount(2);
        // HTTP handler should only be called once per unique IP
        // Pi-hole probe: 3 attempts (port 80, 443, 8080)
        // AdGuard Home probe: 3 attempts (port 80, 443, 3000)
        callCount.Should().BeLessThanOrEqualTo(6);
    }

    #endregion
}
