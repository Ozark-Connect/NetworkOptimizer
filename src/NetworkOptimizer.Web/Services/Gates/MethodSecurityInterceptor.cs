using System.Reflection;
using System.Security.Claims;
using Castle.DynamicProxy;
using Microsoft.AspNetCore.Authorization;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Auditing;
using NetworkOptimizer.Web.Services.Authorization;
using NetworkOptimizer.Web.Services.Identity;

namespace NetworkOptimizer.Web.Services.Gates;

/// <summary>
/// The single declarative method-security gate (design doc 06, gate 9). Wraps mutating-service
/// interfaces via Castle DynamicProxy and, per call: authorizes the ambient caller against the
/// method's <see cref="RequireRoleAttribute"/> / <see cref="RequireSiteRoleAttribute"/> (using
/// the same <see cref="SiteRoleHandler"/> as the endpoint/page gates), then emits the
/// <see cref="AuditActionAttribute"/> envelope with the execution outcome. System callers skip authz;
/// an unset caller throws (no silent bypass).
/// </summary>
public sealed class MethodSecurityInterceptor : AsyncInterceptorBase
{
    private readonly ICallerContext _caller;
    private readonly IAuthorizationService _authz;
    private readonly IAuditLogger _audit;
    private readonly IAuditContext _auditContext;
    private readonly IEffectiveSiteRoleResolver _siteRoles;
    private readonly SiteContextService _siteContext;

    public MethodSecurityInterceptor(
        ICallerContext caller,
        IAuthorizationService authz,
        IAuditLogger audit,
        IAuditContext auditContext,
        IEffectiveSiteRoleResolver siteRoles,
        SiteContextService siteContext)
    {
        _caller = caller;
        _authz = authz;
        _audit = audit;
        _auditContext = auditContext;
        _siteRoles = siteRoles;
        _siteContext = siteContext;
    }

    /// <inheritdoc />
    protected override Task InterceptAsync(
        IInvocation invocation, IInvocationProceedInfo proceedInfo,
        Func<IInvocation, IInvocationProceedInfo, Task> proceed)
        => GuardAsync(invocation, () => proceed(invocation, proceedInfo));

    /// <inheritdoc />
    protected override async Task<TResult> InterceptAsync<TResult>(
        IInvocation invocation, IInvocationProceedInfo proceedInfo,
        Func<IInvocation, IInvocationProceedInfo, Task<TResult>> proceed)
    {
        var result = default(TResult)!;
        await GuardAsync(invocation, async () => { result = await proceed(invocation, proceedInfo); });
        return result;
    }

    private async Task GuardAsync(IInvocation invocation, Func<Task> proceed)
    {
        var method = invocation.Method; // the interface method carries the gate attributes
        var requireGlobal = GetGate<RequireRoleAttribute>(method);
        var requireSite = GetGate<RequireSiteRoleAttribute>(method);
        var auditAttr = method.GetCustomAttribute<AuditActionAttribute>();

        var caller = _caller.Require(); // unset caller on a gated call is a loud failure
        var siteSlug = ResolveSiteSlug(invocation, method);

        // System (scheduler/poller) and auth-disabled installs have no principal to authorize; both
        // are still audited, so the action is attributed either way.
        // A service that acts on one site has its required role checked against the caller's role on
        // that site, not their instance-wide role (see MutatingServiceAttribute.SiteScoped).
        var siteScoped = method.DeclaringType?
            .GetCustomAttribute<MutatingServiceAttribute>()?.SiteScoped ?? false;

        if (!caller.IsSystem && !caller.AuthenticationDisabled)
        {
            // A method on a gated interface that declares no gate is refused, not waved through.
            // Authorization used to be skipped entirely in that case, so the "every mutating service
            // is gated" guarantee rested wholly on architecture test A2 - a build-time check over a
            // source file, which cannot see a method added on a branch where tests were not run, and
            // could not see one at all for an interface reached by an anonymous caller (the login
            // circuit runs one). Failing closed makes the runtime agree with the invariant, and A2
            // stays as the check that tells you at build time instead of at 3am.
            // Event add/remove accessors are subscription plumbing rather than an action, and A2
            // exempts them for the same reason.
            if (requireGlobal is null && requireSite is null && !GateReflection.IsEventAccessor(method))
            {
                Deny(caller, auditAttr, siteSlug,
                    $"{method.DeclaringType?.Name}.{method.Name} declares no role gate");
            }

            await AuthorizeAsync(caller, requireGlobal, requireSite, siteSlug, auditAttr, siteScoped);
        }

        if (auditAttr is null)
        {
            await proceed();
            return;
        }

        var outcome = AuditOutcomes.Success;
        try
        {
            await proceed();
        }
        catch
        {
            outcome = AuditOutcomes.Failure;
            throw;
        }
        finally
        {
            EmitAudit(caller, auditAttr, siteSlug, outcome);
        }
    }

