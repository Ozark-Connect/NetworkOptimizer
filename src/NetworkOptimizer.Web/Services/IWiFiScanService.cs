using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Gates;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// The mutating slice of the Wi-Fi Optimizer: triggering spectrum scans on the access points. A scan
/// takes the radio off-channel briefly, so it is an action rather than a read - Admin-only and
/// audited (design doc 06, gate 9). Every analysis read stays on <see cref="WiFiOptimizerService"/>.
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
