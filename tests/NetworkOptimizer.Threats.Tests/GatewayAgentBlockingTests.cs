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
/// Gateway blocking through a gateway agent: the agent gets every change, and the UniFi Console
/// group waits for the daily window while the agent confirms it holds the set.
/// </summary>
public class GatewayAgentBlockingTests
{
    private const string DefaultKey = "default";
    // Two hours after a daily window, so a few minutes later is still the same window.
    private static readonly DateTime T0 = ThreatBlockingService.LastConsoleWindow(3, new DateTime(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc)).AddHours(2);

    private sealed class FakeSites(IReadOnlyList<string> keys) : IScheduleSiteContext
    {
        public string DefaultKey => GatewayAgentBlockingTests.DefaultKey;
        public Task<IReadOnlyList<string>> GetSiteKeysAsync(CancellationToken ct) => Task.FromResult(keys);
        public void PinScope(IServiceScope scope, string siteKey) { }
    }

    private sealed class RecordingGateway : IGatewayBlocklistWriter
    {
        public List<IReadOnlyList<string>> Syncs { get; } = [];
        public List<string?> SyncedSites { get; } = [];
        public bool Fail { get; set; }
        public Task<BlockTargetResult> SyncMembersAsync(string? site, IReadOnlyList<string> ips, CancellationToken ct)
        {
            Syncs.Add(ips);
            SyncedSites.Add(site);
            return Task.FromResult(Fail ? BlockTargetResult.Failure("unreachable") : BlockTargetResult.Success(ips.Count));
        }
        public Task<BlockTargetResult> SyncFeedsAsync(string? site, IReadOnlyList<string> entries, IReadOnlyList<string> outbound, CancellationToken ct) =>
            Task.FromResult(BlockTargetResult.Success(entries.Count));
        public Task<int> EnsureSourcePoliciesNewOnlyAsync(string? site, CancellationToken ct) => Task.FromResult(0);
    }

    private sealed class FakeAgents : IGatewayAgentBlocklist
    {
        public bool Connected { get; set; } = true;
        public bool Enforcing { get; set; } = true;
        public List<(bool Enabled, IReadOnlyList<GatewayAgentEntry> Entries)> Pushes { get; } = [];
        public IReadOnlyList<GatewayAgentEntry> LastOutbound { get; private set; } = [];

        public bool Push(string? site, bool enabled, IReadOnlyList<GatewayAgentEntry> entries, IReadOnlyList<GatewayAgentEntry> outbound,
            string fingerprint)
        {
            Pushes.Add((enabled, entries));
            LastOutbound = outbound;
            return Connected;
        }

        public bool IsEnforcing(string? site, string fingerprint) => Connected && Enforcing;
        public string? Problem(string? site, string fingerprint) => Connected && !Enforcing ? "Rules not in place" : null;
    }

    private sealed class Harness
    {
        public FakeSettings Settings { get; } = new();
        public RecordingGateway Console { get; } = new();
        public FakeAgents Agents { get; } = new();
        public List<ThreatBlock> Blocks { get; } = [];
        public ThreatBlockingService Service { get; }

        public Harness(params string[] otherSites) : this(null, otherSites)
        {
        }

