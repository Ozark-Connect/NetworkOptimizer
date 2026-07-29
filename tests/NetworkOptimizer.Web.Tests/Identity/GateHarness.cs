using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using NetworkOptimizer.Storage.Models.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.Web.Services;
using NetworkOptimizer.Web.Services.Authorization;
using NetworkOptimizer.Web.Services.Gates;
using NetworkOptimizer.Web.Services.Identity;

namespace NetworkOptimizer.Web.Tests.Identity;

/// <summary>
/// What a bare identity container needs before a <c>[MutatingService]</c> interface can be resolved from
/// it: the proxy generator and interceptor, the authorization services the interceptor asks, and a site
/// context for the site-scoped gates to resolve against. Production gets all of this from Program.cs.
/// </summary>
internal static class GateHarness
{
    /// <summary>Adds the gate plumbing, pinned to <paramref name="currentSite"/> as the ambient site.</summary>
    public static IServiceCollection AddGatePlumbing(this IServiceCollection services, string currentSite = "default")
    {
        // The site-role policies and their handler, but NOT AddNetOptAuthorization: that also registers
        // the real IEffectiveSiteRoleResolver, which would silently replace the stub each test builds
        // its scenario out of.
        // SiteRoleHandler asks whether the install has authentication on at all. A container that has
        // not said otherwise is testing an install that does - TryAdd, so a test wiring the real
        // service keeps it.
        services.TryAddScoped<IAdminAuthService, AuthenticationOnStub>();
        services.AddScoped<IAuthorizationHandler, SiteRoleHandler>();
        services.AddAuthorizationBuilder()
            .AddPolicy(Policies.SiteViewer, p => p.AddRequirements(new SiteRoleRequirement(SiteRole.SiteViewer)))
            .AddPolicy(Policies.SiteOperator, p => p.AddRequirements(new SiteRoleRequirement(SiteRole.SiteOperator)))
            .AddPolicy(Policies.SiteAdmin, p => p.AddRequirements(new SiteRoleRequirement(SiteRole.SiteAdmin)));
        services.AddNetOptGates();
        services.AddScoped(_ => SiteContextFor(currentSite));
        return services;
    }

    /// <summary>
    /// A scope acting as one signed-in account. Gated services refuse a call with no caller, so a test
    /// that resolves one has to say who is making it - which is also what makes "as an Admin" and "as
    /// the account itself" distinguishable in the test, rather than implicit.
    /// </summary>
    public static IServiceScope ScopeAs(this IServiceProvider provider, string userId, params string[] roles)
    {
        var scope = provider.CreateScope();
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId),
            new(ClaimTypes.Name, userId),
        };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));

        scope.ServiceProvider.GetRequiredService<ICallerContext>().SetUser(
            CallerInfo.ForUser(new ClaimsPrincipal(new ClaimsIdentity(claims, "test")),
                sourceIp: null, userAgent: null, correlationId: "test"));
        return scope;
    }

    /// <summary>A scope acting as some global Admin - for setup steps where which admin does not matter.</summary>
    public static IServiceScope AdminScope(this IServiceProvider provider)
        => provider.ScopeAs("test-admin", Roles.Admin);

    private static SiteContextService SiteContextFor(string slug)
    {
        var context = new SiteContextService(new HttpContextAccessor(), new SiteDatabasePaths("/tmp"));
        context.OverrideSite(slug);
        return context;
    }

}

/// <summary>An install with authentication on, so the gates actually authorize rather than wave through.</summary>
internal sealed class AuthenticationOnStub : IAdminAuthService
{
    public Task<bool> IsAuthenticationRequiredAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(true);

    public Task<AdminPasswordSource> GetPasswordSourceAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(AdminPasswordSource.Database);

    public Task<bool> ValidatePasswordAsync(string password, CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    public Task<AdminSettings?> GetAdminSettingsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<AdminSettings?>(null);

    public Task SaveAdminSettingsAsync(string? plainPassword, bool enabled, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task ClearDatabasePasswordAsync(CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task LogStartupConfigurationAsync(CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public PasswordValidationResult ValidateNewPassword(string password, string confirmPassword)
        => throw new NotSupportedException();
}
