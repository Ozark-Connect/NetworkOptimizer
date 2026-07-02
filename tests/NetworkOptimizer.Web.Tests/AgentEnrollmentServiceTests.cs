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

    private readonly TestDbFactory _factory;
    private readonly AgentEnrollmentService _service;

    public AgentEnrollmentServiceTests()
    {
        var options = new DbContextOptionsBuilder<NetworkOptimizerDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _factory = new TestDbFactory(options);
        _service = new AgentEnrollmentService(_factory, new Mock<ILogger<AgentEnrollmentService>>().Object);
    }

    private async Task<int> SeedSiteAsync(string slug = "lake-house")
    {
        await using var db = _factory.CreateDbContext();
        var site = new Site { Slug = slug, Name = "Lake House" };
        db.Sites.Add(site);
        await db.SaveChangesAsync();
        return site.Id;
    }

    [Fact]
    public async Task CreateAgent_StoresOnlyTokenHash()
    {
        var siteId = await SeedSiteAsync();

        var (agent, token) = await _service.CreateAgentAsync(siteId, "Primary");

        token.Should().StartWith("noa_");
        agent.EnrollmentTokenHash.Should().NotBeNullOrEmpty();
        agent.EnrollmentTokenHash.Should().NotContain(token);
        agent.AgentKeyHash.Should().BeNull();
    }

    [Fact]
    public async Task Enroll_ExchangesTokenForKeyAndSiteSlug()
    {
        var siteId = await SeedSiteAsync("branch-office");
        var (_, token) = await _service.CreateAgentAsync(siteId, "Primary");

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
        var (_, token) = await _service.CreateAgentAsync(siteId, "Primary");

        (await _service.EnrollAsync(token, null)).Success.Should().BeTrue();
        (await _service.EnrollAsync(token, null)).Success.Should().BeFalse();
    }

    [Fact]
    public async Task Enroll_RejectsUnknownAndDisabledTokens()
    {
        var siteId = await SeedSiteAsync();
        var (agent, token) = await _service.CreateAgentAsync(siteId, "Primary");

        (await _service.EnrollAsync("noa_deadbeef", null)).Success.Should().BeFalse();

        await _service.SetEnabledAsync(agent.Id, false);
        (await _service.EnrollAsync(token, null)).Success.Should().BeFalse();
    }

    [Fact]
    public async Task Heartbeat_UpdatesLastSeenAndVersion_OnlyForValidKey()
    {
        var siteId = await SeedSiteAsync();
        var (_, token) = await _service.CreateAgentAsync(siteId, "Primary");
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
        var (agent, token) = await _service.CreateAgentAsync(siteId, "Primary");
        var (_, agentKey, _, _) = await _service.EnrollAsync(token, null);

        await _service.SetEnabledAsync(agent.Id, false);

        (await _service.HeartbeatAsync(agentKey!, null)).Should().BeFalse();
    }
}
