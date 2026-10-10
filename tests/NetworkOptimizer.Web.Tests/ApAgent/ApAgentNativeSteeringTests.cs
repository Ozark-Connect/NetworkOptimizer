using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkOptimizer.Web.Services;
using NetworkOptimizer.Web.Services.ApAgent;
using Xunit;

namespace NetworkOptimizer.Web.Tests.ApAgent;

public class ApAgentNativeSteeringTests
{
    private const string Mac = "02:00:00:00:00:10";
    private const string Element = "02000000020107000000732409";
    private static readonly DateTime Now = new(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);

    private static ApAgentHealthPayload Health(int version = 28, int probeAgeMinutes = 1,
        IReadOnlyList<string>? native = null)
        => new("test", version, Now.AddMinutes(-probeAgeMinutes), Now, true, new[] { "ubus" }, native);

    private static ApAgentClient Client() => new()
    {
        Mac = Mac, Band = "5", Authorized = true,
        Links = new() { new() { Mac = Mac, Vap = "wifi1ap0", Ssid = "TestNet", Band = "5" } }
    };

    private static ApAgentNeighborReport Report(string element = Element, string ssid = "TestNet",
        ApAgentBssSecurity? security = null)
        => new() { Element = element, Bssid = "02:00:00:00:02:01", Ssid = ssid,
            Security = security ?? new() { Wpa = "2", KeyMgmt = "WPA-PSK", Pairwise = "CCMP" } };

    [Fact]
    public void AnOldAgentOrUnknownCapabilityCannotEnableNativeSteering()
    {
        ApAgentNativeSteering.SupportedVaps(Health(27, native: new[] { "wifi1ap0" })).Should().BeEmpty();
        ApAgentNativeSteering.SupportedVaps(Health()).Should().BeEmpty();
        ApAgentNativeSteering.SupportedVaps(null).Should().BeEmpty();
        ApAgentNativeSteering.SupportedVaps(Health(native: new[] { "wifi1ap0" })).Should().Equal("wifi1ap0");
        ApAgentDeploymentService.ExpectedBinaryVersion.Should().BeGreaterThanOrEqualTo(ApAgentNativeSteering.ContractVersion);
    }

    [Fact]
    public void MixedVersionsKeepLegacyUbusButCannotInferNativeSupport()
    {
        var old = Health(27) with { Unavailable = Array.Empty<string>() };
        var assessment = ApAgentHealthClassifier.Classify(new(ApAgentReach.Answered, 200,
            Health: old, ExpectedBinaryVersion: 28));
        assessment.State.Should().Be(ApAgentState.OutOfDate);
        ApAgentDeploymentService.CanSteer(assessment, old).Should().BeTrue();
        ApAgentDeploymentService.CanSteer(assessment, Health(27, native: new[] { "wifi1ap0" })).Should().BeFalse();
        var current = Health(native: new[] { "wifi1ap0" });
        ApAgentDeploymentService.CanSteer(new(ApAgentState.Healthy, ApAgentAction.None, "fixture"), current).Should().BeTrue();
        ApAgentDeploymentService.CanSteer(new(ApAgentState.Wedged, ApAgentAction.RestartInPlace, "fixture"), current).Should().BeFalse();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(11)]
    public void StaleOrFutureProbesCannotEnableNativeSteering(int probeAge)
        => ApAgentNativeSteering.SupportedVaps(Health(probeAgeMinutes: probeAge,
            native: new[] { "wifi1ap0" })).Should().BeEmpty();

    [Fact]
    public void OnlyAnAuthorizedSingleNonMloFiveGHzLinkIsEligible()
    {
        var vaps = new[] { "wifi1ap0" };
        ApAgentNativeSteering.EligibleClient(Client(), vaps).Should().BeTrue();
        foreach (var change in new Action<ApAgentClient>[] {
            c => c.IsMlo = true, c => c.MldMac = Mac, c => c.Authorized = false,
            c => c.Band = "2.4", c => c.Links[0].Band = "6", c => c.Links.Clear(),
            c => c.Links.Add(new() { Mac = Mac, Vap = "wifi1ap0" }),
            c => c.Links[0].Mac = "02:00:00:00:00:11",
            c => c.Links[0].Vap = "wifi2ap0", c => c.Links[0].Ssid = null })
        {
            var client = Client();
            change(client);
            ApAgentNativeSteering.EligibleClient(client, vaps).Should().BeFalse();
        }
    }

