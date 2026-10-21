using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Gates;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// On-demand read of one monitored SFP module, outside the slow collection tier: the Dashboard
/// ONT card's Refresh. Runs on the site's <see cref="MonitoringCollectionAgent"/>, so it writes
/// through the same path the tier does.
/// </summary>
[MutatingService(SiteScoped = true)]
public interface ISfpModulePollService
{
    /// <summary>
    /// Reads the module's DDM from the UniFi Console now and polls the ONT attached to it, if any.
    /// Alerts are left to the collection tier. Returns why the read fell short, or null.
    /// </summary>
    /// <remarks>Viewer: the card is open to any role, as is the external ONT Refresh.</remarks>
    [RequireRole(Roles.Viewer)]
    Task<string?> PollModuleAsync(string deviceMac, string portName, CancellationToken ct = default);
}