    private async Task AuthorizeAsync(
        CallerInfo caller,
        RequireRoleAttribute? requireGlobal,
        RequireSiteRoleAttribute? requireSite,
        string? siteSlug,
        AuditActionAttribute? auditAttr,
        bool siteScoped)
    {
        if (caller.Principal is null)
            Deny(caller, auditAttr, siteSlug, "no principal");

        // No MFA-enrolment confinement here, deliberately - it belongs to the entry gates (see
        // MfaEnrollmentGuard). A gated interface carries its reads alongside its writes, and the
        // interceptor cannot tell one from the other: NavMenu asks ISiteManagementService whether
        // multi-site is on to decide whether to draw one item, and the layout renders on every page.
        // Refusing here therefore took down the shell for a confined session - including the enrolment
        // page it had just been sent to, which is the one page that has to work.

        if (requireGlobal is not null)
        {
            var rank = siteScoped
                ? await SiteRankAsync(caller.Principal!, siteSlug ?? _siteContext.Slug)
                : EffectiveRank(caller.Principal!);

            if (rank < Roles.Rank(requireGlobal.Role))
                Deny(caller, auditAttr, siteSlug,
                    siteScoped
                        ? $"missing {requireGlobal.Role} on this site"
                        : $"missing global role {requireGlobal.Role}");
        }

        if (requireSite is not null)
        {
            if (siteSlug is null)
                throw new InvalidOperationException(
                    "[RequireSiteRole] requires a [SiteSlug]-marked parameter on the method.");

            var result = await _authz.AuthorizeAsync(
                caller.Principal!, siteSlug, Policies.ForSiteRole(requireSite.Minimum));
            if (!result.Succeeded)
                Deny(caller, auditAttr, siteSlug, $"insufficient site role (need {requireSite.Minimum})");
        }
    }

    private void Deny(CallerInfo caller, AuditActionAttribute? auditAttr, string? siteSlug, string reason)
    {
        _audit.Log(AuditEventBuilder.From(
            caller,
            auditAttr?.Category ?? AuditCategories.Action,
            auditAttr?.Action ?? "authz.denied",
            outcome: AuditOutcomes.Denied,
            siteSlug: AuditSiteSlug(siteSlug),
            details: new { reason }));
        throw new AuthorizationDeniedException($"Access denied: {reason}.");
    }

    private void EmitAudit(CallerInfo caller, AuditActionAttribute auditAttr, string? siteSlug, string outcome)
    {
        var (details, targetId, targetName) = _auditContext.Drain();
        _audit.Log(AuditEventBuilder.From(
            caller,
            auditAttr.Category,
            auditAttr.Action,
            outcome: outcome,
            targetType: auditAttr.TargetType,
            targetId: targetId,
            targetName: targetName,
            siteSlug: AuditSiteSlug(siteSlug),
            details: details));
    }

    /// <summary>
    /// The site an audited call acted on: the method's own [SiteSlug] argument when it declares one,
    /// otherwise the site the caller is working in. Only a gated method that takes an explicit slug
    /// used to be stamped, so everything done from the normal UI - which is nearly everything - landed
    /// with no site at all, and filtering the Audit Log by the default site's ID returned nothing while
    /// other sites worked. Authorization deliberately keeps reading the raw argument (a null there is a
    /// missing [SiteSlug] parameter and must still fail loudly); this fallback is for attribution only,
    /// and matches what AuthorizeAsync already does when it ranks a site-scoped call.
    /// </summary>
    private string? AuditSiteSlug(string? siteSlug) => siteSlug ?? _siteContext.Slug;

    /// <summary>
    /// Reads a gate attribute from the invoked member. Property accessors carry the gate on the
    /// property itself (a gated interface's read-only status properties), so fall back to it.
    /// </summary>
    private static TAttribute? GetGate<TAttribute>(MethodInfo method) where TAttribute : Attribute
    {
        var direct = method.GetCustomAttribute<TAttribute>();
        if (direct is not null)
            return direct;

        return GateReflection.DeclaringProperty(method)?.GetCustomAttribute<TAttribute>();
    }

    /// <summary>
    /// Highest global-role rank the principal holds. An authenticated user with no role claim still
    /// ranks as Viewer - "Viewer" is the read tier, which is any authenticated user (design doc 04).
    /// </summary>
    private static int EffectiveRank(ClaimsPrincipal principal)
    {
        var rank = principal.Identity?.IsAuthenticated == true ? Roles.Rank(Roles.Viewer) : 0;
        foreach (var role in Roles.All)
        {
            if (principal.IsInRole(role))
                rank = Math.Max(rank, Roles.Rank(role));
        }
        return rank;
    }

    /// <summary>
    /// The rank a caller holds for a service that acts on one site: their effective role on the site
    /// in context, which <see cref="EffectiveSiteRole"/> already computes from the global role and
    /// every applicable membership. This is what makes a per-site grant mean something - without it a
    /// Site Operator has to be handed an instance-wide Operator role and fenced back in, which is the
    /// opposite of least privilege.
    /// </summary>
    private async Task<int> SiteRankAsync(ClaimsPrincipal principal, string slug)
        => await _siteRoles.GetEffectiveRoleAsync(principal, slug) switch
        {
            SiteRole.SiteAdmin => Roles.Rank(Roles.Admin),
            SiteRole.SiteOperator => Roles.Rank(Roles.Operator),
            SiteRole.SiteViewer => Roles.Rank(Roles.Viewer),
            _ => 0,
        };

    private static string? ResolveSiteSlug(IInvocation invocation, MethodInfo method)
    {
        var parameters = method.GetParameters();
        for (var i = 0; i < parameters.Length; i++)
        {
            if (parameters[i].GetCustomAttribute<SiteSlugAttribute>() is not null)
                return invocation.Arguments.ElementAtOrDefault(i) as string;
        }
        return null;
    }
}
