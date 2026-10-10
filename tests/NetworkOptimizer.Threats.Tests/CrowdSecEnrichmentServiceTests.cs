using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using NetworkOptimizer.Threats.CrowdSec;
using NetworkOptimizer.Threats.Interfaces;
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

    private CrowdSecEnrichmentService MakeService()
    {
        var client = new CrowdSecClient(_httpFactory.Object, _clientLogger.Object);
        return new CrowdSecEnrichmentService(client, _serviceLogger.Object);
    }

    [Theory]
    [InlineData("10.99.99.99")]
    [InlineData("192.168.1.5")]
    [InlineData("172.16.5.5")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.10.10")]
    public async Task GetReputationAsync_PrivateIp_ReturnsNotApplicable_WithoutTouchingCacheOrApi(string ip)
    {
        var svc = MakeService();
        var repo = new Mock<IThreatRepository>(MockBehavior.Strict);
        // Strict mock: any unconfigured call throws. If the guard fails to fire,
        // GetCrowdSecCacheAsync would be called and this test would fail.

        var (info, outcome) = await svc.GetReputationAsync(ip, "fake-api-key", repo.Object);

        info.Should().BeNull();
        outcome.Should().Be(CrowdSecLookupOutcome.NotApplicable);
        repo.Verify(r => r.GetCrowdSecCacheAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        repo.Verify(r => r.SaveCrowdSecCacheAsync(It.IsAny<CrowdSecReputation>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetReputationAsync_PublicIp_DoesNotShortCircuit()
    {
        // Verifies the guard is RFC1918-only and does not over-block. A public IP
        // hits the cache first (which returns null here) then would query the API.
        // We do not run the API since there is no HttpClient wired up; the test just
        // verifies the cache layer was consulted before falling through.
        var svc = MakeService();
        var repo = new Mock<IThreatRepository>();
        repo.Setup(r => r.GetCrowdSecCacheAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CrowdSecReputation?)null);

        var (_, outcome) = await svc.GetReputationAsync("8.8.8.8", "fake-key", repo.Object);

        repo.Verify(r => r.GetCrowdSecCacheAsync("8.8.8.8", It.IsAny<CancellationToken>()), Times.Once);
        outcome.Should().NotBe(CrowdSecLookupOutcome.NotApplicable);
    }
}
