using System.Security.Claims;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services;
using NetworkOptimizer.Web.Services.Auditing;
using NetworkOptimizer.Web.Services.Authorization;
using NetworkOptimizer.Web.Services.Gates;
using NetworkOptimizer.Web.Services.Identity;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Identity;

/// <summary>
/// Per-site settings are decided by the caller's role on the site in context: a Site Admin saves
/// their own site's, a Site Viewer does not, and neither reaches keys outside the site's own options.
/// </summary>
public class SiteSettingsAdminTests
{
    private const string ThisSite = "site-a";
    private readonly Mock<ISystemSettingsService> _settings = new();
    private readonly List<AuditEvent> _events = new();

    private sealed class RecordingAudit(List<AuditEvent> events) : IAuditLogger
    {
        public void Log(AuditEvent auditEvent) => events.Add(auditEvent);
    }

    /// <summary>Reports the given role on <see cref="ThisSite"/> and nothing anywhere else.</summary>
    private sealed class RoleOnThisSite(SiteRole? role) : IEffectiveSiteRoleResolver
    {
        public void Invalidate(string userId) { }

        public void InvalidateAll() { }

        public Task<string?> FirstAdministeredSlugAsync(ClaimsPrincipal user) => Task.FromResult<string?>(null);

        public Task<SiteRole?> GetEffectiveRoleAsync(ClaimsPrincipal user, string slug) =>
            Task.FromResult(string.Equals(slug, ThisSite, StringComparison.OrdinalIgnoreCase) ? role : null);

        public Task<IReadOnlySet<string>> GetAuthorizedSlugsAsync(ClaimsPrincipal user) =>
            Task.FromResult<IReadOnlySet<string>>(new HashSet<string> { ThisSite });
    }

    private ServiceProvider Build(SiteRole? roleOnThisSite)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization();
        services.AddSingleton<IAuditLogger>(new RecordingAudit(_events));
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
        services.AddMutatingService<ISiteSettingsAdmin>(sp =>
            new SiteSettingsAdmin(_settings.Object, sp.GetRequiredService<SiteContextService>()));
        return services.BuildServiceProvider();
    }

    private static IServiceScope ActingScope(ServiceProvider provider)
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "u1"),
                new Claim(ClaimTypes.Name, "user1"),
                new Claim(ClaimTypes.Role, Roles.Viewer),
            },
            "test"));
        var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<ICallerContext>()
            .SetUser(CallerInfo.ForUser(user, "203.0.113.5", "test-agent", "corr-1"));
        return scope;
    }

    [Fact]
    public async Task ASiteAdminSavesTheirSitesAuditOptions_AsOneAuditedChange()
    {
        await using var provider = Build(SiteRole.SiteAdmin);
        using var scope = ActingScope(provider);

        await scope.ServiceProvider.GetRequiredService<ISiteSettingsAdmin>().SaveAuditOptionsAsync(
            new Dictionary<string, string?> { ["audit:a"] = "true", ["audit:b"] = "5" });

        _settings.Verify(s => s.SetAsync("audit:a", "true"), Times.Once);
        _settings.Verify(s => s.SetAsync("audit:b", "5"), Times.Once);
        _events.Should().ContainSingle(e =>
            e.Action == AuditActions.SettingsChanged && e.Outcome == AuditOutcomes.Success);
    }

    [Fact]
    public async Task ASiteViewerMayNotSaveThem()
    {
        await using var provider = Build(SiteRole.SiteViewer);
        using var scope = ActingScope(provider);

        var act = () => scope.ServiceProvider.GetRequiredService<ISiteSettingsAdmin>()
            .SetThreatCollectionEnabledAsync(false);

        await act.Should().ThrowAsync<AuthorizationDeniedException>();
        _settings.Verify(s => s.SetGlobalAsync(It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task AKeyOutsideTheAuditOptionsIsRefused()
    {
        await using var provider = Build(SiteRole.SiteAdmin);
        using var scope = ActingScope(provider);

        var act = () => scope.ServiceProvider.GetRequiredService<ISiteSettingsAdmin>().SaveAuditOptionsAsync(
            new Dictionary<string, string?> { ["audit:a"] = "true", ["maxmind.license_key"] = "x" });

        await act.Should().ThrowAsync<ArgumentException>(
            "on the main site the site database also holds instance-wide settings");
        _settings.Verify(s => s.SetAsync(It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task ThreatCollectionIsWrittenUnderTheSitesOwnKey()
    {
        await using var provider = Build(SiteRole.SiteAdmin);
        using var scope = ActingScope(provider);

        await scope.ServiceProvider.GetRequiredService<ISiteSettingsAdmin>().SetThreatCollectionEnabledAsync(false);

        _settings.Verify(s => s.SetGlobalAsync($"threats.enabled:{ThisSite}", "false"), Times.Once);
    }
}
