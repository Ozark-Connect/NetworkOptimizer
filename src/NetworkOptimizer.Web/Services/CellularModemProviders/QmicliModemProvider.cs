using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using NetworkOptimizer.Monitoring;
using NetworkOptimizer.Monitoring.Models;
using NetworkOptimizer.Monitoring.Providers;
using NetworkOptimizer.Web.Services.Ssh;

namespace NetworkOptimizer.Web.Services.CellularModemProviders;

/// <summary>
/// Cellular modem provider for Ubiquiti modems (U-LTE, U5G-Max, U5G Backup, ...).
/// Tries uiwwand first (available on all modern UniFi modems and the UDR-5G-Max),
/// then falls back to raw qmicli commands for older firmware.
/// SSH transport uses the shared UniFiSshService, or the Gateway SSH service for a gateway
/// with a built-in modem (<see cref="ForGateway"/>).
/// </summary>
public sealed class QmicliModemProvider : ICellularModemProvider, ISupportsRadioReset
{
    private const string DefaultQmiDevice = "/dev/wwan0qmi0";

    /// <summary>
    /// The UDR-5G-Max's modem: QMI over MBIM on an MHI pipe, the device UniFi OS's own modem
    /// scripts use (/etc/mbbcfg/functions.sh, MBIM_IFACE).
    /// </summary>
    internal const string GatewayQmiDevice = "/dev/mhi_0308_02.01.00_pipe_12";

    /// <summary>
    /// The QMI device to command, defaulting when the configuration leaves it blank. The value
    /// is interpolated into a root shell command, so it may only look like what qmicli -d takes:
    /// a /dev node (LTE: /dev/cdc-wdm0, 5G-Max: /dev/wwan0qmi0, UDR-5G-Max: an MHI pipe) or a
    /// QRTR node (U5G-Backup: qrtr://3) - same guard the GL transport puts on its own bus and device values.
    /// </summary>
    internal static string QmiDevice(ModemPollContext context, string defaultDevice = DefaultQmiDevice)
    {
        var path = string.IsNullOrWhiteSpace(context.TransportPath)
            ? defaultDevice
            : context.TransportPath.Trim();

        return Regex.IsMatch(path, @"^(/dev/(?!.*\.\.)[A-Za-z0-9_.-]+|qrtr://[0-9]+)$")
            ? path
            : throw new ArgumentException($"Invalid QMI device path: {path}");
    }

    /// <summary>
    /// How a UniFi device is asked for its modem data. A UniFi modem (OpenWrt) exposes uiwwand on ubus
    /// and its QMI device through qmi-proxy. UniFi OS on a gateway has no ubus: uiwwand answers on its
    /// socket through uiwwand-ctl, and the modem's QMI device is reached over MBIM.
    /// </summary>
    internal sealed record Dialect(Func<string, string> Uiwwand, string QmiOpen, string DefaultDevice)
    {
        public static readonly Dialect Modem = new(
            method => $"ubus call uiwwand call '{{\"method\":\"{method}\",\"params\":{{}}}}'",
            "--device-open-proxy",
            DefaultQmiDevice);

        public static readonly Dialect Gateway = new(
            method => $"echo '{{\"method\":\"{method}\"}}' | uiwwand-ctl",
            "--device-open-proxy --device-open-mbim",
            GatewayQmiDevice);
    }

    /// <inheritdoc/>
    public string ProviderKey { get; }

    /// <inheritdoc/>
    public string DisplayName { get; }

    private readonly ILogger<QmicliModemProvider> _logger;
    private readonly IModemShell _shell;
    private readonly Dialect _dialect;

    /// <summary>
    /// Which module is fitted. Read once per modem: an EM9291 does not become something else.
    /// Its FIRMWARE is deliberately not cached - see <see cref="ModuleCommands"/>.
    /// </summary>
    private readonly ConcurrentDictionary<string, ModuleIdentity> _modules = new();

    /// <summary>Also holds where it was read, so repointing a config at another device re-reads it.</summary>
    private sealed record ModuleIdentity(string? Vendor, string? Model, string Host, string TransportPath);

