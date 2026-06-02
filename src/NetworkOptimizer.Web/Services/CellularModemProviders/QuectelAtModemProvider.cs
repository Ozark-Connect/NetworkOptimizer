using NetworkOptimizer.Monitoring;
using NetworkOptimizer.Monitoring.Models;
using NetworkOptimizer.Monitoring.Providers;
using NetworkOptimizer.Web.Services.Ssh;

namespace NetworkOptimizer.Web.Services.CellularModemProviders;

/// <summary>
/// Cellular modem provider for GL-iNet routers and other devices with Quectel modems.
/// Uses per-modem SSH credentials (not the shared UniFi SSH service) to connect
/// and run <c>gl_modem AT AT+QENG="servingcell"</c>, then parses the response
/// with <see cref="QuectelAtParser"/>.
///
/// The TransportPath field stores the USB bus path (e.g. "1-1.2") used by
/// gl_modem's <c>-B</c> flag. If empty, gl_modem auto-detects the first modem.
/// </summary>
public sealed class QuectelAtModemProvider : ICellularModemProvider
{
    /// <inheritdoc/>
    public string ProviderKey => "quectel-at";

    /// <inheritdoc/>
    public string DisplayName => "GL-iNet / Quectel modem (SSH)";

    private readonly ILogger<QuectelAtModemProvider> _logger;
    private readonly SshClientService _sshClient;

    public QuectelAtModemProvider(
        ILogger<QuectelAtModemProvider> logger,
        SshClientService sshClient)
    {
        _logger = logger;
        _sshClient = sshClient;
    }

    /// <inheritdoc/>
    public async Task<CellularModemStats?> PollAsync(
        ModemPollContext context,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Polling GL-iNet modem {Name} at {Host}", context.Name, context.Host);

        var connection = ToConnectionInfo(context);
        if (!connection.HasCredentials)
        {
            _logger.LogWarning("No SSH credentials configured for modem {Name}", context.Name);
            return null;
        }

        try
        {
            var command = BuildAtCommand(context.TransportPath);
            var result = await _sshClient.ExecuteCommandAsync(connection, command, cancellationToken: cancellationToken);

            if (!result.Success)
            {
                _logger.LogWarning("AT command failed on {Name}: {Error}", context.Name, result.Error);
                return null;
            }

            var stats = QuectelAtParser.Parse(result.Output, context.Host, context.Name, context.ModemType);

            if (stats != null)
            {
                _logger.LogInformation(
                    "Successfully polled GL-iNet modem {Name}: Signal Quality: {Quality}%",
                    context.Name, stats.SignalQuality);
            }

            return stats;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error polling GL-iNet modem {Name}", context.Name);
            return null;
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
            var command = BuildAtCommand(context.TransportPath);
            var result = await _sshClient.ExecuteCommandAsync(connection, command, cancellationToken: cancellationToken);

            if (result.Success && !string.IsNullOrWhiteSpace(result.Output) && result.Output.Contains("+QENG"))
            {
                return (true, "Connected and modem responded to AT command");
            }

            if (result.Success)
            {
                return (false, $"SSH connected but modem did not respond. Output: {Truncate(result.Output, 200)}");
            }

            return (false, $"SSH command failed: {Truncate(result.Error, 200)}");
        }
        catch (Exception ex)
        {
            return (false, $"Connection failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Build the AT command string. Uses gl_modem with optional bus path.
    /// </summary>
    private static string BuildAtCommand(string? transportPath)
    {
        if (!string.IsNullOrWhiteSpace(transportPath))
            return $"gl_modem -B {transportPath} AT AT+QENG=\\\"servingcell\\\"";

        return "gl_modem AT AT+QENG=\\\"servingcell\\\"";
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

    private static string Truncate(string? s, int maxLength)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= maxLength ? s : s[..maxLength] + "...";
    }
}
