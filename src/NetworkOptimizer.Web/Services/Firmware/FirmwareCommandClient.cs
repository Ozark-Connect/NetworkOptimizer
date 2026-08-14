using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.Web.Services.Firmware;

/// <summary>
/// The real <see cref="IFirmwareCommandClient"/>: the site's UniFi console for API commands and
/// the site's device SSH service for the direct <c>upgrade &lt;url&gt;</c> path. Both are already
/// tunnel-routed, so an agent-connected site needs nothing extra here.
/// </summary>
public class FirmwareCommandClient : IFirmwareCommandClient
{
    /// <summary>
    /// Marks an API call whose request shape has not been captured yet. Each returns
    /// NotSupported so its caller runs, logs, and falls through to a path that works.
    /// </summary>
    private const string PendingSampleSuffix = "the request shape has not been captured yet";

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
    public Task<FirmwareCommandResult> TriggerUpgradeAsync(string deviceMac, CancellationToken cancellationToken = default)
    {
        // TODO(sample): cmd/devmgr roll-forward upgrade. Until the request body is captured, the
        // orchestrator falls through to upgrade-external with the catalog URL and then to SSH.
        _logger.LogDebug("Roll-forward upgrade for {Mac} is not implemented yet; the caller falls through", deviceMac);
        return Task.FromResult(FirmwareCommandResult.NotSupported(
            $"The UniFi roll-forward upgrade command is not available: {PendingSampleSuffix}."));
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
            var accepted = await client.TriggerExternalFirmwareUpgradeAsync(deviceMac, firmwareUrl, cancellationToken);
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
    public Task<FirmwareCommandResult> TriggerBackupAsync(CancellationToken cancellationToken = default)
    {
        // TODO(sample): console backup trigger. The pre-flight gate treats NotSupported as
        // "note it and carry on" so an uncaptured call cannot block every rollout.
        return Task.FromResult(FirmwareCommandResult.NotSupported(
            $"The console backup trigger is not available: {PendingSampleSuffix}."));
    }
}
