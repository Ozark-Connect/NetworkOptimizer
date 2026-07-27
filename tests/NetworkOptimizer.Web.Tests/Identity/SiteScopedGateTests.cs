using System.Security.Claims;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services;
using NetworkOptimizer.Web.Services.Auditing;
using NetworkOptimizer.Web.Services.Authorization;
using NetworkOptimizer.Web.Services.Gates;
using NetworkOptimizer.Web.Services.Identity;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Identity;

/// <summary>
/// A service marked <c>SiteScoped</c> checks the role the caller holds ON THE SITE IN CONTEXT rather
/// than their instance-wide role. Without this a Site Operator has to be handed an instance-wide
/// Operator role and fenced back in with a toggle, which is the opposite of least privilege and makes
/// the per-site role picker decorative.
/// </summary>
public class SiteScopedGateTests
{
    private const string ThisSite = "site-a";
    private const string OtherSite = "site-b";

    [MutatingService(SiteScoped = true)]
    public interface ISiteWidgetService
    {
        [RequireRole(Roles.Operator)]
        Task OperateAsync();
    }

    [MutatingService]
    public interface IInstanceWidgetService
    {
        [RequireRole(Roles.Operator)]
        Task OperateAsync();
    }

    private sealed class SiteWidgetService : ISiteWidgetService
    {
        public Task OperateAsync() => Task.CompletedTask;
    }

    private sealed class InstanceWidgetService : IInstanceWidgetService
    {
        public Task OperateAsync() => Task.CompletedTask;
    }

    private sealed class NoOpAudit : IAuditLogger
    {
        public void Log(AuditEvent auditEvent) { }
    }

    /// <summary>Reports the given role on <see cref="ThisSite"/> and nothing anywhere else.</summary>
    private sealed class RoleOnThisSite : IEffectiveSiteRoleResolver
    {
        public void Invalidate(string userId) { }

        private readonly SiteRole? _role;
        public RoleOnThisSite(SiteRole? role) => _role = role;

        public Task<SiteRole?> GetEffectiveRoleAsync(ClaimsPrincipal user, string slug) =>
            Task.FromResult(string.Equals(slug, ThisSite, StringComparison.OrdinalIgnoreCase) ? _role : null);

        public Task<IReadOnlySet<string>> GetAuthorizedSlugsAsync(ClaimsPrincipal user) =>
            Task.FromResult<IReadOnlySet<string>>(new HashSet<string> { ThisSite });
    }

    private static ServiceProvider Build(SiteRole? roleOnThisSite, string currentSite)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization();
        services.AddSingleton<IAuditLogger>(new NoOpAudit());
        services.AddScoped<ICallerContext, CallerContext>();
        services.AddScoped<IEffectiveSiteRoleResolver>(_ => new RoleOnThisSite(roleOnThisSite));
        services.AddScoped(_ => SiteContextFor(currentSite));
        services.AddNetOptGates();
        services.AddMutatingService<ISiteWidgetService, SiteWidgetService>();
        services.AddMutatingService<IInstanceWidgetService, InstanceWidgetService>();
        return services.BuildServiceProvider();
    }

    /// <summary>A site context pinned to one slug, standing in for the request/circuit's site.</summary>
    private static SiteContextService SiteContextFor(string slug)
    {
        var context = new SiteContextService(
            new Microsoft.AspNetCore.Http.HttpContextAccessor(),
            new NetworkOptimizer.Storage.Services.SiteDatabasePaths("/tmp"));
        context.OverrideSite(slug);
        return context;
    }

    /// <summary>A signed-in account holding no global role above Viewer.</summary>
    private static ClaimsPrincipal SiteUser() => new(new ClaimsIdentity(
        new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "u1"),
            new Claim(ClaimTypes.Name, "operator1"),
            new Claim(ClaimTypes.Role, Roles.Viewer),
        },
        "test"));

    private static IServiceScope ActingScope(ServiceProvider provider)
    {
        var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<ICallerContext>()
            .SetUser(CallerInfo.ForUser(SiteUser(), "203.0.113.5", "test-agent", "corr-1"));
        return scope;
    }

    [Fact]
    public async Task ASiteOperatorMayOperateTheirOwnSite()
    {
        await using var provider = Build(SiteRole.SiteOperator, ThisSite);
        using var scope = ActingScope(provider);

        var act = async () => await scope.ServiceProvider
            .GetRequiredService<ISiteWidgetService>().OperateAsync();

        await act.Should().NotThrowAsync(
            "the grant says Site Operator here, so it has to mean operator capability here");
    }

    [Fact]
    public async Task TheSameOperatorMayNotOperateAnotherSite()
    {
        await using var provider = Build(SiteRole.SiteOperator, OtherSite);
        using var scope = ActingScope(provider);

        var act = async () => await scope.ServiceProvider
            .GetRequiredService<ISiteWidgetService>().OperateAsync();

        await act.Should().ThrowAsync<AuthorizationDeniedException>(
            "capability must not follow the caller to a site they were never granted");
    }

    [Fact]
    public async Task ASiteViewerMayNotOperate()
    {
        await using var provider = Build(SiteRole.SiteViewer, ThisSite);
        using var scope = ActingScope(provider);

        var act = async () => await scope.ServiceProvider
            .GetRequiredService<ISiteWidgetService>().OperateAsync();

        await act.Should().ThrowAsync<AuthorizationDeniedException>();
    }

    [Fact]
    public async Task ASiteAdminOutranksOperator()
    {
        await using var provider = Build(SiteRole.SiteAdmin, ThisSite);
        using var scope = ActingScope(provider);

        var act = async () => await scope.ServiceProvider
            .GetRequiredService<ISiteWidgetService>().OperateAsync();

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task AnInstanceWideServiceStillIgnoresSiteRole()
    {
        await using var provider = Build(SiteRole.SiteAdmin, ThisSite);
        using var scope = ActingScope(provider);

        var act = async () => await scope.ServiceProvider
            .GetRequiredService<IInstanceWidgetService>().OperateAsync();

        await act.Should().ThrowAsync<AuthorizationDeniedException>(
            "administering one site must never confer instance-wide capability");
    }
}
