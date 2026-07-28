using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Gates;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// The mutating slice of the Wi-Fi Optimizer: triggering spectrum scans on the access points. A scan
/// takes the radio off-channel briefly, so it is an action rather than a read - Operator and audited
/// (design doc 06, gate 9). Every analysis read stays on <see cref="WiFiOptimizerService"/>.
///
/// This is also the ONLY mutating call the product makes against the UniFi Network API. Everything
/// else that writes goes to our own gateway features, which is why the rest of the optimizer and all
/// of Monitoring weigh their roles against what we measure rather than against what the network does.
/// </summary>
[MutatingService(SiteScoped = true)]
public interface IWiFiScanService
{
    /// <summary>Runs quick spectrum scans on the given AP/band targets.</summary>
    /// <remarks>Operator: a scan takes APs off-channel and briefly drops their clients. Disruptive measurement,
    /// which is the same reason a speed test is Operator rather than Viewer.</remarks>
    [RequireRole(Roles.Operator)]
    [AuditAction(AuditActions.OptimizerApplied, TargetType = "spectrum_scan")]
    Task RunQuickScansAsync(
        IEnumerable<(string ApMac, string BandCode)> targets,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default);
}
