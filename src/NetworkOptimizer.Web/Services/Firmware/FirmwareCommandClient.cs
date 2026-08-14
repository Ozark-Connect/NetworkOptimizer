using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.Web.Services.Firmware;

/// <summary>
/// The real <see cref="IFirmwareCommandClient"/>: the site's UniFi console for API commands and
/// the site's device SSH service for the direct <c>upgrade &lt;url&gt;</c> path. Both are already
/// tunnel-routed, so an agent-connected site needs nothing extra here.
/// </summary>
public class FirmwareCommandClient : IFirmwareCommandClient
{
    private readonly UniFiConnectionService _connection;
    private readonly IUniFiSshService _ssh;
    private readonly ILogger<FirmwareCommandClient> _logger;
    private readonly string _siteSlug;

    /// <param name="siteConnections">Per-site console connections.</param>
    /// <param name="sshRegistry">Per-site device SSH services.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="siteSlug">Site this client commands.</param>
    public FirmwareCommandClient(
        SiteConnectionRegistry siteConnections,
        UniFiSshRegistry sshRegistry,
        ILogger<FirmwareCommandClient> logger,
        string siteSlug = SiteManagementService.DefaultSiteSlug)
    {
        _siteSlug = string.IsNullOrEmpty(siteSlug) ? SiteManagementService.DefaultSiteSlug : siteSlug;
        _connection = siteConnections.GetFor(_siteSlug);
        _ssh = sshRegistry.GetFor(_siteSlug);
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<FirmwareCommandResult> TriggerUpgradeAsync(string deviceMac, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(deviceMac))
            return FirmwareCommandResult.Failed("No device MAC to command.");

        var client = _connection.Client;
        if (client == null)
            return FirmwareCommandResult.Failed("The UniFi Console is not connected.");

        try
        {
            var accepted = await client.TriggerDeviceUpgradeAsync(deviceMac, cancellationToken);
            return accepted
                ? FirmwareCommandResult.Ok()
                : FirmwareCommandResult.Failed("The UniFi Console refused the upgrade command.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Upgrade command for {Mac} on site {Site} threw", deviceMac, _siteSlug);
            return FirmwareCommandResult.Failed($"The upgrade command failed: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<FirmwareCommandResult> TriggerExternalUpgradeAsync(
        string deviceMac, string firmwareUrl, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(deviceMac))
            return FirmwareCommandResult.Failed("No device MAC to command.");
        if (string.IsNullOrWhiteSpace(firmwareUrl))
            return FirmwareCommandResult.Failed("No firmware image URL for this device.");

        var client = _connection.Client;
        if (client == null)
            return FirmwareCommandResult.Failed("The UniFi Console is not connected.");

        try
        {
            var accepted = await client.TriggerDeviceExternalUpgradeAsync(deviceMac, firmwareUrl, cancellationToken);
            return accepted
                ? FirmwareCommandResult.Ok()
                : FirmwareCommandResult.Failed("The UniFi Console rejected the upgrade command.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Upgrade command for {Mac} on site {Site} threw", deviceMac, _siteSlug);
            return FirmwareCommandResult.Failed($"The upgrade command failed: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<FirmwareCommandResult> TriggerSshUpgradeAsync(
        string host, string firmwareUrl, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(host))
            return FirmwareCommandResult.Failed("No device address for the SSH upgrade path.");
        if (string.IsNullOrWhiteSpace(firmwareUrl))
            return FirmwareCommandResult.Failed("No firmware image URL for this device.");

        try
        {
            var (success, output) = await _ssh.RunCommandAsync(host, $"upgrade {firmwareUrl}", null, cancellationToken);
            if (success)
                return FirmwareCommandResult.Ok(output);

            return FirmwareCommandResult.Failed(
                string.IsNullOrWhiteSpace(output) ? "The SSH upgrade command failed." : output.Trim());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "SSH upgrade on {Host} for site {Site} threw", host, _siteSlug);
            return FirmwareCommandResult.Failed($"The SSH upgrade command failed: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<UniFiFirmwareCatalogEntry>> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        var client = _connection.Client;
        if (client == null)
        {
            _logger.LogDebug("Cannot check for firmware updates on site {Site}: the console is not connected", _siteSlug);
            return [];
        }

        try
        {
            return await client.ListAvailableFirmwareAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Firmware check failed for site {Site}", _siteSlug);
            return [];
        }
    }

    /// <inheritdoc />
    public async Task<bool> CheckForApplicationUpdatesAsync(CancellationToken cancellationToken = default)
    {
        var client = _connection.Client;
        if (client == null) return false;

        try
        {
            return await client.TriggerConsoleAppUpdateCheckAsync([UniFiConsoleController.NetworkName], cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "The application update check failed for site {Site}", _siteSlug);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<string?> GetDeviceChannelAsync(CancellationToken cancellationToken = default)
    {
        var client = _connection.Client;
        if (client == null) return null;

        try
        {
            var settings = await client.GetFirmwareUpdateSettingsAsync(cancellationToken);
            return settings?.FirmwareChannel;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Reading the device firmware channel failed for site {Site}", _siteSlug);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<RolloutChannelAvailability> GetChannelAvailabilityAsync(CancellationToken cancellationToken = default)
    {
        var availability = new RolloutChannelAvailability();
        var client = _connection.Client;
        if (client == null) return availability;

        try
        {
            var settings = await client.GetFirmwareUpdateSettingsAsync(cancellationToken);
            if (settings != null)
            {
                availability.CurrentDeviceChannel = settings.FirmwareChannel ?? FirmwareChannels.Release;
                availability.AvailableDeviceChannels = settings.AvailableFirmwareChannels;
                availability.AvailableNetworkAppChannels = settings.AvailableControllerChannels;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Reading the firmware channel options failed for site {Site}", _siteSlug);
        }

        // The two console-level channels live on /api/system, not in the Network application's
        // settings: UniFi OS under firmware, the application under apps.controllers[network].
        var console = await GetConsoleSystemInfoAsync(cancellationToken);
        if (console != null)
        {
            availability.CurrentUniFiOsChannel = console.Firmware?.ReleaseChannel;
            availability.AvailableUniFiOsChannels = console.Firmware?.Channels ?? [];
            availability.CurrentNetworkAppChannel = console.NetworkApplication?.ReleaseChannel;
            availability.CurrentNetworkAppVersion = console.NetworkApplication?.Version;
        }
        else
        {
            _logger.LogWarning("Console channels unavailable for site {Site}: /api/system did not answer", _siteSlug);
        }

        if (console != null && console.Firmware == null && console.Apps == null)
        {
            _logger.LogInformation(
                "Console API out of reach on site {Site}: /api/system answered with nothing, which is what an API-key connection returns",
                _siteSlug);
        }

        _logger.LogInformation(
            "Channels on site {Site}: devices={Device}, network={App} ({AppVersion}), os={Os}",
            _siteSlug,
            availability.CurrentDeviceChannel,
            availability.CurrentNetworkAppChannel ?? "unknown",
            availability.CurrentNetworkAppVersion ?? "unknown",
            availability.CurrentUniFiOsChannel ?? "unknown");

        return availability;
    }

    /// <inheritdoc />
    public async Task<bool?> GetAutoUpgradeEnabledAsync(CancellationToken cancellationToken = default)
    {
        var client = _connection.Client;
        if (client == null) return null;

        try
        {
            return await client.GetDeviceAutoUpgradeEnabledAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Reading the console auto-upgrade flag failed for site {Site}", _siteSlug);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<bool> SetDeviceChannelAsync(string channel, CancellationToken cancellationToken = default)
    {
        var client = _connection.Client;
        if (client == null) return false;

        try
        {
            return await client.SetDeviceFirmwareChannelAsync(channel, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Setting the device firmware channel to {Channel} failed for site {Site}", channel, _siteSlug);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> SetConsoleChannelsAsync(
        string? networkAppChannel, string? unifiOsChannel, CancellationToken cancellationToken = default)
    {
        if (networkAppChannel == null && unifiOsChannel == null) return true;

        var client = _connection.Client;
        if (client == null) return false;

        try
        {
            return await client.SetConsoleUpdateChannelsAsync(networkAppChannel, unifiOsChannel, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Setting the console update channels failed for site {Site}", _siteSlug);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<UniFiConsoleSystemInfo?> GetConsoleSystemInfoAsync(CancellationToken cancellationToken = default)
    {
        var client = _connection.Client;
        if (client == null) return null;

        try
        {
            return await client.GetConsoleSystemInfoAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Reading console system info failed for site {Site}", _siteSlug);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<FirmwareCommandResult> TriggerBackupAsync(CancellationToken cancellationToken = default)
    {
        var client = _connection.Client;
        if (client == null)
            return FirmwareCommandResult.Failed("The UniFi Console is not connected.");

        try
        {
            return MapBackupResult(await client.TriggerConsoleBackupAsync(cancellationToken));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Console backup on site {Site} threw", _siteSlug);
            return FirmwareCommandResult.Failed($"the backup request failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Turns the console's backup response into a rollout result.
    /// <para>
    /// The overall flag can be false while most components succeeded, so the ones that failed are
    /// named: "the backup failed" on its own gives the operator nothing to go and look at, and this
    /// message is what the postpone alert carries.
    /// </para>
    /// </summary>
    /// <param name="result">What the console answered, or null when it did not answer.</param>
    public static FirmwareCommandResult MapBackupResult(UniFiConsoleBackupResult? result)
    {
        if (result == null)
            return FirmwareCommandResult.Failed("the console did not answer the backup request");

        if (result.Success)
            return FirmwareCommandResult.Ok();

        var failed = result.Controllers.Concat(result.Services)
            .Where(c => !c.Value.Success)
            .Select(c => c.Key)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return FirmwareCommandResult.Failed(failed.Count == 0
            ? "the console reported the backup as unsuccessful"
            : $"the console could not back up {string.Join(", ", failed)}");
    }

    /// <inheritdoc />
    public async Task<bool> TriggerNetworkApplicationUpdateAsync(CancellationToken cancellationToken = default)
    {
        var client = _connection.Client;
        if (client == null) return false;

        try
        {
            // The availability check first, so the install acts on what the console knows now
            // rather than on whatever it last happened to look up.
            await client.TriggerConsoleAppUpdateCheckAsync(["network"], cancellationToken);
            return await client.TriggerNetworkApplicationUpdateAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "UniFi Network application update on site {Site} threw", _siteSlug);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<UniFiConsoleFirmwareRelease?> GetPendingUniFiOsUpdateAsync(CancellationToken cancellationToken = default)
    {
        var client = _connection.Client;
        if (client == null) return null;

        try
        {
            return await client.GetUniFiOsPendingUpdateAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Reading the pending UniFi OS build on site {Site} threw", _siteSlug);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<bool> TriggerUniFiOsUpdateAsync(CancellationToken cancellationToken = default)
    {
        var client = _connection.Client;
        if (client == null) return false;

        try
        {
            return await client.TriggerUniFiOsUpdateAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "UniFi OS update on site {Site} threw", _siteSlug);
            return false;
        }
    }
}
