using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Repositories;
using Xunit;

namespace NetworkOptimizer.Storage.Tests;

public class OntRepositoryTests : IDisposable
{
    private readonly NetworkOptimizerDbContext _context;
    private readonly OntRepository _repository;

    public OntRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<NetworkOptimizerDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new NetworkOptimizerDbContext(options);
        var logger = new Mock<ILogger<OntRepository>>();
        _repository = new OntRepository(_context, logger.Object);
    }

    public void Dispose() => _context.Dispose();

    [Fact]
    public async Task GetEnabledOntConfigurationsAsync_ReturnsOnlyEnabled()
    {
        _context.OntConfigurations.AddRange(
            new OntConfiguration { Name = "Live ONT", Host = "192.168.1.1", Enabled = true },
            new OntConfiguration { Name = "Paused ONT", Host = "192.168.1.2", Enabled = false });
        await _context.SaveChangesAsync();

        var results = await _repository.GetEnabledOntConfigurationsAsync();

        results.Should().ContainSingle().Which.Name.Should().Be("Live ONT");
    }

    [Fact]
    public async Task SetOntEnabledAsync_Disable_SetsFlagFalseAndClearsLastError()
    {
        var ont = new OntConfiguration
        {
            Name = "Zyxel", Host = "10.10.1.1", Enabled = true,
            LastError = "timeout after 10s", LastPolled = new DateTime(2026, 7, 22, 8, 0, 0, DateTimeKind.Utc),
        };
        _context.OntConfigurations.Add(ont);
        await _context.SaveChangesAsync();

        await _repository.SetOntEnabledAsync(ont.Id, false);

        var reloaded = await _repository.GetOntConfigurationAsync(ont.Id);
        reloaded!.Enabled.Should().BeFalse();
        reloaded.LastError.Should().BeNull("a paused ONT should not keep showing a stale poll error");
        reloaded.LastPolled.Should().Be(new DateTime(2026, 7, 22, 8, 0, 0, DateTimeKind.Utc),
            "LastPolled is history and is preserved");
    }

    [Fact]
    public async Task SetOntEnabledAsync_Enable_SetsFlagTrueAndLeavesLastErrorForNextPoll()
    {
        var ont = new OntConfiguration
        {
            Name = "Luleey", Host = "192.168.1.1", Enabled = false, LastError = "was unreachable",
        };
        _context.OntConfigurations.Add(ont);
        await _context.SaveChangesAsync();

        await _repository.SetOntEnabledAsync(ont.Id, true);

        var reloaded = await _repository.GetOntConfigurationAsync(ont.Id);
        reloaded!.Enabled.Should().BeTrue();
        reloaded.LastError.Should().Be("was unreachable",
            "re-enabling does not fabricate a healthy state; the next poll overwrites LastError");
    }

    [Fact]
    public async Task SetOntEnabledAsync_BumpsUpdatedAt()
    {
        var ont = new OntConfiguration
        {
            Name = "ONT", Host = "192.168.1.1", Enabled = true,
            UpdatedAt = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        _context.OntConfigurations.Add(ont);
        await _context.SaveChangesAsync();

        await _repository.SetOntEnabledAsync(ont.Id, false);

        var reloaded = await _repository.GetOntConfigurationAsync(ont.Id);
        reloaded!.UpdatedAt.Should().BeAfter(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task SetOntEnabledAsync_UnknownId_DoesNotThrow()
    {
        var act = async () => await _repository.SetOntEnabledAsync(9999, false);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task UpdateOntPollResultAsync_EnabledConfig_WritesResultAndReturnsTrue()
    {
        var ont = new OntConfiguration { Name = "ONT", Host = "192.168.1.1", Enabled = true };
        _context.OntConfigurations.Add(ont);
        await _context.SaveChangesAsync();
        var when = new DateTime(2026, 7, 22, 9, 0, 0, DateTimeKind.Utc);

        var persisted = await _repository.UpdateOntPollResultAsync(ont.Id, when, null);

        persisted.Should().BeTrue();
        var reloaded = await _repository.GetOntConfigurationAsync(ont.Id);
        reloaded!.LastPolled.Should().Be(when);
        reloaded.LastError.Should().BeNull();
    }

    [Fact]
    public async Task UpdateOntPollResultAsync_ErrorPath_SetsErrorWithoutAdvancingLastPolled()
    {
        var lastSuccess = new DateTime(2026, 7, 22, 8, 0, 0, DateTimeKind.Utc);
        var ont = new OntConfiguration { Name = "ONT", Host = "192.168.1.1", Enabled = true, LastPolled = lastSuccess };
        _context.OntConfigurations.Add(ont);
        await _context.SaveChangesAsync();

        var persisted = await _repository.UpdateOntPollResultAsync(ont.Id, lastPolled: null, "boom");

        persisted.Should().BeTrue();
        var reloaded = await _repository.GetOntConfigurationAsync(ont.Id);
        reloaded!.LastError.Should().Be("boom");
        reloaded.LastPolled.Should().Be(lastSuccess, "an error must not advance LastPolled, which tracks the last success");
    }

    [Fact]
    public async Task UpdateOntPollResultAsync_ConfigDisabledMidPoll_DoesNotResurrectOrOverwrite()
    {
        // Regression: an in-flight poll finishing after the user clicked Disable must not
        // re-enable the ONT or rewrite the LastError that Disable cleared.
        var ont = new OntConfiguration
        {
            Name = "Zyxel", Host = "10.10.1.1", Enabled = true, LastError = "timeout",
            LastPolled = new DateTime(2026, 7, 22, 8, 0, 0, DateTimeKind.Utc),
        };
        _context.OntConfigurations.Add(ont);
        await _context.SaveChangesAsync();

        await _repository.SetOntEnabledAsync(ont.Id, false);            // user pauses it
        var persisted = await _repository.UpdateOntPollResultAsync(     // late poll result lands
            ont.Id, new DateTime(2026, 7, 22, 8, 5, 0, DateTimeKind.Utc), "timeout again");

        persisted.Should().BeFalse();
        var reloaded = await _repository.GetOntConfigurationAsync(ont.Id);
        reloaded!.Enabled.Should().BeFalse("the late poll must not re-enable a paused ONT");
        reloaded.LastError.Should().BeNull("the late poll must not overwrite the LastError that Disable cleared");
        reloaded.LastPolled.Should().Be(new DateTime(2026, 7, 22, 8, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task UpdateOntPollResultAsync_UnknownId_ReturnsFalse()
    {
        (await _repository.UpdateOntPollResultAsync(9999, DateTime.UtcNow, null)).Should().BeFalse();
    }
}
