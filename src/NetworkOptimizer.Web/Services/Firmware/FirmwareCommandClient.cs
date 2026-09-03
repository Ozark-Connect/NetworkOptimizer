using NetworkOptimizer.UniFi;
using NetworkOptimizer.UniFi.Models;
using NetworkOptimizer.Web.Services.Ssh;

namespace NetworkOptimizer.Web.Services.Firmware;

/// <summary>
/// The real <see cref="IFirmwareCommandClient"/>: the site's UniFi console for API commands,
/// the site's device SSH service for the direct device upgrade path, and the gateway SSH
/// service for console-level SSH fallbacks. All are already tunnel-routed, so an
/// agent-connected site needs nothing extra here.
/// </summary>
public class FirmwareCommandClient : IFirmwareCommandClient
{
    private readonly UniFiConnectionService _connection;
    private readonly IUniFiSshService _ssh;
    private readonly IGatewaySshService _gatewaySsh;
    private readonly ILogger<FirmwareCommandClient> _logger;
    private readonly string _siteSlug;

    /// <param name="siteConnections">Per-site console connections.</param>
    /// <param name="sshRegistry">Per-site device SSH services.</param>
    /// <param name="gatewaySshRegistry">Per-site gateway SSH services.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="siteSlug">Site this client commands.</param>
    public FirmwareCommandClient(
        SiteConnectionRegistry siteConnections,
        UniFiSshRegistry sshRegistry,
        GatewaySshRegistry gatewaySshRegistry,
        ILogger<FirmwareCommandClient> logger,
        string siteSlug = SiteManagementService.DefaultSiteSlug)
    {
        _siteSlug = string.IsNullOrEmpty(siteSlug) ? SiteManagementService.DefaultSiteSlug : siteSlug;
        _connection = siteConnections.GetFor(_siteSlug);
        _ssh = sshRegistry.GetFor(_siteSlug);
        _gatewaySsh = gatewaySshRegistry.GetFor(_siteSlug);
        _logger = logger;
    }

    /// <summary>
    /// The site's console client, waiting for the connection to come up rather than giving up on
    /// it. Every call here can land while the console is still connecting - the wizard builds its
    /// preview as the page loads - and a null client is silently indistinguishable from a console
    /// that answered with nothing: no channels read, no options, no log line.
    /// </summary>
    private async Task<UniFiApiClient?> ConnectedClientAsync(CancellationToken cancellationToken)
    {
        var client = _connection.Client;
        if (client != null) return client;

        await _connection.WaitForConnectionAsync(ConnectWait);
        client = _connection.Client;
        if (client == null)
            _logger.LogWarning("The UniFi Console for site {Site} is still not connected after {Wait}", _siteSlug, ConnectWait);

        return client;
    }

    /// <inheritdoc />
    public bool UsesApiKey => _connection.Client?.UseApiKey == true;

    /// <summary>How long a console command waits for a connection that is still coming up.</summary>
    private static readonly TimeSpan ConnectWait = TimeSpan.FromSeconds(20);

    /// <inheritdoc />
    public async Task<FirmwareCommandResult> TriggerUpgradeAsync(string deviceMac, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(deviceMac))
            return FirmwareCommandResult.Failed("No device MAC to command.");

        var client = await ConnectedClientAsync(cancellationToken);
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

        var client = await ConnectedClientAsync(cancellationToken);
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

