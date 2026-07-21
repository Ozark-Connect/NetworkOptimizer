using Microsoft.EntityFrameworkCore;

namespace NetworkOptimizer.Storage.Models.Identity;

/// <summary>
/// Custom <see cref="IDbContextFactory{TContext}"/> for singleton services that need main-DB
/// identity/audit access (the audit background writer, the startup migration/seed, break-glass).
/// Mirrors <see cref="NetworkOptimizerDbContextFactory"/>: owns its own options instance to avoid the
/// scoped-vs-singleton <c>DbContextOptions</c> DI-validation conflict that arises from registering
/// both <c>AddDbContext</c> and <c>AddDbContextFactory</c> for the same context.
/// </summary>
public class AuthDbContextFactory : IDbContextFactory<AuthDbContext>
{
    private readonly DbContextOptions<AuthDbContext> _options;

    public AuthDbContextFactory(DbContextOptions<AuthDbContext> options)
    {
        _options = options;
    }

    public AuthDbContext CreateDbContext() => new(_options);
}
