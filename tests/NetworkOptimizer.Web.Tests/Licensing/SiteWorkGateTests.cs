using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services;
using NetworkOptimizer.Web.Services.Licensing;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Licensing;

/// <summary>
/// The one gate every per-site background loop consults. These cover the contract the loops rely on
/// rather than the licensing policy itself (which <see cref="LicenseStateServiceTests"/> owns).
/// </summary>
public class SiteWorkGateTests
{
    private sealed class TestDbFactory : IDbContextFactory<NetworkOptimizerDbContext>
    {
        private readonly DbContextOptions<NetworkOptimizerDbContext> _options;
        public TestDbFactory(DbContextOptions<NetworkOptimizerDbContext> options) => _options = options;
        public NetworkOptimizerDbContext CreateDbContext() => new(_options);
    }

    private readonly TestDbFactory _factory;
    private readonly LicenseStateService _service;

    public SiteWorkGateTests()
    {
        var options = new DbContextOptionsBuilder<NetworkOptimizerDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _factory = new TestDbFactory(options);
        _service = new LicenseStateService(
            _factory, TimeProvider.System, new Mock<ILogger<LicenseStateService>>().Object);
    }

    private async Task SeedSitesAsync(params string[] slugs)
    {
        await using var db = _factory.CreateDbContext();
        foreach (var slug in slugs)
        {
            db.Sites.Add(new Site
            {
                Slug = slug,
                Name = slug,
                IsDefault = slug == SiteManagementService.DefaultSiteSlug,
                Enabled = true,
                CreatedAt = DateTime.UtcNow.AddDays(-10),
            });
        }
        await db.SaveChangesAsync();
    }

    [Fact]
    public void Before_the_first_compute_every_site_is_operational()
    {
        // Fail-open: a licensing problem must never take monitoring down harder than policy demands.
        NetworkOptimizer.Core.ISiteWorkGate gate = _service;

        gate.IsSiteOperational("anything").Should().BeTrue();
        gate.IsSiteOperational(SiteManagementService.DefaultSiteSlug).Should().BeTrue();
    }

    [Fact]
    public async Task An_empty_site_key_is_the_default_site_not_an_unknown_slug()
    {
        // Alerts and Threats identify the default site by an empty key while the snapshot keys it by
        // slug. Without normalization the empty string looks unknown, reads as operational forever,
        // and the gate silently never closes on the commonest install shape.
        await SeedSitesAsync(SiteManagementService.DefaultSiteSlug);
        await _service.RecomputeAsync();

        NetworkOptimizer.Core.ISiteWorkGate gate = _service;
        var byEmptyKey = gate.IsSiteOperational("");
        var byNull = gate.IsSiteOperational(null);
        var bySlug = gate.IsSiteOperational(SiteManagementService.DefaultSiteSlug);

        byEmptyKey.Should().Be(bySlug);
        byNull.Should().Be(bySlug);
    }

    [Fact]
    public async Task A_free_tier_install_keeps_every_site_operational()
    {
        // Three or fewer sites with no keys is the free-tier floor, so nothing is gated off.
        await SeedSitesAsync(SiteManagementService.DefaultSiteSlug, "branch", "lake-house");
        await _service.RecomputeAsync();

        NetworkOptimizer.Core.ISiteWorkGate gate = _service;

        gate.IsSiteOperational(SiteManagementService.DefaultSiteSlug).Should().BeTrue();
        gate.IsSiteOperational("branch").Should().BeTrue();
        gate.IsSiteOperational("lake-house").Should().BeTrue();
    }

    [Fact]
    public async Task An_unknown_slug_reads_as_operational()
    {
        await SeedSitesAsync(SiteManagementService.DefaultSiteSlug);
        await _service.RecomputeAsync();

        NetworkOptimizer.Core.ISiteWorkGate gate = _service;

        gate.IsSiteOperational("no-such-site").Should().BeTrue();
    }

    [Fact]
    public void The_licensing_service_is_the_gate_implementation()
    {
        // The loops in Alerts and Threats cannot see the Web project, so they depend on the Core
        // interface. If this ever stops being the same object, those loops would read a different
        // state than the one Web enforces.
        _service.Should().BeAssignableTo<NetworkOptimizer.Core.ISiteWorkGate>();
    }
}
