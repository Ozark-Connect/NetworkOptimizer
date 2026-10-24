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
/// The iperf3 defaults are instance-wide, so saving them through <see cref="ISystemSettingsAdmin"/>
/// must need the global Admin role. Administering one site must not change them for every site.
/// </summary>
public class Iperf3SettingsGateTests
{
    private const string ThisSite = "site-a";

    private sealed class RecordingSettingsAdmin : ISystemSettingsAdmin
    {
        public Iperf3Settings? Saved { get; private set; }
        public Task SetAsync(string key, string? value) => Task.CompletedTask;
        public Task SetIntAsync(string key, int value) => Task.CompletedTask;
        public Task SetGlobalAsync(string key, string? value) => Task.CompletedTask;
        public Task SetGlobalIntAsync(string key, int value) => Task.CompletedTask;
        public Task SaveIperf3SettingsAsync(Iperf3Settings settings)
        {
            Saved = settings;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingAudit : IAuditLogger
    {
        public List<AuditEvent> Events { get; } = new();
        public void Log(AuditEvent auditEvent) => Events.Add(auditEvent);
    }

    /// <summary>Reports the given role on <see cref="ThisSite"/> and nothing anywhere else.</summary>
    private sealed class RoleOnThisSite : IEffectiveSiteRoleResolver
    {
        private readonly SiteRole? _role;
        public RoleOnThisSite(SiteRole? role) => _role = role;

        public void Invalidate(string userId) { }

        public void InvalidateAll() { }

        public Task<string?> FirstAdministeredSlugAsync(ClaimsPrincipal user) => Task.FromResult<string?>(null);

        public Task<SiteRole?> GetEffectiveRoleAsync(ClaimsPrincipal user, string slug) =>
            Task.FromResult(string.Equals(slug, ThisSite, StringComparison.OrdinalIgnoreCase) ? _role : null);

        public Task<IReadOnlySet<string>> GetAuthorizedSlugsAsync(ClaimsPrincipal user) =>
            Task.FromResult<IReadOnlySet<string>>(new HashSet<string> { ThisSite });
    }

    private readonly RecordingSettingsAdmin _inner = new();
    private readonly RecordingAudit _audit = new();

    private ServiceProvider Build(SiteRole? roleOnThisSite)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization();
        services.AddSingleton<IAuditLogger>(_audit);
        services.AddScoped<ICallerContext, CallerContext>();
        services.AddScoped<IEffectiveSiteRoleResolver>(_ => new RoleOnThisSite(roleOnThisSite));
        services.AddScoped(_ =>
        {
            var context = new SiteContextService(
                new Microsoft.AspNetCore.Http.HttpContextAccessor(),
                new NetworkOptimizer.Storage.Services.SiteDatabasePaths("/tmp"));
            context.OverrideSite(ThisSite);
            return context;
        });
        services.AddNetOptGates();
        services.AddMutatingService<ISystemSettingsAdmin>(_ => _inner);
        return services.BuildServiceProvider();
    }

    private static IServiceScope ActingScope(ServiceProvider provider, string globalRole)
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "u1"),
                new Claim(ClaimTypes.Name, "user1"),
                new Claim(ClaimTypes.Role, globalRole),
            },
            "test"));
        var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<ICallerContext>()
            .SetUser(CallerInfo.ForUser(user, "203.0.113.5", "test-agent", "corr-1"));
        return scope;
    }

    private static Iperf3Settings Sample() => new()
    {
        GatewayParallelStreams = 4,
        UniFiParallelStreams = 3,
        OtherParallelStreams = 8,
        DurationSeconds = 12
    };

    [Fact]
    public async Task ASiteAdminMayNotSaveTheInstanceWideIperf3Settings()
    {
        await using var provider = Build(SiteRole.SiteAdmin);
        using var scope = ActingScope(provider, Roles.Viewer);

        var act = async () => await scope.ServiceProvider
            .GetRequiredService<ISystemSettingsAdmin>().SaveIperf3SettingsAsync(Sample());

        await act.Should().ThrowAsync<AuthorizationDeniedException>(
            "administering one site must not change the iperf3 defaults for every site");
        _inner.Saved.Should().BeNull();
        _audit.Events.Should().NotContain(e =>
            e.Action == AuditActions.SettingsChanged && e.Outcome == AuditOutcomes.Success);
    }

    [Fact]
    public async Task AGlobalAdminSavesThemAndTheChangeIsAudited()
    {
        await using var provider = Build(roleOnThisSite: null);
        using var scope = ActingScope(provider, Roles.Admin);

        await scope.ServiceProvider
            .GetRequiredService<ISystemSettingsAdmin>().SaveIperf3SettingsAsync(Sample());

        _inner.Saved.Should().NotBeNull();
        _audit.Events.Should().ContainSingle(e =>
                e.Action == AuditActions.SettingsChanged && e.Outcome == AuditOutcomes.Success)
            .Which.TargetType.Should().Be("iperf3_settings");
    }
}
