using System.Reflection;
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
/// method's <see cref="RequireGlobalRoleAttribute"/> / <see cref="RequireSiteRoleAttribute"/> (using
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

    public MethodSecurityInterceptor(
        ICallerContext caller,
        IAuthorizationService authz,
        IAuditLogger audit,
        IAuditContext auditContext)
    {
        _caller = caller;
        _authz = authz;
        _audit = audit;
        _auditContext = auditContext;
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
        var requireGlobal = method.GetCustomAttribute<RequireGlobalRoleAttribute>();
        var requireSite = method.GetCustomAttribute<RequireSiteRoleAttribute>();
        var auditAttr = method.GetCustomAttribute<AuditActionAttribute>();

        var caller = _caller.Require(); // unset caller on a gated call is a loud failure
        var siteSlug = ResolveSiteSlug(invocation, method);

        if (!caller.IsSystem)
            await AuthorizeAsync(caller, requireGlobal, requireSite, siteSlug, auditAttr);

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
        RequireGlobalRoleAttribute? requireGlobal,
        RequireSiteRoleAttribute? requireSite,
        string? siteSlug,
        AuditActionAttribute? auditAttr)
    {
        if (caller.Principal is null)
            Deny(caller, auditAttr, siteSlug, "no principal");

        if (requireGlobal is not null && !caller.Principal!.IsInRole(requireGlobal.Role)
            && !caller.Principal.IsInRole(GlobalRoles.Admin))
        {
            Deny(caller, auditAttr, siteSlug, $"missing global role {requireGlobal.Role}");
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
            siteSlug: siteSlug,
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
            siteSlug: siteSlug,
            details: details));
    }

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
