using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using NetworkOptimizer.Storage.Interfaces;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Repositories;
using Xunit;

namespace NetworkOptimizer.Storage.Tests;

/// <summary>
/// Uses SQLite in-memory (not the EF InMemory provider) because the repository relies on
/// ExecuteDeleteAsync and grouped max-per-key queries that must translate to real SQL.
/// </summary>
public class ChannelMemoryRepositoryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ChannelMemoryRepository _repository;
    private readonly IDbContextFactory<NetworkOptimizerDbContext> _factory;

    public ChannelMemoryRepositoryTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<NetworkOptimizerDbContext>()
            .UseSqlite(_connection)
            .Options;
        _factory = new NetworkOptimizerDbContextFactory(options);

        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();

        var logger = new Mock<ILogger<ChannelMemoryRepository>>();
        // These tests exercise the default site, which the repository serves from
        // the main factory; the site factory is required but never invoked here.
        var siteDbFactory = new NetworkOptimizer.Storage.Services.SiteDbContextFactory(
            new NetworkOptimizer.Storage.Services.SiteDatabasePaths("unused.db"));
        _repository = new ChannelMemoryRepository(_factory, siteDbFactory, logger.Object);
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    private static ChannelOutcomeSample Sample(
        DateTime timestamp, int channel = 6, string mac = "aa:bb:cc:dd:ee:01",
        string band = "ng", int width = 20,
        double util = 40, double interf = 30, double txRetry = 10) =>
        new(mac, band, channel, width, timestamp, util, interf, txRetry);

    [Fact]
    public async Task AddOutcomeSamples_CreatesAndAccumulatesDailyBucket()
    {
        var day = new DateTime(2026, 6, 20, 0, 0, 0, DateTimeKind.Utc);

        await _repository.AddOutcomeSamplesAsync(new[]
        {
            Sample(day.AddHours(1), util: 40, interf: 30, txRetry: 10),
            Sample(day.AddHours(2), util: 60, interf: 50, txRetry: 20)
        });
        // Second call must accumulate into the same bucket, not create a duplicate
        await _repository.AddOutcomeSamplesAsync(new[]
        {
            Sample(day.AddHours(3), util: 20, interf: 10, txRetry: 0)
        });

        var outcomes = await _repository.GetOutcomesSinceAsync(day.AddDays(-1));
        outcomes.Should().HaveCount(1);
        var bucket = outcomes[0];
        bucket.SampleCount.Should().Be(3);
        bucket.UtilizationSum.Should().BeApproximately(120, 0.001);
        bucket.InterferenceSum.Should().BeApproximately(90, 0.001);
        bucket.TxRetrySum.Should().BeApproximately(30, 0.001);
        bucket.LastSampleUtc.Should().Be(day.AddHours(3));
    }

    [Fact]
    public async Task AddOutcomeSamples_SplitsBucketsByDayChannelAndWidth()
    {
        var day = new DateTime(2026, 6, 20, 0, 0, 0, DateTimeKind.Utc);

        await _repository.AddOutcomeSamplesAsync(new[]
        {
            Sample(day.AddHours(1), channel: 6, width: 20),
            Sample(day.AddHours(2), channel: 1, width: 0),
            Sample(day.AddDays(1).AddHours(1), channel: 6, width: 20)
        });

        var outcomes = await _repository.GetOutcomesSinceAsync(day.AddDays(-1));
        outcomes.Should().HaveCount(3);
    }

    [Fact]
    public async Task GetLatestConfigs_ReturnsNewestPerRadio()
    {
        await _repository.AddChangesAsync(new[]
        {
            new ApChannelChange { ApMac = "aa:bb:cc:dd:ee:01", Band = "ng", NewChannel = 1, ChangedAtUtc = DateTime.UtcNow.AddDays(-10), Source = ApChannelChangeSource.Initial },
            new ApChannelChange { ApMac = "aa:bb:cc:dd:ee:01", Band = "ng", PreviousChannel = 1, NewChannel = 6, ChangedAtUtc = DateTime.UtcNow.AddDays(-2), Source = ApChannelChangeSource.UniFiEvent },
            new ApChannelChange { ApMac = "aa:bb:cc:dd:ee:01", Band = "na", NewChannel = 36, ChangedAtUtc = DateTime.UtcNow.AddDays(-10), Source = ApChannelChangeSource.Initial }
        });

        var latest = await _repository.GetLatestConfigsAsync();

        latest.Should().HaveCount(2);
        latest.Single(c => c.Band == "ng").NewChannel.Should().Be(6);
        latest.Single(c => c.Band == "na").NewChannel.Should().Be(36);
    }

    [Fact]
    public async Task Prune_RemovesOldData_ButKeepsLatestChangePerRadio()
    {
        var old = DateTime.UtcNow.AddDays(-400);
        await _repository.AddChangesAsync(new[]
        {
            new ApChannelChange { ApMac = "aa:bb:cc:dd:ee:01", Band = "ng", NewChannel = 1, ChangedAtUtc = old, Source = ApChannelChangeSource.Initial },
            new ApChannelChange { ApMac = "aa:bb:cc:dd:ee:01", Band = "ng", PreviousChannel = 1, NewChannel = 6, ChangedAtUtc = old.AddDays(1), Source = ApChannelChangeSource.UniFiEvent }
        });
        await _repository.AddOutcomeSamplesAsync(new[]
        {
            Sample(old, channel: 1),
            Sample(DateTime.UtcNow.AddDays(-1), channel: 6)
        });

        await _repository.PruneAsync(retentionDays: 365, neighborRetentionDays: 60);

        var outcomes = await _repository.GetOutcomesSinceAsync(DateTime.UtcNow.AddDays(-500));
        outcomes.Should().HaveCount(1);
        outcomes[0].Channel.Should().Be(6);

        // The newest change survives as the last-known-config baseline even though it is
        // past retention; the superseded one is gone.
        var changes = await _repository.GetChangesSinceAsync(DateTime.MinValue);
        changes.Should().HaveCount(1);
        changes[0].NewChannel.Should().Be(6);
    }

    [Fact]
    public async Task CommitCollection_PersistsSamplesChangesAndWatermarkTogether()
    {
        var day = new DateTime(2026, 6, 20, 0, 0, 0, DateTimeKind.Utc);
        var watermark = new DateTime(2026, 6, 20, 6, 0, 0, DateTimeKind.Utc);

        await _repository.CommitCollectionAsync(
            new[] { Sample(day.AddHours(1)), Sample(day.AddHours(2)) },
            new[]
            {
                new ApChannelChange { ApMac = "aa:bb:cc:dd:ee:01", Band = "ng", NewChannel = 6, ChangedAtUtc = day, Source = ApChannelChangeSource.Initial }
            },
            watermark);

        (await _repository.GetOutcomesSinceAsync(day.AddDays(-1))).Should().ContainSingle()
            .Which.SampleCount.Should().Be(2);
        (await _repository.GetChangesSinceAsync(DateTime.MinValue)).Should().ContainSingle();
        (await _repository.GetCollectionWatermarkAsync()).Should().Be(watermark);
    }

    private static NeighborSightingSample Sighting(
        DateTime seenAt, string bssid = "11:22:33:44:55:66", int channel = 6,
        string mac = "aa:bb:cc:dd:ee:01", string band = "ng",
        int width = 20, int signal = -70, string? ssid = "Net") =>
        new(mac, band, bssid, channel, width, signal, seenAt, ssid);

    [Fact]
    public async Task UpsertNeighborSightings_CreatesThenUpdatesRow()
    {
        var day1 = new DateTime(2026, 6, 20, 12, 0, 0, DateTimeKind.Utc);
        var day2 = day1.AddDays(2);

        await _repository.UpsertNeighborSightingsAsync(new[] { Sighting(day1, signal: -70) });
        // Later, weaker sighting: LastSeen advances, signal keeps the strongest observed.
        await _repository.UpsertNeighborSightingsAsync(new[] { Sighting(day2, signal: -78, width: 40) });

        var rows = await _repository.GetNeighborSightingsSinceAsync(DateTime.MinValue);
        var row = rows.Should().ContainSingle().Which;
        row.FirstSeenUtc.Should().Be(day1);
        row.LastSeenUtc.Should().Be(day2);
        row.SignalDbm.Should().Be(-70, "the strongest observed signal is kept");
        row.WidthMhz.Should().Be(40, "width follows the newest sighting");
        row.SightingCount.Should().Be(2, "each upsert cycle increments the count once");
    }

    [Fact]
    public async Task UpsertNeighborSightings_SplitsRowsByChannel_AndDedupsWithinBatch()
    {
        var seen = new DateTime(2026, 6, 20, 12, 0, 0, DateTimeKind.Utc);

        // Same key twice in one batch (must not violate the unique index) plus a second channel.
        await _repository.UpsertNeighborSightingsAsync(new[]
        {
            Sighting(seen, channel: 6, signal: -75),
            Sighting(seen.AddHours(1), channel: 6, signal: -68),
            Sighting(seen, channel: 11)
        });

        var rows = await _repository.GetNeighborSightingsSinceAsync(DateTime.MinValue);
        rows.Should().HaveCount(2);
        var ch6 = rows.Single(r => r.Channel == 6);
        ch6.SignalDbm.Should().Be(-68);
        ch6.SightingCount.Should().Be(1, "two samples in one cycle count as a single sighting");
    }

    [Fact]
    public async Task Prune_RemovesSightingsUnseenPastRetention()
    {
        await _repository.UpsertNeighborSightingsAsync(new[]
        {
            Sighting(DateTime.UtcNow.AddDays(-90), bssid: "11:22:33:44:55:66"),
            Sighting(DateTime.UtcNow.AddDays(-5), bssid: "22:33:44:55:66:77")
        });

        await _repository.PruneAsync(retentionDays: 365, neighborRetentionDays: 60);

        var rows = await _repository.GetNeighborSightingsSinceAsync(DateTime.MinValue);
        rows.Should().ContainSingle().Which.Bssid.Should().Be("22:33:44:55:66:77");
    }

    [Fact]
    public async Task CollectionWatermark_RoundTrips()
    {
        (await _repository.GetCollectionWatermarkAsync()).Should().BeNull();

        var mark = new DateTime(2026, 6, 30, 18, 0, 0, DateTimeKind.Utc);
        await _repository.SetCollectionWatermarkAsync(mark);
        (await _repository.GetCollectionWatermarkAsync()).Should().Be(mark);

        await _repository.SetCollectionWatermarkAsync(mark.AddHours(6));
        (await _repository.GetCollectionWatermarkAsync()).Should().Be(mark.AddHours(6));
    }
}