        /// <param name="listBodies">Download bodies by URL; when given, the public lists are fetched from them.</param>
        public Harness(Dictionary<string, string>? listBodies, params string[] otherSites)
        {
            var services = new ServiceCollection();
            services.AddScoped<IThreatRepository>(_ => Repo());
            services.AddSingleton<IThreatSettingsAccessor>(Settings);
            services.AddSingleton<IGatewayBlocklistWriter>(Console);
            var provider = services.BuildServiceProvider();
            var factory = new Mock<IHttpClientFactory>();
            factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(new ScriptedHandler((req, _) =>
                listBodies != null && listBodies.TryGetValue(req.RequestUri!.ToString(), out var body)
                    ? new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body) }
                    : new HttpResponseMessage(System.Net.HttpStatusCode.NotFound))));
            var lists = listBodies == null ? null : new PublicBlockListCache(factory.Object, NullLogger<PublicBlockListCache>.Instance);
            Service = new ThreatBlockingService(provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<ThreatBlockingService>.Instance, factory.Object, new FakeSites([DefaultKey, .. otherSites]), lists: lists, agents: Agents);

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
                .ReturnsAsync((DateTime now, DateTime since, CancellationToken _) => Blocks.Where(b => b.IsActive(now)).ToList());
            repo.Setup(r => r.GetBlockCountsBySourceAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Dictionary<string, int>());
            return repo.Object;
        }

        public void Block(string ip) => Blocks.Add(new ThreatBlock
        {
            SourceIp = ip,
            Trigger = BlockTrigger.Manual,
            Reason = "test",
            Enforced = true,
            CreatedAt = T0.AddDays(-1),
            ExpiresAt = T0.AddDays(5),
        });

        public Task Cycle(DateTime at) => Service.RunCycleAsync(at, CancellationToken.None);
    }

    [Fact]
    public async Task AgentEnforcing_GetsEveryChange_ConsoleWaitsForTheDailyWindow()
    {
        var h = new Harness();
        h.Block("203.0.113.7");
        await h.Cycle(T0);
        h.Console.Syncs.Should().BeEmpty("a restart with a gateway agent connected does not rewrite the console");

        h.Block("203.0.113.8");
        await h.Cycle(T0.AddMinutes(1));
        h.Agents.Pushes.Last().Entries.Select(e => e.Address).Should().BeEquivalentTo("203.0.113.7", "203.0.113.8");
        h.Console.Syncs.Should().BeEmpty("the console waits while the agent holds the set");

        await h.Cycle(T0.AddHours(24).AddMinutes(1));
        h.Console.Syncs.Should().ContainSingle().Which.Should().BeEquivalentTo("203.0.113.7", "203.0.113.8");
        h.Service.GatewayAgentEnforcing(null).Should().BeTrue();
    }

    [Fact]
    public async Task FailedConsoleWrite_IsRetriedWhileTheAgentEnforces()
    {
        var h = new Harness();
        h.Agents.Connected = false;
        h.Console.Fail = true;
        h.Block("203.0.113.7");
        await h.Cycle(T0);
        h.Console.Syncs.Should().ContainSingle();

        h.Agents.Connected = true;
        h.Console.Fail = false;
        await h.Cycle(T0.AddMinutes(1));
        h.Console.Syncs.Should().HaveCount(2);
    }

    [Fact]
    public async Task NoAgent_ConsoleGetsEveryChange()
    {
        var h = new Harness();
        h.Agents.Connected = false;
        h.Block("203.0.113.7");
        await h.Cycle(T0);
        h.Block("203.0.113.8");
        await h.Cycle(T0.AddMinutes(1));

        h.Console.Syncs.Should().HaveCount(2);
    }

    [Fact]
    public async Task AttackerLists_BlockInboundOnly_AndRaiseNoContactMatch()
    {
        var h = new Harness(new Dictionary<string, string>
        {
            ["https://www.spamhaus.org/drop/drop_v4.json"] = "{\"cidr\":\"203.0.113.0/24\"}\n",
            ["https://www.spamhaus.org/drop/drop_v6.json"] = "",
            ["https://cinsscore.com/list/ci-badguys.txt"] = "198.51.100.9\n",
        });
        h.Settings.Values[BlockingSettingKeys.Feeds] = $"{PublicBlockLists.SpamhausDrop},{PublicBlockLists.CinsArmy}";
        h.Settings.Values[BlockingSettingKeys.FeedsOutbound] = "true";

        await h.Cycle(T0);

        h.Agents.Pushes.Last().Entries.Select(e => e.Address).Should().BeEquivalentTo("203.0.113.0/24", "198.51.100.9");
        h.Agents.LastOutbound.Select(e => e.Address).Should().Equal("203.0.113.0/24");
        h.Service.For(null).Find("203.0.113.5")!.Id.Should().Be(PublicBlockLists.SpamhausDrop);
        h.Service.For(null).Find("198.51.100.9").Should().BeNull();
    }

    [Fact]
    public async Task PublicLists_AreCapped_LeastUsefulListsCutFirst()
    {
        static string Ips(int first, int count) =>
            string.Join("\n", Enumerable.Range(first, count).Select(i => $"{11 + i / 65536}.{i / 256 % 256}.{i % 256}.1"));
        var h = new Harness(new Dictionary<string, string>
        {
            ["https://www.spamhaus.org/drop/drop_v4.json"] = "",
            ["https://www.spamhaus.org/drop/drop_v6.json"] = "",
            ["https://feodotracker.abuse.ch/downloads/ipblocklist_recommended.txt"] = Ips(0, 60_000),
            ["https://cinsscore.com/list/ci-badguys.txt"] = Ips(60_000, 70_000),
        });
        h.Settings.Values[BlockingSettingKeys.Feeds] = $"{PublicBlockLists.CinsArmy},{PublicBlockLists.Feodo}";
        h.Settings.Values[BlockingSettingKeys.FeedsOutbound] = "true";

        await h.Cycle(T0);

        h.Agents.LastOutbound.Should().HaveCount(ThreatBlockingService.MaxOutboundFeedEntries);
        h.Agents.Pushes.Last().Entries.Should().HaveCount(ThreatBlockingService.MaxFeedEntries);
        h.Service.FeedEntriesCapped(null).Should().Be(130_000 - ThreatBlockingService.MaxFeedEntries);
    }

    [Fact]
    public async Task AgentRestart_ConsoleWaits()
    {
        var h = new Harness();
        h.Block("203.0.113.7");
        await h.Cycle(T0);
        h.Agents.Connected = false;
        h.Block("203.0.113.8");
        await h.Cycle(T0.AddMinutes(1));
        h.Console.Syncs.Should().BeEmpty("a restarting agent's rules are still on the gateway");

        h.Agents.Connected = true;
        await h.Cycle(T0.AddMinutes(2));
        h.Console.Syncs.Should().BeEmpty();
        h.Agents.Pushes.Last().Entries.Select(e => e.Address).Should().BeEquivalentTo("203.0.113.7", "203.0.113.8");
    }

    [Fact]
    public async Task AgentGone_ConsoleTakesOverAfterTheDisconnectGrace()
    {
        var h = new Harness();
        h.Block("203.0.113.7");
        await h.Cycle(T0);
        h.Agents.Connected = false;
        h.Block("203.0.113.8");

        await h.Cycle(T0.AddMinutes(1));
        await h.Cycle(T0.AddMinutes(2));
        h.Console.Syncs.Should().BeEmpty();

        await h.Cycle(T0.AddMinutes(3));
        h.Console.Syncs.Should().ContainSingle().Which.Should().BeEquivalentTo("203.0.113.7", "203.0.113.8");
        h.Service.GatewayAgentEnforcing(null).Should().BeFalse();
    }

    [Fact]
    public async Task AgentStopsConfirming_ConsoleWritesAfterTheGrace()
    {
        var h = new Harness();
        h.Block("203.0.113.7");
        await h.Cycle(T0);
        h.Agents.Enforcing = false;
        h.Block("203.0.113.8");

        await h.Cycle(T0.AddMinutes(1));
        h.Console.Syncs.Should().BeEmpty();
        h.Service.TargetStatus(null).Should().NotContain(s => !s.Ok);

        await h.Cycle(T0.AddMinutes(6));
        h.Console.Syncs.Should().ContainSingle();
        h.Service.TargetStatus(null).Should().ContainSingle(s => s.Kind == BlockTargetKind.GatewayAgent && !s.Ok)
            .Which.Error.Should().Be("Rules not in place. Updating UniFi Network instead.");
    }

    [Fact]
    public async Task ConsoleSyncRequested_WritesAtOnce()
    {
        var h = new Harness();
        h.Block("203.0.113.7");
        h.Block("203.0.113.8");
        await h.Cycle(T0);
        h.Blocks.RemoveAt(1);

        h.Service.TriggerConsoleSync();
        await h.Cycle(T0.AddMinutes(1));

        h.Console.Syncs.Should().ContainSingle().Which.Should().Equal("203.0.113.7");
    }

    [Fact]
    public async Task SiteConsoleSync_WritesOnlyThatSite()
    {
        var h = new Harness("b");
        h.Block("203.0.113.7");
        await h.Cycle(T0);
        h.Console.Syncs.Should().BeEmpty();

        h.Service.TriggerConsoleSyncFor("b");
        await h.Cycle(T0.AddMinutes(1));

        h.Console.SyncedSites.Should().Equal("b");
    }

    [Fact]
    public async Task ConsoleSync_WritesEverySite()
    {
        var h = new Harness("b");
        h.Block("203.0.113.7");
        await h.Cycle(T0);

        h.Service.TriggerConsoleSync();
        await h.Cycle(T0.AddMinutes(1));

        h.Console.SyncedSites.Should().BeEquivalentTo(new string?[] { null, "b" });
    }

    [Fact]
    public async Task Off_DisablesTheAgent()
    {
        var h = new Harness();
        h.Settings.Values[BlockingSettingKeys.Mode] = "Off";
        await h.Cycle(T0);

        h.Agents.Pushes.Should().ContainSingle().Which.Enabled.Should().BeFalse();
    }

    [Fact]
    public void AgentFingerprint_ChangesWithAnExtendedBan()
    {
        var block = new ThreatBlock { SourceIp = "203.0.113.7", ExpiresAt = T0 };
        var a = ThreatBlockingService.AgentFingerprint(true, [block], []);
        var b = ThreatBlockingService.AgentFingerprint(true, [new ThreatBlock { SourceIp = "203.0.113.7", ExpiresAt = T0.AddHours(4) }], []);
        a.Should().NotBe(b);
        ThreatBlockingService.AgentFingerprint(false, [block], []).Should().NotBe(a);
        ThreatBlockingService.AgentFingerprint(true, [block], [], outbound: true).Should().NotBe(a);
    }

    [Fact]
    public void WafFingerprint_ChangesWithABan_TheLists_OrAWafRestart()
    {
        var started = new DateTimeOffset(2026, 10, 16, 9, 0, 0, TimeSpan.Zero);
        var block = new ThreatBlock { SourceIp = "203.0.113.7", ExpiresAt = T0 };
        var a = ThreatBlockingService.WafFingerprint([block], ["198.51.100.0/24"], started);

        ThreatBlockingService.WafFingerprint([block], ["198.51.100.0/24"], started).Should().Be(a);
        ThreatBlockingService.WafFingerprint([new ThreatBlock { SourceIp = "203.0.113.7", ExpiresAt = T0.AddHours(4) }], ["198.51.100.0/24"], started)
            .Should().NotBe(a, "an extended ban must reach the WAF");
        ThreatBlockingService.WafFingerprint([block], ["198.51.100.0/24", "192.0.2.0/24"], started).Should().NotBe(a);
        ThreatBlockingService.WafFingerprint([block], ["198.51.100.0/24"], started.AddMinutes(5))
            .Should().NotBe(a, "a restarted WAF has lost its list");
    }

    [Fact]
    public void LastConsoleWindow_IsTheMostRecentLocalHour()
    {
        var utc = TimeZoneInfo.Utc;
        ThreatBlockingService.LastConsoleWindow(3, new DateTime(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc), utc)
            .Should().Be(new DateTime(2026, 10, 11, 3, 0, 0));
        ThreatBlockingService.LastConsoleWindow(3, new DateTime(2026, 10, 11, 2, 0, 0, DateTimeKind.Utc), utc)
            .Should().Be(new DateTime(2026, 10, 10, 3, 0, 0));
    }

    [Fact]
    public void LastConsoleWindow_InASpringForwardGap_StartsAfterIt()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("Test", TimeSpan.FromHours(-6), "Test", "Test", "Test Daylight",
        [
            TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(DateTime.MinValue.Date, DateTime.MaxValue.Date, TimeSpan.FromHours(1),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 11, 1, DayOfWeek.Sunday))
        ]);
        // 2026-03-08 is the second Sunday of March: 02:00-03:00 local does not exist.
        var window = ThreatBlockingService.LastConsoleWindow(2, new DateTime(2026, 3, 8, 12, 0, 0, DateTimeKind.Utc), zone);
        TimeZoneInfo.ConvertTimeFromUtc(window, zone).Should().Be(new DateTime(2026, 3, 8, 3, 0, 0));
    }

    [Theory]
    [InlineData(new[] { "203.0.113.7", "198.51.100.0/24" }, new[] { "198.51.100.0/24", "203.0.113.7/32" }, true)]
    [InlineData(new[] { "2001:DB8::1" }, new[] { "2001:db8::1/128" }, true)]
    [InlineData(new[] { "203.0.113.7" }, new[] { "203.0.113.8" }, false)]
    [InlineData(new string[0], new string[0], true)]
    public void SameMembers_IgnoresSpelling(string[] a, string[] b, bool same)
    {
        GatewayBlocklist.SameMembers(a, b).Should().Be(same);
    }
}
