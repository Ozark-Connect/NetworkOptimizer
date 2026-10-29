using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NetworkOptimizer.Storage.Interfaces;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Services;

/// <summary>
/// The site-routed settings read and write the site the caller names, not whatever a fresh scope
/// would resolve from the circuit's connection request (no ?site=, so the cookie or the main site).
/// </summary>
public class SystemSettingsServiceSiteRoutingTests
{
    private const string TabSite = "site-b";
    private readonly List<string> _repositorySites = new();
    private readonly Mock<ISettingsRepository> _repository = new();

    private SystemSettingsService Build()
    {
        var services = new ServiceCollection();
        // The connection request a circuit sees: no ?site=, no site cookie, so it resolves the main site.
        services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = new DefaultHttpContext() });
        services.AddSingleton(new SiteDatabasePaths("/tmp"));
        services.AddScoped<SiteContextService>();
        services.AddScoped(sp =>
        {
            _repositorySites.Add(sp.GetRequiredService<SiteContextService>().Slug);
            return _repository.Object;
        });
        return new SystemSettingsService(services.BuildServiceProvider(), NullLogger<SystemSettingsService>.Instance);
    }

    [Fact]
    public async Task GetAsync_ReadsTheNamedSite()
    {
        _repository.Setup(r => r.GetSystemSettingAsync("audit:dnatExcludedVlans", It.IsAny<CancellationToken>()))
            .ReturnsAsync("100");

        var value = await Build().GetAsync(TabSite, "audit:dnatExcludedVlans");

        value.Should().Be("100");
        _repositorySites.Should().Equal(TabSite);
    }

    [Fact]
    public async Task SetAsync_WritesTheNamedSite()
    {
        await Build().SetAsync(TabSite, "audit:dnatExcludedVlans", "100");

        _repository.Verify(r => r.SaveSystemSettingAsync("audit:dnatExcludedVlans", "100", It.IsAny<CancellationToken>()), Times.Once);
        _repositorySites.Should().Equal(TabSite);
    }

    [Fact]
    public async Task GetIntAsync_ReadsTheNamedSite()
    {
        _repository.Setup(r => r.GetSystemSettingAsync("audit:unusedPortInactivityDays", It.IsAny<CancellationToken>()))
            .ReturnsAsync("15");

        var value = await Build().GetIntAsync(TabSite, "audit:unusedPortInactivityDays", 30);

        value.Should().Be(15);
        _repositorySites.Should().Equal(TabSite);
    }
}
