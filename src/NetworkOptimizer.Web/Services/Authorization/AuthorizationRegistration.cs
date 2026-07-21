using Microsoft.AspNetCore.Authorization;
using NetworkOptimizer.Storage.Models.Identity;

namespace NetworkOptimizer.Web.Services.Authorization;

/// <summary>
/// Registers the RBAC policy set, the single site-role handler, and the effective-role resolver
/// (design doc 04). Replaces the bare <c>AddAuthorization()</c> call.
/// </summary>
public static class AuthorizationRegistration
{
    public static IServiceCollection AddNetOptAuthorization(this IServiceCollection services)
    {
        services.AddMemoryCache();
        services.AddScoped<IEffectiveSiteRoleResolver, EffectiveSiteRoleResolver>();
        services.AddScoped<IAuthorizationHandler, SiteRoleHandler>();

        services.AddAuthorizationBuilder()
            .AddPolicy(Policies.RequireAdmin, p => p.RequireRole(GlobalRoles.Admin))
            .AddPolicy(Policies.RequireOperator, p => p.RequireRole(GlobalRoles.Admin, GlobalRoles.Operator))
            // "Viewer" is any authenticated user; per-site visibility is enforced by the site-scoped
            // policies and the site-context filter, not by requiring a specific global role here.
            .AddPolicy(Policies.RequireViewer, p => p.RequireAuthenticatedUser())
            .AddPolicy(Policies.SiteViewer, p => p.AddRequirements(new SiteRoleRequirement(SiteRole.SiteViewer)))
            .AddPolicy(Policies.SiteOperator, p => p.AddRequirements(new SiteRoleRequirement(SiteRole.SiteOperator)))
            .AddPolicy(Policies.SiteAdmin, p => p.AddRequirements(new SiteRoleRequirement(SiteRole.SiteAdmin)));

        return services;
    }
}
