using System.Text.Json;
using System.Text.Json.Serialization;
using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.Web.Services.Firmware;

/// <summary>
/// The console channel settings as they were before a rollout touched them, serialized into
/// <c>FirmwareRolloutPlan.OriginalChannelSettingsJson</c>.
/// </summary>
public class OriginalChannelSettings
{
    /// <summary>The release channel UniFi devices followed.</summary>
    [JsonPropertyName("deviceChannel")]
    public string? DeviceChannel { get; set; }

    /// <summary>The UniFi OS channel the console was on.</summary>
    [JsonPropertyName("unifiOsChannel")]
    public string? UniFiOsChannel { get; set; }

    /// <summary>The channel the UniFi Network application was on.</summary>
    [JsonPropertyName("networkAppChannel")]
    public string? NetworkAppChannel { get; set; }

    /// <summary>Reads the stored settings, treating unreadable content as nothing to restore.</summary>
    /// <param name="json">Serialized settings.</param>
    public static OriginalChannelSettings? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<OriginalChannelSettings>(json); }
        catch (JsonException) { return null; }
    }
}

/// <summary>
/// Sets and restores the three firmware channels a rollout touches: the device channel per channel
/// group, and the two console-level ones - the UniFi Network application and UniFi OS - each set
/// once, ahead of their own step. All three are readable, so all three are put back afterwards.
/// <para>
/// Stateless by design: the plan row is the only record of what to put back, so the orchestrator
/// captures the original settings onto the plan and persists them BEFORE any change is made. A
/// crash between the two would otherwise leave a site permanently on a channel it never chose.
/// </para>
/// </summary>
public class RolloutChannelManager
{
    private readonly IFirmwareCommandClient _commands;
    private readonly ILogger<RolloutChannelManager> _logger;
    private readonly string _siteSlug;

    /// <param name="commands">Firmware command surface for this site.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="siteSlug">Site whose console is being changed.</param>
    public RolloutChannelManager(
        IFirmwareCommandClient commands,
        ILogger<RolloutChannelManager> logger,
        string siteSlug = SiteManagementService.DefaultSiteSlug)
    {
        _commands = commands;
        _logger = logger;
        _siteSlug = string.IsNullOrEmpty(siteSlug) ? SiteManagementService.DefaultSiteSlug : siteSlug;
    }

    /// <summary>Whether the console's device channel differs from what this group needs.</summary>
    /// <param name="channel">Channel the group's devices must follow.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<bool> NeedsChangeAsync(string channel, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(channel)) return false;
        var current = await _commands.GetDeviceChannelAsync(cancellationToken);
        if (current == null)
        {
            _logger.LogWarning("Could not read the device firmware channel for site {Site}; leaving it alone", _siteSlug);
            return false;
        }
        return !string.Equals(current, channel, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether the UniFi Network application or UniFi OS is already on a given channel.</summary>
    /// <param name="current">Channel the console reports, or null when it could not be read.</param>
    /// <param name="wanted">Channel the rollout wants.</param>
    public static bool AlreadyOn(string? current, string? wanted) =>
        !string.IsNullOrWhiteSpace(current)
        && !string.IsNullOrWhiteSpace(wanted)
        && string.Equals(current, wanted, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Adds the channels this rollout is about to change to the capture, leaving anything already
    /// recorded alone. Only surfaces the rollout actually sets are recorded: a channel that was
    /// never changed must never be "restored" over what the site chose.
    /// </summary>
    /// <param name="existingJson">The capture so far, or null on the first change.</param>
    /// <param name="device">Capture the device firmware channel.</param>
    /// <param name="networkApp">Capture the UniFi Network application channel.</param>
    /// <param name="unifiOs">Capture the UniFi OS channel.</param>
    /// <param name="console">Console info the caller already read, or null to read it here.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The capture to persist before anything is written.</returns>
    public async Task<string> CaptureAsync(
        string? existingJson,
        bool device = false,
        bool networkApp = false,
        bool unifiOs = false,
        UniFiConsoleSystemInfo? console = null,
        CancellationToken cancellationToken = default)
    {
        var original = OriginalChannelSettings.Parse(existingJson) ?? new OriginalChannelSettings();

        if (device && original.DeviceChannel == null)
            original.DeviceChannel = await _commands.GetDeviceChannelAsync(cancellationToken);

        var wantsConsole = (networkApp && original.NetworkAppChannel == null)
            || (unifiOs && original.UniFiOsChannel == null);
        if (wantsConsole)
        {
            console ??= await _commands.GetConsoleSystemInfoAsync(cancellationToken);
            if (networkApp)
                original.NetworkAppChannel ??= console?.NetworkApplication?.ReleaseChannel;
            if (unifiOs)
                original.UniFiOsChannel ??= console?.Firmware?.ReleaseChannel;
        }

        _logger.LogInformation(
            "Captured the original firmware channels for site {Site}: devices {DeviceChannel}, UniFi Network {AppChannel}, UniFi OS {OsChannel}",
            _siteSlug,
            original.DeviceChannel ?? "not changed",
            original.NetworkAppChannel ?? "not changed",
            original.UniFiOsChannel ?? "not changed");
        return JsonSerializer.Serialize(original);
    }

    /// <summary>
    /// Puts the console on a channel and re-runs the firmware check, which is what makes that
    /// channel's builds appear in the catalog.
    /// </summary>
    /// <param name="channel">Channel to set.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<bool> ApplyAsync(string channel, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(channel)) return false;

        var set = await _commands.SetDeviceChannelAsync(channel, cancellationToken);
        if (!set)
        {
            _logger.LogWarning("Could not set the device firmware channel to {Channel} on site {Site}", channel, _siteSlug);
            return false;
        }

        await _commands.TriggerDeviceFirmwareCheckAsync(cancellationToken);
        await _commands.CheckForUpdatesAsync(cancellationToken);
        _logger.LogInformation("Site {Site} is now on the {Channel} device firmware channel", _siteSlug, channel);
        return true;
    }

    /// <summary>
    /// Puts the UniFi Network application on a channel and re-runs the console's application update
    /// check, without which the console is still reporting what the old channel offered.
    /// </summary>
    /// <param name="channel">Channel to set.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<bool> ApplyNetworkAppChannelAsync(string channel, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(channel)) return false;

        if (!await _commands.SetConsoleChannelsAsync(channel, null, cancellationToken))
        {
            _logger.LogWarning(
                "Could not set the UniFi Network application channel to {Channel} on site {Site}", channel, _siteSlug);
            return false;
        }

        await _commands.CheckForApplicationUpdatesAsync(cancellationToken);
        _logger.LogInformation(
            "The UniFi Network application on site {Site} is now on the {Channel} channel", _siteSlug, channel);
        return true;
    }

