using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

/// <summary>A threat setting's UpdatedAt moves when its value changes, and an unchanged save writes nothing.</summary>
public class ThreatSettingsAccessorTests : IDisposable
{
    private readonly SqliteConnection _main = new("DataSource=:memory:");
    private readonly IDbContextFactory<NetworkOptimizerDbContext> _factory;
    private readonly ThreatSettingsAccessor _accessor;

    public ThreatSettingsAccessorTests()
    {
        _main.Open();
        _factory = new NetworkOptimizerDbContextFactory(
            new DbContextOptionsBuilder<NetworkOptimizerDbContext>().UseSqlite(_main).Options);
        using (var db = _factory.CreateDbContext())
            db.Database.EnsureCreated();
        _accessor = new ThreatSettingsAccessor(_factory, Mock.Of<ICredentialProtectionService>());
    }

    public void Dispose() => _main.Dispose();

    private DateTime UpdatedAt(string key)
    {
        using var db = _factory.CreateDbContext();
        return db.SystemSettings.AsNoTracking().Single(s => s.Key == key).UpdatedAt;
    }

    private void Backdate(string key)
    {
        using var db = _factory.CreateDbContext();
        db.SystemSettings.Single(s => s.Key == key).UpdatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        db.SaveChanges();
    }

    [Fact]
    public async Task A_changed_value_moves_UpdatedAt()
    {
        await _accessor.SaveSettingAsync("waf.cursor", "a:1");
        Backdate("waf.cursor");

        await _accessor.SaveSettingAsync("waf.cursor", "a:2");

        UpdatedAt("waf.cursor").Should().BeAfter(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task An_unchanged_value_leaves_UpdatedAt_alone()
    {
        await _accessor.SaveSettingAsync("waf.cursor", "a:1");
        Backdate("waf.cursor");

        await _accessor.SaveSettingAsync("waf.cursor", "a:1");

        UpdatedAt("waf.cursor").Should().Be(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    }
}