    // Created per site by ModemMonitorRegistry with that site's device SSH
    // service, so qmicli commands reach the site's modem host (tunnel-routed
    // when the site's devices are reached via agent).
    public QmicliModemProvider(
        ILogger<QmicliModemProvider> logger,
        UniFiSshService sshService)
        : this(logger, ModemProviderKeys.Qmicli, "Ubiquiti modem (SSH)", new DeviceShell(sshService), Dialect.Modem)
    {
    }

    private QmicliModemProvider(
        ILogger<QmicliModemProvider> logger, string providerKey, string displayName, IModemShell shell, Dialect dialect)
    {
        _logger = logger;
        ProviderKey = providerKey;
        DisplayName = displayName;
        _shell = shell;
        _dialect = dialect;
    }

    /// <summary>
    /// The provider for a gateway whose modem is built in (UDR-5G-Max). Same queries and parsing, asked
    /// in UniFi OS's dialect (<see cref="Dialect.Gateway"/>), and every command runs on the site's
    /// Gateway SSH host with its credentials, so the modem row's host is not dialed.
    /// </summary>
    public static QmicliModemProvider ForGateway(ILogger<QmicliModemProvider> logger, IGatewaySshService gatewaySsh) =>
        new(logger, ModemProviderKeys.UniFiGateway, "UniFi UDR-5G-Max (Gateway SSH)", new GatewayShell(gatewaySsh), Dialect.Gateway);

    /// <summary>Where the provider's commands run: device SSH to the modem host, or gateway SSH.</summary>
    private interface IModemShell
    {
        Task<(bool success, string output)> RunCommandAsync(string host, string command);
        Task<(bool success, string message)> TestConnectionAsync(string host);
    }

    private sealed class DeviceShell(UniFiSshService ssh) : IModemShell
    {
        public Task<(bool success, string output)> RunCommandAsync(string host, string command) =>
            ssh.RunCommandAsync(host, command);

        public Task<(bool success, string message)> TestConnectionAsync(string host) =>
            ssh.TestConnectionAsync(host);
    }

    private sealed class GatewayShell(IGatewaySshService ssh) : IModemShell
    {
        public Task<(bool success, string output)> RunCommandAsync(string host, string command) =>
            ssh.RunCommandAsync(command);

        public Task<(bool success, string message)> TestConnectionAsync(string host) =>
            ssh.TestConnectionAsync();
    }

    /// <inheritdoc/>
    public async Task<PollResult<CellularModemStats>> PollAsync(
        ModemPollContext context,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Polling modem {Name} at {Host}", context.Name, context.Host);

        // Try uiwwand first - available on all modern UniFi cellular modems
        var stats = await TryPollViaUiwwandAsync(context);
        var result = stats != null
            ? PollResult<CellularModemStats>.Ok(stats)
            : await PollViaQmicliAsync(context);

        // Covers a module swapped behind the same configuration. The firmware version is not
        // cached at all, so it needs no eviction.
        if (!result.Success)
            _modules.TryRemove(context.CacheKey, out _);

        return result;
    }

