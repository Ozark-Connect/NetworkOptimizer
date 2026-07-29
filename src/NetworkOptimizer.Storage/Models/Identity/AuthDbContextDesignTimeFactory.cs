using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace NetworkOptimizer.Storage.Models.Identity;

/// <summary>
/// Design-time factory so EF Core tooling (dotnet ef migrations) can construct
/// <see cref="AuthDbContext"/> without the app's DI. Not used at runtime. Mirrors the runtime
/// registration's custom migrations-history table so generated migrations target the right table.
/// </summary>
public class AuthDbContextDesignTimeFactory : IDesignTimeDbContextFactory<AuthDbContext>
{
    public AuthDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AuthDbContext>()
            .UseSqlite(
                "Data Source=auth-design-time.db",
                o => o.MigrationsHistoryTable(AuthDbContext.MigrationsHistoryTable))
            .Options;
        return new AuthDbContext(options);
    }
}
