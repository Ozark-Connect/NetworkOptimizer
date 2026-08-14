using System.Text.Json;
using System.Text.Json.Serialization;

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
/// Sets and restores the console's firmware channels around a rollout's channel groups.
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

    /// <summary>
    /// Reads the channel state to put back afterwards. Called once per rollout, before the first
    /// change; the caller persists the result before applying anything.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<string> CaptureOriginalAsync(CancellationToken cancellationToken = default)
    {
        var deviceChannel = await _commands.GetDeviceChannelAsync(cancellationToken);
        var console = await _commands.GetConsoleSystemInfoAsync(cancellationToken);
        var original = new OriginalChannelSettings
        {
            DeviceChannel = deviceChannel,
            UniFiOsChannel = console?.Firmware?.ReleaseChannel,
        };
        _logger.LogInformation(
            "Captured the original firmware channels for site {Site}: devices {DeviceChannel}, UniFi OS {OsChannel}",
            _siteSlug, original.DeviceChannel ?? "unknown", original.UniFiOsChannel ?? "unknown");
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

        await _commands.CheckForUpdatesAsync(cancellationToken);
        _logger.LogInformation("Site {Site} is now on the {Channel} device firmware channel", _siteSlug, channel);
        return true;
    }

    /// <summary>
    /// Puts the captured channels back and refreshes the catalog. Runs between channel groups, at
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

        if (!string.IsNullOrWhiteSpace(original.UniFiOsChannel))
            await _commands.SetConsoleChannelsAsync(null, original.UniFiOsChannel, cancellationToken);

        await _commands.CheckForUpdatesAsync(cancellationToken);
        _logger.LogInformation("Restored the original firmware channels on site {Site}", _siteSlug);
        return restored;
    }
}
