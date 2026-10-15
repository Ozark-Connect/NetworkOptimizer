using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NetworkOptimizer.Threats.Blocking;
using NetworkOptimizer.Threats.Interfaces;
using NetworkOptimizer.Threats.Models;
using NetworkOptimizer.Threats.Waf;
using Xunit;

namespace NetworkOptimizer.Threats.Tests;

/// <summary>Answers requests from a script and records what was sent.</summary>
internal sealed class ScriptedHandler(Func<HttpRequestMessage, string?, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<(HttpMethod Method, string Url, string? Body)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content == null ? null : await request.Content.ReadAsStringAsync(ct);
        Requests.Add((request.Method, request.RequestUri!.ToString(), body));
        return respond(request, body);
    }

    public static HttpResponseMessage Json(HttpStatusCode code, string json) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

/// <summary>In-memory settings, with "decryption" being the identity.</summary>
internal sealed class FakeSettings : IThreatSettingsAccessor
{
    public Dictionary<string, string> Values { get; } = new();
    public Task<string?> GetSettingAsync(string key, CancellationToken ct = default) => Task.FromResult(Values.GetValueOrDefault(key));
    public Task<string?> GetDecryptedSettingAsync(string key, CancellationToken ct = default) => GetSettingAsync(key, ct);
    public Task SaveSettingAsync(string key, string value, CancellationToken ct = default) { Values[key] = value; return Task.CompletedTask; }
}

public class BlockTargetTests
{
    private static readonly DateTime Now = new(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);

    private static ThreatBlock Block(string ip, int minutes = 60) =>
        new() { SourceIp = ip, Reason = "5 WAF events in 10 minutes", Enforced = true, CreatedAt = Now, ExpiresAt = Now.AddMinutes(minutes) };

    [Fact]
    public async Task Waf_PutsTheFullListWithExpiry()
    {
        var handler = new ScriptedHandler((_, _) => ScriptedHandler.Json(HttpStatusCode.OK, """{"count":2,"bans":[]}"""));
        var settings = new FakeSettings();
        settings.Values[WafSettingKeys.Url] = "http://127.0.0.1:8044/";
        settings.Values[WafSettingKeys.Token] = "secret";
        var target = new WafBlockTarget(new WafClient(new HttpClient(handler)), settings, null);

        var result = await target.SyncAsync([Block("203.0.113.7"), Block("2001:db8::7", 120)], CancellationToken.None);

        result.Ok.Should().BeTrue();
        result.Count.Should().Be(2);
        var (method, url, body) = handler.Requests.Single();
        method.Should().Be(HttpMethod.Put);
        url.Should().Be("http://127.0.0.1:8044/api/bans");
        using var doc = JsonDocument.Parse(body!);
        var bans = doc.RootElement.GetProperty("bans");
        bans.GetArrayLength().Should().Be(2);
        bans[0].GetProperty("ip").GetString().Should().Be("203.0.113.7");
        bans[0].GetProperty("expires").GetDateTimeOffset().Should().Be(new DateTimeOffset(Now.AddMinutes(60)));
    }

