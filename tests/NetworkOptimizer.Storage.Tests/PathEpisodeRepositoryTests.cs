using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Repositories;
using Xunit;

namespace NetworkOptimizer.Storage.Tests;

/// <summary>
/// SQLite in-memory rather than the EF InMemory provider: pruning uses ExecuteDeleteAsync, which only
/// translates against a real relational provider.
/// </summary>
public class PathEpisodeRepositoryTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 10, 29, 0, 0, 0, DateTimeKind.Utc);
    private readonly SqliteConnection _connection;
    private readonly NetworkOptimizerDbContext _context;
    private readonly PathEpisodeRepository _repository;

    public PathEpisodeRepositoryTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = new NetworkOptimizerDbContext(new DbContextOptionsBuilder<NetworkOptimizerDbContext>()
            .UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();
        _repository = new PathEpisodeRepository(_context);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    private static PathEpisode Episode(DateTime opened, DateTime? closed = null) => new()
    {
        OpenedAt = opened,
        ClosedAt = closed,
        UpdatedAt = opened,
        Targets = { new PathEpisodeTarget { MonitoringTargetId = 7, Role = PathEpisodeTargetRole.Degraded, PeakLossPct = 5 } }
    };

    [Fact]
    public async Task Update_WritesTheVerdictAndAddsNewTargets()
    {
        var id = await _repository.AddAsync(Episode(T0));

        var update = Episode(T0);
        update.Id = id;
        update.Scope = "Corridor";
        update.ReturnPathFinding = ReturnPathFinding.NoSharedForwardHop;
        update.Targets[0].PeakLossPct = 13;
        update.Targets.Add(new PathEpisodeTarget { MonitoringTargetId = 9, Role = PathEpisodeTargetRole.Control });
        await _repository.UpdateAsync(update);
        _context.ChangeTracker.Clear();

        var stored = await _repository.GetWithTracesAsync(id);
        stored!.Scope.Should().Be("Corridor");
        stored.ReturnPathFinding.Should().Be(ReturnPathFinding.NoSharedForwardHop);
        stored.Targets.Should().HaveCount(2);
        stored.Targets.Single(t => t.MonitoringTargetId == 7).PeakLossPct.Should().Be(13);
    }

    [Fact]
    public async Task Overlapping_IncludesOpenEpisodesAndExcludesOnesThatEndedBefore()
    {
        await _repository.AddAsync(Episode(T0, T0.AddHours(1)));
        await _repository.AddAsync(Episode(T0.AddHours(5)));

        var r = await _repository.GetOverlappingAsync(T0.AddHours(2), T0.AddHours(10), 10);

        r.Should().ContainSingle().Which.OpenedAt.Should().Be(T0.AddHours(5));
    }

    [Fact]
    public async Task ClosedWithoutRecovery_SkipsOpenAndRecoveredEpisodes()
    {
        await _repository.AddAsync(Episode(T0));
        var recovered = Episode(T0, T0.AddHours(1));
        recovered.RecoveryTracedAt = T0.AddHours(2);
        await _repository.AddAsync(recovered);
        var waitingId = await _repository.AddAsync(Episode(T0, T0.AddHours(1)));

        var r = await _repository.GetClosedWithoutRecoveryAsync();

        r.Should().ContainSingle().Which.Id.Should().Be(waitingId);
    }

    [Fact]
    public async Task Prune_RemovesOldEpisodesWithTheirTargetsAndTraces()
    {
        var oldId = await _repository.AddAsync(Episode(T0.AddDays(-100), T0.AddDays(-100)));
        await _repository.AddTraceAsync(new PathEpisodeTrace { EpisodeId = oldId, MonitoringTargetId = 7, Timestamp = T0.AddDays(-100) });
        var keptId = await _repository.AddAsync(Episode(T0));

        var removed = await _repository.PruneAsync(T0.AddDays(-90));

        removed.Should().Be(1);
        _context.ChangeTracker.Clear();
        (await _context.PathEpisodes.Select(e => e.Id).ToListAsync()).Should().Equal(keptId);
        (await _context.PathEpisodeTraces.CountAsync()).Should().Be(0);
        (await _context.PathEpisodeTargets.CountAsync()).Should().Be(1);
    }
}
