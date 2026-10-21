using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using NetworkOptimizer.Threats.CrowdSec;
using NetworkOptimizer.Threats.Models;
using Xunit;

namespace NetworkOptimizer.Threats.Tests;

/// <summary>
/// Tests the centralized private-IP guard on CrowdSecEnrichmentService.
/// Private IPs must short-circuit BEFORE any cache read or API call - otherwise
/// background hydration on RFC1918 sources would burn through the daily quota for
/// reputation data that CrowdSec's CTI does not have.
/// </summary>
public class CrowdSecEnrichmentServiceTests
{
    private readonly Mock<ILogger<CrowdSecClient>> _clientLogger = new();
    private readonly Mock<ILogger<CrowdSecEnrichmentService>> _serviceLogger = new();
    private readonly Mock<IHttpClientFactory> _httpFactory = new();

    private CrowdSecEnrichmentService MakeService(ICtiReputationCache cache)
    {
        var client = new CrowdSecClient(_httpFactory.Object, _clientLogger.Object);
        return new CrowdSecEnrichmentService(client, cache, _serviceLogger.Object);
    }

    [Theory]
    [InlineData("10.99.99.99")]
    [InlineData("192.168.1.5")]
    [InlineData("172.16.5.5")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.10.10")]
    [InlineData("100.64.1.1")]
    public async Task GetReputationAsync_PrivateIp_ReturnsNotApplicable_WithoutTouchingCacheOrApi(string ip)
    {
        // Strict mock: any unconfigured call throws. If the guard fails to fire,
        // GetAsync would be called and this test would fail.
        var cache = new Mock<ICtiReputationCache>(MockBehavior.Strict);
        var svc = MakeService(cache.Object);

        var (info, outcome) = await svc.GetReputationAsync(ip, "fake-api-key");

        info.Should().BeNull();
        outcome.Should().Be(CrowdSecLookupOutcome.NotApplicable);
        cache.Verify(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        cache.Verify(c => c.SaveAsync(It.IsAny<CrowdSecReputation>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetReputationAsync_PublicIp_DoesNotShortCircuit()
    {
        // Verifies the guard does not over-block. A public IP hits the cache first (which returns
        // null here) then would query the API. There is no HttpClient wired up; the test just
        // verifies the cache was consulted before falling through.
        var cache = new Mock<ICtiReputationCache>();
        cache.Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CrowdSecReputation?)null);
        var svc = MakeService(cache.Object);

        var (_, outcome) = await svc.GetReputationAsync("8.8.8.8", "fake-key");

        cache.Verify(c => c.GetAsync("8.8.8.8", It.IsAny<CancellationToken>()), Times.Once);
        outcome.Should().NotBe(CrowdSecLookupOutcome.NotApplicable);
    }

    [Fact]
    public async Task GetReputationAsync_CachedAnswer_SkipsTheApiWhicheverKeyAsks()
    {
        // One cache serves every key: an answer paid for with one key is reused by another.
        var cache = new Mock<ICtiReputationCache>();
        cache.Setup(c => c.GetAsync("198.51.100.7", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CrowdSecReputation { Ip = "198.51.100.7", ReputationJson = "{\"reputation\":\"malicious\"}" });
        var svc = MakeService(cache.Object);

        var (info, outcome) = await svc.GetReputationAsync("198.51.100.7", "another-tenants-key");

        outcome.Should().Be(CrowdSecLookupOutcome.Success);
        info!.Reputation.Should().Be("malicious");
        _httpFactory.Verify(f => f.CreateClient(It.IsAny<string>()), Times.Never);
    }
}