    /// <summary>
    /// Puts the console's UniFi OS on a channel. Refused on a self-hosted UniFi OS Server: its
    /// operating system is never ours to update, so its channel is never ours to set either.
    /// </summary>
    /// <param name="channel">Channel to set.</param>
    /// <param name="console">Console info the caller already read, or null to read it here.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<bool> ApplyUniFiOsChannelAsync(
        string channel, UniFiConsoleSystemInfo? console = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(channel)) return false;

        console ??= await _commands.GetConsoleSystemInfoAsync(cancellationToken);
        if (console?.IsStandaloneConsole == true)
        {
            _logger.LogWarning(
                "Leaving the UniFi OS channel alone on site {Site}: the console is a self-hosted UniFi OS Server, which this app never updates",
                _siteSlug);
            return false;
        }

        if (!await _commands.SetConsoleChannelsAsync(null, channel, cancellationToken))
        {
            _logger.LogWarning("Could not set the UniFi OS channel to {Channel} on site {Site}", channel, _siteSlug);
            return false;
        }

        _logger.LogInformation("The console on site {Site} is now on the {Channel} UniFi OS channel", _siteSlug, channel);
        return true;
    }

    /// <summary>
    /// Puts the captured channels back and re-checks against them. Runs between channel groups, at
    /// the end of a rollout, on abort, and on the first pass after a restart that found a leftover
    /// capture.
    /// </summary>
    /// <param name="originalJson">The captured settings.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<bool> RestoreAsync(string? originalJson, CancellationToken cancellationToken = default)
    {
        var original = OriginalChannelSettings.Parse(originalJson);
        if (original == null)
            return false;

        var restored = false;

        if (!string.IsNullOrWhiteSpace(original.DeviceChannel))
        {
            var current = await _commands.GetDeviceChannelAsync(cancellationToken);
            if (!string.Equals(current, original.DeviceChannel, StringComparison.OrdinalIgnoreCase))
            {
                restored = await _commands.SetDeviceChannelAsync(original.DeviceChannel, cancellationToken);
                if (!restored)
                {
                    _logger.LogError(
                        "Could not restore the device firmware channel to {Channel} on site {Site}",
                        original.DeviceChannel, _siteSlug);
                }
            }
        }

        // Both console channels go back in one PATCH, and only the ones the capture holds: the
        // capture records a surface exactly when this rollout set it.
        if (!string.IsNullOrWhiteSpace(original.NetworkAppChannel) || !string.IsNullOrWhiteSpace(original.UniFiOsChannel))
        {
            restored |= await _commands.SetConsoleChannelsAsync(
                original.NetworkAppChannel, original.UniFiOsChannel, cancellationToken);
        }

        // Re-derive against the restored channel, so the console describes the site's own choice
        // rather than the one this rollout ran on.
        await _commands.TriggerDeviceFirmwareCheckAsync(cancellationToken);
        await _commands.CheckForUpdatesAsync(cancellationToken);
        _logger.LogInformation("Restored the original firmware channels on site {Site}", _siteSlug);
        return restored;
    }
}