    [Fact]
    public void IneligibilityNamesWhatIsNotSupported()
    {
        var vaps = new[] { "wifi1ap0" };
        ApAgentNativeSteering.Ineligibility(Client(), vaps).Should().BeNull();
        foreach (var (change, expected) in new (Action<ApAgentClient>, string)[] {
            (c => c.IsMlo = true, "MLO clients"),
            (c => c.Links.Add(new() { Mac = "02:00:00:00:00:11", Vap = "wifi2ap0" }), "MLO clients"),
            (c => c.Band = "2.4", "only supported on 5 GHz"),
            (c => c.Links[0].Band = "6", "only supported on 5 GHz"),
            (c => c.Authorized = false, "hasn't finished connecting"),
            (c => c.Links[0].Vap = "wifi2ap0", "on this network on this Access Point") })
        {
            var client = Client();
            change(client);
            ApAgentNativeSteering.Ineligibility(client, vaps).Should().Contain(expected);
        }
    }

    [Fact]
    public void DestinationsNeedPublicSecurityAndAnIntactFiveGHzReport()
    {
        var missing = Report(); missing.Security = null;
        var mismatched = Report(); mismatched.Bssid = "02:00:00:00:02:02";
        var invalid = new[] { missing, mismatched, Report(ssid: "OtherNet"), Report(element: "zz"),
            Report(element: Element + "0305ff"), Report(element: "02000000020107000000510109"),
            Report(security: new() { Wpa = "1", KeyMgmt = "WPA-PSK", Pairwise = "CCMP" }) };
        ApAgentNativeSteering.Candidates(invalid, "TestNet").Should().BeEmpty();
        var candidates = ApAgentNativeSteering.Candidates(new[] { Report(), Report(), Report(element: Element + "0301ff") }, "TestNet");
        candidates.Should().ContainSingle();
        candidates[0].Element.Should().Be(Element);
    }

    [Fact]
    public void CandidateSetsAreNotSilentlyTruncated()
    {
        var reports = Enumerable.Range(1, 9).Select(i => {
            var report = Report(element: $"0200000002{i:x2}07000000732409");
            report.Bssid = $"02:00:00:00:02:{i:x2}";
            return report;
        });
        ApAgentNativeSteering.Candidates(reports, "TestNet").Should().BeEmpty();
    }

    [Fact]
    public void CandidateBytesStayWithinHostapdsBuffer()
    {
        var reports = Enumerable.Range(1, 4).Select(i => {
            // A 242-byte vendor subelement fills each valid report to the 255-byte limit.
            var report = Report(element: $"0200000002{i:x2}07000000732409" + "ddf0" + new string('0', 480));
            report.Bssid = $"02:00:00:00:02:{i:x2}";
            return report;
        }).ToList();
        ApAgentNativeSteering.Candidates(reports.Take(3), "TestNet").Should().HaveCount(3);
        ApAgentNativeSteering.Candidates(reports, "TestNet").Should().BeEmpty();
    }

    [Fact]
    public void HealthAndClientReadersPreserveTheCapabilityAndHoldingLink()
    {
        var health = ApAgentHealthClient.ParseHealth("""
            {"binary_version":28,"last_probe_run":"2026-10-07T11:59:00Z",
             "collected_at":"2026-10-07T12:00:00Z","unavailable":["ubus"],
             "native_voluntary_vaps":["wifi1ap0"]}
            """);
        ApAgentNativeSteering.SupportedVaps(health).Should().Equal("wifi1ap0");
        var client = JsonSerializer.Deserialize<ApAgentClient>("""
            {"mac":"02:00:00:00:00:10","authorized":true,"band":"5",
             "links":[{"mac":"02:00:00:00:00:10","vap":"wifi1ap0","ssid":"TestNet","band":"5"}]}
            """);
        ApAgentNativeSteering.EligibleClient(client!, ApAgentNativeSteering.SupportedVaps(health)).Should().BeTrue();
    }

