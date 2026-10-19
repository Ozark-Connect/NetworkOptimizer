using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.UniFi.Models;
using NetworkOptimizer.Web.Services.Ssh;

namespace NetworkOptimizer.Web.Services.Firmware;

/// <summary>
/// The real <see cref="IFirmwareCommandClient"/>: the site's UniFi console for API commands,
/// the site's device SSH service for a standalone gateway's direct upgrade, the SSH router for
/// every other device's, and the gateway SSH service for console-level SSH fallbacks. All are
/// already tunnel-routed, so an agent-connected site needs nothing extra here.
/// </summary>
public class FirmwareCommandClient : IFirmwareCommandClient
{
    private readonly UniFiConnectionService _connection;
    private readonly IUniFiSshService _ssh;
    private readonly IGatewaySshService _gatewaySsh;
    private readonly DeviceSshRouter _router;
    private readonly ILogger<FirmwareCommandClient> _logger;
    private readonly string _siteSlug;

    /// <param name="siteConnections">Per-site console connections.</param>
    /// <param name="sshRegistry">Per-site device SSH services.</param>
    /// <param name="gatewaySshRegistry">Per-site gateway SSH services.</param>
    /// <param name="sshRouters">Per-site SSH routers, for every non-gateway SSH upgrade.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="siteSlug">Site this client commands.</param>
    public FirmwareCommandClient(
        SiteConnectionRegistry siteConnections,
        UniFiSshRegistry sshRegistry,
        GatewaySshRegistry gatewaySshRegistry,
        DeviceSshRouterRegistry sshRouters,
        ILogger<FirmwareCommandClient> logger,
        string siteSlug = SiteManagementService.DefaultSiteSlug)
    {
        _siteSlug = string.IsNullOrEmpty(siteSlug) ? SiteManagementService.DefaultSiteSlug : siteSlug;
        _connection = siteConnections.GetFor(_siteSlug);
        _ssh = sshRegistry.GetFor(_siteSlug);
        _gatewaySsh = gatewaySshRegistry.GetFor(_siteSlug);
        _router = sshRouters.GetFor(_siteSlug);
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

    /// <inheritdoc />
    public async Task<FirmwareCommandResult> TriggerSshUpgradeAsync(
        string deviceMac, string host, string firmwareUrl, DeviceType role, bool isGateway,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(host))
            return FirmwareCommandResult.Failed("No device address for the SSH upgrade path.");
        if (string.IsNullOrWhiteSpace(firmwareUrl))
            return FirmwareCommandResult.Failed("No firmware image URL for this device.");

        if (!NetworkOptimizer.Core.Helpers.UrlSafety.IsSafeHttpUrl(firmwareUrl))
            return FirmwareCommandResult.Failed("The firmware image URL is not a usable http(s) URL.");

        try
        {
            // UniFi OS gateways have no `upgrade` shell command; theirs is ubnt-systool. On an AP
            // `upgrade` is only an /etc/profile alias for `mca-cli-op upgrade`, which a
            // non-interactive SSH command never loads (exit 127), so the target is called directly.
            var command = isGateway
                ? $"ubnt-systool fwupdate '{firmwareUrl}'"
                : $"if command -v mca-cli-op >/dev/null 2>&1; then mca-cli-op upgrade '{firmwareUrl}'; else upgrade '{firmwareUrl}'; fi";
            var timeout = TimeSpan.FromMinutes(5);
            // A gateway step is a standalone UXG / USG (a Cloud Gateway never becomes a device step):
            // an adopted device, so Device SSH is its login. Never route it through Gateway SSH,
            // which is optional and may not be set up.
            var (success, output) = role == DeviceType.Gateway
                ? await _ssh.RunCommandAsync(host, command, null, timeout, cancellationToken)
                : await _router.RunAsync(
                    // Off a non-gateway step, isGateway marks an Express (UX, UX7) adopted as an AP: gateway
                    // hardware. Passed rather than looked up, since the console may be mid-update.
                    new DeviceSshTarget(deviceMac, host, role, isGateway ? DeviceType.Gateway : role),
                    command, timeout, cancellationToken);
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
            // A channel the console holds a build for is offered, whether or not channels lists it.
            availability.AvailableUniFiOsChannels = (console.Firmware?.Channels ?? [])
                .Concat(console.Firmware?.LatestByChannel?.Keys ?? Enumerable.Empty<string>())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
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
            "Channels on site {Site}: devices={Device} (offers {DeviceOptions}), network={App} ({AppVersion}, offers {AppOptions}), os={Os} (offers {OsOptions}), osByChannel=[{OsByChannel}]",
            _siteSlug,
            availability.CurrentDeviceChannel ?? "unknown",
            OptionsSummary(availability.AvailableDeviceChannels),
            availability.CurrentNetworkAppChannel ?? "unknown",
            availability.CurrentNetworkAppVersion ?? "unknown",
            OptionsSummary(availability.AvailableNetworkAppChannels),
            availability.CurrentUniFiOsChannel ?? "unknown",
            OptionsSummary(availability.AvailableUniFiOsChannels),
            osByChannelSummary);

        return availability;
    }

