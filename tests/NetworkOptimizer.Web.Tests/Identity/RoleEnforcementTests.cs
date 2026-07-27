using System.Security.Claims;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Auditing;
using NetworkOptimizer.Web.Services.Gates;
using NetworkOptimizer.Web.Services.Identity;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Identity;

/// <summary>
/// Proves the three MVP tiers are a real boundary at the service layer (design docs 06 and 08):
/// reads are any authenticated user, running a speed test is Operator, every other mutation is Admin.
/// The gated interface here mirrors the shape of the product services (a read, a speed-test run, and a
/// config apply on one interface) so the tiers are exercised the way they are actually declared.
/// </summary>
public class RoleEnforcementTests
{
    [MutatingService]
    public interface INetworkService
    {
        [RequireRole(Roles.Viewer)]
        Task<string> GetStatusAsync();

        [RequireRole(Roles.Operator)]
        [AuditAction(AuditActions.SpeedTestRun, TargetType = "wan_speedtest")]
        Task<int> RunSpeedTestAsync();

        [RequireRole(Roles.Admin)]
        [AuditAction(AuditActions.SqmApplied, TargetType = "wan")]
        Task ApplyConfigAsync();
    }

    private sealed class NetworkService : INetworkService
    {
        public Task<string> GetStatusAsync() => Task.FromResult("ok");
        public Task<int> RunSpeedTestAsync() => Task.FromResult(940);
        public Task ApplyConfigAsync() => Task.CompletedTask;
    }

    private sealed class CapturingAudit : IAuditLogger
    {
        public List<AuditEvent> Events { get; } = new();
        public void Log(AuditEvent auditEvent) => Events.Add(auditEvent);
    }

