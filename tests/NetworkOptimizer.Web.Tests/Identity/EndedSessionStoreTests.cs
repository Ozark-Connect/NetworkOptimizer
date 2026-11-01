using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Identity;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Identity;

/// <summary>
/// Signing out ends one login on the server, so a copy of its cookie stops working. These run the store
/// against a real migrated auth database, because the row has to survive a restart to mean anything.
/// </summary>
public sealed class EndedSessionStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"netopt-ended-{Guid.NewGuid():N}.db");

    private DbContextOptions<AuthDbContext> Options() => new DbContextOptionsBuilder<AuthDbContext>()
        .UseSqlite($"Data Source={_dbPath}", x => x.MigrationsHistoryTable(AuthDbContext.MigrationsHistoryTable))
        .Options;

    private async Task<EndedSessionStore> NewStoreAsync()
    {
        await using (var db = new AuthDbContext(Options()))
            await db.Database.MigrateAsync();
        return new EndedSessionStore(new AuthDbContextFactory(Options()), NullLogger<EndedSessionStore>.Instance);
    }

    [Fact]
    public async Task EndedLogin_IsRefused_AndOthersAreNot()
    {
        var store = await NewStoreAsync();

        await store.EndAsync("login-a", "user-1", DateTime.UtcNow.AddDays(30));

        (await store.IsEndedAsync("login-a")).Should().BeTrue();
        (await store.IsEndedAsync("login-b")).Should().BeFalse("ending one login leaves the user's others signed in");
        (await store.IsEndedAsync(null)).Should().BeFalse("a cookie with no login id yet is not ended");
    }

    [Fact]
    public async Task EndedLogin_StaysEnded_AfterARestart()
    {
        var store = await NewStoreAsync();
        await store.EndAsync("login-a", "user-1", DateTime.UtcNow.AddDays(30));

        var restarted = new EndedSessionStore(new AuthDbContextFactory(Options()), NullLogger<EndedSessionStore>.Instance);

        (await restarted.IsEndedAsync("login-a")).Should().BeTrue();
    }

    [Fact]
    public async Task ExpiredRows_AreNotEnded_AndArePruned()
    {
        var store = await NewStoreAsync();
        await store.EndAsync("old", "user-1", DateTime.UtcNow.AddMinutes(-1));
        await store.EndAsync("new", "user-1", DateTime.UtcNow.AddDays(30));

        (await store.IsEndedAsync("old")).Should().BeFalse("no cookie of that login can still be valid");
        await using var db = new AuthDbContext(Options());
        (await db.EndedSessions.Select(s => s.LoginId).ToListAsync()).Should().Equal("new");
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch (IOException) { }
    }
}
