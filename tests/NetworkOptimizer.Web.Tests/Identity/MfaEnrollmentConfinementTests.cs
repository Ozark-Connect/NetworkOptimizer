using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Auditing;
using NetworkOptimizer.Web.Services.Authorization;
using NetworkOptimizer.Web.Services.Gates;
using NetworkOptimizer.Web.Services.Identity;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Identity;

/// <summary>
/// A session carrying <see cref="NetOptClaims.MfaSetupPending"/> holds a cookie that must be worth
/// nothing until enrolment finishes. That was enforced only by <see cref="GlobalRoleHandler"/>, which
/// left two ways round it: a site role, and the service-layer gate - whose global-role branch reads
/// role claims directly rather than going through that handler. These prove all three gates refuse,
/// and that the account's own self-service survives the confinement (otherwise the way out is shut).
/// </summary>
public class MfaEnrollmentConfinementTests
{
    [MutatingService]
    public interface IThingService
    {
        [RequireRole(Roles.Admin)]
        Task ChangeInstanceAsync();

        [RequireRole(Roles.Viewer)]
        [SelfServiceAction]
        Task ChangeOwnAsync();
    }

    private sealed class ThingService : IThingService
    {
        public Task ChangeInstanceAsync() => Task.CompletedTask;
        public Task ChangeOwnAsync() => Task.CompletedTask;
    }

    private sealed class CapturingAudit : IAuditLogger
    {
        public List<AuditEvent> Events { get; } = new();
        public void Log(AuditEvent auditEvent) => Events.Add(auditEvent);
    }

    private static ServiceProvider Build()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IAuditLogger>(new CapturingAudit());
        services.AddScoped<ICallerContext, CallerContext>();
        services.AddScoped<IEffectiveSiteRoleResolver, SiteAdminEverywhereResolver>();
        services.AddGatePlumbing();
        services.AddMutatingService<IThingService, ThingService>();
        return services.BuildServiceProvider();
    }

    /// <summary>A principal that has every role asked of it, and has not enrolled a second factor.</summary>
    private static ClaimsPrincipal Confined(params string[] roles)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "u1"),
            new(ClaimTypes.Name, "tester"),
            new(NetOptClaims.MfaSetupPending, "1"),
        };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private static ClaimsPrincipal Enrolled(params string[] roles)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "u1"),
            new(ClaimTypes.Name, "tester"),
        };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private static IServiceScope ScopeFor(ServiceProvider provider, ClaimsPrincipal user)
    {
        var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<ICallerContext>()
            .SetUser(CallerInfo.ForUser(user, sourceIp: null, userAgent: null, correlationId: "test"));
        return scope;
    }

    [Fact]
    public async Task Confined_Admin_CannotCallAGatedService()
    {
        await using var provider = Build();
        using var scope = ScopeFor(provider, Confined(Roles.Admin));

        var act = async () => await scope.ServiceProvider.GetRequiredService<IThingService>().ChangeInstanceAsync();

        await act.Should().ThrowAsync<AuthorizationDeniedException>(
            "holding Admin does not release a session that has not enrolled its second factor");
    }

    [Fact]
    public async Task Confined_Caller_KeepsTheirOwnSelfService()
    {
        await using var provider = Build();
        using var scope = ScopeFor(provider, Confined(Roles.Viewer));

        var act = async () => await scope.ServiceProvider.GetRequiredService<IThingService>().ChangeOwnAsync();

        await act.Should().NotThrowAsync(
            "enrolment happens from a live session, so the account can still maintain itself");
    }

    [Fact]
    public async Task Enrolled_Admin_IsUnaffected()
    {
        await using var provider = Build();
        using var scope = ScopeFor(provider, Enrolled(Roles.Admin));

        var act = async () => await scope.ServiceProvider.GetRequiredService<IThingService>().ChangeInstanceAsync();

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Confined_SiteAdmin_IsRefusedBySiteScopedPolicy()
    {
        await using var provider = Build();
        using var scope = ScopeFor(provider, Confined());
        var authz = scope.ServiceProvider.GetRequiredService<IAuthorizationService>();

        var result = await authz.AuthorizeAsync(Confined(), "any-site", Policies.SiteAdmin);

        result.Succeeded.Should().BeFalse(
            "a site role was the other way round the confinement");
    }

    [Fact]
    public async Task Enrolled_SiteAdmin_StillPassesTheSamePolicy()
    {
        await using var provider = Build();
        using var scope = ScopeFor(provider, Enrolled());
        var authz = scope.ServiceProvider.GetRequiredService<IAuthorizationService>();

        var result = await authz.AuthorizeAsync(Enrolled(), "any-site", Policies.SiteAdmin);

        result.Succeeded.Should().BeTrue("the refusal must be the enrolment state, not the site role");
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
