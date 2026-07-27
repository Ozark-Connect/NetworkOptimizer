using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

public class AgentEnrollmentServiceTests
{
    private sealed class TestDbFactory : IDbContextFactory<NetworkOptimizerDbContext>
    {
        private readonly DbContextOptions<NetworkOptimizerDbContext> _options;
        public TestDbFactory(DbContextOptions<NetworkOptimizerDbContext> options) => _options = options;
        public NetworkOptimizerDbContext CreateDbContext() => new(_options);
    }

    /// <summary>No caller in scope, so nothing is narrowed - the background/system behaviour.</summary>
    private sealed class UnfilteredSiteAccess : NetworkOptimizer.Web.Services.Authorization.ISiteAccessFilter
    {
        public Task<IReadOnlySet<string>?> AuthorizedSlugsAsync() => Task.FromResult<IReadOnlySet<string>?>(null);
        public Task<bool> IsAuthorizedAsync(string? slug) => Task.FromResult(true);
        public Task<List<T>> FilterAsync<T>(IEnumerable<T> sites, Func<T, string> slugSelector) => Task.FromResult(sites.ToList());
        public Task<string> FallbackSlugAsync() => Task.FromResult("main");
    }

    private readonly TestDbFactory _factory;
    private readonly AgentTunnelRegistry _tunnelRegistry = new(new AgentTunnelOptions(Enabled: true, Port: 0));
    private readonly AgentEnrollmentService _service;

    public AgentEnrollmentServiceTests()
    {
        var options = new DbContextOptionsBuilder<NetworkOptimizerDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _factory = new TestDbFactory(options);
        _service = new AgentEnrollmentService(_factory, _tunnelRegistry, new UnfilteredSiteAccess(), new Mock<ILogger<AgentEnrollmentService>>().Object);
    }

    private const string Slug = "lake-house";

    private async Task<int> SeedSiteAsync(string slug = Slug)
    {
        await using var db = _factory.CreateDbContext();
        var site = new Site { Slug = slug, Name = "Lake House" };
        db.Sites.Add(site);
        await db.SaveChangesAsync();
        return site.Id;
    }

    /// <summary>The slug for a seeded site id - the write paths name the site by slug now.</summary>
    private string SiteSlugOf(int siteId)
    {
        using var db = _factory.CreateDbContext();
        return db.Sites.Single(x => x.Id == siteId).Slug;
    }

    [Fact]
    public async Task AgentActionsIgnoreAnAgentThatBelongsToAnotherSite()
    {
        // The slug is what the gate authorized; the agent id is whatever the caller passed. Naming a
        // site you administer must not reach an agent sitting on one you do not.
        var mine = await SeedSiteAsync("my-site");
        var theirs = await SeedSiteAsync("their-site");
        var (theirAgent, theirToken) = await _service.CreateAgentAsync("their-site", "Theirs");

        await _service.SetEnabledAsync("my-site", theirAgent.Id, false);
        await _service.DeleteAgentAsync("my-site", theirAgent.Id);
        var reissued = await _service.ReissueTokenAsync("my-site", theirAgent.Id);

        reissued.Should().BeNull("the agent is not on the site the caller was authorized for");
        var survivors = await _service.GetAgentsForSiteAsync(theirs);
        survivors.Single().Id.Should().Be(theirAgent.Id);
        survivors.Single().Enabled.Should().BeTrue("disabling it from another site must not have landed");
        (await _service.EnrollAsync(theirToken, null)).Success.Should()
            .BeTrue("its token must still work - nothing was reissued out from under it");
        (await _service.GetAgentsForSiteAsync(mine)).Should().BeEmpty();
    }

    [Fact]
    public async Task CreateAgent_StoresOnlyTokenHash()
    {
        var siteId = await SeedSiteAsync();

        var (agent, token) = await _service.CreateAgentAsync(SiteSlugOf(siteId), "Primary");

        token.Should().StartWith("noa_");
        agent.EnrollmentTokenHash.Should().NotBeNullOrEmpty();
        agent.EnrollmentTokenHash.Should().NotContain(token);
        agent.AgentKeyHash.Should().BeNull();
    }