    [Fact]
    public async Task Waf_OlderProxy_WithoutBanList_SaysToUpdate()
    {
        var handler = new ScriptedHandler((_, _) => new HttpResponseMessage(HttpStatusCode.MethodNotAllowed));
        var settings = new FakeSettings();
        settings.Values[WafSettingKeys.Url] = "http://127.0.0.1:8044";
        settings.Values[WafSettingKeys.Token] = "secret";

        var result = await new WafBlockTarget(new WafClient(new HttpClient(handler)), settings, null).SyncAsync([Block("203.0.113.7")], CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Error.Should().Contain("Update NetworkOptimizer-Proxy");
    }

    [Fact]
    public async Task Waf_NotConnected_FailsWithoutCalling()
    {
        var handler = new ScriptedHandler((_, _) => throw new InvalidOperationException("must not be called"));
        var result = await new WafBlockTarget(new WafClient(new HttpClient(handler)), new FakeSettings(), null).SyncAsync([], CancellationToken.None);

        result.Ok.Should().BeFalse();
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Waf_ASite_UsesItsOwnConnection()
    {
        var handler = new ScriptedHandler((_, _) => ScriptedHandler.Json(HttpStatusCode.OK, """{"count":1,"bans":[]}"""));
        var settings = new FakeSettings();
        settings.Values[WafSettingKeys.Url] = "http://default-site:8044";
        settings.Values[WafSettingKeys.Token] = "default";
        settings.Values[WafSettingKeys.ForSite(WafSettingKeys.Url, "site-b")] = "http://127.0.0.1:8044";
        settings.Values[WafSettingKeys.ForSite(WafSettingKeys.Token, "site-b")] = "site-b-token";

        var result = await new WafBlockTarget(new WafClient(new HttpClient(handler)), settings, "site-b")
            .SyncAsync([Block("203.0.113.7")], CancellationToken.None);

        result.Ok.Should().BeTrue();
        handler.Requests.Single().Url.Should().Be("http://127.0.0.1:8044/api/bans");
    }
    private const string ListsWithOurs = """{"success":true,"errors":[],"result":[{"id":"other","name":"office","kind":"ip"},{"id":"L1","name":"netopt_blocklist","kind":"ip","num_items":3,"num_referencing_filters":1}]}""";

    [Fact]
    public async Task Cloudflare_Ensure_FindsTheListByName()
    {
        var handler = new ScriptedHandler((_, _) => ScriptedHandler.Json(HttpStatusCode.OK, ListsWithOurs));
        var list = await new CloudflareListClient(new HttpClient(handler)).EnsureListAsync("t", "acct", null, CancellationToken.None);

        list.Id.Should().Be("L1");
        list.NumReferencingFilters.Should().Be(1);
        handler.Requests.Should().ContainSingle().Which.Url.Should().EndWith("/accounts/acct/rules/lists");
    }

    [Fact]
    public async Task Cloudflare_Ensure_CreatesTheListWhenMissing()
    {
        var handler = new ScriptedHandler((req, _) => req.Method == HttpMethod.Get
            ? ScriptedHandler.Json(HttpStatusCode.OK, """{"success":true,"result":[]}""")
            : ScriptedHandler.Json(HttpStatusCode.OK, """{"success":true,"result":{"id":"NEW","name":"netopt_blocklist","kind":"ip"}}"""));

        var list = await new CloudflareListClient(new HttpClient(handler)).EnsureListAsync("t", "acct", null, CancellationToken.None);

        list.Id.Should().Be("NEW");
        var post = handler.Requests[1];
        post.Method.Should().Be(HttpMethod.Post);
        using var doc = JsonDocument.Parse(post.Body!);
        doc.RootElement.GetProperty("name").GetString().Should().Be("netopt_blocklist");
        doc.RootElement.GetProperty("kind").GetString().Should().Be("ip");
    }

    [Fact]
    public async Task Cloudflare_Ensure_RefusesANameClashWithAnotherKind()
    {
        var handler = new ScriptedHandler((_, _) => ScriptedHandler.Json(HttpStatusCode.OK,
            """{"success":true,"result":[{"id":"X","name":"netopt_blocklist","kind":"hostname"}]}"""));

        var act = () => new CloudflareListClient(new HttpClient(handler)).EnsureListAsync("t", "acct", null, CancellationToken.None);

        await act.Should().ThrowAsync<CloudflareException>().WithMessage("*does not hold IP addresses*");
    }

    [Fact]
    public async Task Cloudflare_Replace_PollsUntilCompleted()
    {
        var polls = 0;
        var handler = new ScriptedHandler((req, _) =>
        {
            if (req.Method == HttpMethod.Put)
                return ScriptedHandler.Json(HttpStatusCode.OK, """{"success":true,"result":{"operation_id":"op1"}}""");
            polls++;
            return ScriptedHandler.Json(HttpStatusCode.OK, polls < 3
                ? """{"success":true,"result":{"id":"op1","status":"running"}}"""
                : """{"success":true,"result":{"id":"op1","status":"completed"}}""");
        });
        var client = new CloudflareListClient(new HttpClient(handler), (_, _) => Task.CompletedTask);

        await client.ReplaceItemsAsync("t", "acct", "L1", [new CloudflareListItem("203.0.113.7", "c")], CancellationToken.None);

        polls.Should().Be(3);
        handler.Requests[0].Url.Should().EndWith("/accounts/acct/rules/lists/L1/items");
        handler.Requests[1].Url.Should().EndWith("/accounts/acct/rules/lists/bulk_operations/op1");
    }

    [Fact]
    public async Task Cloudflare_Replace_FailedOperation_Throws()
    {
        var handler = new ScriptedHandler((req, _) => req.Method == HttpMethod.Put
            ? ScriptedHandler.Json(HttpStatusCode.OK, """{"success":true,"result":{"operation_id":"op1"}}""")
            : ScriptedHandler.Json(HttpStatusCode.OK, """{"success":true,"result":{"status":"failed","error":"bad item"}}"""));

        var act = () => new CloudflareListClient(new HttpClient(handler), (_, _) => Task.CompletedTask)
            .ReplaceItemsAsync("t", "acct", "L1", [], CancellationToken.None);

        await act.Should().ThrowAsync<CloudflareException>().WithMessage("*bad item*");
    }

    [Fact]
    public async Task Cloudflare_Forbidden_NamesThePermission()
    {
        var handler = new ScriptedHandler((_, _) => ScriptedHandler.Json(HttpStatusCode.Forbidden, """{"success":false,"errors":[{"code":10000,"message":"Authentication error"}]}"""));

        var act = () => new CloudflareListClient(new HttpClient(handler)).GetListAsync("t", "acct", "L1", CancellationToken.None);

        await act.Should().ThrowAsync<CloudflareException>().WithMessage("*Account Rule Lists*Account Filter Lists*");
    }
}

public class ThreatBlockingServiceTests
{
    private static readonly DateTime Now = new(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);

    private sealed class FakeGateway : IGatewayBlocklistWriter
    {
        public List<IReadOnlyList<string>> Syncs { get; } = [];
        public Task<BlockTargetResult> SyncMembersAsync(string? site, IReadOnlyList<string> ips, CancellationToken ct)
        {
            Syncs.Add(ips);
            return Task.FromResult(BlockTargetResult.Success(ips.Count));
        }

        public List<IReadOnlyList<string>> FeedSyncs { get; } = [];

        public Task<BlockTargetResult> SyncFeedsAsync(string? site, IReadOnlyList<string> entries, CancellationToken ct)
        {
            FeedSyncs.Add(entries);
            return Task.FromResult(BlockTargetResult.Success(entries.Count));
        }

        public int PolicyChecks { get; private set; }
        public int StalePolicies { get; set; }
        public bool FailPolicyCheck { get; set; }

        public Task<int> EnsureSourcePoliciesNewOnlyAsync(string? site, CancellationToken ct)
        {
            PolicyChecks++;
            if (FailPolicyCheck) throw new InvalidOperationException("console unreachable");
            var changed = StalePolicies;
            StalePolicies = 0;
            return Task.FromResult(changed);
        }
    }

    private sealed class Harness
    {
        public FakeSettings Settings { get; } = new();
        public FakeGateway Gateway { get; } = new();
        public List<ThreatBlock> Blocks { get; } = [];
        public Dictionary<string, int> WafCounts { get; } = new();
        public Mock<IThreatSystemAudit> Audit { get; } = new();
        public ThreatBlockingService Service { get; }

        public Harness()
        {
            var repo = new Mock<IThreatRepository>();
            repo.Setup(r => r.GetNoiseFiltersAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
            repo.Setup(r => r.GetWafEventCountsBySourceAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new Dictionary<string, int>(WafCounts));
            repo.Setup(r => r.GetPatternsSeenAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
            repo.Setup(r => r.GetBlocksAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((DateTime now, DateTime since, CancellationToken _) =>
                    Blocks.Where(b => b.IsActive(now) || b.CreatedAt >= since).ToList());
            repo.Setup(r => r.GetBlockCountsBySourceAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Dictionary<string, int>());
            repo.Setup(r => r.AddBlocksAsync(It.IsAny<List<ThreatBlock>>(), It.IsAny<CancellationToken>()))
                .Callback((List<ThreatBlock> b, CancellationToken _) => Blocks.AddRange(b))
                .Returns(Task.CompletedTask);

            var services = new ServiceCollection();
            services.AddSingleton(repo.Object);
            services.AddSingleton<IThreatSettingsAccessor>(Settings);
            services.AddSingleton<IGatewayBlocklistWriter>(Gateway);
            var provider = services.BuildServiceProvider();

            var factory = new Mock<IHttpClientFactory>();
            factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new HttpClient(new ScriptedHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK))));
            Service = new ThreatBlockingService(provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<ThreatBlockingService>.Instance, factory.Object, audit: Audit.Object);
        }

        public void Set(string key, string value) => Settings.Values[key] = value;
    }

    [Fact]
    public async Task Enforce_BlocksAndSendsToTheGateway_AndAudits()
    {
        var h = new Harness();
        h.Set(BlockingSettingKeys.Mode, "Enforce");
        h.Set(BlockingSettingKeys.TargetGateway, "true");
        h.WafCounts["203.0.113.7"] = 6;

        await h.Service.RunCycleAsync(Now, CancellationToken.None);

        h.Blocks.Should().ContainSingle().Which.Enforced.Should().BeTrue();
        h.Gateway.Syncs.Should().ContainSingle().Which.Should().Equal("203.0.113.7");
        h.Audit.Verify(a => a.SourceBlocked(null, "203.0.113.7", It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<IReadOnlyList<string>>()), Times.Once);
        h.Service.TargetStatus(null).Should().ContainSingle(s => s.Kind == BlockTargetKind.Gateway && s.Ok && s.Count == 1);
    }

    [Fact]
    public async Task DryRun_RecordsButSendsNothing_AndDoesNotAudit()
    {
        var h = new Harness();
        h.Set(BlockingSettingKeys.Mode, "DryRun");
        h.Set(BlockingSettingKeys.TargetGateway, "true");
        h.WafCounts["203.0.113.7"] = 6;

        await h.Service.RunCycleAsync(Now, CancellationToken.None);

        h.Blocks.Should().ContainSingle().Which.Enforced.Should().BeFalse();
        h.Gateway.Syncs.Should().OnlyContain(s => s.Count == 0); // only the one-time cleanup, never an entry
        h.Audit.Verify(a => a.SourceBlocked(It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<IReadOnlyList<string>>()), Times.Never);
    }

    [Fact]
    public async Task DryRun_SendsManualBlocks_ButNotItsOwnDecisions()
    {
        var h = new Harness();
        h.Set(BlockingSettingKeys.Mode, "DryRun");
        h.Set(BlockingSettingKeys.TargetGateway, "true");
        h.WafCounts["203.0.113.7"] = 6; // decided in Dry Run: recorded, not sent
        h.Blocks.Add(new ThreatBlock
        {
            SourceIp = "2001:db8::9",
            Trigger = BlockTrigger.Manual,
            Reason = "Blocked by admin",
            Enforced = true,
            CreatedAt = Now.AddMinutes(-1),
            ExpiresAt = Now.AddHours(1),
        });

        await h.Service.RunCycleAsync(Now, CancellationToken.None);

        h.Gateway.Syncs.Last().Should().Equal("2001:db8::9");
    }

    [Fact]
    public async Task Enforce_BlocksASourceStillActiveFromDryRun_AndDryRunKeepsOneRecord()
    {
        var h = new Harness();
        h.Set(BlockingSettingKeys.Mode, "DryRun");
        h.Set(BlockingSettingKeys.TargetGateway, "true");
        h.WafCounts["203.0.113.7"] = 6;

        await h.Service.RunCycleAsync(Now, CancellationToken.None);
        await h.Service.RunCycleAsync(Now.AddMinutes(1), CancellationToken.None);
        h.Blocks.Should().ContainSingle().Which.Enforced.Should().BeFalse();

        h.Set(BlockingSettingKeys.Mode, "Enforce");
        await h.Service.RunCycleAsync(Now.AddMinutes(2), CancellationToken.None);

        h.Blocks.Should().HaveCount(2).And.ContainSingle(b => b.Enforced);
        h.Gateway.Syncs.Last().Should().Equal("203.0.113.7");
    }

    [Fact]
    public async Task SourcePolicies_CheckedHourly_AndAuditedOnlyWhenChanged()
    {
        var h = new Harness();
        h.Set(BlockingSettingKeys.Mode, "Enforce");
        h.Set(BlockingSettingKeys.TargetGateway, "true");
        h.Gateway.StalePolicies = 10;

        await h.Service.RunCycleAsync(Now, CancellationToken.None);
        await h.Service.RunCycleAsync(Now.AddMinutes(1), CancellationToken.None);
        h.Gateway.PolicyChecks.Should().Be(1);

        h.Gateway.StalePolicies = 1; // someone set one back to every state in UniFi Network
        await h.Service.RunCycleAsync(Now.AddMinutes(61), CancellationToken.None);

        h.Gateway.PolicyChecks.Should().Be(2);
        h.Audit.Verify(a => a.GatewayPoliciesNarrowed(null, 10), Times.Once);
        h.Audit.Verify(a => a.GatewayPoliciesNarrowed(null, 1), Times.Once);
    }

    [Fact]
    public async Task SourcePolicies_FailedCheck_RetriesAfterThirtyMinutes()
    {
        var h = new Harness();
        h.Set(BlockingSettingKeys.Mode, "Enforce");
        h.Gateway.FailPolicyCheck = true;

        await h.Service.RunCycleAsync(Now, CancellationToken.None);
        await h.Service.RunCycleAsync(Now.AddMinutes(10), CancellationToken.None);
        h.Gateway.FailPolicyCheck = false;
        await h.Service.RunCycleAsync(Now.AddMinutes(31), CancellationToken.None);

        h.Gateway.PolicyChecks.Should().Be(2);
        h.Audit.Verify(a => a.GatewayPoliciesNarrowed(It.IsAny<string?>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task DryRun_DropsAutomaticBlocksEnforcedBeforeTheSwitch()
    {
        var h = new Harness();
        h.Set(BlockingSettingKeys.Mode, "DryRun");
        h.Set(BlockingSettingKeys.TargetGateway, "true");
        h.Blocks.Add(new ThreatBlock
        {
            SourceIp = "203.0.113.20",
            Trigger = BlockTrigger.Pattern,
            Reason = "Scan sweep",
            Enforced = true, // decided while the site was in Enforce
            CreatedAt = Now.AddMinutes(-5),
            ExpiresAt = Now.AddHours(1),
        });
        h.Blocks.Add(new ThreatBlock
        {
            SourceIp = "2001:db8::9",
            Trigger = BlockTrigger.Manual,
            Reason = "Blocked by admin",
            Enforced = true,
            CreatedAt = Now.AddMinutes(-1),
            ExpiresAt = Now.AddHours(1),
        });

        await h.Service.RunCycleAsync(Now, CancellationToken.None);

        h.Gateway.Syncs.Last().Should().Equal("2001:db8::9");
    }

    [Fact]
    public async Task Off_SendsNothing_EvenManualBlocks()
    {
        var h = new Harness();
        h.Set(BlockingSettingKeys.Mode, "Off");
        h.Set(BlockingSettingKeys.TargetGateway, "true");
        h.Blocks.Add(new ThreatBlock
        {
            SourceIp = "203.0.113.9",
            Trigger = BlockTrigger.Manual,
            Enforced = true,
            CreatedAt = Now.AddMinutes(-1),
            ExpiresAt = Now.AddHours(1),
        });

        await h.Service.RunCycleAsync(Now, CancellationToken.None);

        h.Gateway.Syncs.Should().OnlyContain(s => s.Count == 0);
    }

    [Fact]
    public async Task UnchangedSet_IsNotResent_UntilTheHourlyResync()
    {
        var h = new Harness();
        h.Set(BlockingSettingKeys.Mode, "Enforce");
        h.Set(BlockingSettingKeys.TargetGateway, "true");
        h.Set(BlockingSettingKeys.BanMinutes, "240"); // outlives the hour, so only the resync can trigger a send
        h.WafCounts["203.0.113.7"] = 6;

        await h.Service.RunCycleAsync(Now, CancellationToken.None);
        await h.Service.RunCycleAsync(Now.AddMinutes(1), CancellationToken.None);
        h.Gateway.Syncs.Should().HaveCount(1);

        await h.Service.RunCycleAsync(Now.AddMinutes(59), CancellationToken.None);
        h.Gateway.Syncs.Should().HaveCount(1);

        await h.Service.RunCycleAsync(Now.AddMinutes(61), CancellationToken.None); // the hourly resync
        h.Gateway.Syncs.Should().HaveCount(2);
    }

    [Fact]
    public async Task Expiry_EmptiesTheTarget_AndAuditsTheExpiry()
    {
        var h = new Harness();
        h.Set(BlockingSettingKeys.Mode, "Enforce");
        h.Set(BlockingSettingKeys.TargetGateway, "true");
        h.Set(BlockingSettingKeys.BanMinutes, "5");
        h.WafCounts["203.0.113.7"] = 6;

        await h.Service.RunCycleAsync(Now, CancellationToken.None);
        h.WafCounts.Clear();
        await h.Service.RunCycleAsync(Now.AddMinutes(6), CancellationToken.None);

        h.Gateway.Syncs.Last().Should().BeEmpty();
        h.Audit.Verify(a => a.BlockExpired(null, "203.0.113.7"), Times.Once);
    }

    [Fact]
    public async Task TurningATargetOff_EmptiesItOnce()
    {
        var h = new Harness();
        h.Set(BlockingSettingKeys.Mode, "Enforce");
        h.Set(BlockingSettingKeys.TargetGateway, "true");
        h.WafCounts["203.0.113.7"] = 6;
        await h.Service.RunCycleAsync(Now, CancellationToken.None);

        h.Set(BlockingSettingKeys.TargetGateway, "false");
        await h.Service.RunCycleAsync(Now.AddMinutes(1), CancellationToken.None);
        await h.Service.RunCycleAsync(Now.AddMinutes(2), CancellationToken.None);

        h.Gateway.Syncs.Should().HaveCount(2);
        h.Gateway.Syncs[1].Should().BeEmpty();
    }
}
