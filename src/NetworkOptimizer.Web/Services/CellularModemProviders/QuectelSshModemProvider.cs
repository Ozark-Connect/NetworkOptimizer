using NetworkOptimizer.Monitoring;
using NetworkOptimizer.Monitoring.Models;
using NetworkOptimizer.Monitoring.Providers;
using NetworkOptimizer.Web.Services.Ssh;

namespace NetworkOptimizer.Web.Services.CellularModemProviders;

/// <summary>
/// Cellular modem provider for any router with a Quectel module that allows SSH, such as a stock
/// OpenWrt box. Writes AT commands straight to the module's AT port through
/// <see cref="QuectelSshTransport"/>, with per-modem SSH credentials, and parses the answers with
/// <see cref="QuectelAtParser"/>. GL.iNet routers keep <see cref="QuectelAtModemProvider"/>, which
/// uses their own <c>gl_modem</c> and ubus service.
/// </summary>
public sealed class QuectelSshModemProvider : ICellularModemProvider
{
    /// <inheritdoc/>
    public string ProviderKey => ModemProviderKeys.QuectelSsh;

    /// <inheritdoc/>
    public string DisplayName => "Quectel modem (SSH)";

    /// <summary>The ModemType the Settings form writes for this provider. A placeholder, not a model.</summary>
    public const string ModemTypePlaceholder = "Quectel";

    private const string ServingCellCommand = "AT+QENG=\"servingcell\"";
    private const string OperatorCommand = "AT+COPS?";

    /// <summary>The module's own firmware, asked every poll rather than taken from cached discovery.</summary>
    private const string RevisionCommand = "AT+CGMR";

    private readonly ILogger<QuectelSshModemProvider> _logger;
    private readonly QuectelSshTransport _transport;

    public QuectelSshModemProvider(
        ILogger<QuectelSshModemProvider> logger,
        QuectelSshTransport transport)
    {
        _logger = logger;
        _transport = transport;
    }

    /// <inheritdoc/>
    public async Task<PollResult<CellularModemStats>> PollAsync(
        ModemPollContext context,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Polling Quectel modem {Name} at {Host}", context.Name, context.Host);

        var host = context.Host;
        var connection = await ToConnectionInfoAsync(context);
        if (!connection.HasCredentials)
        {
            _logger.LogWarning("No SSH credentials configured for modem {Name}", context.Name);
            return PollResult<CellularModemStats>.Failed("SSH credentials are not configured for this modem.");
        }

        try
        {
            var result = await _transport.RunAtAsync(
                context, connection, new[] { ServingCellCommand, OperatorCommand, RevisionCommand }, cancellationToken);

            if (!result.Success || result.Endpoint == null)
            {
                _logger.LogWarning("AT command failed on {Name}: {Error}", context.Name, result.Error);
                _transport.Forget(context.CacheKey);
                return PollResult<CellularModemStats>.Failed(result.Error ?? $"{host} did not answer the AT command.");
            }

            var stats = BuildStats(result, host, context.Name, context.ModemType);
            if (stats == null)
            {
                _transport.Forget(context.CacheKey);
                return PollResult<CellularModemStats>.Failed($"{host} answered over SSH but the modem returned no signal data.");
            }

            _logger.LogInformation(
                "Successfully polled Quectel modem {Name}: Signal Quality: {Quality}%",
                context.Name, stats.SignalQuality);

            return PollResult<CellularModemStats>.Ok(stats);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error polling Quectel modem {Name}", context.Name);
            _transport.Forget(context.CacheKey);
            return PollResult<CellularModemStats>.Failed(SshFailureSummary.Describe(ex.Message, host));
        }
    }

    /// <summary>
    /// Turn one poll's answers into stats, or null when the serving-cell answer carried no signal.
    /// The card shows the module ("Quectel RM551E-GL") as the device, since the router's brand is
    /// unknown. Internal for tests.
    /// </summary>
    internal static CellularModemStats? BuildStats(QuectelSshAtResult result, string host, string name, string? modemType)
    {
        var endpoint = result.Endpoint;
        var product = endpoint?.Model
            ?? (string.IsNullOrWhiteSpace(modemType) || modemType == ModemTypePlaceholder ? "" : modemType);

        var stats = QuectelAtParser.Parse(result.For(ServingCellCommand), host, name, product);
        if (stats == null)
            return null;

        // AT+QENG reports only MCC/MNC, so the operator name comes from AT+COPS?.
        stats.Carrier = QuectelAtParser.ParseOperator(result.For(OperatorCommand)) ?? stats.Carrier;
        stats.SoftwareVersion = QuectelAtParser.ParseRevisionResponse(result.For(RevisionCommand))
            ?? endpoint?.Revision;
        stats.HostModel = endpoint?.HostModel;
        stats.HostVersion = endpoint?.HostVersion;
        stats.ModuleVendor = endpoint?.Vendor;
        stats.ModuleModel = endpoint?.Model;
        return stats;
    }

    /// <inheritdoc/>
    public async Task<(bool success, string message)> TestConnectionAsync(
        ModemPollContext context,
        CancellationToken cancellationToken = default)
    {
        var connection = await ToConnectionInfoAsync(context);
        if (!connection.HasCredentials)
            return (false, "SSH credentials not configured for this modem");

        try
        {
            var result = await _transport.RunAtAsync(
                context, connection, new[] { ServingCellCommand }, cancellationToken);

            if (!result.Success || result.Endpoint == null)
                return (false, result.Error ?? "The AT command failed.");

            var found = $"Found {result.Endpoint.Description ?? "a modem"} on {result.Endpoint.Device}. ";

            if (result.For(ServingCellCommand).Contains("+QENG"))
                return (true, $"{found}Connected and the modem responded to the AT command.");

            return (false, $"{found}SSH connected but the modem did not respond to AT+QENG.");
        }
        catch (Exception ex)
        {
            return (false, $"Connection failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Build SSH connection info from per-modem credentials in the poll context.
    /// </summary>
    private static async Task<SshConnectionInfo> ToConnectionInfoAsync(ModemPollContext context)
    {
        var (host, port) = await context.Dialer.ResolveAsync(context.Host, context.Port > 0 ? context.Port : 22);
        return new SshConnectionInfo
        {
            Host = host,
            Port = port,
            Username = context.Username ?? "root",
            Password = context.Password,
            PrivateKeyPath = context.PrivateKeyPath,
        };
    }
}
