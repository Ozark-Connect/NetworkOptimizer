using NetworkOptimizer.Monitoring.Models;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Gates;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Polls cable modems for one site and caches their stats.
///
/// Site-scoped, and gated the same way as its sibling monitors: configuration lives behind the
/// site-admin-only Settings page so writes require Admin ON THAT SITE, while reads are Viewer
/// because the monitoring cards showing them are open to any role.
///
/// Cache reads are asynchronous only because a gated member must return a Task for the
/// interceptor to authorize and audit around it; they are still served from memory.
/// </summary>
[MutatingService(SiteScoped = true)]
public interface ICableModemService
{
    /// <summary>Most recent stats for one cable modem, or null when it has not been polled.</summary>
    [RequireRole(Roles.Viewer)]
    Task<CableModemStats?> GetCachedStatsAsync(int cmId);

    /// <summary>Most recent stats for every polled cable modem on this site.</summary>
    [RequireRole(Roles.Viewer)]
    Task<IReadOnlyDictionary<int, CableModemStats>> GetAllCachedStatsAsync();

    /// <summary>Every cable modem configuration on this site.</summary>
    [RequireRole(Roles.Viewer)]
    Task<List<CmConfiguration>> GetConfigsAsync();

    /// <summary>Polls one cable modem now and caches the result.</summary>
    [RequireRole(Roles.Viewer)]
    Task PollCmAsync(int cmId);

    /// <summary>Adds or updates a cable modem configuration.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.MonitoringSetupChanged, TargetType = "cable_modem")]
    Task SaveCmAsync(CmConfiguration config);

    /// <summary>Removes a cable modem configuration.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.MonitoringSetupChanged, TargetType = "cable_modem")]
    Task DeleteCmAsync(int id);

    /// <summary>Pauses or resumes polling of one cable modem.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.MonitoringSetupChanged, TargetType = "cable_modem")]
    Task SetCmEnabledAsync(int id, bool enabled);

    /// <summary>Verifies a configuration can reach its modem, without saving it.</summary>
    [RequireRole(Roles.Admin)]
    Task<(bool Success, string Message)> ProbeAsync(CmConfiguration config);
}
