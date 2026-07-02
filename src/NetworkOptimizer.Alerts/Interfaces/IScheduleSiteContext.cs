using Microsoft.Extensions.DependencyInjection;

namespace NetworkOptimizer.Alerts.Interfaces;

/// <summary>
/// Lets the schedule loop fan out over the host's sites without the Alerts
/// project knowing what a site is. The host enumerates site keys and pins a
/// DI scope to one, so scoped services (repositories, the audit pipeline)
/// resolve against that site's data. When no implementation is registered the
/// scheduler behaves single-site.
/// </summary>
public interface IScheduleSiteContext
{
    /// <summary>Key of the default site (the single site when multi-site is off).</summary>
    string DefaultKey { get; }

    /// <summary>Keys of every site whose schedules should be evaluated.</summary>
    Task<IReadOnlyList<string>> GetSiteKeysAsync(CancellationToken ct);

    /// <summary>Pins a freshly created scope to a site so scoped services resolve its data.</summary>
    void PinScope(IServiceScope scope, string siteKey);
}
