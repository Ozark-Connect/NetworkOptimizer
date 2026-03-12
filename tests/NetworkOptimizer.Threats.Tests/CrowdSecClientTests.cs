using Microsoft.Extensions.Logging;
using Moq;
using NetworkOptimizer.Threats.CrowdSec;
using Xunit;

namespace NetworkOptimizer.Threats.Tests;

public class CrowdSecClientTests
{
    private readonly CrowdSecClient _client;

    public CrowdSecClientTests()
    {
        var httpClientFactory = new Mock<IHttpClientFactory>();
        var logger = new Mock<ILogger<CrowdSecClient>>();
        _client = new CrowdSecClient(httpClientFactory.Object, logger.Object);
    }

    [Fact]
    public void LoadRateLimitState_SetsCurrentState()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        _client.LoadRateLimitState(25, today);

        var (requests, date, _) = _client.GetRateLimitState();
        Assert.Equal(25, requests);
        Assert.Equal(today, date);
    }

    [Fact]
    public void GetRateLimitState_ReturnsCurrentCountAndDate()
    {
        var testDate = new DateOnly(2025, 6, 15);
        _client.LoadRateLimitState(10, testDate);

        var (requests, date, _) = _client.GetRateLimitState();
        Assert.Equal(10, requests);
        Assert.Equal(testDate, date);
    }

    [Fact]
    public async Task GetIpReputationAsync_EmptyApiKey_ReturnsError()
    {
        var (info, outcome) = await _client.GetIpReputationAsync("192.0.2.10", "");

        Assert.Null(info);
        Assert.Equal(CrowdSecLookupOutcome.Error, outcome);
    }

    [Fact]
    public async Task GetIpReputationAsync_NullApiKey_ReturnsError()
    {
        var (info, outcome) = await _client.GetIpReputationAsync("192.0.2.10", null!);

        Assert.Null(info);
        Assert.Equal(CrowdSecLookupOutcome.Error, outcome);
    }

    [Fact]
    public void IsRateLimited_InitialState_ReturnsFalse()
    {
        Assert.False(_client.IsRateLimited);
    }

    [Fact]
    public void LoadRateLimitState_Overwrite_ReplacesState()
    {
        var day1 = new DateOnly(2025, 1, 1);
        var day2 = new DateOnly(2025, 1, 2);

        _client.LoadRateLimitState(30, day1);
        _client.LoadRateLimitState(5, day2);

        var (requests, date, _) = _client.GetRateLimitState();
        Assert.Equal(5, requests);
        Assert.Equal(day2, date);
    }

    [Fact]
    public void GetRateLimitState_InitialState_ReturnsZeroToday()
    {
        // Fresh client should have 0 requests for today
        var (requests, _, _) = _client.GetRateLimitState();
        Assert.Equal(0, requests);
    }

    [Fact]
    public async Task TestApiKeyAsync_EmptyKey_ReturnsFalse()
    {
        var (success, message) = await _client.TestApiKeyAsync("");

        Assert.False(success);
        Assert.Equal("API key is empty", message);
    }

    [Fact]
    public async Task TestApiKeyAsync_NullKey_ReturnsFalse()
    {
        var (success, message) = await _client.TestApiKeyAsync(null!);

        Assert.False(success);
        Assert.Equal("API key is empty", message);
    }
}