    /// <summary>
    /// Poll via UniFi's uiwwand daemon. Returns null if uiwwand is not available
    /// on this device, allowing fallback to qmicli.
    /// </summary>
    private async Task<CellularModemStats?> TryPollViaUiwwandAsync(ModemPollContext context)
    {
        try
        {
            var command = _dialect.Uiwwand("get-radio-status");
            var (success, output) = await _shell.RunCommandAsync(context.Host, command);

            if (!success || string.IsNullOrWhiteSpace(output))
            {
                _logger.LogDebug("uiwwand not available on {Name}, falling back to qmicli", context.Name);
                return null;
            }

            // ubus returns "not found" when the service doesn't exist (uiwwand-ctl prints "socket
            // error"), or a JSON object without "result" when the method is unknown
            if (output.Contains("not found") || !output.Contains("\"result\""))
            {
                _logger.LogDebug("uiwwand not available on {Name}, falling back to qmicli", context.Name);
                return null;
            }

            var stats = UiwwandParser.Parse(output, context.Host, context.Name, context.ModemType);

            if (stats != null && stats.Lte == null && stats.Nr5g == null)
            {
                _logger.LogDebug("uiwwand returned no signal data for {Name}, trying qmicli", context.Name);
                return null;
            }

            if (stats != null)
            {
                await TryEnrichWithCellTowerInfoAsync(context, stats);

                _logger.LogDebug(
                    "Successfully polled modem {Name} via uiwwand: {Carrier}, Signal Quality: {Quality}%",
                    context.Name, stats.Carrier, stats.SignalQuality);
            }

            return stats;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "uiwwand poll failed for {Name}, falling back to qmicli", context.Name);
            return null;
        }
    }

    /// <summary>
    /// Add the cell tower detail and EN-DC flags that get-radio-status does not report:
    /// timing advance, tracking area, and neighbors from uiwwand's get-cell-tower-info,
    /// and the 5G NSA availability pair from qmicli's system info, which uiwwand exposes
    /// on no method at all. Best effort - the signal data is already in hand, so a failure
    /// here just leaves these unset.
    /// </summary>
    private async Task TryEnrichWithCellTowerInfoAsync(ModemPollContext context, CellularModemStats stats)
    {
        try
        {
            var qmiDevice = QmiDevice(context, _dialect.DefaultDevice);
            var combinedCommand =
                $"echo '===TOWER===' && {_dialect.Uiwwand("get-cell-tower-info")}; " +
                $"echo '===SYSINFO===' && qmicli -d {qmiDevice} {_dialect.QmiOpen} --nas-get-system-info" +
                ModuleCommands(qmiDevice, context);

            var (success, output) = await _shell.RunCommandAsync(context.Host, combinedCommand);
            if (!success || string.IsNullOrWhiteSpace(output))
            {
                _logger.LogDebug("Cell tower enrichment unavailable on {Name}", context.Name);
                return;
            }

            var sections = ParseCombinedOutput(output, "TOWER", "SYSINFO", "REVISION", "MODULE", "MAKER");

            if (sections.TryGetValue("TOWER", out var towerOutput) && towerOutput.Contains("\"result\""))
                UiwwandParser.ParseCellTowerInfo(towerOutput, stats);

            if (sections.TryGetValue("SYSINFO", out var sysInfoOutput))
            {
                var (nsaAvailable, dcnrRestricted) = QmicliParser.ParseSystemInfo(sysInfoOutput);
                stats.Is5gNsaAvailable = nsaAvailable;
                stats.IsDcnrRestricted = dcnrRestricted;
            }

            ApplyModuleIdentity(context, sections, stats);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Cell tower enrichment failed for {Name}", context.Name);
        }
    }

    /// <inheritdoc/>
    public async Task<(bool success, string message)> ResetRadioAsync(
        ModemPollContext context,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Resetting radio on modem {Name}", context.Name);

        try
        {
            var qmiDevice = QmiDevice(context, _dialect.DefaultDevice);

            // Six seconds of low-power, then back online. The modem watchdog needs two failed
            // 60 s checks before it forces a reset, so this must stay well under a minute.
            var command =
                $"qmicli -d {qmiDevice} {_dialect.QmiOpen} --dms-set-operating-mode=low-power && " +
                "sleep 6 && " +
                $"qmicli -d {qmiDevice} {_dialect.QmiOpen} --dms-set-operating-mode=online";

            var (success, output) = await _shell.RunCommandAsync(context.Host, command);

            if (!success)
            {
                _logger.LogWarning("Radio reset failed on {Name}: {Output}", context.Name, output);
                return (false, "The modem did not accept the radio reset. Check the SSH connection in Cellular Modem Settings.");
            }

            _logger.LogInformation("Radio reset completed on modem {Name}", context.Name);
            return (true, "Radio reset. The modem is re-selecting a tower, which takes a few seconds.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resetting radio on modem {Name}", context.Name);
            return (false, "The radio reset could not be sent to the modem.");
        }
    }

    /// <summary>
    /// The DMS queries for this poll. The revision runs every time: firmware is the one field
    /// whose job is to change, and Firmware Rollout changes it from inside this app - an upgrade
    /// that completes between two polls would otherwise leave a cached version standing forever.
    /// Which module is fitted is asked only until it answers.
    ///
    /// Each is guarded: a chain reports only its last command's exit status, so an unsupported
    /// query here must not speak for the signal commands ahead of it.
    /// </summary>
    private string ModuleCommands(string qmiDevice, ModemPollContext context)
    {
        var commands = $"; echo '===REVISION===' && qmicli -d {qmiDevice} {_dialect.QmiOpen} --dms-get-revision || true";

        if (KnownModule(context) == null)
        {
            commands += $"; echo '===MODULE===' && qmicli -d {qmiDevice} {_dialect.QmiOpen} --dms-get-model || true" +
                        $"; echo '===MAKER===' && qmicli -d {qmiDevice} {_dialect.QmiOpen} --dms-get-manufacturer || true";
        }

        return commands;
    }

    /// <summary>The cached module for this config, or null when it was read from a different device.</summary>
    private ModuleIdentity? KnownModule(ModemPollContext context) =>
        _modules.TryGetValue(context.CacheKey, out var identity)
        && identity.Host == context.Host
        && identity.TransportPath == context.TransportPath
            ? identity
            : null;

    /// <summary>
    /// Stamp the module and its firmware on this poll. The firmware is whatever this poll read;
    /// which module is fitted is remembered once it answers, so later polls stop asking.
    /// </summary>
    private void ApplyModuleIdentity(
        ModemPollContext context, Dictionary<string, string> sections, CellularModemStats stats)
    {
        if (sections.TryGetValue("REVISION", out var revision))
            stats.SoftwareVersion = QmicliParser.ParseRevision(revision);

        var identity = KnownModule(context);
        if (identity == null)
        {
            sections.TryGetValue("MODULE", out var model);
            sections.TryGetValue("MAKER", out var maker);

            var vendor = QmicliParser.ParseVendor(maker);
            var name = QmicliParser.ParseQuotedValue(model);

            // Both or neither: a half answer stays uncached so the missing side is asked again.
            if (vendor == null || name == null)
            {
                _logger.LogDebug("Modem {Name} did not say which module is fitted", context.Name);
                return;
            }

            identity = new ModuleIdentity(vendor, name, context.Host, context.TransportPath);
            _modules[context.CacheKey] = identity;
            _logger.LogInformation("Modem {Name} module: {Vendor} {Model}", context.Name, vendor, name);
        }

        stats.ModuleVendor = identity.Vendor;
        stats.ModuleModel = identity.Model;
    }

    /// <summary>
    /// Poll via raw qmicli commands. Fallback path when uiwwand is unavailable.
    /// </summary>
    private async Task<PollResult<CellularModemStats>> PollViaQmicliAsync(ModemPollContext context)
    {
        try
        {
            var qmiDevice = QmiDevice(context, _dialect.DefaultDevice);
            var stats = new CellularModemStats
            {
                ModemHost = context.Host,
                ModemName = context.Name,
                ModemModel = context.ModemType,
                Timestamp = DateTime.UtcNow,
            };

            var combinedCommand =
                $"echo '===SIGNAL===' && qmicli -d {qmiDevice} {_dialect.QmiOpen} --nas-get-signal-info; " +
                $"echo '===SERVING===' && qmicli -d {qmiDevice} {_dialect.QmiOpen} --nas-get-serving-system; " +
                $"echo '===CELL===' && qmicli -d {qmiDevice} {_dialect.QmiOpen} --nas-get-cell-location-info; " +
                $"echo '===BAND===' && qmicli -d {qmiDevice} {_dialect.QmiOpen} --nas-get-rf-band-info; " +
                $"echo '===SYSINFO===' && qmicli -d {qmiDevice} {_dialect.QmiOpen} --nas-get-system-info" +
                ModuleCommands(qmiDevice, context);

            var (success, output) = await _shell.RunCommandAsync(context.Host, combinedCommand);

            if (!success)
            {
                _logger.LogWarning("Failed to poll modem {Name} via qmicli: {Output}", context.Name, output);
                return PollResult<CellularModemStats>.Failed(
                    SshFailureSummary.Describe(output, context.Host));
            }

            var sections = ParseCombinedOutput(output, "SIGNAL", "SERVING", "CELL", "BAND", "SYSINFO", "REVISION", "MODULE", "MAKER");

            ApplyModuleIdentity(context, sections, stats);

            if (sections.TryGetValue("SIGNAL", out var signalOutput))
            {
                var (lte, nr5g) = QmicliParser.ParseSignalInfo(signalOutput);
                stats.Lte = lte;
                stats.Nr5g = nr5g;
            }

            if (sections.TryGetValue("SERVING", out var servingOutput))
            {
                var (regState, carrier, mcc, mnc, roaming) = QmicliParser.ParseServingSystem(servingOutput);
                stats.RegistrationState = regState;
                stats.Carrier = carrier;
                stats.CarrierMcc = mcc;
                stats.CarrierMnc = mnc;
                stats.IsRoaming = roaming;
            }

            if (sections.TryGetValue("CELL", out var cellOutput))
            {
                var (servingCell, neighbors) = QmicliParser.ParseCellLocationInfo(cellOutput);
                stats.ServingCell = servingCell;
                stats.NeighborCells = neighbors;
            }

            if (sections.TryGetValue("BAND", out var bandOutput))
            {
                stats.ActiveBand = QmicliParser.ParseRfBandInfo(bandOutput);
            }

            if (sections.TryGetValue("SYSINFO", out var sysInfoOutput))
            {
                var (nsaAvailable, dcnrRestricted) = QmicliParser.ParseSystemInfo(sysInfoOutput);
                stats.Is5gNsaAvailable = nsaAvailable;
                stats.IsDcnrRestricted = dcnrRestricted;
            }

            // The section markers are echoed whether or not their command produced anything, and
            // the chain's exit status belongs to the last command, so neither proves the modem
            // answered. Empty radio sections mean it did not - which is not the same as a modem
            // that answered and has no coverage, and must not read as a good poll.
            var answered = new[] { "SIGNAL", "SERVING", "CELL", "BAND" }
                .Any(key => sections.TryGetValue(key, out var body) && !string.IsNullOrWhiteSpace(body));

            if (!answered)
            {
                _logger.LogWarning("Modem {Name} returned no qmicli output", context.Name);
                return PollResult<CellularModemStats>.Failed(
                    $"{context.Host} answered over SSH but the modem returned no data.");
            }

            _logger.LogDebug(
                "Successfully polled modem {Name} via qmicli: {Carrier}, Signal Quality: {Quality}%",
                context.Name, stats.Carrier, stats.SignalQuality);

            return PollResult<CellularModemStats>.Ok(stats);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error polling modem {Name}", context.Name);
            return PollResult<CellularModemStats>.Failed(
                SshFailureSummary.Describe(ex.Message, context.Host));
        }
    }

    /// <inheritdoc/>
    public async Task<(bool success, string message)> TestConnectionAsync(
        ModemPollContext context,
        CancellationToken cancellationToken = default)
    {
        var (success, message) = await _shell.TestConnectionAsync(context.Host);
        return success
            ? (true, message)
            : (false, SshFailureSummary.Describe(message, context.Host));
    }

    /// <summary>
    /// Split combined SSH output into sections by marker.
    /// </summary>
    private static Dictionary<string, string> ParseCombinedOutput(string output, params string[] keys)
    {
        var sections = new Dictionary<string, string>();

        for (int i = 0; i < keys.Length; i++)
        {
            var marker = $"==={keys[i]}===";
            var startIndex = output.IndexOf(marker, StringComparison.Ordinal);
            if (startIndex == -1) continue;

            startIndex += marker.Length;

            var endIndex = output.Length;
            for (int j = i + 1; j < keys.Length; j++)
            {
                var nextMarker = output.IndexOf($"==={keys[j]}===", startIndex, StringComparison.Ordinal);
                if (nextMarker != -1)
                {
                    endIndex = nextMarker;
                    break;
                }
            }

            sections[keys[i]] = output.Substring(startIndex, endIndex - startIndex).Trim();
        }

        return sections;
    }
}
