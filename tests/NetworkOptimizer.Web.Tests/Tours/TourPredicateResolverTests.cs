using System.Reflection;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Repositories;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.UniFi.Models;
using NetworkOptimizer.Web.Services;
using NetworkOptimizer.Web.Services.Authorization;
using NetworkOptimizer.Web.Services.Tours;
using Xunit;
using R = NetworkOptimizer.Web.Services.Tours.TourPredicateResolver;

namespace NetworkOptimizer.Web.Tests.Tours;

/// <summary>
/// The resolver sees only the caller's sites because it reads them through the filtering
/// SiteManagementService and AgentEnrollmentService calls; nothing else enforces it, so these pin it.
/// It also evaluates only the predicates asked for, from one site context and one WAN fetch per site.
/// </summary>
public class TourPredicateResolverTests : IDisposable
{
    private const string SiteA = "site-a";
    private const string SiteB = "site-b";
    private const string Main = SiteManagementService.DefaultSiteSlug;

    private static readonly IReadOnlySet<string> AllPredicates = typeof(R)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.IsLiteral && f.FieldType == typeof(string))
        .Select(f => (string)f.GetRawConstantValue()!)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private readonly string _dir;
    private readonly SiteDatabasePaths _paths;
    private readonly SiteDbContextFactory _siteDbFactory;
    private readonly TestDbFactory _mainDbFactory;

    public TourPredicateResolverTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "no-tour-predicates-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        _paths = new SiteDatabasePaths(Path.Combine(_dir, "network_optimizer.db"));
        _siteDbFactory = new SiteDbContextFactory(_paths);
        _mainDbFactory = new TestDbFactory(new DbContextOptionsBuilder<NetworkOptimizerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    }

    public void Dispose()
    {
        SqliteCleanup();
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir; a leftover is harmless */ }
        GC.SuppressFinalize(this);
    }

    private static void SqliteCleanup() => Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

    // ---- confinement pins ----

    [Fact]
    public async Task MemberSeesOnlyAuthorizedSites_AndIsNeverPointedAtAnother()
    {
        await SeedSitesAsync(multiSite: true);
        await SeedSiteDbAsync(SiteB, cableModem: true);
        var resolver = Resolver(new SubsetAccess(Main, SiteA));

        var ctx = await resolver.ResolveAsync(Needs(R.CableModem));

        ctx.Sites.Select(s => s.Slug).Should().BeEquivalentTo(Main, SiteA);
        ctx.Satisfies([R.CableModem], Main, out _).Should().BeFalse("the only cable modem is on a site the caller cannot see");
    }

    [Fact]
    public async Task UnfilteredScopeSeesEverySite()
    {
        await SeedSitesAsync(multiSite: true);
        await SeedSiteDbAsync(SiteB, cableModem: true);
        var resolver = Resolver(new SubsetAccess(null));

        var ctx = await resolver.ResolveAsync(Needs(R.CableModem));

        ctx.Sites.Select(s => s.Slug).Should().BeEquivalentTo(Main, SiteA, SiteB);
        ctx.Satisfies([R.CableModem], Main, out var slug).Should().BeTrue();
        slug.Should().Be(SiteB);
    }

    [Fact]
    public async Task MultiSiteDoesNotHoldForAMemberWithOneVisibleSite()
    {
        await SeedSitesAsync(multiSite: true);

        var ctx = await Resolver(new SubsetAccess(SiteA)).ResolveAsync(Needs());

        ctx.Satisfies([R.MultiSite], SiteA, out _).Should().BeFalse();
    }

    [Fact]
    public async Task HasAgentDoesNotHoldWhenTheOnlyAgentIsOnAnUnseenSite()
    {
        await SeedSitesAsync(multiSite: true);
        await SeedAgentAsync(SiteB);

        var member = await Resolver(new SubsetAccess(Main, SiteA)).ResolveAsync(Needs(R.HasAgent));
        var admin = await Resolver(new SubsetAccess(null)).ResolveAsync(Needs(R.HasAgent));

        member.Satisfies([R.HasAgent], Main, out _).Should().BeFalse();
        admin.Satisfies([R.HasAgent], Main, out _).Should().BeTrue();
    }

    // ---- demand-driven evaluation ----

    [Fact]
    public async Task OnlyTheNeededPredicatesRun()
    {
        await SeedSitesAsync(multiSite: true);
        var resolver = Resolver(new SubsetAccess(null));

        await resolver.ResolveAsync(Needs(R.CableModem));

        resolver.WanFetches.Should().Be(0);
        resolver.GatewaySshReads.Should().Be(0);
        resolver.AgentReads.Should().Be(0);
        resolver.SiteDbOpens.Should().Be(3, "one per site");
    }

    [Fact]
    public async Task NoPerSitePredicateNeeded_TouchesNothingPerSite()
    {
        await SeedSitesAsync(multiSite: true);
        var resolver = Resolver(new SubsetAccess(null));

        await resolver.ResolveAsync(Needs());

        (resolver.WanFetches + resolver.GatewaySshReads + resolver.AgentReads + resolver.SiteDbOpens).Should().Be(0);
    }

    [Fact]
    public async Task EveryDbPredicateSharesOneSiteContext()
    {
        await SeedSitesAsync(multiSite: true);
        var resolver = Resolver(new SubsetAccess(null));

        await resolver.ResolveAsync(Needs(R.IspHealth, R.HasTargets, R.SqmEnabled, R.Starlink, R.Cellular, R.CableModem));

        resolver.SiteDbOpens.Should().Be(3);
    }

    [Fact]
    public async Task MultiWanAndSmartQueuesShareOneWanFetch()
    {
        await SeedSitesAsync(multiSite: true);
        var resolver = Resolver(new SubsetAccess(null));

        await resolver.ResolveAsync(Needs(R.MultiWan, R.SmartQueues));

        resolver.WanFetches.Should().Be(3);
    }

    [Fact]
    public async Task SatisfiesThrowsForAnUnevaluatedPredicate()
    {
        await SeedSitesAsync(multiSite: true);

        var ctx = await Resolver(new SubsetAccess(null)).ResolveAsync(Needs(R.CableModem));

        var act = () => ctx.Satisfies([R.Starlink], Main, out _);
        act.Should().Throw<InvalidOperationException>();
        ctx.Satisfies([R.MultiSite], Main, out _).Should().BeTrue("multi-site is always evaluated");
    }

    [Fact]
    public async Task EveryPredicateHoldsExactlyWhereItsDataSays()
    {
        await SeedSitesAsync(multiSite: true);
        await SeedSiteDbAsync(Main, ispHealth: true, sqm: true);
        await SeedSiteDbAsync(SiteA, monitoringOnWithTarget: true, starlink: true);
        await SeedSiteDbAsync(SiteB, monitoringOffWithTarget: true, cellular: true, cableModem: true);
        await SeedAgentAsync(SiteA);
        var resolver = Resolver(new SubsetAccess(null));
        resolver.GatewaySshSites.Add(SiteA);
        resolver.Wans[Main] = [Wan(enabled: true, smartq: true), Wan(enabled: true)];
        resolver.Wans[SiteA] = [Wan(enabled: true), Wan(enabled: false, smartq: true)];
        resolver.Wans[SiteB] = null; // not connected

        var ctx = await resolver.ResolveAsync(AllPredicates);

        ctx.QualifyingSites.Should().BeEquivalentTo(new Dictionary<string, string[]>
        {
            [R.MultiSite] = [Main, SiteA, SiteB],
            [R.HasAgent] = [Main, SiteA, SiteB],
            [R.GatewaySsh] = [SiteA],
            [R.IspHealth] = [Main],
            [R.HasTargets] = [Main, SiteA],
            [R.SqmEnabled] = [Main],
            [R.Starlink] = [SiteA],
            [R.Cellular] = [SiteB],
            [R.CableModem] = [SiteB],
            [R.MultiWan] = [Main],
            [R.SmartQueues] = [Main],
        }.ToDictionary(kv => kv.Key, kv => kv.Value.ToHashSet(StringComparer.OrdinalIgnoreCase)));
        ctx.Satisfies([R.Starlink], Main, out var slug).Should().BeTrue();
        slug.Should().Be(SiteA, "the active site does not qualify, so the first qualifying visible site is used");
    }

    [Fact]
    public async Task OneFailingSiteDatabaseDoesNotTakeOtherSitesDown()
    {
        await SeedSitesAsync(multiSite: true);
        await SeedSiteDbAsync(SiteB, cableModem: true);
        var resolver = Resolver(new SubsetAccess(null));
        resolver.BrokenSiteDb = SiteA;

        var ctx = await resolver.ResolveAsync(Needs(R.CableModem));

        ctx.Satisfies([R.CableModem], Main, out var slug).Should().BeTrue();
        slug.Should().Be(SiteB);
    }

    // ---- TourService needed sets ----

    [Fact]
    public void DueOfferNeeds_IsTheUnionOverUnseenStepsOnly()
    {
        var tour = Tour("2.9.0", Step("a", R.CableModem), Step("b", R.Starlink), Step("c"));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "b" };

        TourService.DueOfferNeeds([tour], null, seen).Should().BeEquivalentTo([R.CableModem]);
    }

    [Fact]
    public void DueOfferNeeds_ForATourWithoutRequires_IsEmpty()
    {
        var tour = Tour("2.9.2", Step("a"), Step("b"));

        TourService.DueOfferNeeds([tour], null, new HashSet<string>()).Should().BeEmpty();
    }

    [Fact]
    public void DueOfferNeeds_AddsOnlyMajorHighlightsSteps()
    {
        var highlights = Tour("highlights-1", Step("h1", R.GatewaySsh), Step("h2", R.Cellular, level: TourLevels.Minor));
        highlights.Kind = TourKinds.Highlights;
        highlights.Version = "2.9.0";

        TourService.DueOfferNeeds([], highlights, new HashSet<string>()).Should().BeEquivalentTo([R.GatewaySsh]);
    }

    // ---- fixture ----

    private static IReadOnlySet<string> Needs(params string[] names) => names.ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static UniFiNetworkConfig Wan(bool enabled, bool smartq = false) =>
        new() { Enabled = enabled, WanSmartqEnabled = smartq };

    private static TourStep Step(string id, string? requires = null, string level = TourLevels.Major) => new()
    {
        Id = id,
        Level = level,
        Requires = requires == null ? new List<string>() : new List<string> { requires },
    };

    private static TourDefinition Tour(string id, params TourStep[] steps) => new() { Id = id, Steps = steps.ToList() };

    private CountingResolver Resolver(ISiteAccessFilter access)
    {
        var siteManagement = new SiteManagementService(
            new SiteRepository(_mainDbFactory, NullLogger<SiteRepository>.Instance),
            siteRoles: null!, tunnelRegistry: null!, _mainDbFactory, authDbFactory: null!, _paths,
            licenseState: null!, activation: null!, siteConnections: null!, siteRegistries: [],
            collectionRegistry: null!, changeNotifier: null!, access,
            NullLogger<SiteManagementService>.Instance);
        // Only GetAllAgentsAsync is called, and it touches the main database and the filter alone.
        var agents = new AgentEnrollmentService(_mainDbFactory, tunnelRegistry: null!, access,
            agentCoverage: null!, serviceProvider: null!, tunnelRouting: null!,
            NullLogger<AgentEnrollmentService>.Instance);
        return new CountingResolver(siteManagement, agents, _siteDbFactory);
    }

    private async Task SeedSitesAsync(bool multiSite)
    {
        await using var db = _mainDbFactory.CreateDbContext();
        db.SystemSettings.Add(new SystemSetting { Key = SystemSettingKeys.MultiSiteEnabled, Value = multiSite.ToString() });
        db.Sites.AddRange(
            new Site { Slug = Main, Name = "Main", IsDefault = true },
            new Site { Slug = SiteA, Name = "Site A", SortOrder = 1 },
            new Site { Slug = SiteB, Name = "Site B", SortOrder = 2 });
        await db.SaveChangesAsync();

        foreach (var (slug, isDefault) in new[] { (Main, true), (SiteA, false), (SiteB, false) })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_paths.GetSiteDbPath(slug, isDefault))!);
            await using var siteDb = _siteDbFactory.CreateForSite(slug, isDefault);
            await siteDb.Database.MigrateAsync();
        }
    }

    private async Task SeedSiteDbAsync(string slug, bool ispHealth = false, bool monitoringOnWithTarget = false,
        bool monitoringOffWithTarget = false, bool sqm = false, bool starlink = false, bool cellular = false,
        bool cableModem = false)
    {
        await using var db = _siteDbFactory.CreateForSite(slug, slug == Main);
        if (ispHealth || monitoringOnWithTarget || monitoringOffWithTarget)
            db.MonitoringSettings.Add(new MonitoringSettings { Enabled = !monitoringOffWithTarget });
        if (ispHealth)
            db.MonitoringTargets.Add(new MonitoringTarget { Enabled = true, TargetType = MonitoringTargetType.AccessIsp });
        if (monitoringOnWithTarget || monitoringOffWithTarget)
            db.MonitoringTargets.Add(new MonitoringTarget { Enabled = true });
        if (sqm)
            db.SqmWanConfigurations.Add(new SqmWanConfiguration { Enabled = true, WanNumber = 1 });
        if (starlink)
            db.StarlinkConfigurations.Add(new StarlinkConfiguration { Enabled = true });
        if (cellular)
            db.ModemConfigurations.Add(new ModemConfiguration { Enabled = true });
        if (cableModem)
            db.CmConfigurations.Add(new CmConfiguration { Enabled = true });
        await db.SaveChangesAsync();
    }

    private async Task SeedAgentAsync(string slug)
    {
        await using var db = _mainDbFactory.CreateDbContext();
        var siteId = db.Sites.Single(s => s.Slug == slug).Id;
        db.SiteAgents.Add(new SiteAgent { SiteId = siteId, Name = "Agent", EnrolledAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
    }

    private sealed class TestDbFactory : IDbContextFactory<NetworkOptimizerDbContext>
    {
        private readonly DbContextOptions<NetworkOptimizerDbContext> _options;
        public TestDbFactory(DbContextOptions<NetworkOptimizerDbContext> options) => _options = options;
        public NetworkOptimizerDbContext CreateDbContext() => new(_options);
    }

    /// <summary>A caller authorized for the given slugs; null means no narrowing (system scope or auth off).</summary>
    private sealed class SubsetAccess : ISiteAccessFilter
    {
        private readonly HashSet<string>? _allowed;
        public SubsetAccess(params string[]? allowed) =>
            _allowed = allowed?.ToHashSet(StringComparer.OrdinalIgnoreCase);

        public Task<IReadOnlySet<string>?> AuthorizedSlugsAsync() => Task.FromResult<IReadOnlySet<string>?>(_allowed);
        public Task<bool> IsAuthorizedAsync(string? slug) => Task.FromResult(_allowed == null || (slug != null && _allowed.Contains(slug)));
        public Task<List<T>> FilterAsync<T>(IEnumerable<T> sites, Func<T, string> slugSelector) =>
            Task.FromResult(sites.Where(s => _allowed == null || _allowed.Contains(slugSelector(s))).ToList());
        public Task<string> FallbackSlugAsync() => Task.FromResult(_allowed?.First() ?? Main);
    }

    /// <summary>Real sites, agents, and site databases; the console and gateway SSH are stood in for.</summary>
    private sealed class CountingResolver : R
    {
        public int AgentReads, SiteDbOpens, WanFetches, GatewaySshReads;
        public string? BrokenSiteDb;
        public readonly HashSet<string> GatewaySshSites = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, List<UniFiNetworkConfig>?> Wans = new(StringComparer.OrdinalIgnoreCase);

        public CountingResolver(SiteManagementService sites, AgentEnrollmentService agents, SiteDbContextFactory siteDb)
            : base(sites, gatewaySshRegistry: null!, siteConnections: null!, agents, siteDb, NullLogger<R>.Instance) { }

        protected override Task<List<SiteAgent>> ReadAgentsAsync()
        {
            AgentReads++;
            return base.ReadAgentsAsync();
        }

        protected override NetworkOptimizerDbContext OpenSiteDb(Site site)
        {
            SiteDbOpens++;
            if (string.Equals(site.Slug, BrokenSiteDb, StringComparison.OrdinalIgnoreCase))
                throw new IOException("site database unreachable");
            return base.OpenSiteDb(site);
        }

        protected override Task<List<UniFiNetworkConfig>?> ReadWanConfigsAsync(string slug)
        {
            WanFetches++;
            return Task.FromResult(Wans.GetValueOrDefault(slug));
        }

        protected override Task<bool> HasGatewaySshAsync(string slug)
        {
            GatewaySshReads++;
            return Task.FromResult(GatewaySshSites.Contains(slug));
        }
    }
}
