using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Repositories;
using Xunit;

namespace NetworkOptimizer.Storage.Tests;

public class ModemRepositoryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly NetworkOptimizerDbContext _context;
    private readonly ModemRepository _repository;

    public ModemRepositoryTests()
    {
        // SQLite in-memory (not the EF InMemory provider): UpdateModemPollResultAsync uses
        // ExecuteUpdate for an atomic guarded write, which only a relational provider supports.
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<NetworkOptimizerDbContext>()
            .UseSqlite(_connection)
            .Options;

        _context = new NetworkOptimizerDbContext(options);
        _context.Database.EnsureCreated();
        var logger = new Mock<ILogger<ModemRepository>>();
        _repository = new ModemRepository(_context, logger.Object);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task GetModemConfigurationsAsync_ReturnsAllOrderedByName()
    {
        _context.ModemConfigurations.AddRange(
            new ModemConfiguration { Name = "Modem Z", Host = "192.168.1.3" },
            new ModemConfiguration { Name = "Modem A", Host = "192.168.1.1" }
        );
        await _context.SaveChangesAsync();

        var results = await _repository.GetModemConfigurationsAsync();

        results.Should().HaveCount(2);
        results[0].Name.Should().Be("Modem A");
    }

    [Fact]
    public async Task GetEnabledModemConfigurationsAsync_ReturnsOnlyEnabled()
    {
        _context.ModemConfigurations.AddRange(
            new ModemConfiguration { Name = "Enabled Modem", Host = "192.168.1.1", Enabled = true },
            new ModemConfiguration { Name = "Disabled Modem", Host = "192.168.1.2", Enabled = false }
        );
        await _context.SaveChangesAsync();

        var results = await _repository.GetEnabledModemConfigurationsAsync();

        results.Should().HaveCount(1);
        results[0].Name.Should().Be("Enabled Modem");
    }

    [Fact]
    public async Task GetModemConfigurationAsync_ReturnsById()
    {
        var modem = new ModemConfiguration { Name = "Test Modem", Host = "192.168.1.1" };
        _context.ModemConfigurations.Add(modem);
        await _context.SaveChangesAsync();

        var result = await _repository.GetModemConfigurationAsync(modem.Id);

        result.Should().NotBeNull();
        result!.Name.Should().Be("Test Modem");
    }

    [Fact]
    public async Task SaveModemConfigurationAsync_CreatesNew()
    {
        var modem = new ModemConfiguration { Name = "New Modem", Host = "192.168.1.100" };

        await _repository.SaveModemConfigurationAsync(modem);

        var saved = await _context.ModemConfigurations.FirstOrDefaultAsync(m => m.Name == "New Modem");
        saved.Should().NotBeNull();
    }

    [Fact]
    public async Task SaveModemConfigurationAsync_ExistingConfig_PreservesDatabaseEnabledValue()
    {
        var modem = new ModemConfiguration
        {
            Name = "Paused Modem", Host = "192.168.1.1", Enabled = false,
        };
        _context.ModemConfigurations.Add(modem);
        await _context.SaveChangesAsync();

        await _repository.SaveModemConfigurationAsync(new ModemConfiguration
        {
            Id = modem.Id, Name = "Edited Modem", Host = modem.Host, Enabled = true,
        });

        _context.ChangeTracker.Clear();
        var reloaded = await _repository.GetModemConfigurationAsync(modem.Id);
        reloaded!.Name.Should().Be("Edited Modem");
        reloaded.Enabled.Should().BeFalse("only SetModemEnabledAsync may change Enabled on an existing modem");
    }

    [Fact]
    public async Task DeleteModemConfigurationAsync_RemovesModem()
    {
        var modem = new ModemConfiguration { Name = "To Delete", Host = "192.168.1.1" };
        _context.ModemConfigurations.Add(modem);
        await _context.SaveChangesAsync();
        var id = modem.Id;

        await _repository.DeleteModemConfigurationAsync(id);

        var deleted = await _context.ModemConfigurations.FindAsync(id);
        deleted.Should().BeNull();
    }

    [Fact]
    public async Task SetModemEnabledAsync_Disable_SetsFlagFalseAndClearsLastError()
    {
        var modem = new ModemConfiguration
        {
            Name = "UniFi 5G Max", Host = "192.168.1.1", Enabled = true,
            LastError = "timeout after 10s", LastPolled = new DateTime(2026, 7, 22, 8, 0, 0, DateTimeKind.Utc),
        };
        _context.ModemConfigurations.Add(modem);
        await _context.SaveChangesAsync();

        await _repository.SetModemEnabledAsync(modem.Id, false);

        var reloaded = await _repository.GetModemConfigurationAsync(modem.Id);
        reloaded!.Enabled.Should().BeFalse();
        reloaded.LastError.Should().BeNull("a paused modem should not keep showing a stale poll error");
        reloaded.LastPolled.Should().Be(new DateTime(2026, 7, 22, 8, 0, 0, DateTimeKind.Utc),
            "LastPolled is history and is preserved");
    }

    [Fact]
    public async Task SetModemEnabledAsync_Enable_SetsFlagTrueAndLeavesLastErrorForNextPoll()
    {
        var modem = new ModemConfiguration
        {
            Name = "LTE Backup Pro", Host = "192.168.1.2", Enabled = false, LastError = "was unreachable",
        };
        _context.ModemConfigurations.Add(modem);
        await _context.SaveChangesAsync();

        await _repository.SetModemEnabledAsync(modem.Id, true);

        var reloaded = await _repository.GetModemConfigurationAsync(modem.Id);
        reloaded!.Enabled.Should().BeTrue();
        reloaded.LastError.Should().Be("was unreachable",
            "re-enabling does not fabricate a healthy state; the next poll overwrites LastError");
    }

    [Fact]
    public async Task SetModemEnabledAsync_BumpsUpdatedAt()
    {
        var modem = new ModemConfiguration
        {
            Name = "Modem", Host = "192.168.1.1", Enabled = true,
            UpdatedAt = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        _context.ModemConfigurations.Add(modem);
        await _context.SaveChangesAsync();

        await _repository.SetModemEnabledAsync(modem.Id, false);

        var reloaded = await _repository.GetModemConfigurationAsync(modem.Id);
        reloaded!.UpdatedAt.Should().BeAfter(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task SetModemEnabledAsync_UnknownId_DoesNotThrow()
    {
        var act = async () => await _repository.SetModemEnabledAsync(9999, false);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task UpdateModemPollResultAsync_EnabledConfig_WritesResultAndReturnsTrue()
    {
        var modem = new ModemConfiguration { Name = "Modem", Host = "192.168.1.1", Enabled = true };
        _context.ModemConfigurations.Add(modem);
        await _context.SaveChangesAsync();
        var when = new DateTime(2026, 7, 22, 9, 0, 0, DateTimeKind.Utc);

        var persisted = await _repository.UpdateModemPollResultAsync(modem.Id, when, null);

        persisted.Should().BeTrue();
        var reloaded = await _repository.GetModemConfigurationAsync(modem.Id);
        reloaded!.LastPolled.Should().Be(when);
        reloaded.LastError.Should().BeNull();
    }

    [Fact]
    public async Task UpdateModemPollResultAsync_ErrorPath_SetsErrorWithoutAdvancingLastPolled()
    {
        var lastSuccess = new DateTime(2026, 7, 22, 8, 0, 0, DateTimeKind.Utc);
        var modem = new ModemConfiguration { Name = "Modem", Host = "192.168.1.1", Enabled = true, LastPolled = lastSuccess };
        _context.ModemConfigurations.Add(modem);
        await _context.SaveChangesAsync();

        var persisted = await _repository.UpdateModemPollResultAsync(modem.Id, lastPolled: null, "boom");

        persisted.Should().BeTrue();
        var reloaded = await _repository.GetModemConfigurationAsync(modem.Id);
        reloaded!.LastError.Should().Be("boom");
        reloaded.LastPolled.Should().Be(lastSuccess, "an error must not advance LastPolled, which tracks the last success");
    }

    [Fact]
    public async Task UpdateModemPollResultAsync_ConfigDisabledMidPoll_DoesNotResurrectOrOverwrite()
    {
        // Regression: an in-flight poll finishing after the user clicked Disable must not
        // re-enable the modem or rewrite the LastError that Disable cleared.
        var modem = new ModemConfiguration
        {
            Name = "UniFi 5G Max", Host = "192.168.1.1", Enabled = true, LastError = "timeout",
            LastPolled = new DateTime(2026, 7, 22, 8, 0, 0, DateTimeKind.Utc),
        };
        _context.ModemConfigurations.Add(modem);
        await _context.SaveChangesAsync();

        await _repository.SetModemEnabledAsync(modem.Id, false);            // user pauses it
        var persisted = await _repository.UpdateModemPollResultAsync(       // late poll result lands
            modem.Id, new DateTime(2026, 7, 22, 8, 5, 0, DateTimeKind.Utc), "timeout again");

        persisted.Should().BeFalse();
        var reloaded = await _repository.GetModemConfigurationAsync(modem.Id);
        reloaded!.Enabled.Should().BeFalse("the late poll must not re-enable a paused modem");
        reloaded.LastError.Should().BeNull("the late poll must not overwrite the LastError that Disable cleared");
        reloaded.LastPolled.Should().Be(new DateTime(2026, 7, 22, 8, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task UpdateModemPollResultAsync_SqliteDisabledBeforeLateResult_AtomicallySkipsUpdate()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<NetworkOptimizerDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var context = new NetworkOptimizerDbContext(options);
        await context.Database.EnsureCreatedAsync();
        var repository = new ModemRepository(context, new Mock<ILogger<ModemRepository>>().Object);
        var lastPolled = new DateTime(2026, 7, 22, 8, 0, 0, DateTimeKind.Utc);
        var modem = new ModemConfiguration
        {
            Name = "Modem", Host = "192.168.1.1", Enabled = true, LastPolled = lastPolled,
        };
        context.ModemConfigurations.Add(modem);
        await context.SaveChangesAsync();

        await repository.SetModemEnabledAsync(modem.Id, false);
        context.ChangeTracker.Clear();
        var persisted = await repository.UpdateModemPollResultAsync(
            modem.Id, new DateTime(2026, 7, 22, 8, 5, 0, DateTimeKind.Utc), "late");

        persisted.Should().BeFalse();
        context.ChangeTracker.Clear();
        var reloaded = await repository.GetModemConfigurationAsync(modem.Id);
        reloaded!.Enabled.Should().BeFalse();
        reloaded.LastError.Should().BeNull();
        reloaded.LastPolled.Should().Be(lastPolled);
    }

    [Fact]
    public async Task UpdateModemPollResultAsync_UnknownId_ReturnsFalse()
    {
        (await _repository.UpdateModemPollResultAsync(9999, DateTime.UtcNow, null)).Should().BeFalse();
    }
}
