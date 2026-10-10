using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkOptimizer.Threats.Analysis;
using NetworkOptimizer.Threats.Models;
using NetworkOptimizer.Threats.Waf;
using Xunit;

namespace NetworkOptimizer.Threats.Tests;

public class WafIngestionTests
{
    private static WafEventPage Page(params WafEvent[] events) => new()
    {
        Instance = "abc123",
        Mode = "detect",
        Paranoia = 1,
        Next = 10,
        Events = events.ToList()
    };

    private static WafEvent Event(string action = "detected", params WafRuleHit[] rules) => new()
    {
        Seq = 7,
        Time = new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero),
        SourceIp = "203.0.113.9",
        Host = "app.example.com",
        Method = "GET",
        Uri = "/items?id=1' OR '1'='1",
        Action = action,
        AnomalyScore = 5,
        Rules = rules.ToList()
    };

    private static WafRuleHit Rule(int id, string severity, params string[] tags) =>
        new() { Id = id, Message = $"Rule {id}", Severity = severity, Tags = tags.ToList() };

    [Fact]
    public void Normalize_MapsTopRuleAndRequest()
    {
        var normalizer = new ThreatEventNormalizer(NullLogger<ThreatEventNormalizer>.Instance);

        var events = normalizer.NormalizeWafEvents(Page(Event("blocked",
            Rule(942100, "CRITICAL", "attack-sqli", "paranoia-level/1"),
            Rule(920350, "WARNING", "attack-protocol"))));

        var e = events.Should().ContainSingle().Subject;
        e.InnerAlertId.Should().Be("waf-abc123-7");
        e.EventSource.Should().Be(EventSource.Waf);
        e.SourceIp.Should().Be("203.0.113.9");
        e.SignatureId.Should().Be(942100);
        e.SignatureName.Should().Be("Rule 942100");
        e.Category.Should().Be("attack-sqli");
        e.Severity.Should().Be(4);
        e.Action.Should().Be(ThreatAction.Blocked);
        e.Domain.Should().Be("app.example.com");
        e.Service.Should().Be("GET /items");
        e.Timestamp.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public void Normalize_SkipsEventsWithoutRulesOrSource()
    {
        var normalizer = new ThreatEventNormalizer(NullLogger<ThreatEventNormalizer>.Instance);
        var noRules = Event();
        var noSource = Event("detected", Rule(942100, "CRITICAL", "attack-sqli"));
        noSource.SourceIp = "";

        normalizer.NormalizeWafEvents(Page(noRules, noSource)).Should().BeEmpty();
    }

    [Theory]
    [InlineData("CRITICAL", 4)]
    [InlineData("ERROR", 3)]
    [InlineData("WARNING", 2)]
    [InlineData("NOTICE", 1)]
    [InlineData("", 1)]
    public void CrsSeverity_MapsBelowCritical(string crs, int expected) =>
        ThreatEventNormalizer.MapCrsSeverity(crs).Should().Be(expected);

    [Theory]
    [InlineData("attack-sqli", KillChainStage.AttemptedExploitation)]
    [InlineData("attack-rce", KillChainStage.AttemptedExploitation)]
    [InlineData("attack-reputation-scanner", KillChainStage.Reconnaissance)]
    [InlineData("attack-protocol", KillChainStage.Reconnaissance)]
    [InlineData("waf", KillChainStage.AttemptedExploitation)]
    public void Classifier_MapsCrsFamily(string category, KillChainStage expected)
    {
        var evt = new ThreatEvent { EventSource = EventSource.Waf, Category = category, Severity = 4, Action = ThreatAction.Detected };
        new KillChainClassifier().Classify(evt).Should().Be(expected);
    }

    [Theory]
    [InlineData(null, "", 0UL)]
    [InlineData("", "", 0UL)]
    [InlineData("garbage", "", 0UL)]
    [InlineData("abc:notanumber", "", 0UL)]
    [InlineData("abc:42", "abc", 42UL)]
    public void Cursor_Parses(string? raw, string instance, ulong since) =>
        ThreatCollectionService.ParseWafCursor(raw).Should().Be((instance, since));

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    [Fact]
    public async Task Client_SendsBearerTokenAndCursor()
    {
        var handler = new StubHandler(HttpStatusCode.OK,
            """{"instance":"i1","mode":"block","paranoia":2,"stats":{"inspected":9},"next":3,"events":[]}""");
        var client = new WafClient(new HttpClient(handler));

        var page = await client.GetEventsAsync("http://127.0.0.1:8044/", "tok", 5, default);

        page.Mode.Should().Be("block");
        page.Stats.Inspected.Should().Be(9);
        handler.Request!.RequestUri!.ToString().Should().Be("http://127.0.0.1:8044/api/events?since=5&limit=500");
        handler.Request.Headers.Authorization!.ToString().Should().Be("Bearer tok");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "{}", "API token")]
    [InlineData(HttpStatusCode.InternalServerError, "{}", "HTTP 500")]
    [InlineData(HttpStatusCode.OK, "<html>", "not with netopt-waf events")]
    public async Task Client_FailuresBecomeReadableMessages(HttpStatusCode status, string body, string expected)
    {
        var client = new WafClient(new HttpClient(new StubHandler(status, body)));

        var act = () => client.GetEventsAsync("http://127.0.0.1:8044", "tok", 0, default);

        (await act.Should().ThrowAsync<WafClientException>()).Which.Message.Should().Contain(expected);
    }

    [Theory]
    [InlineData("ftp://host")]
    [InlineData("not a url")]
    public async Task Client_RejectsNonHttpUrls(string url)
    {
        var client = new WafClient(new HttpClient(new StubHandler(HttpStatusCode.OK, "{}")));

        await client.Invoking(c => c.GetEventsAsync(url, "tok", 0, default))
            .Should().ThrowAsync<WafClientException>();
    }
}
