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
        // Singleton: the invalidation tokens have to outlive the scoped resolvers whose cache entries
        // are tied to them, and be the same instance for whoever drops them.
        services.AddSingleton<SiteRoleCacheTokens>();
        services.AddScoped<IEffectiveSiteRoleResolver, EffectiveSiteRoleResolver>();
        services.AddScoped<ISiteAccessFilter, SiteAccessFilter>();
        services.AddScoped<IAuthorizationHandler, SiteRoleHandler>();
        services.AddScoped<IAuthorizationHandler, GlobalRoleHandler>();
        services.AddScoped<IAuthorizationHandler, ManageSettingsHandler>();

        // Global policies go through GlobalRoleRequirement rather than RequireRole so the
        // "authentication disabled for this install" case is answered in one handler, and so the role
        // hierarchy (Admin > Operator > Viewer) is applied consistently with the service-layer gate.
        services.AddAuthorizationBuilder()
            .AddPolicy(Policies.RequireAdmin, p => p.AddRequirements(new GlobalRoleRequirement(Roles.Admin)))
            .AddPolicy(Policies.RequireOperator, p => p.AddRequirements(new GlobalRoleRequirement(Roles.Operator)))
            // "Viewer" is any authenticated user; per-site visibility is enforced by the site-scoped
            // policies and the site-context filter, not by requiring a specific global role here.
            .AddPolicy(Policies.RequireViewer, p => p.AddRequirements(new GlobalRoleRequirement(Roles.Viewer)))
            .AddPolicy(Policies.ManageSettings, p => p.AddRequirements(new ManageSettingsRequirement()))
            .AddPolicy(Policies.SiteViewer, p => p.AddRequirements(new SiteRoleRequirement(SiteRole.SiteViewer)))
            .AddPolicy(Policies.SiteOperator, p => p.AddRequirements(new SiteRoleRequirement(SiteRole.SiteOperator)))
            .AddPolicy(Policies.SiteAdmin, p => p.AddRequirements(new SiteRoleRequirement(SiteRole.SiteAdmin)));

        return services;
    }
}
