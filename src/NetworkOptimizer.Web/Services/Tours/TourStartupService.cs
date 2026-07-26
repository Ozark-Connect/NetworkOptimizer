using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services.Tours;

/// <summary>
/// Startup stamping of the install-level version facts on AdminSettings.
///
/// FirstSeenVersion is written the moment a genuinely new install is detected and never
/// after: on the release that adds the column every existing install has a null
/// LastSeenAppVersion, so null is decided by row age - an AdminSettings row that
/// predates this process is an upgrader, a row created during this startup (or absent)
/// is a new install. Installs predating the column keep a null FirstSeenVersion, which
/// deliberately keeps long-standing users out of the automatic Highlights offer.
///
/// LastSeenAppVersion is then written every startup. Tour dueness is derived from
/// completion state, not from this stamp, so writing it here races nothing.
/// </summary>
public class TourStartupService : IHostedService
{
    private readonly IDbContextFactory<NetworkOptimizerDbContext> _dbFactory;
    private readonly TourDefinitionService _definitions;
    private readonly ILogger<TourStartupService> _logger;
    private readonly DateTime _processStartUtc = DateTime.UtcNow;

    public TourStartupService(
        IDbContextFactory<NetworkOptimizerDbContext> dbFactory,
        TourDefinitionService definitions,
        ILogger<TourStartupService> logger)
    {
        _dbFactory = dbFactory;
        _definitions = definitions;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var version = _definitions.CurrentEffectiveVersion().ToString();
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            var admin = await db.AdminSettings.FirstOrDefaultAsync(cancellationToken);

            // The AdminSettings row alone cannot classify the install: Docker installs
            // using APP_PASSWORD never create one, so its absence proves nothing. A
            // saved UniFi Console connection is the durable evidence an install predates
            // this release - nothing works until one exists.
            var hasConsoleConnection = await db.UniFiConnectionSettings.AnyAsync(cancellationToken);

            if (admin == null)
            {
                db.AdminSettings.Add(new AdminSettings
                {
                    FirstSeenVersion = hasConsoleConnection ? null : version,
                    LastSeenAppVersion = version,
                });
                await db.SaveChangesAsync(cancellationToken);
                if (hasConsoleConnection)
                    _logger.LogDebug("Existing install without an AdminSettings row (env-var password); FirstSeenVersion left null");
                else
                    _logger.LogInformation("New install detected; FirstSeenVersion stamped as {Version}", version);
                return;
            }

            if (admin.LastSeenAppVersion == null && admin.FirstSeenVersion == null)
            {
                // New install only when the row was born during this startup AND no
                // console connection has ever been saved.
                var rowBornThisStartup = admin.CreatedAt >= _processStartUtc.AddMinutes(-2);
                if (rowBornThisStartup && !hasConsoleConnection)
                {
                    admin.FirstSeenVersion = version;
                    _logger.LogInformation("New install detected; FirstSeenVersion stamped as {Version}", version);
                }
            }

            if (admin.LastSeenAppVersion != version)
            {
                admin.LastSeenAppVersion = version;
                admin.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tour startup version stamping failed");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
