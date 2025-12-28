using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Repositories;
using Xunit;

namespace NetworkOptimizer.Storage.Tests;

public class AgentRepositoryTests : IDisposable
{
    private readonly NetworkOptimizerDbContext _context;
    private readonly AgentRepository _repository;

    public AgentRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<NetworkOptimizerDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new NetworkOptimizerDbContext(options);
        var logger = new Mock<ILogger<AgentRepository>>();
        _repository = new AgentRepository(_context, logger.Object);
    }

    public void Dispose()
    {
        _context.Dispose();
    }

    [Fact]
    public async Task SaveAgentConfigAsync_SavesConfig()
    {
        var config = new AgentConfiguration
        {
            AgentId = "agent-1",
            AgentName = "Test Agent",
            IsEnabled = true
        };

        await _repository.SaveAgentConfigAsync(config);

        var saved = await _context.AgentConfigurations.FindAsync("agent-1");
        saved.Should().NotBeNull();
        saved!.AgentName.Should().Be("Test Agent");
    }

    [Fact]
    public async Task GetAgentConfigAsync_ReturnsCorrectConfig()
    {
        var config = new AgentConfiguration
        {
            AgentId = "agent-1",
            AgentName = "Test Agent",
            IsEnabled = true
        };
        _context.AgentConfigurations.Add(config);
        await _context.SaveChangesAsync();

        var result = await _repository.GetAgentConfigAsync("agent-1");

        result.Should().NotBeNull();
        result!.AgentName.Should().Be("Test Agent");
    }

    [Fact]
    public async Task GetAllAgentConfigsAsync_ReturnsOrderedByName()
    {
        _context.AgentConfigurations.AddRange(
            new AgentConfiguration { AgentId = "agent-1", AgentName = "Zebra", IsEnabled = true },
            new AgentConfiguration { AgentId = "agent-2", AgentName = "Alpha", IsEnabled = true },
            new AgentConfiguration { AgentId = "agent-3", AgentName = "Beta", IsEnabled = false }
        );
        await _context.SaveChangesAsync();

        var results = await _repository.GetAllAgentConfigsAsync();

        results.Should().HaveCount(3);
        results[0].AgentName.Should().Be("Alpha");
        results[1].AgentName.Should().Be("Beta");
        results[2].AgentName.Should().Be("Zebra");
    }

    [Fact]
    public async Task DeleteAgentConfigAsync_RemovesConfig()
    {
        var config = new AgentConfiguration
        {
            AgentId = "agent-1",
            AgentName = "Test Agent",
            IsEnabled = true
        };
        _context.AgentConfigurations.Add(config);
        await _context.SaveChangesAsync();

        await _repository.DeleteAgentConfigAsync("agent-1");

        var deleted = await _context.AgentConfigurations.FindAsync("agent-1");
        deleted.Should().BeNull();
    }
}