    /// <summary>
    /// Whether a firmware URL is safe to put on a device's command line: absolute http(s), and
    /// free of whitespace or a single quote, which is all that could escape the quoting around it.
    /// The URL comes from the console, and the app trusts the console's certificate by default, so
    /// this is what stops a substituted href from carrying a second command to the gateway.
    /// </summary>
    internal static bool IsSafeFirmwareUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var parsed)
        && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps)
        && !url.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c == '\'');

    /// <inheritdoc />
    public async Task<FirmwareCommandResult> TriggerSshUpgradeAsync(
        string host, string firmwareUrl, bool isGateway, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(host))
            return FirmwareCommandResult.Failed("No device address for the SSH upgrade path.");
        if (string.IsNullOrWhiteSpace(firmwareUrl))
            return FirmwareCommandResult.Failed("No firmware image URL for this device.");

        if (!IsSafeFirmwareUrl(firmwareUrl))
            return FirmwareCommandResult.Failed("The firmware image URL is not a usable http(s) URL.");

        try
        {
            // UniFi OS gateways have no `upgrade` shell command; theirs is ubnt-systool.
            var command = isGateway ? $"ubnt-systool fwupdate '{firmwareUrl}'" : $"upgrade '{firmwareUrl}'";
            var (success, output) = await _ssh.RunCommandAsync(host, command, null, TimeSpan.FromMinutes(5), cancellationToken);
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
        var client = await ConnectedClientAsync(cancellationToken);
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
    public async Task<bool> TriggerDeviceFirmwareCheckAsync(CancellationToken cancellationToken = default)
    {
        var client = await ConnectedClientAsync(cancellationToken);
        if (client == null) return false;

        try
        {
            return await client.TriggerDeviceFirmwareCheckAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Triggering the device firmware check failed for site {Site}", _siteSlug);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> CheckForApplicationUpdatesAsync(CancellationToken cancellationToken = default)
    {
        var client = await ConnectedClientAsync(cancellationToken);
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
        var client = await ConnectedClientAsync(cancellationToken);
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
        var client = await ConnectedClientAsync(cancellationToken);
        if (client == null) return availability;

        try
        {
            var settings = await client.GetFirmwareUpdateSettingsAsync(cancellationToken);
            if (settings != null)
            {
                availability.CurrentDeviceChannel = settings.FirmwareChannel;
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

        var osByChannel = console?.Firmware?.LatestByChannel;
        var osByChannelSummary = osByChannel is { Count: > 0 }
            ? string.Join(", ", osByChannel.Select(kv => $"{kv.Key}={kv.Value?.Version ?? "?"}"))
            : "none";

        _logger.LogInformation(
            "Channels on site {Site}: devices={Device} (offers {DeviceOptions}), network={App} ({AppVersion}), os={Os}, osByChannel=[{OsByChannel}]",
            _siteSlug,
            availability.CurrentDeviceChannel ?? "unknown",
            availability.AvailableDeviceChannels.Count > 0
                ? string.Join("/", availability.AvailableDeviceChannels)
                : "unreadable",
            availability.CurrentNetworkAppChannel ?? "unknown",
            availability.CurrentNetworkAppVersion ?? "unknown",
            availability.CurrentUniFiOsChannel ?? "unknown",
            osByChannelSummary);

        return availability;
    }

    /// <inheritdoc />
    public async Task<bool?> GetAutoUpgradeEnabledAsync(CancellationToken cancellationToken = default)
    {
        var client = await ConnectedClientAsync(cancellationToken);
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
        var client = await ConnectedClientAsync(cancellationToken);
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

        var client = await ConnectedClientAsync(cancellationToken);
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
        var client = await ConnectedClientAsync(cancellationToken);
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
        var client = await ConnectedClientAsync(cancellationToken);
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
            return FirmwareCommandResult.Failed("the Console did not answer the backup request");

        if (result.Success)
            return FirmwareCommandResult.Ok();

        var failed = result.Controllers.Concat(result.Services)
            .Where(c => !c.Value.Success)
            .Select(c => c.Key)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return FirmwareCommandResult.Failed(failed.Count == 0
            ? "the Console reported the backup as unsuccessful"
            : $"the Console could not back up {string.Join(", ", failed)}");
    }

    /// <inheritdoc />
    public async Task<bool> TriggerNetworkApplicationUpdateAsync(CancellationToken cancellationToken = default)
    {
        var client = await ConnectedClientAsync(cancellationToken);
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
        var client = await ConnectedClientAsync(cancellationToken);
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
        var client = await ConnectedClientAsync(cancellationToken);
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

    /// <inheritdoc />
    public async Task<FirmwareCommandResult> TriggerSshNetworkAppUpdateAsync(
        string debUrl, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(debUrl))
            return FirmwareCommandResult.Failed("No .deb URL for the SSH Network app update.");

        try
        {
            var command = $"curl -fsSo /tmp/unifi-update.deb '{debUrl}' && apt-get install -y /tmp/unifi-update.deb && rm -f /tmp/unifi-update.deb";
            var (success, output) = await _gatewaySsh.RunCommandAsync(command, timeout: TimeSpan.FromMinutes(5), cancellationToken: cancellationToken);
            if (success)
                return FirmwareCommandResult.Ok(output);

            return FirmwareCommandResult.Failed(
                string.IsNullOrWhiteSpace(output) ? "The SSH Network app update failed." : output.Trim());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "SSH Network app update for site {Site} threw", _siteSlug);
            return FirmwareCommandResult.Failed($"The SSH Network app update failed: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<FirmwareCommandResult> TriggerSshUniFiOsUpdateAsync(
        string firmwareUrl, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(firmwareUrl))
            return FirmwareCommandResult.Failed("No firmware URL for the SSH UniFi OS update.");
        if (!IsSafeFirmwareUrl(firmwareUrl))
            return FirmwareCommandResult.Failed("The firmware image URL is not a usable http(s) URL.");

        try
        {
            var (success, output) = await _gatewaySsh.RunCommandAsync(
                $"ubnt-systool fwupdate '{firmwareUrl}'", timeout: TimeSpan.FromMinutes(5), cancellationToken: cancellationToken);
            if (success)
                return FirmwareCommandResult.Ok(output);

            return FirmwareCommandResult.Failed(
                string.IsNullOrWhiteSpace(output) ? "The SSH UniFi OS update failed." : output.Trim());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "SSH UniFi OS update for site {Site} threw", _siteSlug);
            return FirmwareCommandResult.Failed($"The SSH UniFi OS update failed: {ex.Message}");
        }
    }
}
