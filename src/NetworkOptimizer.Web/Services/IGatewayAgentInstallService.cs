using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Gates;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// "Run It for Me": executes the displayed on-gateway agent install/upgrade one-liner on a
/// site's gateway over the existing gateway SSH plumbing. The command runs detached on the
/// gateway and its output is polled back, so the agent restart at the end of the installer
/// cannot kill the run - which is what makes agent-tunnel-routed sites workable.
///
/// Slug-parameterized rather than site-scoped: the Multi-Site settings panel starts runs for
/// the site row being acted on, not the site in context, so authorization must follow the
/// target site - the same shape as <see cref="IAgentEnrollmentService"/>. Run state is per
/// site and lives in the singleton <see cref="GatewayAgentInstallState"/>, so a run survives
/// the circuit that started it.
/// </summary>
[MutatingService]
public interface IGatewayAgentInstallService
{
    /// <summary>
    /// Whether the run button should exist for this site: agent-facing server URL configured,
    /// gateway SSH configured, and a test dial succeeding (through the site's agent tunnel
    /// when it is routed that way). Cached briefly; the button renders only after this
    /// resolves.
    /// </summary>
    [RequireSiteRole(SiteRole.SiteViewer)]
    Task<bool> IsAvailableAsync([SiteSlug] string siteSlug);

    /// <summary>The site's latest run (running or finished), or null when none this process.</summary>
    [RequireSiteRole(SiteRole.SiteViewer)]
    Task<GatewayAgentInstallRun?> GetRunAsync([SiteSlug] string siteSlug);

    /// <summary>
    /// Runs the gateway install one-liner (with the given enrollment token) to completion -
    /// the returned task spans the whole run, so the audit envelope carries the outcome.
    /// <paramref name="onStarted"/> fires with the live run as streaming begins, letting the
    /// caller render the transcript while awaiting. Throws <see cref="InvalidOperationException"/>
    /// while another run is active for the site (refused, not queued).
    /// </summary>
    [RequireSiteRole(SiteRole.SiteAdmin)]
    [AuditAction(AuditActions.AgentGatewayInstallRun, TargetType = "gateway")]
    Task RunInstallAsync([SiteSlug] string siteSlug, string enrollmentToken, Action<GatewayAgentInstallRun>? onStarted = null);

    /// <summary>
    /// Runs the gateway upgrade one-liner (no token - an enrolled agent.json is kept).
    /// Same contract as <see cref="RunInstallAsync"/>.
    /// </summary>
    [RequireSiteRole(SiteRole.SiteAdmin)]
    [AuditAction(AuditActions.AgentGatewayInstallRun, TargetType = "gateway")]
    Task RunUpgradeAsync([SiteSlug] string siteSlug, Action<GatewayAgentInstallRun>? onStarted = null);

    /// <summary>
    /// Cancels the site's running install, if any, by killing the detached process on the
    /// gateway. Safe: the installer is idempotent, so running it again heals a canceled
    /// install.
    /// </summary>
    [RequireSiteRole(SiteRole.SiteAdmin)]
    Task CancelRunAsync([SiteSlug] string siteSlug);
}