    [Theory]
    [InlineData(200, false, true)]
    [InlineData(200, true, false)]
    [InlineData(400, false, false)]
    [InlineData(404, false, false)]
    [InlineData(500, false, false)]
    public async Task NativeTransportUsesOnlyTheVoluntaryRouteAndValidatesItsAcknowledgment(
        int status, bool badAck, bool succeeds)
    {
        var handler = new RecordingHandler(status, badAck);
        var service = Service(handler);
        var candidates = ApAgentNativeSteering.Candidates(new[] { Report() }, "TestNet");
        var result = await service.SendNativeAsync(new("02:00:00:01:01:01", "ap.test", "fixture-token", "Source AP"),
            Client(), candidates, CancellationToken.None);

        result.Success.Should().Be(succeeds);
        handler.Calls.Should().Be(1);
        handler.Method.Should().Be(HttpMethod.Post);
        handler.Path.Should().Be($"/clients/{Mac}/voluntary-bss-transitions");
        handler.Authorization.Should().StartWith("HMAC ");
        using var body = JsonDocument.Parse(handler.Body!);
        body.RootElement.EnumerateObject().Select(p => p.Name).Should().Equal("candidates");
        body.RootElement.GetProperty("candidates")[0].GetProperty("security").GetProperty("wpa").GetString().Should().Be("2");
        if (succeeds) result.Message.Should().Contain("may choose to stay");
    }

    [Fact]
    public async Task ATransportFailureAfterPostNeverRetriesOrFallsBack()
    {
        var handler = new RecordingHandler(200, false) { LoseReply = true };
        var result = await Service(handler).SendNativeAsync(new("02:00:00:01:01:01", "ap.test", null, "Source AP"),
            Client(), ApAgentNativeSteering.Candidates(new[] { Report() }, "TestNet"), CancellationToken.None);
        result.Success.Should().BeFalse();
        result.Message.Should().Contain("Couldn't reach the Access Point");
        handler.Calls.Should().Be(1);
        handler.Path.Should().EndWith("/voluntary-bss-transitions");
    }

    private static ApAgentRoamService Service(RecordingHandler handler)
    {
        var provider = new ServiceCollection().BuildServiceProvider();
        var coverage = new SiteAgentCoverage(provider);
        coverage.Set(SiteManagementService.DefaultSiteSlug, false);
        var routing = new SiteTunnelRouting(provider, coverage, NullLogger<SiteTunnelRouting>.Instance);
        var transport = new ApAgentHttpTransport(new ClientFactory(handler), routing, NullLogger<ApAgentHttpTransport>.Instance);
        return new(transport, null!, null!, null!, NullLogger<ApAgentRoamService>.Instance);
    }

    private sealed class ClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class RecordingHandler(int status, bool badAck) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public HttpMethod? Method { get; private set; }
        public string? Path { get; private set; }
        public string? Body { get; private set; }
        public string? Authorization { get; private set; }
        public bool LoseReply { get; init; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Method = request.Method;
            Path = request.RequestUri!.AbsolutePath;
            Body = await request.Content!.ReadAsStringAsync(ct);
            Authorization = request.Headers.GetValues("Authorization").SingleOrDefault();
            if (LoseReply) throw new HttpRequestException("fixture lost response after sending");
            var vap = badAck ? "wifi0ap0" : "wifi1ap0";
            return new((HttpStatusCode)status) { Content = new StringContent($"{{\"mac\":\"{Mac}\",\"vap\":\"{vap}\",\"candidates\":1}}") };
        }
    }
}
