using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Storage.Interfaces;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Storage;

/// <summary>
/// Extension methods for configuring storage services
/// </summary>
public static class StorageServiceExtensions
{
    /// <summary>
    /// Add InfluxDB storage services to the service collection
    /// </summary>
    public static IServiceCollection AddInfluxDbStorage(
        this IServiceCollection services,
        StorageConfiguration configuration)
    {
        services.AddSingleton<IMetricsStorage>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<InfluxDbStorage>>();
            return new InfluxDbStorage(
                configuration.Url,
                configuration.Token,
                configuration.Bucket,
                configuration.Organization,
                logger,
                configuration.BatchFlushIntervalSeconds,
                configuration.MaxBufferSize);
        });

        return services;
    }

    /// <summary>
    /// Add SQLite repository services to the service collection
    /// </summary>
    public static IServiceCollection AddSqliteRepository(
        this IServiceCollection services,
        SqliteConfiguration configuration)
    {
        services.AddDbContext<NetworkOptimizerDbContext>(options =>
        {
            options.UseSqlite($"Data Source={configuration.DatabasePath}");

            if (configuration.EnableSensitiveDataLogging)
            {
                options.EnableSensitiveDataLogging();
            }

            options.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
        });

        services.AddScoped<ILocalRepository, SqliteRepository>();

        return services;
    }

    /// <summary>
    /// Add both InfluxDB and SQLite storage services
    /// </summary>
    public static IServiceCollection AddNetworkOptimizerStorage(
        this IServiceCollection services,
        StorageConfiguration influxConfig,
        SqliteConfiguration sqliteConfig)
    {
        services.AddInfluxDbStorage(influxConfig);
        services.AddSqliteRepository(sqliteConfig);

        return services;
    }

    /// <summary>
    /// Ensure the database is created and migrations are applied
    /// </summary>
    public static async Task EnsureDatabaseCreatedAsync(this IServiceProvider serviceProvider)
    {
        using var scope = serviceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<NetworkOptimizerDbContext>();
        await context.Database.MigrateAsync();
    }
}
