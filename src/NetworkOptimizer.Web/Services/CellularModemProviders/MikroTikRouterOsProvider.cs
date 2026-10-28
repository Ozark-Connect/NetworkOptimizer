using System.Text.RegularExpressions;
using NetworkOptimizer.Core;
using NetworkOptimizer.Monitoring.Models;
using NetworkOptimizer.Monitoring.Providers;
using NetworkOptimizer.Web.Services.Ssh;

namespace NetworkOptimizer.Web.Services.CellularModemProviders;

/// <summary>
/// Cellular modem provider for MikroTik RouterOS devices with an LTE/5G interface (ATLGM, Chateau,
/// LtAP). Uses per-modem SSH credentials to run <c>/system resource print</c> and
/// <c>/interface lte monitor ... once</c> in one session, parsed by <see cref="RouterOsLteMonitorParser"/>.
///
/// The TransportPath field names the LTE interface. Empty monitors <c>[find]</c>, which is the
/// router's only LTE interface on single-modem boxes.
/// </summary>
[VendorSpecific("MikroTik", "RouterOS console commands and the +ct login option")]
public sealed class MikroTikRouterOsProvider : ICellularModemProvider
{
    /// <inheritdoc/>
    public string ProviderKey => ModemProviderKeys.MikroTikRouterOs;

    /// <inheritdoc/>
    public string DisplayName => "MikroTik RouterOS (SSH)";

    /// <summary>The value the Settings form writes as the model before the first poll; not a model.</summary>
    public const string ModemTypePlaceholder = "MikroTik";

    // The monitor runs last: a RouterOS runtime error ends the script, and the resource section
    // should survive an LTE command that fails.
    private const string MonitorMarker = "<<NO-LTE-MONITOR>>";

    // RouterOS interface names may hold spaces; quoting covers those, and these characters could
    // break out of the quoted name.
    private static readonly Regex SafeInterfaceName = new(@"^[^""\\$\[\];\r\n{}]+$", RegexOptions.Compiled);

    private readonly ILogger<MikroTikRouterOsProvider> _logger;
    private readonly SshClientService _sshClient;

    public MikroTikRouterOsProvider(ILogger<MikroTikRouterOsProvider> logger, SshClientService sshClient)
    {
        _logger = logger;
        _sshClient = sshClient;
    }

    /// <inheritdoc/>
    public async Task<PollResult<CellularModemStats>> PollAsync(
        ModemPollContext context,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Polling MikroTik modem {Name} at {Host}", context.Name, context.Host);

        var (stats, error) = await ReadAsync(context, cancellationToken);
        if (stats == null)
        {
            _logger.LogWarning("MikroTik poll failed on {Name}: {Error}", context.Name, error);
            return PollResult<CellularModemStats>.Failed(error!);
        }

        _logger.LogInformation(
            "Successfully polled MikroTik modem {Name}: Signal Quality: {Quality}%",
            context.Name, stats.SignalQuality);
        return PollResult<CellularModemStats>.Ok(stats);
    }

    /// <inheritdoc/>
    public async Task<(bool success, string message)> TestConnectionAsync(
        ModemPollContext context,
        CancellationToken cancellationToken = default)
    {
        var (stats, error) = await ReadAsync(context, cancellationToken);
        if (stats == null)
            return (false, error!);

        var module = string.Join(" ", new[] { stats.ModuleVendor, stats.ModuleModel }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var device = string.IsNullOrWhiteSpace(stats.ModemModel) ? "MikroTik" : $"MikroTik {stats.ModemModel}";
        var found = module.Length > 0 ? $"{device} with a {module}" : device;
        var carrier = string.IsNullOrWhiteSpace(stats.Carrier) ? "" : $" on {stats.Carrier}";
        return (true, $"Found {found}. Connected{carrier}, {stats.NetworkModeLabel}.");
    }

    private async Task<(CellularModemStats? Stats, string? Error)> ReadAsync(
        ModemPollContext context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(context.Username) ||
            (string.IsNullOrEmpty(context.Password) && string.IsNullOrEmpty(context.PrivateKeyPath)))
        {
            return (null, "SSH credentials are not configured for this modem.");
        }

        var script = BuildScript(context.TransportPath);
        if (script == null)
            return (null, $"The LTE interface name \"{context.TransportPath}\" contains characters RouterOS cannot take here.");

        try
        {
            var connection = await ToConnectionInfoAsync(context);
            var result = await _sshClient.ExecuteCommandAsync(connection, script, cancellationToken: cancellationToken);

            if (!result.Success && string.IsNullOrWhiteSpace(result.Output))
                return (null, SshFailureSummary.Describe(result.CombinedOutput, context.Host));

            return Interpret(result.Output ?? "", context);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error polling MikroTik modem {Name}", context.Name);
            return (null, SshFailureSummary.Describe(ex.Message, context.Host));
        }
    }

