using NetworkOptimizer.Monitoring;
using NetworkOptimizer.Monitoring.Models;
using NetworkOptimizer.Monitoring.Providers;
using NetworkOptimizer.Web.Services.Ssh;

namespace NetworkOptimizer.Web.Services.CellularModemProviders;

/// <summary>
/// Cellular modem provider for GL-iNet routers and other devices with Quectel modems.
/// Uses per-modem SSH credentials (not the shared UniFi SSH service) to run
/// <c>gl_modem ... AT AT+QENG="servingcell"</c> and parse the response with
/// <see cref="QuectelAtParser"/>.
///
/// <see cref="GlModemTransport"/> owns how gl_modem is addressed; the TransportPath
/// field is only a user-supplied hint for hardware discovery cannot identify.
/// </summary>
public sealed class QuectelAtModemProvider : ICellularModemProvider
{
    /// <inheritdoc/>
    public string ProviderKey => "quectel-at";

    /// <inheritdoc/>
    public string DisplayName => "GL-iNet / Quectel modem (SSH)";

    private const string ServingCellCommand = "AT+QENG=\"servingcell\"";
    private const string OperatorCommand = "AT+COPS?";

    private readonly ILogger<QuectelAtModemProvider> _logger;
    private readonly GlModemTransport _transport;

    public QuectelAtModemProvider(
        ILogger<QuectelAtModemProvider> logger,
        GlModemTransport transport)
    {
        _logger = logger;
        _transport = transport;
    }

    /// <inheritdoc/>
    public async Task<PollResult<CellularModemStats>> PollAsync(
        ModemPollContext context,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Polling GL-iNet modem {Name} at {Host}", context.Name, context.ConfiguredHost ?? context.Host);

        // TEMPORARY REVIEW HARNESS - REMOVE BEFORE MERGING THIS BRANCH.
        // A modem named "Test" renders GL.iNet sample data so the card can be reviewed
        // without the hardware, which no test site has. Runs the real parser, so what it
        // shows is what a real E5800 would produce.
        if (context.Name == "Test")
            return PollResult<CellularModemStats>.Ok(BuildSampleStats(context));

        var host = context.ConfiguredHost ?? context.Host;
        var connection = ToConnectionInfo(context);
        if (!connection.HasCredentials)
        {
            _logger.LogWarning("No SSH credentials configured for modem {Name}", context.Name);
            return PollResult<CellularModemStats>.Failed("SSH credentials are not configured for this modem.");
        }

        try
        {
            var result = await _transport.RunAtAsync(
                context, connection, new[] { ServingCellCommand, OperatorCommand }, cancellationToken);

            if (!result.Success)
            {
                _logger.LogWarning("AT command failed on {Name}: {Error}", context.Name, result.Error);
                return PollResult<CellularModemStats>.Failed(result.Error ?? $"{host} did not answer the AT command.");
            }

            // The model is the router the owner bought, not the module inside it - the module
            // is reported separately below.
            var product = string.IsNullOrWhiteSpace(result.Endpoint.Product) ? context.ModemType : result.Endpoint.Product!;
            var stats = QuectelAtParser.Parse(result.For(ServingCellCommand), host, context.Name, product);

            if (stats == null)
                return PollResult<CellularModemStats>.Failed($"{host} answered over SSH but the modem returned no signal data.");

            // AT+QENG reports only MCC/MNC, so the operator name comes from AT+COPS?.
            stats.Carrier = QuectelAtParser.ParseOperator(result.For(OperatorCommand)) ?? stats.Carrier;
            stats.SoftwareVersion = result.Endpoint.SoftwareVersion;
            stats.HostVersion = result.Endpoint.HostVersion;
            stats.ModuleVendor = result.Endpoint.Vendor;
            stats.ModuleModel = result.Endpoint.Model;

            _logger.LogInformation(
                "Successfully polled GL-iNet modem {Name}: Signal Quality: {Quality}%",
                context.Name, stats.SignalQuality);

            return PollResult<CellularModemStats>.Ok(stats);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error polling GL-iNet modem {Name}", context.Name);
            return PollResult<CellularModemStats>.Failed(SshFailureSummary.Describe(ex.Message, host));
        }
    }

    /// <inheritdoc/>
    public async Task<(bool success, string message)> TestConnectionAsync(
        ModemPollContext context,
        CancellationToken cancellationToken = default)
    {
        var connection = ToConnectionInfo(context);
        if (!connection.HasCredentials)
            return (false, "SSH credentials not configured for this modem");

        try
        {
            var result = await _transport.RunAtAsync(
                context, connection, new[] { ServingCellCommand }, cancellationToken);

            var identity = result.Endpoint.Description;
            var found = identity != null ? $"Found {identity}. " : "";

            if (result.Success && result.For(ServingCellCommand).Contains("+QENG"))
                return (true, $"{found}Connected and the modem responded to the AT command.");

            if (result.RejectedCommandLine)
            {
                return (false, identity != null
                    ? $"{found}Its gl_modem did not accept the command line, so no AT command reached the modem."
                    : "The router's gl_modem did not accept the command line, and the modem could not be identified. " +
                      "Check the Modem Bus field.");
            }

            if (result.Success)
                return (false, $"{found}SSH connected but the modem did not respond to AT+QENG.");

            return (false, result.Error ?? "The AT command failed.");
        }
        catch (Exception ex)
        {
            return (false, $"Connection failed: {ex.Message}");
        }
    }

    // TEMPORARY REVIEW HARNESS - REMOVE BEFORE MERGING THIS BRANCH.
    private static CellularModemStats BuildSampleStats(ModemPollContext context)
    {
        const string servingCell = """
+QENG: "servingcell","NOCONN"
+QENG: "LTE","FDD",310,260,"0A1B2C3",438,975,2,4,4,"1234",-95,-7,-68,20,15,150,-
+QENG: "NR5G-NSA",310,260,46,-81,34,-10,502110,41,11,1

OK
""";

        var stats = QuectelAtParser.Parse(servingCell, context.Host, context.Name, "E5800")!;
        stats.Carrier = QuectelAtParser.ParseOperator("+COPS: 0,0,\"T-Mobile\",13") ?? "";
        stats.SoftwareVersion = "QRM650VNA01ACR02A04G8G_OCPU_RGH_01.005.01.005";
        stats.HostVersion = "4.8.5";
        stats.ModuleVendor = "Quectel";
        stats.ModuleModel = "RG650V-NA";
        return stats;
    }

    /// <summary>
    /// Build SSH connection info from per-modem credentials in the poll context.
    /// </summary>
    private static SshConnectionInfo ToConnectionInfo(ModemPollContext context) => new()
    {
        Host = context.Host,
        Port = context.Port > 0 ? context.Port : 22,
        Username = context.Username ?? "root",
        Password = context.Password,
        PrivateKeyPath = context.PrivateKeyPath,
    };
}
