using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Auditing;

namespace NetworkOptimizer.Web.Services.Firmware;

/// <summary>
/// Audits what a rollout does on its own: Autopilot scheduling a plan, a scheduled start, and the
/// health-gate postpone and nothing-left abort. The user's own actions are audited by the service
/// gate; these run from the executor's tick, which no gate sees. Same shape as the user's entries.
/// </summary>
internal static class RolloutAudit
{
    public static void LogSystem(IAuditLogger? audit, string action, string siteSlug, int planId, object? details = null) =>
        audit?.Log(AuditEventBuilder.FromSystem(
            AuditCategories.Action, action,
            targetType: "firmware_rollout", targetId: planId.ToString(), targetName: $"Firmware rollout {planId}",
            siteSlug: siteSlug, details: details));
}
