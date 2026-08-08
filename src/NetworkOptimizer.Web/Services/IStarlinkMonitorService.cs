using NetworkOptimizer.Monitoring.Models;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Gates;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Polls Starlink dishes for one site and caches their stats.
///
/// Site-scoped, and gated the same way as its sibling monitors: configuration lives behind the
/// site-admin-only Settings page so writes require Admin ON THAT SITE, while reads are Viewer
/// because the monitoring cards showing them are open to any role.
///
/// Cache reads are asynchronous only because a gated member must return a Task for the
/// interceptor to authorize and audit around it; they are still served from memory.
/// </summary>
[MutatingService(SiteScoped = true)]
public interface IStarlinkMonitorService
{
    /// <summary>Most recent stats for one dish, or null when it has not been polled.</summary>
    [RequireRole(Roles.Viewer)]
    Task<StarlinkStats?> GetCachedStatsAsync(int id);

    /// <summary>Most recent stats for every polled dish on this site.</summary>
    [RequireRole(Roles.Viewer)]
    Task<IReadOnlyDictionary<int, StarlinkStats>> GetAllCachedStatsAsync();

    /// <summary>Most recent obstruction map for one dish, or null when none has been fetched.</summary>
    [RequireRole(Roles.Viewer)]
    Task<StarlinkObstructionMap?> GetCachedObstructionMapAsync(int id);

    /// <summary>Every Starlink configuration on this site.</summary>
    [RequireRole(Roles.Viewer)]
    Task<List<StarlinkConfiguration>> GetConfigsAsync();

    /// <summary>Polls one dish now and caches the result.</summary>
    [RequireRole(Roles.Viewer)]
    Task PollStarlinkAsync(int id);

    /// <summary>Adds or updates a Starlink configuration.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.MonitoringSetupChanged, TargetType = "starlink")]
    Task SaveStarlinkAsync(StarlinkConfiguration config);

    /// <summary>Removes a Starlink configuration.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.MonitoringSetupChanged, TargetType = "starlink")]
    Task DeleteStarlinkAsync(int id);

    /// <summary>Pauses or resumes polling of one dish.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.MonitoringSetupChanged, TargetType = "starlink")]
    Task SetStarlinkEnabledAsync(int id, bool enabled);

    /// <summary>Verifies a configuration can reach its dish, without saving it.</summary>
    [RequireRole(Roles.Admin)]
    Task<(bool Success, string Message)> ProbeAsync(StarlinkConfiguration config);
}
