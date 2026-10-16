using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NetworkOptimizer.Threats.Blocking;
using Xunit;

namespace NetworkOptimizer.Threats.Tests;

/// <summary>Public lists that need the site's own API token: auth, per-download caching, and rate limits.</summary>
public class PublicBlockListCacheTests
{
    private static readonly DateTime T0 = new(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);

    private sealed class Source
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.OK);

        public PublicBlockListCache Cache()
        {
            var factory = new Mock<IHttpClientFactory>();
            factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(new ScriptedHandler((req, _) =>
            {
                Requests.Add(req);
                return Respond(req);
            })));
            return new PublicBlockListCache(factory.Object, NullLogger<PublicBlockListCache>.Instance);
        }
    }

    private static HttpResponseMessage Body(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text) };

    [Fact]
    public async Task WithoutAToken_NothingIsFetched()
    {
        var source = new Source();
        var items = await source.Cache().GetAsync(PublicBlockLists.QFeeds, new FakeSettings(), T0, CancellationToken.None);
        items.Should().BeEmpty();
        source.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task QFeeds_SendsTheTokenAsHttpBasic_AndFetchesEachIpVersion()
    {
        var source = new Source
        {
            Respond = req => Body(req.RequestUri!.Query.Contains("ipv6=only") ? "2001:db8::/32\n" : "203.0.113.0/24\n198.51.100.7\n"),
        };
        var items = await source.Cache().GetAsync(PublicBlockLists.QFeeds, new FakeSettings(), T0, CancellationToken.None, "secret");

        items.Should().BeEquivalentTo("203.0.113.0/24", "198.51.100.7", "2001:db8::/32");
        source.Requests.Should().HaveCount(2);
        var expected = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("api_token:secret"));
        source.Requests.Should().OnlyContain(r => r.Headers.Authorization!.Scheme == "Basic"
            && r.Headers.Authorization.Parameter == expected
            && !r.RequestUri!.ToString().Contains("secret"));
    }

    [Fact]
    public async Task ThreatFox_SendsTheTokenInItsHeader()
    {
        var source = new Source { Respond = _ => Body("") };
        await source.Cache().GetAsync(PublicBlockLists.ThreatFox, new FakeSettings(), T0, CancellationToken.None, "key");
        source.Requests.Should().ContainSingle().Which.Headers.GetValues("Auth-Key").Should().Equal("key");
    }

    [Fact]
    public async Task TooManyRequests_KeepsTheLastCopy_WithoutAnError()
    {
        var source = new Source { Respond = _ => Body("203.0.113.5\n") };
        var cache = source.Cache();
        var settings = new FakeSettings();
        await cache.GetAsync(PublicBlockLists.QFeeds, settings, T0, CancellationToken.None, "secret");

        source.Respond = _ => new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        var later = await cache.GetAsync(PublicBlockLists.QFeeds, settings, T0.AddHours(2), CancellationToken.None, "secret");

        later.Should().Equal("203.0.113.5");
        cache.Status(PublicBlockLists.QFeeds, "secret").Error.Should().BeNull();
    }

    [Fact]
    public async Task ARefusedToken_IsReported()
    {
        var source = new Source { Respond = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized) };
        var cache = source.Cache();
        await cache.GetAsync(PublicBlockLists.QFeeds, new FakeSettings(), T0, CancellationToken.None, "wrong");
        cache.Status(PublicBlockLists.QFeeds, "wrong").Error.Should().Be("the key was refused");
    }

    [Fact]
    public async Task TheCacheIsKeyedByAHashOfTheToken_NeverTheToken()
    {
        var source = new Source { Respond = _ => Body("203.0.113.5\n") };
        var settings = new FakeSettings();
        await source.Cache().GetAsync(PublicBlockLists.QFeeds, settings, T0, CancellationToken.None, "secret");
        settings.Values.Keys.Should().NotBeEmpty().And.OnlyContain(k => !k.Contains("secret"));
    }
}
