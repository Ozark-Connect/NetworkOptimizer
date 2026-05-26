using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Repositories;
using NetworkOptimizer.Threats.Models;
using Xunit;

namespace NetworkOptimizer.Storage.Tests;

/// <summary>
/// Verifies persistence of the new Category / Label / IsSystem fields on
/// ThreatNoiseFilter through the repository layer.
/// </summary>
public class ThreatNoiseFilterPersistenceTests : IDisposable
{
    private readonly NetworkOptimizerDbContext _context;
    private readonly ThreatRepository _repository;

    public ThreatNoiseFilterPersistenceTests()
    {
        var options = new DbContextOptionsBuilder<NetworkOptimizerDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new NetworkOptimizerDbContext(options);
        var logger = new Mock<ILogger<ThreatRepository>>();
        _repository = new ThreatRepository(_context, logger.Object);
    }

    public void Dispose() => _context.Dispose();

    [Fact]
    public async Task SaveNoiseFilterAsync_RoundTripsCategoryLabelAndIsSystem()
    {
        var filter = new ThreatNoiseFilter
        {
            SourceIp = "192.0.2.10",
            Category = ThreatFilterCategory.Infrastructure,
            Label = "Network Optimizer (self)",
            Description = "Auto-detected.",
            IsSystem = true,
            Enabled = true
        };

        await _repository.SaveNoiseFilterAsync(filter);

        var stored = (await _repository.GetNoiseFiltersAsync()).Single();
        stored.Category.Should().Be(ThreatFilterCategory.Infrastructure);
        stored.Label.Should().Be("Network Optimizer (self)");
        stored.IsSystem.Should().BeTrue();
    }

    [Fact]
    public async Task SaveNoiseFilterAsync_LegacyFilterDefaultsToNoiseCategory()
    {
        // A pre-PR filter written without an explicit Category should land as Noise.
        // Documents the implicit default that pre-existing rows pick up via the
        // migration's defaultValue: 0 on the Category column.
        var filter = new ThreatNoiseFilter
        {
            SourceIp = "198.51.100.50",
            Description = "legacy filter"
        };

        await _repository.SaveNoiseFilterAsync(filter);

        var stored = (await _repository.GetNoiseFiltersAsync()).Single();
        stored.Category.Should().Be(ThreatFilterCategory.Noise);
        stored.IsSystem.Should().BeFalse();
        stored.Label.Should().BeNull();
    }

    [Fact]
    public async Task DemoteAndDisableSystemFilterAsync_StripsIsSystemAndDisables()
    {
        var filter = new ThreatNoiseFilter
        {
            SourceIp = "192.0.2.10",
            Category = ThreatFilterCategory.Infrastructure,
            Label = "Network Optimizer (self)",
            Description = "self",
            IsSystem = true,
            Enabled = true
        };
        await _repository.SaveNoiseFilterAsync(filter);
        var stored = (await _repository.GetNoiseFiltersAsync()).Single();

        await _repository.DemoteAndDisableSystemFilterAsync(stored.Id);

        var after = (await _repository.GetNoiseFiltersAsync()).Single();
        after.IsSystem.Should().BeFalse("system lock removed so user can manage it");
        after.Enabled.Should().BeFalse("disabled since the IP no longer represents this host");
        after.SourceIp.Should().Be("192.0.2.10", "row is kept in the table for audit history");
        after.Label.Should().Be("Network Optimizer (self)", "label preserved so user knows what it was");
    }

    [Fact]
    public async Task PromoteToSystemFilterAsync_RestoresIsSystemAndEnables()
    {
        var filter = new ThreatNoiseFilter
        {
            SourceIp = "192.0.2.10",
            Category = ThreatFilterCategory.Infrastructure,
            Label = "Network Optimizer (self)",
            Description = "self",
            IsSystem = false,
            Enabled = false
        };
        await _repository.SaveNoiseFilterAsync(filter);
        var stored = (await _repository.GetNoiseFiltersAsync()).Single();

        await _repository.PromoteToSystemFilterAsync(stored.Id);

        var after = (await _repository.GetNoiseFiltersAsync()).Single();
        after.IsSystem.Should().BeTrue();
        after.Enabled.Should().BeTrue();
    }
}