    [Fact]
    public async Task Enroll_ExchangesTokenForKeyAndSiteSlug()
    {
        var siteId = await SeedSiteAsync("branch-office");
        var (_, token) = await _service.CreateAgentAsync(SiteSlugOf(siteId), "Primary");

        var (success, agentKey, siteSlug, error) = await _service.EnrollAsync(token, "1.0.0");

        success.Should().BeTrue(error);
        agentKey.Should().StartWith("noak_");
        siteSlug.Should().Be("branch-office");

        var agents = await _service.GetAgentsForSiteAsync(siteId);
        agents.Single().EnrollmentTokenHash.Should().BeNull();
        agents.Single().AgentKeyHash.Should().NotBeNullOrEmpty();
        agents.Single().AgentKeyHash.Should().NotContain(agentKey!);
        agents.Single().EnrolledAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Enroll_TokenIsSingleUse()
    {
        var siteId = await SeedSiteAsync();
        var (_, token) = await _service.CreateAgentAsync(SiteSlugOf(siteId), "Primary");

        (await _service.EnrollAsync(token, null)).Success.Should().BeTrue();
        (await _service.EnrollAsync(token, null)).Success.Should().BeFalse();
    }

    [Fact]
    public async Task Enroll_RejectsExpiredToken()
    {
        var siteId = await SeedSiteAsync();
        var (agent, token) = await _service.CreateAgentAsync(SiteSlugOf(siteId), "Primary");

        await using (var db = _factory.CreateDbContext())
        {
            var row = await db.SiteAgents.FindAsync(agent.Id);
            row!.TokenCreatedAt = DateTime.UtcNow - AgentEnrollmentService.TokenLifetime - TimeSpan.FromMinutes(1);
            await db.SaveChangesAsync();
        }

        var result = await _service.EnrollAsync(token, null);
        result.Success.Should().BeFalse();
        result.Error.Should().Contain("expired");
    }

    [Fact]
    public async Task Enroll_RejectsUnknownAndDisabledTokens()
    {
        var siteId = await SeedSiteAsync();
        var (agent, token) = await _service.CreateAgentAsync(SiteSlugOf(siteId), "Primary");

        (await _service.EnrollAsync("noa_deadbeef", null)).Success.Should().BeFalse();

        await _service.SetEnabledAsync(SiteSlugOf(siteId), agent.Id, false);
        (await _service.EnrollAsync(token, null)).Success.Should().BeFalse();
    }

    [Fact]
    public async Task Heartbeat_UpdatesLastSeenAndVersion_OnlyForValidKey()
    {
        var siteId = await SeedSiteAsync();
        var (_, token) = await _service.CreateAgentAsync(SiteSlugOf(siteId), "Primary");
        var (_, agentKey, _, _) = await _service.EnrollAsync(token, "1.0.0");

        (await _service.HeartbeatAsync(agentKey!, "1.0.1")).Should().BeTrue();
        (await _service.HeartbeatAsync("noak_bogus", null)).Should().BeFalse();

        var agent = (await _service.GetAgentsForSiteAsync(siteId)).Single();
        agent.LastVersion.Should().Be("1.0.1");
        AgentEnrollmentService.IsOnline(agent.LastSeenAt).Should().BeTrue();
    }

    [Fact]
    public async Task Heartbeat_RejectedForDisabledAgent()
    {
        var siteId = await SeedSiteAsync();
        var (agent, token) = await _service.CreateAgentAsync(SiteSlugOf(siteId), "Primary");
        var (_, agentKey, _, _) = await _service.EnrollAsync(token, null);

        await _service.SetEnabledAsync(SiteSlugOf(siteId), agent.Id, false);

        (await _service.HeartbeatAsync(agentKey!, null)).Should().BeFalse();
    }

    [Fact]
    public async Task Enroll_StoresReportedLanIp()
    {
        var siteId = await SeedSiteAsync();
        var (_, token) = await _service.CreateAgentAsync(SiteSlugOf(siteId), "Primary");

        await _service.EnrollAsync(token, "1.0.0", "192.0.2.50");

        var agent = (await _service.GetAgentsForSiteAsync(siteId)).Single();
        agent.LanIp.Should().Be("192.0.2.50");
    }

    [Fact]
    public async Task Heartbeat_UpdatesLanIp_AndIgnoresInvalidValues()
    {
        var siteId = await SeedSiteAsync();
        var (_, token) = await _service.CreateAgentAsync(SiteSlugOf(siteId), "Primary");
        var (_, agentKey, _, _) = await _service.EnrollAsync(token, null, "192.0.2.50");

        await _service.HeartbeatAsync(agentKey!, null, "198.51.100.10");
        (await _service.GetAgentsForSiteAsync(siteId)).Single().LanIp.Should().Be("198.51.100.10");

        // A blank or malformed value must not clobber the known-good LAN IP.
        await _service.HeartbeatAsync(agentKey!, null, "not-an-ip");
        (await _service.GetAgentsForSiteAsync(siteId)).Single().LanIp.Should().Be("198.51.100.10");

        await _service.HeartbeatAsync(agentKey!, null, null);
        (await _service.GetAgentsForSiteAsync(siteId)).Single().LanIp.Should().Be("198.51.100.10");
    }

    [Fact]
    public async Task GetOnlineAgentLanIp_ReturnsIp_ForOnlineEnrolledAgent()
    {
        var siteId = await SeedSiteAsync("branch-office");
        var (_, token) = await _service.CreateAgentAsync(SiteSlugOf(siteId), "Primary");
        await _service.EnrollAsync(token, "1.0.0", "192.0.2.50");

        (await _service.GetOnlineAgentLanIpAsync("branch-office")).Should().Be("192.0.2.50");
    }

    [Fact]
    public async Task GetOnlineAgentLanIp_ReturnsNull_ForDefaultSite()
    {
        (await _service.GetOnlineAgentLanIpAsync(SiteManagementService.DefaultSiteSlug)).Should().BeNull();
    }

    [Fact]
    public async Task GetOnlineAgentLanIp_ReturnsNull_WhenAgentOffline()
    {
        var siteId = await SeedSiteAsync("branch-office");
        var (agent, token) = await _service.CreateAgentAsync(SiteSlugOf(siteId), "Primary");
        await _service.EnrollAsync(token, "1.0.0", "192.0.2.50");

        // Push LastSeenAt outside the online window.
        await using (var db = _factory.CreateDbContext())
        {
            var row = await db.SiteAgents.FindAsync(agent.Id);
            row!.LastSeenAt = DateTime.UtcNow - AgentEnrollmentService.OnlineWindow - TimeSpan.FromMinutes(1);
            await db.SaveChangesAsync();
        }

        (await _service.GetOnlineAgentLanIpAsync("branch-office")).Should().BeNull();
    }

    [Fact]
    public async Task GetOnlineAgentLanIp_FallsBackToTunnelLiveAgent_WhenMostRecentIsStale()
    {
        var siteId = await SeedSiteAsync("branch-office");

        // Older-but-tunnel-connected agent: heartbeat far outside the online
        // window, but its tunnel is registered - IsAgentLive must count it.
        var (liveAgent, liveToken) = await _service.CreateAgentAsync(SiteSlugOf(siteId), "Tunnel");
        await _service.EnrollAsync(liveToken, "1.0.0", "192.0.2.60");

        // Heartbeat-stale AND most recently seen agent: must be skipped in
        // favor of the live one, not returned (old behavior) or null.
        var (staleAgent, staleToken) = await _service.CreateAgentAsync(SiteSlugOf(siteId), "Stale");
        await _service.EnrollAsync(staleToken, "1.0.0", "192.0.2.70");

        await using (var db = _factory.CreateDbContext())
        {
            var live = await db.SiteAgents.FindAsync(liveAgent.Id);
            live!.LastSeenAt = DateTime.UtcNow - AgentEnrollmentService.OnlineWindow - TimeSpan.FromHours(2);
            var stale = await db.SiteAgents.FindAsync(staleAgent.Id);
            stale!.LastSeenAt = DateTime.UtcNow - AgentEnrollmentService.OnlineWindow - TimeSpan.FromMinutes(1);
            await db.SaveChangesAsync();
        }

        _tunnelRegistry.Register(liveAgent.Id, "branch-office", "Tunnel");

        (await _service.GetOnlineAgentLanIpAsync("branch-office")).Should().Be("192.0.2.60");
    }

    [Fact]
    public async Task GetOnlineAgentLanIp_ReturnsNull_WhenLanIpUnknown()
    {
        var siteId = await SeedSiteAsync("branch-office");
        var (_, token) = await _service.CreateAgentAsync(SiteSlugOf(siteId), "Primary");
        await _service.EnrollAsync(token, "1.0.0");

        (await _service.GetOnlineAgentLanIpAsync("branch-office")).Should().BeNull();
    }
}
