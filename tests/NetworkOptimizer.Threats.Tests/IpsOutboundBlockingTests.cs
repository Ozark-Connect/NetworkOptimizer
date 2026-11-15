using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NetworkOptimizer.Alerts.Interfaces;
using NetworkOptimizer.Threats.Blocking;
using NetworkOptimizer.Threats.Interfaces;
using NetworkOptimizer.Threats.Models;
using Xunit;

namespace NetworkOptimizer.Threats.Tests;

/// <summary>
/// An outbound IPS alert reaches the gateway agents of the origin and the sites sharing its blocks at once,
/// as an outbound entry, and never the WAF or Cloudflare.
/// </summary>
public class IpsOutboundBlockingTests
{
    private const string DefaultKey = "default";
    private const string Remote = "203.0.113.10";

    private sealed class FakeSites(IReadOnlyList<string> keys) : IScheduleSiteContext
    {
        public string DefaultKey => IpsOutboundBlockingTests.DefaultKey;
        public Task<IReadOnlyList<string>> GetSiteKeysAsync(CancellationToken ct) => Task.FromResult(keys);
        public void PinScope(IServiceScope scope, string siteKey) { }
    }

    private sealed class SiteAgents : IGatewayAgentBlocklist
    {
        public Dictionary<string, IReadOnlyList<GatewayAgentEntry>> Outbound { get; } = new();
        public Dictionary<string, IReadOnlyList<GatewayAgentEntry>> Entries { get; } = new();
        public int Pushes { get; private set; }

        public bool Push(string? site, bool enabled, IReadOnlyList<GatewayAgentEntry> entries, IReadOnlyList<GatewayAgentEntry> outbound,
            string fingerprint, IReadOnlyList<GatewayAgentEntry>? feeds = null, IReadOnlyList<BlockingException>? exceptions = null)
        {
            Pushes++;
            Outbound[site ?? ""] = outbound;
            Entries[site ?? ""] = entries;
            return true;
        }

        public bool IsEnforcing(string? site, string fingerprint) => true;
        public string? Problem(string? site, string fingerprint) => null;
        public bool HasGatewayAgent(string? site) => true;
        public bool EnforcesExceptions(string? site) => true;
    }

    private sealed class Harness
    {
        public FakeSettings Settings { get; } = new();
        public SiteAgents Agents { get; } = new();
        public List<ThreatBlock> Blocks { get; } = [];
        public List<string> WafOrCloudflare { get; } = [];
        public ThreatBlockingService Service { get; }