    private static string OptionsSummary(List<string> options) =>
        options.Count > 0 ? string.Join("/", options) : "unreadable";

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
    public async Task<bool> DisableDeviceAutoUpgradeAsync(CancellationToken cancellationToken = default)
    {
        var client = await ConnectedClientAsync(cancellationToken);
        if (client == null) return false;

        try
        {
            return await client.SetDeviceAutoUpgradeAsync(false, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Turning off device auto-upgrade failed for site {Site}", _siteSlug);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> DisableConsoleAutoUpdateAsync(string scheduleKey, CancellationToken cancellationToken = default)
    {
        var client = await ConnectedClientAsync(cancellationToken);
        if (client == null) return false;

        try
        {
            return await client.DisableConsoleAutoUpdateAsync(scheduleKey, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Turning off the {Schedule} auto-update failed for site {Site}", scheduleKey, _siteSlug);
            return false;
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
        if (client == null)
        {
            _logger.LogWarning(
                "Cannot set the console update channels on site {Site} (Network application: {App}, UniFi OS: {Os}): the UniFi Console is not connected",
                _siteSlug, networkAppChannel ?? "unchanged", unifiOsChannel ?? "unchanged");
            return false;
        }

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
        string debUrl, IReadOnlyList<string>? companionUrls = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(debUrl))
            return FirmwareCommandResult.Failed("No .deb URL for the SSH Network app update.");
        if (!IsPlainPackageUrl(debUrl))
            return FirmwareCommandResult.Failed("The Network package URL is not a usable https link.");

        try
        {
            // Detached: the download and install take minutes, and nothing should wait on them (the
            // wizard's Start Now did). The rollout's own step watches the version, and reads the log
            // for the reason when the script ends on a failure.
            var script = NetworkOptimizer.Core.Helpers.GatewayFile.ToBase64(NetworkInstallCommand(debUrl, companionUrls));
            var command = $"echo {script} | base64 -d > {NetworkInstallScript} && "
                + $"nohup sh {NetworkInstallScript} > {NetworkInstallLog} 2>&1 < /dev/null & echo started";
            var (success, output) = await _gatewaySsh.RunCommandAsync(command, timeout: TimeSpan.FromSeconds(60), cancellationToken: cancellationToken);
            if (success && output.Contains("started", StringComparison.Ordinal))
                return FirmwareCommandResult.Ok(output);

            return FirmwareCommandResult.Failed(
                string.IsNullOrWhiteSpace(output) ? "The SSH Network app update could not be started." : output.Trim());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "SSH Network app update for site {Site} threw", _siteSlug);
            return FirmwareCommandResult.Failed($"The SSH Network app update failed: {ex.Message}");
        }
    }

    /// <summary>
    /// The Network install, with the companions it was released with. Network 11.x depends on exact
    /// versions of packages that ship beside it (unifi-matter-controller 0.0.9 for 11.0.81), which a
    /// console installs in the same batch; apt cannot fetch them on its own. Only the companions the
    /// package's own Depends names are fetched, so another app updated in that batch never is.
    /// </summary>
    internal static string NetworkInstallCommand(string debUrl, IReadOnlyList<string>? companionUrls)
    {
        const string dir = "/tmp/netopt-network";
        const string fail = "{ echo \"NETOPT_EXIT 1\"; exit 1; }";
        var command = new System.Text.StringBuilder();
        command.Append($"rm -rf {dir}; mkdir -p {dir} && curl -fsSo {dir}/unifi.deb '{debUrl}' || {fail}; ");

        var companions = (companionUrls ?? [])
            .Select(u => (Url: u, Name: CompanionName(u)))
            .Where(c => c.Name != null && IsPlainPackageUrl(c.Url))
            .ToList();
        if (companions.Count > 0)
        {
            command.Append($"deps=$(dpkg-deb -f {dir}/unifi.deb Depends); ");
            foreach (var (url, name) in companions)
                command.Append($"echo \"$deps\" | grep -qE '(^|[ ,]){name}( |,|$)' && {{ curl -fsSo {dir}/{name}.deb '{url}' || {fail}; }}; ");
        }

        command.Append($"apt-get install -y {dir}/*.deb; rc=$?; rm -rf {dir}; echo \"NETOPT_EXIT $rc\"; exit $rc");
        return command.ToString();
    }

    private const string NetworkInstallScript = "/tmp/netopt-network.sh";
    private const string NetworkInstallLog = "/tmp/netopt-network.log";

    /// <inheritdoc />
    public async Task<NetworkInstallLog?> ReadSshNetworkInstallLogAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var (success, output) = await _gatewaySsh.RunCommandAsync(
                $"tail -c 4000 {NetworkInstallLog} 2>/dev/null", timeout: TimeSpan.FromSeconds(30), cancellationToken: cancellationToken);
            return success ? ParseNetworkInstallLog(output) : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not read the Network install log on site {Site}", _siteSlug);
            return null;
        }
    }

    /// <summary>The script's exit code, once it has written one, and the output before it.</summary>
    internal static NetworkInstallLog ParseNetworkInstallLog(string? output)
    {
        var text = output ?? string.Empty;
        var match = System.Text.RegularExpressions.Regex.Match(text, @"NETOPT_EXIT (\d+)");
        return new NetworkInstallLog(match.Success ? int.Parse(match.Groups[1].Value) : null, text[..(match.Success ? match.Index : text.Length)]);
    }

    /// <summary>The package name a console package URL is filed under (<c>/data/&lt;name&gt;/...</c>).</summary>
    private static string? CompanionName(string url)
    {
        var match = System.Text.RegularExpressions.Regex.Match(url, @"^https://fw-download\.ubnt\.com/data/([a-z0-9-]+)/[^/]+\.deb$");
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>An https package URL with nothing a shell could read as anything but the URL.</summary>
    private static bool IsPlainPackageUrl(string url) =>
        System.Text.RegularExpressions.Regex.IsMatch(url, @"^https://[A-Za-z0-9.-]+/[A-Za-z0-9._~/+-]+\.deb$");

    private const string OsInstallScript = "/tmp/netopt-unifi-os.sh";
    private const string OsInstallLog = "/tmp/netopt-unifi-os.log";

    /// <inheritdoc />
    public async Task<FirmwareCommandResult> TriggerSshUniFiOsUpdateAsync(
        string firmwareUrl, string attemptId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(firmwareUrl))
            return FirmwareCommandResult.Failed("No firmware URL for the SSH UniFi OS update.");
        if (!NetworkOptimizer.Core.Helpers.UrlSafety.IsSafeHttpUrl(firmwareUrl))
            return FirmwareCommandResult.Failed("The firmware image URL is not a usable http(s) URL.");
        if (string.IsNullOrWhiteSpace(attemptId) || !attemptId.All(char.IsAsciiLetterOrDigit))
            return FirmwareCommandResult.Failed("The install attempt id must be alphanumeric.");

        try
        {
            // Detached, as the Network install is: the download and write take minutes, and the
            // writer must outlive the SSH session that started it. The start line is what tells a
            // later read that this attempt reached the gateway when the session itself dropped.
            var script = NetworkOptimizer.Core.Helpers.GatewayFile.ToBase64(
                $"echo \"NETOPT_START {attemptId}\"; ubnt-systool fwupdate '{firmwareUrl}'; echo \"NETOPT_EXIT $?\"");
            var command = $"echo {script} | base64 -d > {OsInstallScript} && "
                + $"nohup sh {OsInstallScript} > {OsInstallLog} 2>&1 < /dev/null & echo started";
            var (success, output) = await _gatewaySsh.RunCommandAsync(command, timeout: TimeSpan.FromSeconds(60), cancellationToken: cancellationToken);
            if (success && output.Contains("started", StringComparison.Ordinal))
                return FirmwareCommandResult.Ok(output);

            return FirmwareCommandResult.Failed(
                string.IsNullOrWhiteSpace(output) ? "The SSH UniFi OS update could not be started." : output.Trim());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "SSH UniFi OS update for site {Site} threw", _siteSlug);
            return FirmwareCommandResult.Failed($"The SSH UniFi OS update failed: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<UniFiOsInstallLog?> ReadSshUniFiOsInstallLogAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var (success, output) = await _gatewaySsh.RunCommandAsync(
                $"tail -c 4000 {OsInstallLog} 2>/dev/null", timeout: TimeSpan.FromSeconds(30), cancellationToken: cancellationToken);
            return success ? ParseUniFiOsInstallLog(output) : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not read the UniFi OS install log on site {Site}", _siteSlug);
            return null;
        }
    }

