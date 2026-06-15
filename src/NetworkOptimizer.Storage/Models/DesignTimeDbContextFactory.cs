using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace NetworkOptimizer.Storage.Models;

/// <summary>
/// Design-time factory so EF Core tooling (dotnet ef migrations) can construct the
/// context without the app's DI. Not used at runtime.
/// </summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<NetworkOptimizerDbContext>
{
    public NetworkOptimizerDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<NetworkOptimizerDbContext>()
            .UseSqlite("Data Source=design-time.db")
            .Options;
        return new NetworkOptimizerDbContext(options);
    }
}