        public Harness(params string[] otherSites)
        {
            var sites = new FakeSites([DefaultKey, .. otherSites]);
            var services = new ServiceCollection();
            services.AddScoped<IThreatRepository>(_ => Repo());
            services.AddSingleton<IThreatSettingsAccessor>(Settings);
            services.AddSingleton<IBlockSharingScope>(new InstanceBlockSharingScope(Settings, sites));
            var gateway = new Mock<IGatewayBlocklistWriter>();
            gateway.Setup(g => g.SyncMembersAsync(It.IsAny<string?>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string? _, IReadOnlyList<string> ips, CancellationToken _) => BlockTargetResult.Success(ips.Count));
            gateway.Setup(g => g.SyncFeedsAsync(It.IsAny<string?>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(BlockTargetResult.Success(0));
            gateway.Setup(g => g.SyncExceptionsAsync(It.IsAny<string?>(), It.IsAny<IReadOnlyList<BlockingException>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(BlockTargetResult.Success(0));
            services.AddSingleton(gateway.Object);
            var provider = services.BuildServiceProvider();
            var factory = new Mock<IHttpClientFactory>();
            factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(new ScriptedHandler((req, body) =>
            {
                WafOrCloudflare.Add($"{req.RequestUri} {body}");
                // Failing keeps the WAF due every cycle, so each cycle shows what it would be sent.
                return new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable);
            })));
            Service = new ThreatBlockingService(provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<ThreatBlockingService>.Instance, factory.Object, sites, agents: Agents);

            foreach (var site in otherSites.Prepend(null))
            {
                Settings.Values[BlockingSettingKeys.ForSite(BlockingSettingKeys.Mode, site)] = "Enforce";
                Settings.Values[BlockingSettingKeys.ForSite(BlockingSettingKeys.TargetGateway, site)] = "true";
                Settings.Values[BlockingSettingKeys.ForSite(BlockingSettingKeys.Feeds, site)] = "";
            }
        }

        private IThreatRepository Repo()
        {
            var repo = new Mock<IThreatRepository>();
            repo.Setup(r => r.GetNoiseFiltersAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
            repo.Setup(r => r.GetWafEventCountsBySourceAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Dictionary<string, int>());
            repo.Setup(r => r.GetPatternsSeenAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
            repo.Setup(r => r.GetBlocksAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((DateTime now, DateTime _, CancellationToken _) => Blocks.Where(b => b.IsActive(now)).ToList());
            repo.Setup(r => r.GetBlockCountsBySourceAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Dictionary<string, int>());
            repo.Setup(r => r.AddBlocksAsync(It.IsAny<List<ThreatBlock>>(), It.IsAny<CancellationToken>()))
                .Callback((List<ThreatBlock> added, CancellationToken _) => Blocks.AddRange(added))
                .Returns(Task.CompletedTask);
            return repo.Object;
        }
    }

    private static IpsOutboundAlert C2(bool fromAgent = true) => new()
    {
        ClientIp = "192.168.1.50",
        RemoteIp = Remote,
        RemotePort = 443,
        SignatureId = 2023764,
        Signature = "ET MALWARE Example Checkin",
        Category = "Malware Command and Control Activity Detected",
        UbntCategory = "MALWARE",
        Severity = 1,
        Confidence = "High",
        Timestamp = DateTime.UtcNow,
        FromAgent = fromAgent,
    };

    [Fact]
    public async Task Alert_ReachesEverySharingSitesAgent_AsAnOutboundEntry_WithoutWaitingForACycle()
    {
        var h = new Harness("branch");
        await h.Service.RunCycleAsync(DateTime.UtcNow, CancellationToken.None);
        var before = h.Agents.Pushes;

        var outcome = await h.Service.OnIpsOutboundAlertAsync(null, C2(), CancellationToken.None);

        outcome!.Block.Should().NotBeNull();
        h.Agents.Pushes.Should().BeGreaterThan(before);
        h.Agents.Outbound[""].Select(e => e.Address).Should().Contain(Remote);
        h.Agents.Outbound["branch"].Select(e => e.Address).Should().Contain(Remote);
        // Blocked as a source too, once.
        h.Agents.Entries[""].Count(e => e.Address == Remote).Should().Be(1);
    }

    [Fact]
    public async Task SiteThatDoesNotShare_DoesNotGetIt()
    {
        var h = new Harness("branch");
        h.Settings.Values[BlockingSettingKeys.ForSite(BlockingSettingKeys.Share, "branch")] = "false";
        await h.Service.RunCycleAsync(DateTime.UtcNow, CancellationToken.None);

        await h.Service.OnIpsOutboundAlertAsync(null, C2(), CancellationToken.None);

        h.Agents.Outbound[""].Select(e => e.Address).Should().Contain(Remote);
        h.Agents.Outbound["branch"].Select(e => e.Address).Should().NotContain(Remote);
    }

    [Fact]
    public async Task SameAlertFromTheConsole_ActsOnce()
    {
        var h = new Harness();
        await h.Service.RunCycleAsync(DateTime.UtcNow, CancellationToken.None);

        (await h.Service.OnIpsOutboundAlertAsync(null, C2(), CancellationToken.None)).Should().NotBeNull();
        (await h.Service.OnIpsOutboundAlertAsync(null, C2(fromAgent: false), CancellationToken.None)).Should().BeNull();
        h.Blocks.Should().ContainSingle();
    }

    [Fact]
    public async Task GatewayAgent_IsCappedPerHour_AndTheConsoleStillDecidesPastIt()
    {
        var h = new Harness();
        await h.Service.RunCycleAsync(DateTime.UtcNow, CancellationToken.None);
        for (var i = 1; i <= ThreatBlockingService.MaxAgentIpsBlocksPerHour + 1; i++)
            await h.Service.OnIpsOutboundAlertAsync(null, C2() with { RemoteIp = $"203.0.113.{i}" }, CancellationToken.None);
        h.Blocks.Should().HaveCount(ThreatBlockingService.MaxAgentIpsBlocksPerHour);

        var last = $"203.0.113.{ThreatBlockingService.MaxAgentIpsBlocksPerHour + 1}";
        (await h.Service.OnIpsOutboundAlertAsync(null, C2(fromAgent: false) with { RemoteIp = last }, CancellationToken.None))!
            .Block.Should().NotBeNull();
    }

    [Fact]
    public async Task OutboundBlock_StaysOffTheWafAndCloudflare()
    {
        var h = new Harness();
        h.Settings.Values[BlockingSettingKeys.TargetWaf] = "true";
        h.Settings.Values[Waf.WafSettingKeys.Url] = "https://waf.test";
        h.Settings.Values[Waf.WafSettingKeys.Token] = "t";
        await h.Service.RunCycleAsync(DateTime.UtcNow, CancellationToken.None);
        await h.Service.OnIpsOutboundAlertAsync(null, C2(), CancellationToken.None);
        h.WafOrCloudflare.Clear();

        await h.Service.RunCycleAsync(DateTime.UtcNow, CancellationToken.None);

        h.WafOrCloudflare.Should().Contain(r => r.StartsWith("https://waf.test"),
            string.Join("; ", h.Service.TargetStatus(null).Select(s => $"{s.Kind} {s.Ok} {s.Error}")));
        h.WafOrCloudflare.Should().NotContain(r => r.Contains(Remote));
    }
}