    /// <summary>The attempt that wrote the log, its exit code once it has one, and the output between.</summary>
    internal static UniFiOsInstallLog ParseUniFiOsInstallLog(string? output)
    {
        var text = output ?? string.Empty;
        var start = System.Text.RegularExpressions.Regex.Match(text, @"NETOPT_START (\w+)");
        var exit = System.Text.RegularExpressions.Regex.Match(text, @"NETOPT_EXIT (\d+)");
        var from = start.Success ? start.Index + start.Length : 0;
        var to = exit.Success ? exit.Index : text.Length;
        return new UniFiOsInstallLog(
            start.Success ? start.Groups[1].Value : null,
            exit.Success ? int.Parse(exit.Groups[1].Value) : null,
            text[from..Math.Max(from, to)].Trim());
    }

    /// <inheritdoc />
    public async Task<bool> HasGatewaySshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var settings = await _gatewaySsh.GetSettingsAsync();
            return settings != null && settings.Enabled && settings.HasCredentials && !string.IsNullOrEmpty(settings.Host);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not read gateway SSH settings for site {Site}", _siteSlug);
            return false;
        }
    }

    // UniFi OS answers an unregistered /proxy path with its login page (200 text/html), and answers
    // a registered one itself (401 JSON) even while Network is stopped. So the route says only
    // whether UniFi OS knows Network; Network's own port says whether it is up.
    private const string NetworkRouteCommand =
        "echo \"NETOPT_STATUS $(curl -s -m 5 http://127.0.0.1:8081/status 2>/dev/null | tr -d '\\r\\n')\"; "
        + "echo \"NETOPT_ROUTE $(curl -sk -m 5 -o /dev/null -w '%{content_type}' https://127.0.0.1/proxy/network/status 2>/dev/null)\"";

    /// <inheritdoc />
    public async Task<NetworkRouteState?> ProbeNetworkRouteAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var (success, output) = await _gatewaySsh.RunCommandAsync(
                NetworkRouteCommand, timeout: TimeSpan.FromSeconds(30), cancellationToken: cancellationToken);
            return success ? ParseNetworkRoute(output) : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not read the UniFi Network route on site {Site}", _siteSlug);
            return null;
        }
    }

    /// <summary>
    /// Unrouted only on both signals together: Network says it is running, and UniFi OS serves its
    /// login page for the route. Anything unrecognized reads as not ready, which acts on nothing.
    /// </summary>
    internal static NetworkRouteState ParseNetworkRoute(string? output)
    {
        string? status = null;
        string? route = null;
        foreach (var raw in (output ?? string.Empty).Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("NETOPT_STATUS ", StringComparison.Ordinal))
                status = line["NETOPT_STATUS ".Length..];
            else if (line.StartsWith("NETOPT_ROUTE ", StringComparison.Ordinal))
                route = line["NETOPT_ROUTE ".Length..].Trim();
        }

        UniFiNetworkStatus? parsed = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(status))
                parsed = System.Text.Json.JsonSerializer.Deserialize<UniFiNetworkStatus>(status);
        }
        catch (System.Text.Json.JsonException)
        {
            // A truncated or non-JSON answer reads as not ready below.
        }

        if (parsed?.IsRunning != true)
            return NetworkRouteState.NotReady;
        if (route != null && route.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
            return NetworkRouteState.Routed;
        if (route != null && route.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
            return NetworkRouteState.Unrouted;
        return NetworkRouteState.NotReady;
    }

    /// <inheritdoc />
    public async Task<FirmwareCommandResult> RestartNetworkApplicationAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var (success, output) = await _gatewaySsh.RunCommandAsync(
                "systemctl restart --no-block unifi && echo restarting", timeout: TimeSpan.FromSeconds(30), cancellationToken: cancellationToken);
            return success && output.Contains("restarting", StringComparison.Ordinal)
                ? FirmwareCommandResult.Ok(output)
                : FirmwareCommandResult.Failed(string.IsNullOrWhiteSpace(output) ? "UniFi Network could not be restarted over SSH." : output.Trim());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "SSH restart of UniFi Network on site {Site} threw", _siteSlug);
            return FirmwareCommandResult.Failed($"The SSH restart of UniFi Network failed: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<FirmwareCommandResult> RebootConsoleAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // Backgrounded with a short delay, as the health check reboot remedy is, so the session
            // gets its answer back before the console goes away.
            var (success, output) = await _gatewaySsh.RunCommandAsync(
                "( sleep 2; reboot ) >/dev/null 2>&1 & echo rebooting", timeout: TimeSpan.FromSeconds(30), cancellationToken: cancellationToken);
            return success && output.Contains("rebooting", StringComparison.Ordinal)
                ? FirmwareCommandResult.Ok(output)
                : FirmwareCommandResult.Failed(string.IsNullOrWhiteSpace(output) ? "The Console could not be rebooted over SSH." : output.Trim());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "SSH reboot of the console on site {Site} threw", _siteSlug);
            return FirmwareCommandResult.Failed($"The SSH reboot of the Console failed: {ex.Message}");
        }
    }

    /// <summary>One read per site per window: every preview and Deploy Known Firmware asks for it.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime At, ConsoleNetworkPackages Read)> NetworkPackageReads =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly TimeSpan NetworkPackageReadWindow = TimeSpan.FromMinutes(10);

    // uos.log records each download as "Downloading runnable package_name=<name> url=<url>", and each
    // install as "Install downloaded packages package_paths={".../<name>_<file>.deb", ...}": the batch
    // is what says which packages a Network build was installed with.
    private const string NetworkPackagesCommand =
        ". /etc/os-release; echo \"PLATFORM uos-deb${VERSION_ID%%.*}-$(dpkg --print-architecture)\"; "
        + "grep -hoE 'url=https://fw-download\\.ubnt\\.com/data/[a-z0-9-]+/[^ ]+\\.deb|package_paths=\\{[^}]*\\}' "
        + "/data/unifi-core/logs/uos.log* 2>/dev/null | tail -400";

    /// <inheritdoc />
    public async Task<ConsoleNetworkPackages?> ReadConsoleNetworkPackagesAsync(bool fresh = false, CancellationToken cancellationToken = default)
    {
        if (!fresh && NetworkPackageReads.TryGetValue(_siteSlug, out var cached) && DateTime.UtcNow - cached.At < NetworkPackageReadWindow)
            return cached.Read;
        if (!await HasGatewaySshAsync(cancellationToken))
            return null;

        try
        {
            var (success, output) = await _gatewaySsh.RunCommandAsync(
                NetworkPackagesCommand, timeout: TimeSpan.FromSeconds(30), cancellationToken: cancellationToken);
            if (!success) return null;

            var read = ParseConsoleNetworkPackages(output);
            NetworkPackageReads[_siteSlug] = (DateTime.UtcNow, read);
            return read;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not read the console's Network packages on site {Site}", _siteSlug);
            return null;
        }
    }

    /// <summary>Parses <see cref="NetworkPackagesCommand"/>'s output.</summary>
    internal static ConsoleNetworkPackages ParseConsoleNetworkPackages(string? output)
    {
        string? platform = null;
        var urlByFile = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var batches = new List<List<string>>();
        foreach (var raw in (output ?? string.Empty).Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("PLATFORM ", StringComparison.Ordinal))
            {
                var value = line["PLATFORM ".Length..].Trim().ToLowerInvariant();
                platform = System.Text.RegularExpressions.Regex.IsMatch(value, @"^uos-deb\d+-(amd64|arm64)$") ? value : null;
            }
            else if (line.StartsWith("url=", StringComparison.Ordinal))
            {
                var url = line["url=".Length..];
                if (NetworkOptimizer.Core.Helpers.UrlSafety.IsSafeHttpUrl(url)) urlByFile[url[(url.LastIndexOf('/') + 1)..]] = url;
            }
            else if (line.StartsWith("package_paths=", StringComparison.Ordinal))
            {
                // Downloaded as "<name>_<file>"; the file is the URL's last segment.
                batches.Add(System.Text.RegularExpressions.Regex.Matches(line, "\"[^\"]*/([a-z0-9-]+)_([^\"/]+\\.deb)\"")
                    .Select(m => m.Groups[2].Value).ToList());
            }
        }

        var downloaded = new List<NetworkOptimizer.Storage.Models.SharedNetworkAppPackage>();
        foreach (var url in urlByFile.Values)
        {
            if (FirmwareUrlParser.NetworkPackage(url) is not { } package) continue;

            var file = url[(url.LastIndexOf('/') + 1)..];
            var companions = batches.LastOrDefault(b => b.Contains(file, StringComparer.OrdinalIgnoreCase))?
                .Where(f => !string.Equals(f, file, StringComparison.OrdinalIgnoreCase) && urlByFile.ContainsKey(f))
                .Select(f => urlByFile[f])
                .ToList();
            downloaded.Add(new NetworkOptimizer.Storage.Models.SharedNetworkAppPackage
            {
                Platform = package.Platform,
                Version = package.Version,
                Url = url,
                CompanionUrlsJson = companions is { Count: > 0 } ? System.Text.Json.JsonSerializer.Serialize(companions) : null,
            });
        }
        return new ConsoleNetworkPackages(platform, downloaded);
    }
}
