using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Repositories;
using Xunit;

namespace NetworkOptimizer.Storage.Tests;

public class CmRepositoryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly NetworkOptimizerDbContext _context;
    private readonly CmRepository _repository;

    public CmRepositoryTests()
    {
        // SQLite in-memory (not the EF InMemory provider): UpdateCmPollResultAsync uses
        // ExecuteUpdate for an atomic guarded write, which only a relational provider supports.
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<NetworkOptimizerDbContext>()
            .UseSqlite(_connection)
            .Options;

        _context = new NetworkOptimizerDbContext(options);
        _context.Database.EnsureCreated();
        var logger = new Mock<ILogger<CmRepository>>();
        _repository = new CmRepository(_context, logger.Object);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task GetEnabledCmConfigurationsAsync_ReturnsOnlyEnabled()
    {
        _context.CmConfigurations.AddRange(
            new CmConfiguration { Name = "Live CM", Host = "192.168.100.1", Enabled = true },
            new CmConfiguration { Name = "Paused CM", Host = "192.168.100.2", Enabled = false });
        await _context.SaveChangesAsync();

        var results = await _repository.GetEnabledCmConfigurationsAsync();

        results.Should().ContainSingle().Which.Name.Should().Be("Live CM");
    }

    [Fact]
    public async Task SaveCmConfigurationAsync_ExistingConfig_PreservesDatabaseEnabledValue()
    {
        var cm = new CmConfiguration
        {
            Name = "Paused CM", Host = "192.168.100.1", Enabled = false,
        };
        _context.CmConfigurations.Add(cm);
        await _context.SaveChangesAsync();

        await _repository.SaveCmConfigurationAsync(new CmConfiguration
        {
            Id = cm.Id, Name = "Edited CM", Host = cm.Host, Enabled = true,
        });

        _context.ChangeTracker.Clear();
        var reloaded = await _repository.GetCmConfigurationAsync(cm.Id);
        reloaded!.Name.Should().Be("Edited CM");
        reloaded.Enabled.Should().BeFalse("only SetCmEnabledAsync may change Enabled on an existing CM");
    }

    [Fact]
    public async Task SetCmEnabledAsync_Disable_SetsFlagFalseAndClearsLastError()
    {
        var cm = new CmConfiguration
        {
            Name = "CM1000", Host = "192.168.100.1", Enabled = true,
            LastError = "timeout after 10s", LastPolled = new DateTime(2026, 7, 22, 8, 0, 0, DateTimeKind.Utc),
        };
        _context.CmConfigurations.Add(cm);
        await _context.SaveChangesAsync();

        await _repository.SetCmEnabledAsync(cm.Id, false);

        var reloaded = await _repository.GetCmConfigurationAsync(cm.Id);
        reloaded!.Enabled.Should().BeFalse();
        reloaded.LastError.Should().BeNull("a paused CM should not keep showing a stale poll error");
        reloaded.LastPolled.Should().Be(new DateTime(2026, 7, 22, 8, 0, 0, DateTimeKind.Utc),
            "LastPolled is history and is preserved");
    }

    [Fact]
    public async Task SetCmEnabledAsync_Enable_SetsFlagTrueAndLeavesLastErrorForNextPoll()
    {
        var cm = new CmConfiguration
        {
            Name = "S33", Host = "192.168.100.1", Enabled = false, LastError = "was unreachable",
        };
        _context.CmConfigurations.Add(cm);
        await _context.SaveChangesAsync();

        await _repository.SetCmEnabledAsync(cm.Id, true);

        var reloaded = await _repository.GetCmConfigurationAsync(cm.Id);
        reloaded!.Enabled.Should().BeTrue();
        reloaded.LastError.Should().Be("was unreachable",
            "re-enabling does not fabricate a healthy state; the next poll overwrites LastError");
    }

    [Fact]
    public async Task SetCmEnabledAsync_BumpsUpdatedAt()
    {
        var cm = new CmConfiguration
        {
            Name = "CM", Host = "192.168.100.1", Enabled = true,
            UpdatedAt = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        _context.CmConfigurations.Add(cm);
        await _context.SaveChangesAsync();

        await _repository.SetCmEnabledAsync(cm.Id, false);

        var reloaded = await _repository.GetCmConfigurationAsync(cm.Id);
        reloaded!.UpdatedAt.Should().BeAfter(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task SetCmEnabledAsync_UnknownId_DoesNotThrow()
    {
        var act = async () => await _repository.SetCmEnabledAsync(9999, false);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task UpdateCmPollResultAsync_EnabledConfig_WritesResultAndReturnsTrue()
    {
        var cm = new CmConfiguration { Name = "CM", Host = "192.168.100.1", Enabled = true };
        _context.CmConfigurations.Add(cm);
        await _context.SaveChangesAsync();
        var when = new DateTime(2026, 7, 22, 9, 0, 0, DateTimeKind.Utc);

        var persisted = await _repository.UpdateCmPollResultAsync(cm.Id, when, null);

        persisted.Should().BeTrue();
        var reloaded = await _repository.GetCmConfigurationAsync(cm.Id);
        reloaded!.LastPolled.Should().Be(when);
        reloaded.LastError.Should().BeNull();
    }

    [Fact]
    public async Task UpdateCmPollResultAsync_ErrorPath_SetsErrorWithoutAdvancingLastPolled()
    {
        var lastSuccess = new DateTime(2026, 7, 22, 8, 0, 0, DateTimeKind.Utc);
        var cm = new CmConfiguration { Name = "CM", Host = "192.168.100.1", Enabled = true, LastPolled = lastSuccess };
        _context.CmConfigurations.Add(cm);
        await _context.SaveChangesAsync();

        var persisted = await _repository.UpdateCmPollResultAsync(cm.Id, lastPolled: null, "boom");

        persisted.Should().BeTrue();
        var reloaded = await _repository.GetCmConfigurationAsync(cm.Id);
        reloaded!.LastError.Should().Be("boom");
        reloaded.LastPolled.Should().Be(lastSuccess, "an error must not advance LastPolled, which tracks the last success");
    }

    [Fact]
    public async Task UpdateCmPollResultAsync_ConfigDisabledMidPoll_DoesNotResurrectOrOverwrite()
    {
        // Regression: an in-flight poll finishing after the user clicked Disable must not
        // re-enable the CM or rewrite the LastError that Disable cleared.
        var cm = new CmConfiguration
        {
            Name = "CM1000", Host = "192.168.100.1", Enabled = true, LastError = "timeout",
            LastPolled = new DateTime(2026, 7, 22, 8, 0, 0, DateTimeKind.Utc),
        };
        _context.CmConfigurations.Add(cm);
        await _context.SaveChangesAsync();

        await _repository.SetCmEnabledAsync(cm.Id, false);            // user pauses it
        var persisted = await _repository.UpdateCmPollResultAsync(     // late poll result lands
            cm.Id, new DateTime(2026, 7, 22, 8, 5, 0, DateTimeKind.Utc), "timeout again");

        persisted.Should().BeFalse();
        var reloaded = await _repository.GetCmConfigurationAsync(cm.Id);
        reloaded!.Enabled.Should().BeFalse("the late poll must not re-enable a paused CM");
        reloaded.LastError.Should().BeNull("the late poll must not overwrite the LastError that Disable cleared");
        reloaded.LastPolled.Should().Be(new DateTime(2026, 7, 22, 8, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task UpdateCmPollResultAsync_SqliteDisabledBeforeLateResult_AtomicallySkipsUpdate()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<NetworkOptimizerDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var context = new NetworkOptimizerDbContext(options);
        await context.Database.EnsureCreatedAsync();
        var repository = new CmRepository(context, new Mock<ILogger<CmRepository>>().Object);
        var lastPolled = new DateTime(2026, 7, 22, 8, 0, 0, DateTimeKind.Utc);
        var cm = new CmConfiguration
        {
            Name = "CM", Host = "192.168.100.1", Enabled = true, LastPolled = lastPolled,
        };
        context.CmConfigurations.Add(cm);
        await context.SaveChangesAsync();

        await repository.SetCmEnabledAsync(cm.Id, false);
        context.ChangeTracker.Clear();
        var persisted = await repository.UpdateCmPollResultAsync(
            cm.Id, new DateTime(2026, 7, 22, 8, 5, 0, DateTimeKind.Utc), "late");

        persisted.Should().BeFalse();
        context.ChangeTracker.Clear();
        var reloaded = await repository.GetCmConfigurationAsync(cm.Id);
        reloaded!.Enabled.Should().BeFalse();
        reloaded.LastError.Should().BeNull();
        reloaded.LastPolled.Should().Be(lastPolled);
    }

    [Fact]
    public async Task UpdateCmPollResultAsync_UnknownId_ReturnsFalse()
    {
        (await _repository.UpdateCmPollResultAsync(9999, DateTime.UtcNow, null)).Should().BeFalse();
    }
}
