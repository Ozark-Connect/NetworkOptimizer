using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.Web.Services.Firmware;

/// <summary>
/// How a firmware command ended. <see cref="NotSupported"/> is distinct from
/// <see cref="Failed"/> on purpose: it means this build cannot issue the call at all, which is a
/// reason to fall through to another path rather than to fail the step.
/// </summary>
public enum FirmwareCommandOutcome
{
    /// <summary>The console or device accepted the command. Acceptance only - never success.</summary>
    Ok,
    /// <summary>The call was made and the far end rejected it or errored.</summary>
    Failed,
    /// <summary>The call is not implemented here yet, so nothing was sent.</summary>
    NotSupported
}

/// <summary>
/// Result of one firmware command.
/// <para>
/// <see cref="FirmwareCommandOutcome.Ok"/> means accepted, NOT upgraded: a live revert returned
/// rc:ok, cycled the AP, and left it on the same version. Every step verifies the reported version
/// after the device is back.
/// </para>
/// </summary>
/// <param name="Outcome">How the command ended.</param>
/// <param name="Message">Detail for the log and the step's Error field.</param>
public sealed record FirmwareCommandResult(FirmwareCommandOutcome Outcome, string? Message = null)
{
    /// <summary>True when the command was accepted.</summary>
    public bool IsOk => Outcome == FirmwareCommandOutcome.Ok;

    /// <summary>Accepted.</summary>
    public static FirmwareCommandResult Ok(string? message = null) => new(FirmwareCommandOutcome.Ok, message);

    /// <summary>Sent and rejected.</summary>
    public static FirmwareCommandResult Failed(string message) => new(FirmwareCommandOutcome.Failed, message);

    /// <summary>Not implemented here, so nothing was sent.</summary>
    public static FirmwareCommandResult NotSupported(string message) => new(FirmwareCommandOutcome.NotSupported, message);
}

/// <summary>
/// The command surface a rollout drives: upgrade triggers on all three paths, the catalog refresh
/// that doubles as UniFi's "Check for Updates", channel reads and writes, and the pre-flight backup.
/// <para>
/// An interface rather than the API client directly so the executor can be driven by a scripted
/// fake: nothing else can exercise a device going down, coming back on the wrong version, and
/// being retried over SSH.
/// </para>
/// </summary>
public interface IFirmwareCommandClient
{
    /// <summary>
    /// Roll a device forward to the version the console has staged for it (cmd/devmgr upgrade).
    /// </summary>
    /// <param name="deviceMac">Colonized device MAC.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<FirmwareCommandResult> TriggerUpgradeAsync(string deviceMac, CancellationToken cancellationToken = default);

    /// <summary>
    /// Move a device to an explicit image (cmd/devmgr upgrade-external). The path a rollback and a
    /// pinned-version step take through the API.
    /// </summary>
    /// <param name="deviceMac">Colonized device MAC.</param>
    /// <param name="firmwareUrl">Direct firmware image URL.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<FirmwareCommandResult> TriggerExternalUpgradeAsync(string deviceMac, string firmwareUrl, CancellationToken cancellationToken = default);

    /// <summary>
    /// Run <c>upgrade &lt;url&gt;</c> on the device over SSH. The escalation path when a console
    /// command is accepted but nothing happens, and the first path for a rollback.
    /// </summary>
    /// <param name="host">Device address.</param>
    /// <param name="firmwareUrl">Direct firmware image URL.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<FirmwareCommandResult> TriggerSshUpgradeAsync(string host, string firmwareUrl, CancellationToken cancellationToken = default);

    /// <summary>
    /// UniFi's "Check for Updates": checks and prepares new firmware, refreshing what the console
    /// reports as upgradable. Run before planning, at rollout start, and after every channel change.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The catalog entries, or an empty list when the console would not answer.</returns>
    Task<IReadOnlyList<UniFiFirmwareCatalogEntry>> CheckForUpdatesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The console-level "check now" for application updates: refreshes what the console is
    /// offering for the UniFi Network application. Run after a channel change, because the offer
    /// on the new channel is only known once the console has looked.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when the console accepted the check.</returns>
    Task<bool> CheckForApplicationUpdatesAsync(CancellationToken cancellationToken = default);

    /// <summary>The release channel UniFi devices currently follow, or null when it cannot be read.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<string?> GetDeviceChannelAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The device channel in force plus the channel options this console offers. The options list is
    /// the early-access check: EA only appears when the console offers it.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<RolloutChannelAvailability> GetChannelAvailabilityAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether UniFi's own nightly auto-upgrade is on, or null when it cannot be read. It races a
    /// rollout, so the wizard warns about it.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<bool?> GetAutoUpgradeEnabledAsync(CancellationToken cancellationToken = default);

    /// <summary>Sets the release channel UniFi devices follow.</summary>
    /// <param name="channel">"release", "release-candidate", or "beta".</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<bool> SetDeviceChannelAsync(string channel, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the console-level UniFi Network application and/or UniFi OS channels. Both are read
    /// back from <see cref="GetConsoleSystemInfoAsync"/>, so both are captured and restored.
    /// </summary>
    /// <param name="networkAppChannel">Network application channel, or null to leave it alone.</param>
    /// <param name="unifiOsChannel">UniFi OS channel, or null to leave it alone.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<bool> SetConsoleChannelsAsync(string? networkAppChannel, string? unifiOsChannel, CancellationToken cancellationToken = default);

    /// <summary>The console's UniFi OS view, including whether it is a standalone console.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<UniFiConsoleSystemInfo?> GetConsoleSystemInfoAsync(CancellationToken cancellationToken = default);

    /// <summary>Triggers the pre-flight console backup.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<FirmwareCommandResult> TriggerBackupAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Refreshes the console's application-update availability and installs the UniFi Network
    /// application update. The application restarts as it installs, so the Network API goes dark
    /// and comes back.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when the console accepted the install; false when there was nothing to do.</returns>
    Task<bool> TriggerNetworkApplicationUpdateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The UniFi OS build the console is offering to install, or null when it is current.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<UniFiConsoleFirmwareRelease?> GetPendingUniFiOsUpdateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Installs the pending UniFi OS build. The whole console goes dark for the cycle, taking the
    /// Network API and any agent tunnel with it.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when the console accepted the install. Acceptance is not success.</returns>
    Task<bool> TriggerUniFiOsUpdateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// SSH fallback: install a UniFi Network application .deb on the gateway via
    /// <c>curl</c> + <c>apt-get install</c>. The gateway host is resolved from the controller URL.
    /// </summary>
    Task<FirmwareCommandResult> TriggerSshNetworkAppUpdateAsync(string debUrl, CancellationToken cancellationToken = default);

    /// <summary>
    /// SSH fallback: install a UniFi OS firmware image on the gateway via
    /// <c>ubnt-systool fwupdate</c>. The gateway host is resolved from the controller URL.
    /// </summary>
    Task<FirmwareCommandResult> TriggerSshUniFiOsUpdateAsync(string firmwareUrl, CancellationToken cancellationToken = default);
}
