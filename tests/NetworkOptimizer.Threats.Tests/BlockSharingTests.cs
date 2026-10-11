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

public class BlockSharingTests
{
    private static readonly DateTime Now = new(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);
    private const string DefaultKey = "default";

    /// <summary>Which site a scope was pinned to.</summary>
    private sealed class SiteHolder { public string Site { get; set; } = DefaultKey; }

    private sealed class FakeSites(params string[] keys) : IScheduleSiteContext
    {
        public string DefaultKey => BlockSharingTests.DefaultKey;
        public Task<IReadOnlyList<string>> GetSiteKeysAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(keys);
        public void PinScope(IServiceScope scope, string siteKey) => scope.ServiceProvider.GetRequiredService<SiteHolder>().Site = siteKey;
    }

    private sealed class RecordingGateway : IGatewayBlocklistWriter
    {
        public List<(string? Site, IReadOnlyList<string> Ips)> Syncs { get; } = [];
        public Task<BlockTargetResult> SyncMembersAsync(string? site, IReadOnlyList<string> ips, CancellationToken ct)
        {
            Syncs.Add((site, ips));
            return Task.FromResult(BlockTargetResult.Success(ips.Count));
        }
        public IReadOnlyList<string> LastFor(string? site) => Syncs.Last(s => s.Site == site).Ips;
    }

    private sealed class Harness
    {
        public FakeSettings Settings { get; } = new();
        public RecordingGateway Gateway { get; } = new();
        public Dictionary<string, List<ThreatBlock>> Blocks { get; } = new() { [DefaultKey] = [], ["b"] = [] };
        public Dictionary<string, Dictionary<string, int>> Waf { get; } = new() { [DefaultKey] = new(), ["b"] = new() };
        public ThreatBlockingService Service { get; }

        public Harness()
        {
            var sites = new FakeSites(DefaultKey, "b");
            var services = new ServiceCollection();
            services.AddScoped<SiteHolder>();
            services.AddScoped<IThreatRepository>(sp => Repo(sp.GetRequiredService<SiteHolder>().Site));
            services.AddSingleton<IThreatSettingsAccessor>(Settings);
            services.AddSingleton<IGatewayBlocklistWriter>(Gateway);
            services.AddSingleton<IScheduleSiteContext>(sites);
            services.AddScoped<IBlockSharingScope, InstanceBlockSharingScope>();
            var provider = services.BuildServiceProvider();

            var factory = new Mock<IHttpClientFactory>();
            factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new HttpClient());
            Service = new ThreatBlockingService(provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<ThreatBlockingService>.Instance, factory.Object, sites);
        }

        private IThreatRepository Repo(string site)
        {
            var repo = new Mock<IThreatRepository>();
            repo.Setup(r => r.GetNoiseFiltersAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
            repo.Setup(r => r.GetWafEventCountsBySourceAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new Dictionary<string, int>(Waf[site]));
            repo.Setup(r => r.GetPatternsSeenAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
            repo.Setup(r => r.GetBlocksAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((DateTime now, DateTime since, CancellationToken _) =>
                    Blocks[site].Where(b => b.IsActive(now) || b.CreatedAt >= since).ToList());
            repo.Setup(r => r.GetBlockCountsBySourceAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Dictionary<string, int>());
            repo.Setup(r => r.AddBlocksAsync(It.IsAny<List<ThreatBlock>>(), It.IsAny<CancellationToken>()))
                .Callback((List<ThreatBlock> b, CancellationToken _) => Blocks[site].AddRange(b))
                .Returns(Task.CompletedTask);
            return repo.Object;
        }

        public void Set(string? site, string key, string value) => Settings.Values[BlockingSettingKeys.ForSite(key, site)] = value;

        public void Enforce(string? site, bool share)
        {
            Set(site, BlockingSettingKeys.Mode, "Enforce");
            Set(site, BlockingSettingKeys.TargetGateway, "true");
            Set(site, BlockingSettingKeys.Share, share.ToString());
        }
    }

    [Fact]
    public async Task NotSharing_EachSiteEnforcesOnlyItsOwn()
    {
        var h = new Harness();
        h.Enforce(null, share: false);
        h.Enforce("b", share: false);
        h.Waf["b"]["203.0.113.7"] = 9;

        await h.Service.RunCycleAsync(Now, CancellationToken.None);

        h.Blocks["b"].Should().ContainSingle();
        h.Blocks[DefaultKey].Should().BeEmpty();
        h.Gateway.LastFor("b").Should().Equal("203.0.113.7");
        h.Gateway.LastFor(null).Should().BeEmpty();
    }

    [Fact]
    public async Task Sharing_APeersBlockReachesThisSitesGateway_WithoutCopyingTheRecord()
    {
        var h = new Harness();
        h.Enforce(null, share: true);
        h.Enforce("b", share: true);
        h.Waf["b"]["203.0.113.7"] = 9;

        await h.Service.RunCycleAsync(Now, CancellationToken.None);

        h.Gateway.LastFor(null).Should().Equal("203.0.113.7");
        h.Gateway.LastFor("b").Should().Equal("203.0.113.7");
        h.Blocks[DefaultKey].Should().BeEmpty(); // the decision stays with the site that made it
    }

    [Fact]
    public async Task Sharing_NeedsBothSides()
    {
        var h = new Harness();
        h.Enforce(null, share: true);
        h.Enforce("b", share: false);
        h.Waf["b"]["203.0.113.7"] = 9;

        await h.Service.RunCycleAsync(Now, CancellationToken.None);

        h.Gateway.LastFor(null).Should().BeEmpty();
    }

    [Fact]
    public async Task Sharing_TheReceivingSitesAllowlistWins()
    {
        var h = new Harness();
        h.Enforce(null, share: true);
        h.Enforce("b", share: true);
        h.Set(null, BlockingSettingKeys.Allowlist, "203.0.113.0/24");
        h.Waf["b"]["203.0.113.7"] = 9;

        await h.Service.RunCycleAsync(Now, CancellationToken.None);

        h.Gateway.LastFor("b").Should().Equal("203.0.113.7");
        h.Gateway.LastFor(null).Should().BeEmpty();
    }

    [Fact]
    public async Task Sharing_ADryRunPeerContributesNothing()
    {
        var h = new Harness();
        h.Enforce(null, share: true);
        h.Enforce("b", share: true);
        h.Set("b", BlockingSettingKeys.Mode, "DryRun");
        h.Waf["b"]["203.0.113.7"] = 9;

        await h.Service.RunCycleAsync(Now, CancellationToken.None);

        h.Blocks["b"].Should().ContainSingle().Which.Enforced.Should().BeFalse();
        h.Gateway.Syncs.Where(s => s.Site == null).Should().OnlyContain(s => s.Ips.Count == 0);
    }

    [Fact]
    public async Task Scope_ReturnsOnlySharingSites_DefaultAsNull()
    {
        var settings = new FakeSettings();
        settings.Values[BlockingSettingKeys.Share] = "true";
        settings.Values[BlockingSettingKeys.ForSite(BlockingSettingKeys.Share, "b")] = "true";
        var scope = new InstanceBlockSharingScope(settings, new FakeSites(DefaultKey, "b", "c"));

        (await scope.GetPeerSitesAsync(null, CancellationToken.None)).Should().Equal(null, "b");
        (await scope.GetPeerSitesAsync("c", CancellationToken.None)).Should().Equal("c");
    }
}
