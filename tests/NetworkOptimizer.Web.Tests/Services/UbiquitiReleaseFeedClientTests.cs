using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using Moq;
using Moq.Protected;
using NetworkOptimizer.Web.Services.Firmware;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Services;

/// <summary>
/// Parsing and request composition for Ubiquiti's public release feed. The fixture below is the
/// envelope the live feed returned when the shape was confirmed
/// (https://fw-update.ui.com/api/firmware-latest?filter=eq~~platform~~UP1&amp;filter=eq~~channel~~release),
/// with the ids and checksums replaced.
/// </summary>
public class UbiquitiReleaseFeedClientTests
{
    private const string FeedFixtureJson = """
    {
      "_embedded": {
        "firmware": [
          {
            "channel": "release",
            "created": "2024-02-08T17:18:05Z",
            "file_size": 1352704,
            "id": "00000000-0000-0000-0000-000000000001",
            "md5": "00000000000000000000000000000000",
            "sha256_checksum": "1111111111111111111111111111111111111111111111111111111111111111",
            "platform": "UP1",
            "product": "unifi-firmware",
            "updated": "2024-02-08T17:18:06Z",
            "version": "v2.2.6+532",
            "version_major": 2,
            "version_minor": 2,
            "version_patch": 6,
            "version_build": "532",
            "probability_computed": 0,
            "_links": {
              "self": { "href": "https://fw-update.ui.com/api/firmware/00000000-0000-0000-0000-000000000001" },
              "upload": [
                { "name": "data", "href": "https://fw-update.ui.com/api/firmware/00000000-0000-0000-0000-000000000001/data" },
                { "name": "changelog", "href": "https://fw-update.ui.com/api/firmware/00000000-0000-0000-0000-000000000001/changelog" }
              ],
              "data": { "href": "https://fw-download.ubnt.com/data/unifi-firmware/0000-UP1-2.2.6-00000000-0000-0000-0000-000000000001.bin" }
            }
          }
        ]
      },
      "_links": { "self": { "href": "https://fw-update.ui.com/api/firmware-latest" } }
    }
    """;

    private const string EmptyFeedJson = """
    {"_embedded":{"firmware":[]},"_links":{"self":{"href":"https://fw-update.ui.com/api/firmware"}}}
    """;

    [Fact]
    public async Task GetLatestAsync_ParsesTheFeedEntry()
    {
        var (client, requests) = CreateClient(_ => (HttpStatusCode.OK, FeedFixtureJson));

        var release = await client.GetLatestAsync("UP1");

        release.Should().NotBeNull();
        release!.Id.Should().Be("00000000-0000-0000-0000-000000000001");
        release.Platform.Should().Be("UP1");
        release.Product.Should().Be("unifi-firmware");
        release.Channel.Should().Be("release");
        release.Version.Should().Be("v2.2.6+532");
        release.Created.Should().Be(new DateTime(2024, 2, 8, 17, 18, 5, DateTimeKind.Utc));
        release.DownloadUrl.Should().StartWith("https://fw-download.ubnt.com/data/unifi-firmware/");
        release.Md5.Should().Be("00000000000000000000000000000000");
        release.Sha256.Should().HaveLength(64);
        release.ChangelogUrl.Should().EndWith("/changelog");
        release.FileSizeBytes.Should().Be(1352704);

        requests.Should().ContainSingle();
        requests[0].Should().Be(
            "https://fw-update.ui.com/api/firmware-latest?filter=eq~~platform~~UP1&filter=eq~~channel~~release");
    }

    [Fact]
    public async Task GetLatestAsync_WithAProduct_AddsThatFilter()
    {
        var (client, requests) = CreateClient(_ => (HttpStatusCode.OK, FeedFixtureJson));

        await client.GetLatestAsync("linux-x64", "release", "unifi-os-server");

        requests[0].Should().Be(
            "https://fw-update.ui.com/api/firmware-latest"
            + "?filter=eq~~platform~~linux-x64&filter=eq~~channel~~release&filter=eq~~product~~unifi-os-server");
    }

    [Fact]
    public async Task GetLatestAsync_OnANonSuccessStatus_ReturnsNull()
    {
        var (client, _) = CreateClient(_ => (HttpStatusCode.ServiceUnavailable, ""));

        (await client.GetLatestAsync("UP1")).Should().BeNull();
    }

    [Fact]
    public async Task GetLatestAsync_OnNonJson_ReturnsNull()
    {
        var (client, _) = CreateClient(_ => (HttpStatusCode.OK, "<html>not the feed</html>"));

        (await client.GetLatestAsync("UP1")).Should().BeNull();
    }

