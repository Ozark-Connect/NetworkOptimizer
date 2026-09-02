using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Repositories;
using Xunit;

namespace NetworkOptimizer.Storage.Tests;

/// <summary>SQLite in-memory round trips for acknowledgments and kept radios.</summary>
public class WiFiInsightRepositoryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly WiFiInsightRepository _repository;

    public WiFiInsightRepositoryTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<NetworkOptimizerDbContext>()
            .UseSqlite(_connection)
            .Options;
        var factory = new NetworkOptimizerDbContextFactory(options);
        using var db = factory.CreateDbContext();
        db.Database.EnsureCreated();

        var siteDbFactory = new Services.SiteDbContextFactory(new Services.SiteDatabasePaths("unused.db"));
        _repository = new WiFiInsightRepository(factory, siteDbFactory, new Mock<ILogger<WiFiInsightRepository>>().Object);
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task Acknowledge_is_remembered_once_and_restore_forgets_it()
    {
        await _repository.AcknowledgeIssueAsync("WIFI-WEAK-SIGNAL-001|cc:00:00:00:00:01");
        await _repository.AcknowledgeIssueAsync("WIFI-WEAK-SIGNAL-001|cc:00:00:00:00:01");
        await _repository.AcknowledgeIssueAsync("WIFI-COCHANNEL-001|na|aa:bb:cc:dd:ee:01,aa:bb:cc:dd:ee:02");

        var keys = await _repository.GetAcknowledgedIssueKeysAsync();
        keys.Should().BeEquivalentTo(new[]
        {
            "WIFI-WEAK-SIGNAL-001|cc:00:00:00:00:01",
            "WIFI-COCHANNEL-001|na|aa:bb:cc:dd:ee:01,aa:bb:cc:dd:ee:02"
        });

        await _repository.RestoreIssueAsync("WIFI-WEAK-SIGNAL-001|cc:00:00:00:00:01");
        await _repository.RestoreIssueAsync("never-acknowledged");

        (await _repository.GetAcknowledgedIssueKeysAsync()).Should().BeEquivalentTo(new[]
        {
            "WIFI-COCHANNEL-001|na|aa:bb:cc:dd:ee:01,aa:bb:cc:dd:ee:02"
        });
    }

    [Fact]
    public async Task An_empty_key_is_never_stored()
    {
        await _repository.AcknowledgeIssueAsync("");
        await _repository.AcknowledgeIssueAsync("   ");

        (await _repository.GetAcknowledgedIssueKeysAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Keep_and_release_round_trip_per_radio()
    {
        await _repository.SetKeptAsync("AA:BB:CC:DD:EE:01", "na", kept: true);
        await _repository.SetKeptAsync("aa:bb:cc:dd:ee:01", "6e", kept: true);
        await _repository.SetKeptAsync("aa:bb:cc:dd:ee:01", "na", kept: true);

        var kept = await _repository.GetKeptRadiosAsync();
        kept.Should().BeEquivalentTo(new[] { ("aa:bb:cc:dd:ee:01", "na"), ("aa:bb:cc:dd:ee:01", "6e") },
            "the MAC is stored lowercase and a second keep of the same radio is one row");

        await _repository.SetKeptAsync("aa:bb:cc:dd:ee:01", "na", kept: false);
        await _repository.SetKeptAsync("aa:bb:cc:dd:ee:02", "na", kept: false);

        (await _repository.GetKeptRadiosAsync()).Should().BeEquivalentTo(new[] { ("aa:bb:cc:dd:ee:01", "6e") });
    }
}
