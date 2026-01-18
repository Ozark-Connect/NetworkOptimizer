using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Repositories;
using Xunit;

namespace NetworkOptimizer.Storage.Tests;

public class ModemRepositoryTests : IDisposable
{
    private readonly NetworkOptimizerDbContext _context;
    private readonly ModemRepository _repository;

    public ModemRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<NetworkOptimizerDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new NetworkOptimizerDbContext(options);
        var logger = new Mock<ILogger<ModemRepository>>();
        _repository = new ModemRepository(_context, logger.Object);
    }

    public void Dispose()
    {
        _context.Dispose();
    }

    [Fact]
    public async Task GetModemConfigurationsAsync_ReturnsAllOrderedByName()
    {
        _context.ModemConfigurations.AddRange(
            new ModemConfiguration { SiteId = 1, Name = "Modem Z", Host = "192.168.1.3" },
            new ModemConfiguration { SiteId = 1, Name = "Modem A", Host = "192.168.1.1" }
        );
        await _context.SaveChangesAsync();

        var results = await _repository.GetModemConfigurationsAsync(1);

        results.Should().HaveCount(2);
        results[0].Name.Should().Be("Modem A");
    }

    [Fact]
    public async Task GetEnabledModemConfigurationsAsync_ReturnsOnlyEnabled()
    {
        _context.ModemConfigurations.AddRange(
            new ModemConfiguration { SiteId = 1, Name = "Enabled Modem", Host = "192.168.1.1", Enabled = true },
            new ModemConfiguration { SiteId = 1, Name = "Disabled Modem", Host = "192.168.1.2", Enabled = false }
        );
        await _context.SaveChangesAsync();

        var results = await _repository.GetEnabledModemConfigurationsAsync(1);

        results.Should().HaveCount(1);
        results[0].Name.Should().Be("Enabled Modem");
    }

    [Fact]
    public async Task GetModemConfigurationAsync_ReturnsById()
    {
        var modem = new ModemConfiguration { SiteId = 1, Name = "Test Modem", Host = "192.168.1.1" };
        _context.ModemConfigurations.Add(modem);
        await _context.SaveChangesAsync();

        var result = await _repository.GetModemConfigurationAsync(1, modem.Id);

        result.Should().NotBeNull();
        result!.Name.Should().Be("Test Modem");
    }

    [Fact]
    public async Task SaveModemConfigurationAsync_CreatesNew()
    {
        var modem = new ModemConfiguration { SiteId = 1, Name = "New Modem", Host = "192.168.1.100" };

        await _repository.SaveModemConfigurationAsync(1, modem);

        var saved = await _context.ModemConfigurations.FirstOrDefaultAsync(m => m.Name == "New Modem");
        saved.Should().NotBeNull();
    }

    [Fact]
    public async Task DeleteModemConfigurationAsync_RemovesModem()
    {
        var modem = new ModemConfiguration { SiteId = 1, Name = "To Delete", Host = "192.168.1.1" };
        _context.ModemConfigurations.Add(modem);
        await _context.SaveChangesAsync();
        var id = modem.Id;

        await _repository.DeleteModemConfigurationAsync(1, id);

        var deleted = await _context.ModemConfigurations.FindAsync(id);
        deleted.Should().BeNull();
    }
}