    private static ServiceProvider Build(CapturingAudit audit)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization();
        services.AddSingleton<IAuditLogger>(audit);
        services.AddScoped<ICallerContext, CallerContext>();
        // The gate engine resolves site-scoped services against the caller's role on the current site;
        // these tests exercise instance-wide gating, so a neutral resolver and a default site suffice.
        services.AddScoped<NetworkOptimizer.Web.Services.Authorization.IEffectiveSiteRoleResolver, NeutralSiteRoleResolver>();
        services.AddScoped(_ => DefaultSiteContext());
        services.AddNetOptGates();
        services.AddMutatingService<INetworkService, NetworkService>();
        return services.BuildServiceProvider();
    }

    private static ClaimsPrincipal User(params string[] roles)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "u1"), new(ClaimTypes.Name, "tester") };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private static INetworkService ServiceFor(IServiceScope scope, params string[] roles)
    {
        scope.ServiceProvider.GetRequiredService<ICallerContext>()
            .SetUser(CallerInfo.ForUser(User(roles), "203.0.113.5", "test-agent", "corr-1"));
        return scope.ServiceProvider.GetRequiredService<INetworkService>();
    }

    [Fact]
    public async Task Viewer_CanRead_ButIsDeniedEveryMutation()
    {
        var audit = new CapturingAudit();
        await using var provider = Build(audit);
        using var scope = provider.CreateScope();
        var svc = ServiceFor(scope, Roles.Viewer);

        (await svc.GetStatusAsync()).Should().Be("ok");

        var speedTest = async () => await svc.RunSpeedTestAsync();
        await speedTest.Should().ThrowAsync<AuthorizationDeniedException>();

        var apply = async () => await svc.ApplyConfigAsync();
        await apply.Should().ThrowAsync<AuthorizationDeniedException>();

        audit.Events.Should().HaveCount(2).And.OnlyContain(e => e.Outcome == AuditOutcomes.Denied);
    }

    [Fact]
    public async Task Operator_CanRunASpeedTest_ButIsDeniedAConfigApply()
    {
        var audit = new CapturingAudit();
        await using var provider = Build(audit);
        using var scope = provider.CreateScope();
        var svc = ServiceFor(scope, Roles.Operator);

        (await svc.RunSpeedTestAsync()).Should().Be(940);
        (await svc.GetStatusAsync()).Should().Be("ok"); // Operator outranks Viewer

        var apply = async () => await svc.ApplyConfigAsync();
        await apply.Should().ThrowAsync<AuthorizationDeniedException>();

        audit.Events.Should().Contain(e => e.Action == AuditActions.SpeedTestRun && e.Outcome == AuditOutcomes.Success);
        audit.Events.Should().Contain(e => e.Action == AuditActions.SqmApplied && e.Outcome == AuditOutcomes.Denied);
    }

    [Fact]
    public async Task Admin_CanDoBoth()
    {
        var audit = new CapturingAudit();
        await using var provider = Build(audit);
        using var scope = provider.CreateScope();
        var svc = ServiceFor(scope, Roles.Admin);

        (await svc.RunSpeedTestAsync()).Should().Be(940);
        await svc.ApplyConfigAsync();

        audit.Events.Should().HaveCount(2).And.OnlyContain(e => e.Outcome == AuditOutcomes.Success);
        audit.Events.Should().OnlyContain(e => e.ActorName == "tester");
    }

    [Fact]
    public async Task AuthenticatedUserWithNoRoleClaim_IsTreatedAsViewer()
    {
        var audit = new CapturingAudit();
        await using var provider = Build(audit);
        using var scope = provider.CreateScope();
        var svc = ServiceFor(scope);

        (await svc.GetStatusAsync()).Should().Be("ok");

        var apply = async () => await svc.ApplyConfigAsync();
        await apply.Should().ThrowAsync<AuthorizationDeniedException>();
    }

    [Fact]
    public async Task AuthenticationDisabledInstall_RunsEverythingAndStillAudits()
    {
        var audit = new CapturingAudit();
        await using var provider = Build(audit);
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<ICallerContext>()
            .SetUser(CallerInfo.LocalNoAuth("203.0.113.5", "test-agent", "corr-1"));
        var svc = scope.ServiceProvider.GetRequiredService<INetworkService>();

        await svc.ApplyConfigAsync();
        (await svc.RunSpeedTestAsync()).Should().Be(940);

        audit.Events.Should().HaveCount(2).And.OnlyContain(e => e.Outcome == AuditOutcomes.Success);
        audit.Events.Should().OnlyContain(e => e.ActorName == "local");
    }

    [Fact]
    public async Task SystemScope_RunsMutationsAndAttributesThemToTheScheduler()
    {
        var audit = new CapturingAudit();
        await using var provider = Build(audit);
        using var scope = provider.CreateScope();

        using (SystemScope.Enter(scope.ServiceProvider, "scheduler:speedtest"))
        {
            var svc = scope.ServiceProvider.GetRequiredService<INetworkService>();
            (await svc.RunSpeedTestAsync()).Should().Be(940);
        }

        audit.Events.Should().ContainSingle();
        audit.Events[0].ActorName.Should().Be("system:scheduler:speedtest");
        audit.Events[0].Outcome.Should().Be(AuditOutcomes.Success);
    }

    /// <summary>No site role anywhere: site-scoped gating is covered by SiteScopedGateTests.</summary>
    private sealed class NeutralSiteRoleResolver : NetworkOptimizer.Web.Services.Authorization.IEffectiveSiteRoleResolver
    {
        public void Invalidate(string userId) { }

        public void InvalidateAll() { }

        public Task<bool> IsSiteAdminAnywhereAsync(System.Security.Claims.ClaimsPrincipal user)
            => Task.FromResult(false);

        public Task<SiteRole?> GetEffectiveRoleAsync(System.Security.Claims.ClaimsPrincipal user, string slug)
            => Task.FromResult<SiteRole?>(null);

        public Task<IReadOnlySet<string>> GetAuthorizedSlugsAsync(System.Security.Claims.ClaimsPrincipal user)
            => Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());
    }

    private static NetworkOptimizer.Web.Services.SiteContextService DefaultSiteContext()
    {
        var context = new NetworkOptimizer.Web.Services.SiteContextService(
            new Microsoft.AspNetCore.Http.HttpContextAccessor(),
            new NetworkOptimizer.Storage.Services.SiteDatabasePaths("/tmp"));
        context.OverrideSite(NetworkOptimizer.Web.Services.SiteManagementService.DefaultSiteSlug);
        return context;
    }
}
