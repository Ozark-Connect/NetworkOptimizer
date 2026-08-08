using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Repositories;
using Xunit;

namespace NetworkOptimizer.Storage.Tests;

public class StarlinkRepositoryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly NetworkOptimizerDbContext _context;
    private readonly StarlinkRepository _repository;

    public StarlinkRepositoryTests()
    {
        // SQLite in-memory (not the EF InMemory provider): UpdateStarlinkPollResultAsync uses
        // ExecuteUpdate for an atomic guarded write, which only a relational provider supports.
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<NetworkOptimizerDbContext>()
            .UseSqlite(_connection)
            .Options;

        _context = new NetworkOptimizerDbContext(options);
        _context.Database.EnsureCreated();
        var logger = new Mock<ILogger<StarlinkRepository>>();
        _repository = new StarlinkRepository(_context, logger.Object);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task GetEnabledStarlinkConfigurationsAsync_ReturnsOnlyEnabled()
    {
        _context.StarlinkConfigurations.AddRange(
            new StarlinkConfiguration { Name = "Live Dish", Host = "192.168.100.1", Enabled = true },
            new StarlinkConfiguration { Name = "Paused Dish", Host = "192.168.100.2", Enabled = false });
        await _context.SaveChangesAsync();

        var results = await _repository.GetEnabledStarlinkConfigurationsAsync();

        results.Should().ContainSingle().Which.Name.Should().Be("Live Dish");
    }

    [Fact]
    public async Task SaveStarlinkConfigurationAsync_ExistingConfig_PreservesDatabaseEnabledValue()
    {
        var terminal = new StarlinkConfiguration
        {
            Name = "Paused Dish", Host = "192.168.100.1", Enabled = false,
        };
        _context.StarlinkConfigurations.Add(terminal);
        await _context.SaveChangesAsync();

        await _repository.SaveStarlinkConfigurationAsync(new StarlinkConfiguration
        {
            Id = terminal.Id, Name = "Edited Dish", Host = terminal.Host, Enabled = true,
        });

        _context.ChangeTracker.Clear();
        var reloaded = await _repository.GetStarlinkConfigurationAsync(terminal.Id);
        reloaded!.Name.Should().Be("Edited Dish");
        reloaded.Enabled.Should().BeFalse("only SetStarlinkEnabledAsync may change Enabled on an existing terminal");
    }

    [Fact]
    public async Task SetStarlinkEnabledAsync_Disable_SetsFlagFalseAndClearsLastError()
    {
        var terminal = new StarlinkConfiguration
        {
            Name = "Roof", Host = "192.168.100.1", Enabled = true,
            LastError = "gRPC deadline exceeded", LastPolled = new DateTime(2026, 7, 22, 8, 0, 0, DateTimeKind.Utc),
        };
        _context.StarlinkConfigurations.Add(terminal);
        await _context.SaveChangesAsync();

        await _repository.SetStarlinkEnabledAsync(terminal.Id, false);

        var reloaded = await _repository.GetStarlinkConfigurationAsync(terminal.Id);
        reloaded!.Enabled.Should().BeFalse();
        reloaded.LastError.Should().BeNull("a paused terminal should not keep showing a stale poll error");
        reloaded.LastPolled.Should().Be(new DateTime(2026, 7, 22, 8, 0, 0, DateTimeKind.Utc),
            "LastPolled is history and is preserved");
    }

    [Fact]
    public async Task SetStarlinkEnabledAsync_Enable_SetsFlagTrueAndLeavesLastErrorForNextPoll()
    {
        var terminal = new StarlinkConfiguration
        {
            Name = "Roof", Host = "192.168.100.1", Enabled = false, LastError = "was unreachable",
        };
        _context.StarlinkConfigurations.Add(terminal);
        await _context.SaveChangesAsync();

        await _repository.SetStarlinkEnabledAsync(terminal.Id, true);

        var reloaded = await _repository.GetStarlinkConfigurationAsync(terminal.Id);
        reloaded!.Enabled.Should().BeTrue();
        reloaded.LastError.Should().Be("was unreachable",
            "re-enabling does not fabricate a healthy state; the next poll overwrites LastError");
    }

    [Fact]
    public async Task SetStarlinkEnabledAsync_BumpsUpdatedAt()
    {
        var terminal = new StarlinkConfiguration
        {
            Name = "Roof", Host = "192.168.100.1", Enabled = true,
            UpdatedAt = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        _context.StarlinkConfigurations.Add(terminal);
        await _context.SaveChangesAsync();

        await _repository.SetStarlinkEnabledAsync(terminal.Id, false);

        var reloaded = await _repository.GetStarlinkConfigurationAsync(terminal.Id);
        reloaded!.UpdatedAt.Should().BeAfter(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task SetStarlinkEnabledAsync_UnknownId_DoesNotThrow()
    {
        var act = async () => await _repository.SetStarlinkEnabledAsync(9999, false);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task UpdateStarlinkPollResultAsync_EnabledConfig_WritesResultAndReturnsTrue()
    {
        var terminal = new StarlinkConfiguration { Name = "Roof", Host = "192.168.100.1", Enabled = true };
        _context.StarlinkConfigurations.Add(terminal);
        await _context.SaveChangesAsync();
        var when = new DateTime(2026, 7, 22, 9, 0, 0, DateTimeKind.Utc);

        var persisted = await _repository.UpdateStarlinkPollResultAsync(terminal.Id, when, null);

        persisted.Should().BeTrue();
        var reloaded = await _repository.GetStarlinkConfigurationAsync(terminal.Id);
        reloaded!.LastPolled.Should().Be(when);
        reloaded.LastError.Should().BeNull();
    }

    [Fact]
    public async Task UpdateStarlinkPollResultAsync_ErrorPath_SetsErrorWithoutAdvancingLastPolled()
    {
        var lastSuccess = new DateTime(2026, 7, 22, 8, 0, 0, DateTimeKind.Utc);
        var terminal = new StarlinkConfiguration { Name = "Roof", Host = "192.168.100.1", Enabled = true, LastPolled = lastSuccess };
        _context.StarlinkConfigurations.Add(terminal);
        await _context.SaveChangesAsync();

        var persisted = await _repository.UpdateStarlinkPollResultAsync(terminal.Id, lastPolled: null, "boom");

        persisted.Should().BeTrue();
        var reloaded = await _repository.GetStarlinkConfigurationAsync(terminal.Id);
        reloaded!.LastError.Should().Be("boom");
        reloaded.LastPolled.Should().Be(lastSuccess, "an error must not advance LastPolled, which tracks the last success");
    }

    [Fact]
    public async Task UpdateStarlinkPollResultAsync_ConfigDisabledMidPoll_DoesNotResurrectOrOverwrite()
    {
        // Regression: an in-flight poll finishing after the user clicked Disable must not
        // re-enable the terminal or rewrite the LastError that Disable cleared.
        var terminal = new StarlinkConfiguration
        {
            Name = "Roof", Host = "192.168.100.1", Enabled = true, LastError = "timeout",
            LastPolled = new DateTime(2026, 7, 22, 8, 0, 0, DateTimeKind.Utc),
        };
        _context.StarlinkConfigurations.Add(terminal);
        await _context.SaveChangesAsync();

        await _repository.SetStarlinkEnabledAsync(terminal.Id, false);       // user pauses it
        var persisted = await _repository.UpdateStarlinkPollResultAsync(      // late poll result lands
            terminal.Id, new DateTime(2026, 7, 22, 8, 5, 0, DateTimeKind.Utc), "timeout again");

        persisted.Should().BeFalse();
        var reloaded = await _repository.GetStarlinkConfigurationAsync(terminal.Id);
        reloaded!.Enabled.Should().BeFalse("the late poll must not re-enable a paused terminal");
        reloaded.LastError.Should().BeNull("the late poll must not overwrite the LastError that Disable cleared");
        reloaded.LastPolled.Should().Be(new DateTime(2026, 7, 22, 8, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task UpdateStarlinkPollResultAsync_SqliteDisabledBeforeLateResult_AtomicallySkipsUpdate()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<NetworkOptimizerDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var context = new NetworkOptimizerDbContext(options);
        await context.Database.EnsureCreatedAsync();
        var repository = new StarlinkRepository(context, new Mock<ILogger<StarlinkRepository>>().Object);
        var lastPolled = new DateTime(2026, 7, 22, 8, 0, 0, DateTimeKind.Utc);
        var terminal = new StarlinkConfiguration
        {
            Name = "Roof", Host = "192.168.100.1", Enabled = true, LastPolled = lastPolled,
        };
        context.StarlinkConfigurations.Add(terminal);
        await context.SaveChangesAsync();

        await repository.SetStarlinkEnabledAsync(terminal.Id, false);
        context.ChangeTracker.Clear();
        var persisted = await repository.UpdateStarlinkPollResultAsync(
            terminal.Id, new DateTime(2026, 7, 22, 8, 5, 0, DateTimeKind.Utc), "late");

        persisted.Should().BeFalse();
        context.ChangeTracker.Clear();
        var reloaded = await repository.GetStarlinkConfigurationAsync(terminal.Id);
        reloaded!.Enabled.Should().BeFalse();
        reloaded.LastError.Should().BeNull();
        reloaded.LastPolled.Should().Be(lastPolled);
    }

    [Fact]
    public async Task UpdateStarlinkPollResultAsync_UnknownId_ReturnsFalse()
    {
        (await _repository.UpdateStarlinkPollResultAsync(9999, DateTime.UtcNow, null)).Should().BeFalse();
    }
}
