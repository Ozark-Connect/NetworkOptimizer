using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Auditing;
using NetworkOptimizer.Web.Services.Gates;
using NetworkOptimizer.Web.Services.Identity;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Identity;

/// <summary>
/// The audit log is the record of who did what across the whole install - actors, source addresses,
/// target names, and the site each action touched. Reading it is closer to reading a credential store
/// than a status page, so it earns a service-tier check rather than relying on the page and the export
/// endpoint that happen to sit in front of it today.
/// </summary>
public sealed class AuditQueryGateTests
{
    private static ServiceProvider Build()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContextFactory<AuthDbContext>(o =>
            o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddScoped<ICallerContext, CallerContext>();
        services.AddSingleton<IAuditLogger>(new NoOpAudit());
        // Non-site-scoped gate, so the interceptor ranks the global role and never asks this - it is
        // here because SiteRoleHandler takes it as a dependency.
        services.AddScoped<NetworkOptimizer.Web.Services.Authorization.IEffectiveSiteRoleResolver, UnusedResolver>();
        services.AddGatePlumbing();
        services.AddMutatingService<IAuditQueryService, AuditQueryService>();
        return services.BuildServiceProvider();
    }

    private sealed class NoOpAudit : IAuditLogger
    {
        public void Log(AuditEvent auditEvent) { }
    }

    private sealed class UnusedResolver : NetworkOptimizer.Web.Services.Authorization.IEffectiveSiteRoleResolver
    {
        public void Invalidate(string userId) { }
        public void InvalidateAll() { }
        public Task<string?> FirstAdministeredSlugAsync(System.Security.Claims.ClaimsPrincipal user)
            => Task.FromResult<string?>(null);
        public Task<SiteRole?> GetEffectiveRoleAsync(System.Security.Claims.ClaimsPrincipal user, string slug)
            => Task.FromResult<SiteRole?>(null);
        public Task<IReadOnlySet<string>> GetAuthorizedSlugsAsync(System.Security.Claims.ClaimsPrincipal user)
            => Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());
    }

    [Fact]
    public async Task An_admin_may_read_the_audit_log()
    {
        await using var provider = Build();
        using var scope = provider.ScopeAs("admin-1", Roles.Admin);

        var act = async () => await scope.ServiceProvider
            .GetRequiredService<IAuditQueryService>().QueryAsync(new AuditFilter());

        await act.Should().NotThrowAsync();
    }

    [Theory]
    [InlineData(Roles.Viewer)]
    [InlineData(Roles.Operator)]
    public async Task Anyone_below_Admin_is_refused(string role)
    {
        await using var provider = Build();
        using var scope = provider.ScopeAs("someone", role);

        var act = async () => await scope.ServiceProvider
            .GetRequiredService<IAuditQueryService>().QueryAsync(new AuditFilter());

        await act.Should().ThrowAsync<AuthorizationDeniedException>();
    }

    [Fact]
    public async Task Export_is_gated_the_same_way_as_the_page_read()
    {
        // The two exports leave the app as files and were reachable through their own endpoint, so a
        // gate that covered only the interactive read would have missed the larger disclosure.
        await using var provider = Build();
        using var scope = provider.ScopeAs("viewer-1", Roles.Viewer);
        var query = scope.ServiceProvider.GetRequiredService<IAuditQueryService>();

        var json = async () => await query.ExportJsonAsync(new AuditFilter());
        var csv = async () => await query.ExportCsvAsync(new AuditFilter());

        await json.Should().ThrowAsync<AuthorizationDeniedException>();
        await csv.Should().ThrowAsync<AuthorizationDeniedException>();
    }

    /// <summary>
    /// The gate has to stay declared on the interface. Losing the attribute puts the reads back
    /// behind nothing but the page and the endpoint, which is where they started.
    /// </summary>
    [Fact]
    public void Every_member_carries_a_role_gate()
    {
        typeof(IAuditQueryService).Should().BeDecoratedWith<MutatingServiceAttribute>();

        foreach (var method in typeof(IAuditQueryService).GetMethods())
        {
            method.Should().BeDecoratedWith<RequireRoleAttribute>(
                $"{method.Name} reads the audit log and must be gated");
        }
    }
}
