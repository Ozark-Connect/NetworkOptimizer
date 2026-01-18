namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Service for retrieving dashboard data including device counts, client counts,
/// security audit summaries, and SQM status.
/// </summary>
public interface IDashboardService
{
    /// <summary>
    /// Retrieves comprehensive dashboard data from the UniFi controller for a site.
    /// </summary>
    /// <param name="siteId">The site ID to retrieve dashboard data for.</param>
    /// <remarks>
    /// This method aggregates data from multiple sources:
    /// <list type="bullet">
    ///   <item>Device information (gateways, switches, access points)</item>
    ///   <item>Client counts</item>
    ///   <item>Security audit summary (score, critical/warning issues)</item>
    ///   <item>SQM status</item>
    /// </list>
    /// </remarks>
    /// <returns>A <see cref="DashboardData"/> object containing all dashboard metrics.</returns>
    Task<DashboardData> GetDashboardDataAsync(int siteId);
}
