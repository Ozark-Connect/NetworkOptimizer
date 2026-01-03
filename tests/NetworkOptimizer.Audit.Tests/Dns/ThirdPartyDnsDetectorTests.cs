using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using NetworkOptimizer.Audit.Dns;
using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.Core.Enums;
using Xunit;

namespace NetworkOptimizer.Audit.Tests.Dns;

public class ThirdPartyDnsDetectorTests
{
    private readonly Mock<ILogger<ThirdPartyDnsDetector>> _loggerMock;

    public ThirdPartyDnsDetectorTests()
    {
        _loggerMock = new Mock<ILogger<ThirdPartyDnsDetector>>();
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
        var piholeResponse = @"{""status"":""enabled"",""dns_queries_today"":1234,""ads_blocked_today"":567}";
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
    public async Task DetectThirdPartyDnsAsync_PiholeWithVersion_ExtractsVersion()
    {
        var piholeResponse = @"{""status"":""enabled"",""dns_queries_today"":1234,""version"":""5.17.1""}";
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
        result[0].PiholeVersion.Should().Be("5.17.1");
    }

    [Fact]
    public async Task DetectThirdPartyDnsAsync_PiholeWithGravity_DetectsAsPihole()
    {
        var piholeResponse = @"{""gravity_last_updated"":{},""status"":""enabled""}";
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
        // HTTP handler should only be called once per unique IP (3 attempts: port 80, 4711, 443)
        callCount.Should().BeLessThanOrEqualTo(3);
    }

    #endregion
}
