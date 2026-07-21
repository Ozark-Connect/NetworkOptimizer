using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models.Identity;

namespace NetworkOptimizer.Web.Services.Identity;

/// <summary>
/// DI wiring for ASP.NET Core Identity on the dedicated main-DB <see cref="AuthDbContext"/>.
/// </summary>
/// <remarks>
/// This method is intentionally additive: it registers the user/role stores, managers, password
/// hasher, and the bootstrap seeder, but does NOT change the authentication pipeline (cookie schemes,
/// default scheme, sign-in) - that swap is applied separately during the cutover so the identity
/// data model and admin seed can be verified before the live auth artifact changes (design doc 02).
/// </remarks>
public static class IdentityRegistration
{
    /// <summary>
    /// Registers the Identity core (UserManager/RoleManager stores on <see cref="AuthDbContext"/>),
    /// the 600k-iteration password hasher with the legacy-format fallback, and the startup bootstrap
    /// seeder. Both a scoped context (for the managers) and a singleton factory (for background/startup
    /// work) are registered against the MAIN database.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="mainDbPath">Absolute path to the main SQLite database (registry + default site).</param>
    public static IServiceCollection AddNetOptIdentityCore(this IServiceCollection services, string mainDbPath)
    {
        // Scoped context for the Identity managers. Always the MAIN db, with a dedicated
        // migrations-history table so it coexists with the site-routed product context.
        services.AddDbContext<AuthDbContext>(options =>
            options.UseSqlite(
                $"Data Source={mainDbPath}",
                x => x.MigrationsHistoryTable(AuthDbContext.MigrationsHistoryTable)));

        // Singleton factory for the audit writer, startup migration, and break-glass - services that
        // can't take a scoped context. Owns its own options instance (see AuthDbContextFactory).
        var factoryOptions = new DbContextOptionsBuilder<AuthDbContext>()
            .UseSqlite(
                $"Data Source={mainDbPath}",
                x => x.MigrationsHistoryTable(AuthDbContext.MigrationsHistoryTable))
            .Options;
        services.AddSingleton<IDbContextFactory<AuthDbContext>>(new AuthDbContextFactory(factoryOptions));

        services.AddIdentityCore<ApplicationUser>(options =>
            {
                // Match the app's historical local-account policy (>= 8 chars, letter + digit).
                options.Password.RequiredLength = 8;
                options.Password.RequireDigit = true;
                options.Password.RequireLowercase = false;
                options.Password.RequireUppercase = false;
                options.Password.RequireNonAlphanumeric = false;

                // Email is optional on appliance installs; usernames are the primary identifier.
                options.User.RequireUniqueEmail = false;
                options.SignIn.RequireConfirmedAccount = false;

                // Identity's default lockout (5 fails / 5 min), tunable later via Settings.
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
                options.Lockout.AllowedForNewUsers = true;
            })
            .AddRoles<ApplicationRole>()
            .AddEntityFrameworkStores<AuthDbContext>()
            .AddDefaultTokenProviders();

        // Keep new hashes at the app's historical strength (framework default 100k is weaker).
        services.Configure<PasswordHasherOptions>(o => o.IterationCount = 600_000);

        // One-release belt-and-suspenders: verify any still-legacy-format hash and flag it for rehash.
        services.AddScoped<IPasswordHasher<ApplicationUser>, LegacyFallbackPasswordHasher>();

        services.AddScoped<IIdentityBootstrapService, IdentityBootstrapService>();

        return services;
    }
}
