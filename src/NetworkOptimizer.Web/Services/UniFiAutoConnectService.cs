using NetworkOptimizer.Storage.Interfaces;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Background service that automatically connects to the default site's UniFi controller
/// when the application starts. This ensures the app reconnects after restarts without requiring
/// manual intervention. Only the first enabled site with saved credentials is connected.
/// </summary>
public class UniFiAutoConnectService : BackgroundService
{
    private readonly ILogger<UniFiAutoConnectService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly UniFiConnectionService _connectionService;

    public UniFiAutoConnectService(
        ILogger<UniFiAutoConnectService> logger,
        IServiceProvider serviceProvider,
        UniFiConnectionService connectionService)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _connectionService = connectionService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait a moment for the app to fully start
        await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);

        _logger.LogInformation("Starting auto-connect for default site");

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var siteRepository = scope.ServiceProvider.GetRequiredService<ISiteRepository>();

            var sites = await siteRepository.GetAllSitesAsync();
            var defaultSite = sites.FirstOrDefault(s => s.Enabled);

            if (defaultSite == null)
            {
                _logger.LogDebug("No enabled sites found, skipping auto-connect");
                return;
            }

            // Check if the default site has saved credentials
            var settings = await _connectionService.GetSettingsAsync(defaultSite.Id);

            if (!settings.IsConfigured || !settings.HasCredentials)
            {
                _logger.LogDebug("Default site {SiteId} has no saved credentials, skipping auto-connect",
                    defaultSite.Id);
                return;
            }

            _logger.LogInformation("Auto-connecting default site {SiteId} ({SiteName}) to UniFi controller",
                defaultSite.Id, defaultSite.Name);

            var success = await _connectionService.ConnectWithSavedCredentialsAsync(defaultSite.Id);

            if (success)
            {
                _logger.LogInformation("Auto-connected site {SiteId} successfully", defaultSite.Id);
            }
            else
            {
                var error = _connectionService.GetLastError(defaultSite.Id);
                _logger.LogWarning("Failed to auto-connect site {SiteId}: {Error}",
                    defaultSite.Id, error ?? "Unknown error");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during auto-connect startup");
        }
    }
}
