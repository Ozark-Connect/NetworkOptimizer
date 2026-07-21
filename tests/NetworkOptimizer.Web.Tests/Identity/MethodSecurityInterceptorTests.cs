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
/// Proves the declarative service-layer gate (design doc 06, gate 9): the interceptor authorizes the
/// ambient caller against the method's gate attributes, skips authz for system callers, throws on an
/// unset caller (no silent bypass), and emits the audit envelope with the execution outcome.
/// </summary>
public class MethodSecurityInterceptorTests
{
    [MutatingService]
    public interface IWidgetService
    {
        [RequireGlobalRoleAttribute(GlobalRoles.Admin)]
        [AuditActionAttribute("widget.changed", Category = AuditCategories.Settings, TargetType = "widget")]
        Task ApplyAsync(string value);
    }

    private sealed class WidgetService : IWidgetService
    {
        private readonly IAuditContext _auditContext;
        public bool Ran { get; private set; }
        public WidgetService(IAuditContext auditContext) => _auditContext = auditContext;

        public Task ApplyAsync(string value)
        {
            Ran = true;
            _auditContext.SetDetails(new { value });
            _auditContext.SetTarget("widget-1", value);
            return Task.CompletedTask;
        }
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
        services.AddNetOptGates();
        services.AddMutatingService<IWidgetService, WidgetService>();
        return services.BuildServiceProvider();
    }

    private static ClaimsPrincipal User(params string[] roles)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "u1"), new(ClaimTypes.Name, "tester") };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    [Fact]
    public async Task Authorized_User_Runs_And_Audits_Success()
    {
        var audit = new CapturingAudit();
        await using var provider = Build(audit);
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<ICallerContext>()
            .SetUser(CallerInfo.ForUser(User(GlobalRoles.Admin), "203.0.113.5", "test-agent", "corr-1"));

        var svc = scope.ServiceProvider.GetRequiredService<IWidgetService>();
        await svc.ApplyAsync("blue");

        audit.Events.Should().ContainSingle();
        var e = audit.Events[0];
        e.Action.Should().Be("widget.changed");
        e.Outcome.Should().Be(AuditOutcomes.Success);
        e.TargetId.Should().Be("widget-1");
        e.ActorName.Should().Be("tester");
    }

    [Fact]
    public async Task Unauthorized_User_IsDenied_And_Audited()
    {
        var audit = new CapturingAudit();
        await using var provider = Build(audit);
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<ICallerContext>()
            .SetUser(CallerInfo.ForUser(User(GlobalRoles.Viewer), null, null, null));

        var svc = scope.ServiceProvider.GetRequiredService<IWidgetService>();

        var act = async () => await svc.ApplyAsync("blue");
        await act.Should().ThrowAsync<AuthorizationDeniedException>();
        audit.Events.Should().ContainSingle(e => e.Outcome == AuditOutcomes.Denied);
    }

    [Fact]
    public async Task SystemScope_SkipsAuthz_And_Runs()
    {
        var audit = new CapturingAudit();
        await using var provider = Build(audit);
        using var scope = provider.CreateScope();
        var caller = scope.ServiceProvider.GetRequiredService<ICallerContext>();

        using (caller.BeginSystemScope("scheduler:test"))
        {
            var svc = scope.ServiceProvider.GetRequiredService<IWidgetService>();
            await svc.ApplyAsync("green"); // no role, but system bypasses authz
        }

        audit.Events.Should().ContainSingle(e => e.Outcome == AuditOutcomes.Success);
        audit.Events[0].ActorName.Should().Be("system:scheduler:test");
    }

    [Fact]
    public async Task UnsetCaller_OnGatedCall_Throws()
    {
        var audit = new CapturingAudit();
        await using var provider = Build(audit);
        using var scope = provider.CreateScope();
        // No caller set at all.
        var svc = scope.ServiceProvider.GetRequiredService<IWidgetService>();

        var act = async () => await svc.ApplyAsync("blue");
        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
