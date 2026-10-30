using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Services;

/// <summary>The dashboard layout is read, saved and reset on the site in context, never another site's.</summary>
public class DashboardLayoutSiteRoutingTests
{
    private const string TabSite = "site-b";
    private readonly Mock<ISystemSettingsService> _settings = new();

    private DashboardLayoutService Build()
    {
        var site = new SiteContextService(new HttpContextAccessor(), new SiteDatabasePaths("/tmp"));
        site.OverrideSite(TabSite);
        return new DashboardLayoutService(_settings.Object, site, NullLogger<DashboardLayoutService>.Instance);
    }

    [Fact]
    public async Task GetLayoutAsync_ReadsTheSiteInContext()
    {
        await Build().GetLayoutAsync();

        _settings.Verify(s => s.GetAsync(TabSite, SystemSettingKeys.DashboardLayout), Times.Once);
    }

    [Fact]
    public async Task SaveLayoutAsync_WritesTheSiteInContext()
    {
        await Build().SaveLayoutAsync(DashboardLayoutService.GetDefaultLayout());

        _settings.Verify(s => s.SetAsync(TabSite, SystemSettingKeys.DashboardLayout, It.IsAny<string?>()), Times.Once);
    }

    [Fact]
    public async Task ResetLayoutAsync_ClearsTheSiteInContext()
    {
        await Build().ResetLayoutAsync();

        _settings.Verify(s => s.SetAsync(TabSite, SystemSettingKeys.DashboardLayout, null), Times.Once);
    }
}
