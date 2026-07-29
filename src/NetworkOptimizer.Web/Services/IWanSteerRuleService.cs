using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Gates;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// The traffic-class rules WAN Steering deploys. These were edited straight from the page against the
/// site database, which left the UI wrapper as the only thing deciding who could change them - so the
/// rules could not be relaxed to Operator without widening a genuinely ungated path.
///
/// Everything here is Site Admin. Steering decides which uplink traffic leaves by, so a wrong rule
/// does not degrade a measurement - it takes connectivity down for whoever matches it, and editing
/// again is not what puts them back. That is infrastructure, whatever its editing cadence looks like.
/// </summary>
[MutatingService(SiteScoped = true)]
public interface IWanSteerRuleService
{
    /// <summary>Every rule for the current site, in evaluation order.</summary>
    [RequireRole(Roles.Viewer)]
    Task<List<WanSteerTrafficClass>> ListAsync();

    /// <summary>
    /// Creates a rule, or updates the editable fields of an existing one. A new rule is appended to
    /// the end of the evaluation order.
    /// </summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.WanSteeringChanged, TargetType = "wan_steer_rule")]
    Task SaveAsync(WanSteerTrafficClass rule);

    /// <summary>Deletes a rule and closes the gap it leaves in the evaluation order.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.WanSteeringChanged, TargetType = "wan_steer_rule")]
    Task DeleteAsync(int ruleId);

    /// <summary>Enables or disables one rule without otherwise editing it.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.WanSteeringChanged, TargetType = "wan_steer_rule")]
    Task SetEnabledAsync(int ruleId, bool enabled);

    /// <summary>
    /// Swaps the evaluation order of two rules. Order is meaningful - the first match wins - so this
    /// changes behaviour and is gated like any other edit.
    /// </summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.WanSteeringChanged, TargetType = "wan_steer_rule")]
    Task SwapSortOrderAsync(int firstRuleId, int secondRuleId);
}
