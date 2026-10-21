using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Repositories;
using NetworkOptimizer.Threats.Models;
using Xunit;

namespace NetworkOptimizer.Storage.Tests;

/// <summary>The instance-wide CrowdSec CTI cache in the main database.</summary>
public class CtiReputationCacheTests : IDisposable
{
    private readonly SqliteConnection _main = new("DataSource=:memory:");
    private readonly SqliteConnection _site = new("DataSource=:memory:");
    private readonly IDbContextFactory<NetworkOptimizerDbContext> _mainFactory;
    private readonly IDbContextFactory<NetworkOptimizerDbContext> _siteFactory;
    private readonly CtiReputationCache _cache;

    public CtiReputationCacheTests()
    {
        _main.Open();
        _site.Open();
        _mainFactory = new NetworkOptimizerDbContextFactory(new DbContextOptionsBuilder<NetworkOptimizerDbContext>().UseSqlite(_main).Options);
        _siteFactory = new NetworkOptimizerDbContextFactory(new DbContextOptionsBuilder<NetworkOptimizerDbContext>().UseSqlite(_site).Options);
        using (var db = _mainFactory.CreateDbContext()) db.Database.EnsureCreated();
        using (var db = _siteFactory.CreateDbContext()) db.Database.EnsureCreated();
        _cache = new CtiReputationCache(_mainFactory, NullLogger<CtiReputationCache>.Instance);
    }

    public void Dispose()
    {
        _main.Dispose();
        _site.Dispose();
    }

    private static CrowdSecReputation Row(string ip, int expiresInHours = 24, string json = "{}") => new()
    {
        Ip = ip,
        ReputationJson = json,
        FetchedAt = DateTime.UtcNow,
        ExpiresAt = DateTime.UtcNow.AddHours(expiresInHours)
    };

    [Fact]
    public async Task SaveThenGet_ReturnsTheAnswer_AndASecondSaveReplacesIt()
    {
        await _cache.SaveAsync(Row("198.51.100.7", json: "{\"a\":1}"));
        await _cache.SaveAsync(Row("198.51.100.7", json: "{\"a\":2}"));

        (await _cache.GetAsync("198.51.100.7"))!.ReputationJson.Should().Be("{\"a\":2}");
    }

    [Fact]
    public async Task AnExpiredAnswer_IsNotReturned_AndIsPurged()
    {
        await _cache.SaveAsync(Row("198.51.100.7", expiresInHours: -1));

        (await _cache.GetAsync("198.51.100.7")).Should().BeNull();
        await _cache.PurgeExpiredAsync();
        using var db = _mainFactory.CreateDbContext();
        db.CrowdSecReputations.Should().BeEmpty();
    }

    [Theory]
    [InlineData("10.1.2.3")]
    [InlineData("192.168.1.10")]
    [InlineData("100.64.0.9")]
    public async Task NonPublicAddresses_AreNeverStored(string ip)
    {
        await _cache.SaveAsync(Row(ip));

        using var db = _mainFactory.CreateDbContext();
        db.CrowdSecReputations.Should().BeEmpty();
        (await _cache.GetAsync(ip)).Should().BeNull();
    }

    [Fact]
    public void MergeFromSite_CopiesUnexpiredAnswers_AndKeepsTheMainCopy()
    {
        using (var site = _siteFactory.CreateDbContext())
        {
            site.CrowdSecReputations.AddRange(
                Row("198.51.100.1", json: "{\"site\":1}"),
                Row("198.51.100.2", json: "{\"site\":2}"),
                Row("198.51.100.3", expiresInHours: -1));
            site.SaveChanges();
        }
        using (var main = _mainFactory.CreateDbContext())
        {
            main.CrowdSecReputations.Add(Row("198.51.100.2", json: "{\"main\":2}"));
            main.SaveChanges();
        }

        using var mainDb = _mainFactory.CreateDbContext();
        using var siteDb = _siteFactory.CreateDbContext();
        CtiReputationCache.MergeFromSite(mainDb, siteDb).Should().Be(1);

        using var check = _mainFactory.CreateDbContext();
        check.CrowdSecReputations.Select(r => r.Ip).Should().BeEquivalentTo("198.51.100.1", "198.51.100.2");
        check.CrowdSecReputations.Single(r => r.Ip == "198.51.100.2").ReputationJson.Should().Be("{\"main\":2}");
    }

    [Fact]
    public void MergeFromSite_RefreshesAnExpiredMainRow()
    {
        using (var main = _mainFactory.CreateDbContext())
        {
            main.CrowdSecReputations.Add(Row("198.51.100.4", expiresInHours: -1, json: "{\"old\":1}"));
            main.SaveChanges();
        }
        using (var site = _siteFactory.CreateDbContext())
        {
            site.CrowdSecReputations.Add(Row("198.51.100.4", json: "{\"new\":1}"));
            site.SaveChanges();
        }

        using var mainDb = _mainFactory.CreateDbContext();
        using var siteDb = _siteFactory.CreateDbContext();
        CtiReputationCache.MergeFromSite(mainDb, siteDb).Should().Be(1);

        using var check = _mainFactory.CreateDbContext();
        check.CrowdSecReputations.Single().ReputationJson.Should().Be("{\"new\":1}");
    }
}