    [Fact]
    public async Task GetByVersionAsync_TranslatesTheConsolesDottedVersion()
    {
        var (client, requests) = CreateClient(_ => (HttpStatusCode.OK, FeedFixtureJson));

        var release = await client.GetByVersionAsync("UP1", "2.2.6.532");

        release!.Version.Should().Be("v2.2.6+532");
        requests[0].Should().Be(
            "https://fw-update.ui.com/api/firmware"
            + "?filter=eq~~platform~~UP1&filter=eq~~version~~v2.2.6%2B532");
    }

    [Fact]
    public async Task GetByVersionAsync_WhenTheTranslatedFormMisses_RetriesWithTheRawString()
    {
        var responses = new Queue<string>([EmptyFeedJson, FeedFixtureJson]);
        var (client, requests) = CreateClient(_ => (HttpStatusCode.OK, responses.Dequeue()));

        var release = await client.GetByVersionAsync("UP1", "2.2.6.532");

        release.Should().NotBeNull();
        requests.Should().HaveCount(2);
        requests[1].Should().EndWith("filter=eq~~version~~2.2.6.532");
    }

    [Fact]
    public async Task ListVersionsAsync_SortsNewestFirstAndCapsTheCount()
    {
        var (client, requests) = CreateClient(_ => (HttpStatusCode.OK, FeedFixtureJson));

        var releases = await client.ListVersionsAsync("U6M", "release", 10);

        releases.Should().HaveCount(1);
        requests[0].Should().Be(
            "https://fw-update.ui.com/api/firmware"
            + "?filter=eq~~platform~~U6M&filter=eq~~channel~~release&sort=-version&limit=10");
    }

    [Fact]
    public async Task ListVersionsAsync_WithoutAChannel_QueriesEveryChannel()
    {
        var (client, requests) = CreateClient(_ => (HttpStatusCode.OK, FeedFixtureJson));

        await client.ListVersionsAsync("U6M", channel: null, limit: 5);

        requests[0].Should().Be(
            "https://fw-update.ui.com/api/firmware?filter=eq~~platform~~U6M&sort=-version&limit=5");
    }

    [Fact]
    public async Task Fetch_IsCachedForAnHourAndRefetchedAfter()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero));
        var (client, requests) = CreateClient(_ => (HttpStatusCode.OK, FeedFixtureJson), time);

        await client.GetLatestAsync("UP1");
        await client.GetLatestAsync("UP1");
        requests.Should().ContainSingle();

        time.Advance(TimeSpan.FromMinutes(59));
        await client.GetLatestAsync("UP1");
        requests.Should().ContainSingle();

        time.Advance(TimeSpan.FromMinutes(2));
        await client.GetLatestAsync("UP1");
        requests.Should().HaveCount(2);
    }

    [Theory]
    [InlineData("2.2.6.532", "v2.2.6+532")]
    [InlineData("6.8.2.15592", "v6.8.2+15592")]
    [InlineData("v2.2.6+532", "v2.2.6+532")]
    [InlineData("2.2.6+532", "v2.2.6+532")]
    [InlineData("v5.1.34", "v5.1.34")]
    [InlineData("5.1.34", "v5.1.34")]
    [InlineData(" 2.2.6.532 ", "v2.2.6+532")]
    public void ToFeedVersion_MapsConsoleFormsOntoTheFeedForm(string input, string expected)
    {
        UbiquitiReleaseFeedClient.ToFeedVersion(input).Should().Be(expected);
    }

    private static (UbiquitiReleaseFeedClient Client, List<string> Requests) CreateClient(
        Func<HttpRequestMessage, (HttpStatusCode Status, string Body)> responder,
        TimeProvider? timeProvider = null)
    {
        var requests = new List<string>();

        var handler = new Mock<HttpMessageHandler>();
        handler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage request, CancellationToken _) =>
            {
                requests.Add(request.RequestUri!.ToString());
                var (status, body) = responder(request);
                return new HttpResponseMessage(status) { Content = new StringContent(body) };
            });

        var factory = new Mock<IHttpClientFactory>();
        factory
            .Setup(f => f.CreateClient(UbiquitiReleaseFeedClient.HttpClientName))
            .Returns(() => new HttpClient(handler.Object) { Timeout = TimeSpan.FromSeconds(5) });

        var client = new UbiquitiReleaseFeedClient(
            factory.Object,
            NullLogger<UbiquitiReleaseFeedClient>.Instance,
            timeProvider ?? TimeProvider.System);

        return (client, requests);
    }

    /// <summary>Manually advanced clock so the cache TTL can be crossed without waiting.</summary>
    private sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
