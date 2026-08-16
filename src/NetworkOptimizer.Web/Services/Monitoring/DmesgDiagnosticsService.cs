using NetworkOptimizer.Web.Services.Ssh;

namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>
/// Runs dmesg on the site's gateway over SSH and returns a structured diagnostic report.
/// Same SSH path as the interface diagnostics: direct for server-polled sites, agent-proxied
/// for agent-served sites.
///
/// Not a [MutatingService]: dmesg is read-only (same as GatewayDiagnosticsService's interface
/// reads). Viewer is the RBAC floor today, so the page-level RequireViewer policy is sufficient.
/// If Viewer ever stops being the floor, both diagnostics services need explicit gates.
/// </summary>
public class DmesgDiagnosticsService
{
    private readonly IGatewaySshService _gatewaySsh;
    private readonly ILogger<DmesgDiagnosticsService> _logger;

    public DmesgDiagnosticsService(
        IGatewaySshService gatewaySsh,
        ILogger<DmesgDiagnosticsService> logger)
    {
        _gatewaySsh = gatewaySsh;
        _logger = logger;
    }

    public async Task<DmesgDiagnosticsReport> RunAsync(CancellationToken ct = default)
    {
        string output;
        try
        {
            var (success, commandOutput) = await _gatewaySsh.RunCommandAsync(
                "dmesg -T", TimeSpan.FromSeconds(30), ct);

            if (!success)
            {
                return new DmesgDiagnosticsReport
                {
                    RunError = string.IsNullOrWhiteSpace(commandOutput)
                        ? "Couldn't reach the gateway over SSH. Check the gateway SSH credentials in Settings."
                        : commandOutput.Trim()
                };
            }

            output = commandOutput;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "dmesg SSH command failed");
            return new DmesgDiagnosticsReport
            {
                RunError = $"Gateway command failed: {ex.Message}"
            };
        }

        return DmesgParser.Parse(output);
    }
}
