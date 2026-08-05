using NetworkOptimizer.Diagnostics.Models;

namespace NetworkOptimizer.Web.Services.Ssh;

/// <summary>
/// Reads, over SSH, whether the gateway is actually shaping the WANs that have UniFi Smart Queues
/// turned on. UniFi Network regularly accepts the Smart Queues toggle without provisioning the
/// queues, and the only place that shows is the gateway's own traffic control - the controller
/// keeps reporting the feature as enabled.
///
/// Every command is a read, and everything is asked in one round trip. Anything that makes the
/// answer unavailable - SSH off, no credentials, an offline agent tunnel, a failed command -
/// returns no states at all rather than a guess, so a site we cannot see is never accused of a
/// misconfiguration.
/// </summary>
public class GatewayShaperProbeService
{
    private readonly ISqmService _sqmService;
    private readonly IGatewaySshService _gatewaySsh;
    private readonly ILogger<GatewayShaperProbeService> _logger;

    public GatewayShaperProbeService(
        ISqmService sqmService,
        IGatewaySshService gatewaySsh,
        ILogger<GatewayShaperProbeService> logger)
    {
        _sqmService = sqmService;
        _gatewaySsh = gatewaySsh;
        _logger = logger;
    }

    /// <summary>
    /// The shaper state of every WAN with Smart Queues enabled. Empty when there are none, or
    /// when the gateway cannot be read.
    /// </summary>
    public async Task<List<WanShaperState>> RunAsync(CancellationToken ct = default)
    {
        var empty = new List<WanShaperState>();

        try
        {
            var targets = await BuildTargetsAsync();
            if (targets.Count == 0)
                return empty;

            var settings = await _gatewaySsh.GetSettingsAsync();
            if (!settings.Enabled || string.IsNullOrEmpty(settings.Host) || !settings.HasCredentials)
            {
                _logger.LogDebug("Skipping Smart Queues shaper probe: gateway SSH not available");
                return empty;
            }

            if (await _gatewaySsh.IsAwaitingAgentTunnelAsync())
            {
                _logger.LogDebug("Skipping Smart Queues shaper probe: waiting for the site's agent");
                return empty;
            }

            var interfaces = targets
                .SelectMany(t => new[] { t.Interface, t.IfbInterface })
                .ToList();

            var (success, output) = await _gatewaySsh.RunCommandAsync(
                GatewayShaperProbe.BuildCommand(interfaces), TimeSpan.FromSeconds(20), ct);

            if (!success)
            {
                _logger.LogDebug("Smart Queues shaper probe command failed: {Output}", output);
                return empty;
            }

            var states = GatewayShaperProbe.Parse(output, targets);
            _logger.LogDebug(
                "Smart Queues shaper probe read {Count} of {Target} WAN(s) with Smart Queues enabled",
                states.Count, targets.Count);
            return states;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Smart Queues shaper probe failed");
            return empty;
        }
    }

    /// <summary>
    /// The WANs worth reading: Smart Queues on, and interface names the controller gave us that
    /// are safe to put on a command line. Interface resolution is the controller's - "eth6" plain,
    /// "eth6.100" VLAN-tagged, "ppp0" for PPPoE - so this check looks at exactly the devices
    /// Adaptive SQM and Monitoring do.
    /// </summary>
    private async Task<List<ShaperProbeTarget>> BuildTargetsAsync()
    {
        var wans = await _sqmService.GetWanInterfacesFromControllerAsync();
        var targets = new List<ShaperProbeTarget>();

        foreach (var wan in wans.Where(w => w.SmartqEnabled))
        {
            if (!GatewayShaperProbe.IsValidInterfaceName(wan.Interface) ||
                !GatewayShaperProbe.IsValidInterfaceName(wan.TcInterface))
            {
                _logger.LogDebug(
                    "Skipping WAN {Name} in shaper probe: unusable interface name '{Interface}'",
                    wan.Name, wan.Interface);
                continue;
            }

            targets.Add(new ShaperProbeTarget(
                wan.Name,
                wan.Interface,
                wan.TcInterface,
                wan.SmartqDownRateMbps,
                wan.SmartqUpRateMbps));
        }

        return targets;
    }
}
