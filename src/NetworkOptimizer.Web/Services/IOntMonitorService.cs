using NetworkOptimizer.Monitoring.Models;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Gates;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Polls external ONT devices for one site and caches their stats.
///
/// Site-scoped: an ONT belongs to the site in context, and configuration lives behind the
/// site-admin-only Settings page, so the write side requires Admin ON THAT SITE. Reads are
/// Viewer because the monitoring cards showing them are open to any role.
///
/// Cache reads are asynchronous only because a gated member must return a Task for the
/// interceptor to authorize and audit around it; they are still served from memory.
/// </summary>
[MutatingService(SiteScoped = true)]
public interface IOntMonitorService
{
    /// <summary>Most recent stats for one ONT, or null when it has not been polled.</summary>
    [RequireRole(Roles.Viewer)]
    Task<OntStats?> GetCachedStatsAsync(int ontId);

    /// <summary>Most recent stats for every polled ONT on this site.</summary>
    [RequireRole(Roles.Viewer)]
    Task<IReadOnlyDictionary<int, OntStats>> GetAllCachedStatsAsync();

    /// <summary>Every ONT configuration on this site.</summary>
    [RequireRole(Roles.Viewer)]
    Task<List<OntConfiguration>> GetConfigsAsync();

    /// <summary>ONT configurations not attached to an SFP port.</summary>
    [RequireRole(Roles.Viewer)]
    Task<List<OntConfiguration>> GetStandaloneConfigsAsync();

    /// <summary>Polls one ONT now and caches the result.</summary>
    [RequireRole(Roles.Viewer)]
    Task<OntStats?> PollOntAsync(int ontId);

    /// <summary>Adds or updates an ONT configuration.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.MonitoringSetupChanged, TargetType = "ont")]
    Task SaveOntAsync(OntConfiguration config);

    /// <summary>Removes an ONT configuration.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.MonitoringSetupChanged, TargetType = "ont")]
    Task DeleteOntAsync(int id);

    /// <summary>Pauses or resumes polling of one ONT.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.MonitoringSetupChanged, TargetType = "ont")]
    Task SetOntEnabledAsync(int id, bool enabled);

    /// <summary>Verifies a configuration can reach its ONT, without saving it.</summary>
    [RequireRole(Roles.Admin)]
    Task<(bool Success, string Message)> ProbeAsync(OntConfiguration config);
}
