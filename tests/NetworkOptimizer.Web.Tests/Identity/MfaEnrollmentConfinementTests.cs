using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Authorization;
using NetworkOptimizer.Web.Services.Identity;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Identity;

/// <summary>
/// A session carrying <see cref="NetOptClaims.MfaSetupPending"/> holds a cookie that must be worth
/// nothing until enrolment finishes. That was enforced by <see cref="GlobalRoleHandler"/> alone, which
/// left a site role as a way round it: the site-scoped policies never asked, so a confined session
/// could still open a site's pages and act there. These pin every entry gate asking the same question.
///
/// The confinement is deliberately NOT enforced in the service-layer interceptor - a gated interface
/// carries its reads alongside its writes, and refusing there took the layout's own reads down with it
/// (NavMenu asks ISiteManagementService whether multi-site is on). Entry gates are where a session-state
/// question belongs; the role gates below it go on answering the role question.
/// </summary>
public class MfaEnrollmentConfinementTests
{
    private static ServiceProvider Build()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<IEffectiveSiteRoleResolver, SiteAdminEverywhereResolver>();
        services.AddGatePlumbing();
        return services.BuildServiceProvider();
    }

    /// <summary>A principal holding every role asked of it, which has not enrolled a second factor.</summary>
    private static ClaimsPrincipal Confined(params string[] roles)
        => Principal(roles, pendingEnrollment: true);

    private static ClaimsPrincipal Enrolled(params string[] roles)
        => Principal(roles, pendingEnrollment: false);

    private static ClaimsPrincipal Principal(string[] roles, bool pendingEnrollment)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "u1"),
            new(ClaimTypes.Name, "tester"),
        };
        if (pendingEnrollment)
            claims.Add(new Claim(NetOptClaims.MfaSetupPending, "1"));
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    [Fact]
    public async Task Confined_SiteAdmin_IsRefusedBySiteScopedPolicy()
    {
        await using var provider = Build();
        using var scope = provider.CreateScope();
        var authz = scope.ServiceProvider.GetRequiredService<IAuthorizationService>();

        var result = await authz.AuthorizeAsync(Confined(), "any-site", Policies.SiteAdmin);

        result.Succeeded.Should().BeFalse(
            "a site role was the way round the confinement the global gates were enforcing");
    }

    [Fact]
    public async Task Confined_SiteViewer_IsRefusedToo()
    {
        await using var provider = Build();
        using var scope = provider.CreateScope();
        var authz = scope.ServiceProvider.GetRequiredService<IAuthorizationService>();

        var result = await authz.AuthorizeAsync(Confined(), "any-site", Policies.SiteViewer);

        result.Succeeded.Should().BeFalse("the confinement is not a question of which site role is held");
    }

    [Fact]
    public async Task Enrolled_SiteAdmin_StillPassesTheSamePolicy()
    {
        await using var provider = Build();
        using var scope = provider.CreateScope();
        var authz = scope.ServiceProvider.GetRequiredService<IAuthorizationService>();

        var result = await authz.AuthorizeAsync(Enrolled(), "any-site", Policies.SiteAdmin);

        result.Succeeded.Should().BeTrue("the refusal must be the enrolment state, not the site role");
    }

    [Fact]
    public void TheGuardReadsTheClaimItDocuments()
    {
        Confined().IsConfinedToMfaEnrollment().Should().BeTrue();
        Enrolled(Roles.Admin).IsConfinedToMfaEnrollment().Should().BeFalse();
    }

    /// <summary>Grants SiteAdmin on every site, so a refusal can only be the enrolment confinement.</summary>
    private sealed class SiteAdminEverywhereResolver : IEffectiveSiteRoleResolver
    {
        public void Invalidate(string userId) { }

        public void InvalidateAll() { }

        public Task<string?> FirstAdministeredSlugAsync(ClaimsPrincipal user)
            => Task.FromResult<string?>("any-site");

        public Task<SiteRole?> GetEffectiveRoleAsync(ClaimsPrincipal user, string slug)
            => Task.FromResult<SiteRole?>(SiteRole.SiteAdmin);

        public Task<IReadOnlySet<string>> GetAuthorizedSlugsAsync(ClaimsPrincipal user)
            => Task.FromResult<IReadOnlySet<string>>(new HashSet<string> { "any-site" });
    }
}