    /// <summary>
    /// The RouterOS script for one poll, or null when the configured interface name is unsafe to
    /// put on the command line.
    /// </summary>
    public static string? BuildScript(string? interfaceName)
    {
        var name = interfaceName?.Trim();
        string target;
        if (string.IsNullOrEmpty(name))
            target = "[find]";
        else if (SafeInterfaceName.IsMatch(name))
            target = $"\"{name}\"";
        else
            return null;

        return $"/system resource print; :put \"{MonitorMarker}\"; /interface lte monitor {target} once";
    }

    /// <summary>
    /// Turn the script's output into stats, or into the reason there are none.
    /// </summary>
    public static (CellularModemStats? Stats, string? Error) Interpret(string output, ModemPollContext context)
    {
        var markerAt = output.IndexOf(MonitorMarker, StringComparison.Ordinal);
        var resource = markerAt >= 0 ? output[..markerAt] : output;
        var monitor = markerAt >= 0 ? output[(markerAt + MonitorMarker.Length)..] : "";

        if (markerAt < 0)
            return (null, $"{context.Host} answered over SSH but did not run the RouterOS commands: {FirstLine(output)}");

        if (RouterOsLteMonitorParser.HasMultipleInterfaces(RouterOsLteMonitorParser.ParseFields(monitor)))
            return (null, "This router has more than one LTE interface. Enter the one to monitor in the LTE Interface field.");

        // "MikroTik" is the Settings placeholder, not a model; the board name replaces it.
        var model = RouterOsLteMonitorParser.ParseBoardName(resource)
            ?? (context.ModemType == ModemTypePlaceholder ? "" : context.ModemType);
        var stats = RouterOsLteMonitorParser.Parse(monitor, context.Host, context.Name, model);
        if (stats == null)
        {
            var reason = FirstLine(monitor);
            return (null, reason.Length > 0
                ? $"{context.Host} returned no signal data: {reason}"
                : $"{context.Host} returned no signal data. Check that the router has an LTE interface.");
        }

        stats.HostVersion = RouterOsLteMonitorParser.ParseVersion(resource);
        return (stats, null);
    }

    /// <summary>The first non-empty output line, trimmed and capped, for an error message.</summary>
    private static string FirstLine(string text)
    {
        var line = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        return line.Length > 160 ? line[..160] : line;
    }

    /// <summary>
    /// Build SSH connection info from per-modem credentials. The <c>+ct</c> login suffix turns off
    /// RouterOS colors and terminal detection, so the output is plain text.
    /// </summary>
    private static async Task<SshConnectionInfo> ToConnectionInfoAsync(ModemPollContext context)
    {
        var (host, port) = await context.Dialer.ResolveAsync(context.Host, context.Port > 0 ? context.Port : 22);
        return new SshConnectionInfo
        {
            Host = host,
            Port = port,
            Username = LoginName(context.Username!),
            Password = context.Password,
            PrivateKeyPath = context.PrivateKeyPath,
        };
    }

    /// <summary>
    /// The SSH login name: the username plus <c>+ct</c>. A name that already carries RouterOS login
    /// options (anything after a <c>+</c>) is left as typed.
    /// </summary>
    public static string LoginName(string username)
    {
        var trimmed = username.Trim();
        return trimmed.Contains('+') ? trimmed : trimmed + "+ct";
    }
}
