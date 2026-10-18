using System.Net;
using Microsoft.Extensions.Logging;
using Moq;
using NetworkOptimizer.Threats.CrowdSec;
using Xunit;

namespace NetworkOptimizer.Threats.Tests;

public class CrowdSecClientTests
{
    private readonly CrowdSecClient _client;
    private readonly StubHandler _handler = new();

    public CrowdSecClientTests()
    {
        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(_handler, disposeHandler: false));
        var logger = new Mock<ILogger<CrowdSecClient>>();
        _client = new CrowdSecClient(httpClientFactory.Object, logger.Object);
    }

    // Answers every request with the status and body set on it.
    private sealed class StubHandler : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = HttpStatusCode.NotFound;
        public string Body { get; set; } = "";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(Body) });
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
        Assert.False(_client.IsRateLimited("key-a"));
    }

    [Fact]
    public void RequestsToday_InitialState_ReturnsZero()
    {
        Assert.Equal(0, _client.RequestsToday("key-a"));
    }

    [Fact]
    public async Task RequestsToday_CountsEachKeySeparately()
    {
        await _client.GetIpReputationAsync("192.0.2.10", "key-a");
        await _client.GetIpReputationAsync("198.51.100.10", "key-b");
        await _client.GetIpReputationAsync("203.0.113.10", "key-b");

        Assert.Equal(1, _client.RequestsToday("key-a"));
        Assert.Equal(2, _client.RequestsToday("key-b"));
        Assert.Equal(0, _client.RequestsToday("key-c"));
    }

    [Fact]
    public async Task DailyLimitExceeded_StopsOnlyThatKey()
    {
        _handler.Status = HttpStatusCode.TooManyRequests;
        _handler.Body = "Limit Exceeded";
        var (_, outcome) = await _client.GetIpReputationAsync("192.0.2.10", "key-a");
        Assert.Equal(CrowdSecLookupOutcome.QuotaExhausted, outcome);

        _handler.Status = HttpStatusCode.NotFound;
        _handler.Body = "";
        Assert.True(_client.IsRateLimited("key-a"));
        Assert.False(_client.IsRateLimited("key-b"));
        Assert.Equal(CrowdSecLookupOutcome.QuotaExhausted, (await _client.GetIpReputationAsync("192.0.2.11", "key-a")).Outcome);
        Assert.Equal(CrowdSecLookupOutcome.NotFound, (await _client.GetIpReputationAsync("192.0.2.11", "key-b")).Outcome);
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
