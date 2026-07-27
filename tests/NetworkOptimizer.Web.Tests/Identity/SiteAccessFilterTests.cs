using System.Security.Claims;
using FluentAssertions;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services;
using NetworkOptimizer.Web.Services.Authorization;
using NetworkOptimizer.Web.Services.Identity;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Identity;

/// <summary>
/// The read-side site filter. The cases that matter most are the ones where it must do nothing:
/// an install with authentication disabled, background/system work, and a scope with no caller all
/// have to keep seeing every site, because that is how the product behaved before roles existed.
/// </summary>
public class SiteAccessFilterTests
{
    private const string SiteA = "site-a";
    private const string SiteB = "site-b";

    private sealed class StubResolver : IEffectiveSiteRoleResolver
    {
        public void Invalidate(string userId) { }

        private readonly HashSet<string> _slugs;
        public StubResolver(params string[] slugs) =>
            _slugs = new HashSet<string>(slugs, StringComparer.OrdinalIgnoreCase);

        public Task<SiteRole?> GetEffectiveRoleAsync(ClaimsPrincipal user, string slug) =>
            Task.FromResult<SiteRole?>(null);

        public Task<IReadOnlySet<string>> GetAuthorizedSlugsAsync(ClaimsPrincipal user) =>
            Task.FromResult<IReadOnlySet<string>>(_slugs);
    }

    private static ClaimsPrincipal User() => new(new ClaimsIdentity(
        new[] { new Claim(ClaimTypes.NameIdentifier, "user-1"), new Claim(ClaimTypes.Name, "operator1") },
        "test"));

    private static SiteAccessFilter For(CallerInfo? caller, params string[] authorizedSlugs)
    {
        var context = new CallerContext();
        if (caller is not null)
            context.SetUser(caller);
        return new SiteAccessFilter(context, new StubResolver(authorizedSlugs));
    }

    [Fact]
    public async Task AnAuthDisabledInstallIsNotFilteredAtAll()
    {
        var filter = For(CallerInfo.LocalNoAuth("203.0.113.5", "test-agent", "corr-1"), SiteA);

        (await filter.AuthorizedSlugsAsync()).Should().BeNull(
            "no principal exists to filter by, and the local operator has always seen every site");
        (await filter.IsAuthorizedAsync(SiteB)).Should().BeTrue();
    }

    [Fact]
    public async Task BackgroundWorkIsNotFiltered()
    {
        var filter = For(CallerInfo.System("scheduler:speedtest"), SiteA);

        (await filter.AuthorizedSlugsAsync()).Should().BeNull();
        (await filter.IsAuthorizedAsync(SiteB)).Should().BeTrue(
            "a scheduled run fans out across every site and has no user to authorize");
    }

    [Fact]
    public async Task AScopeWithNoCallerIsNotFiltered()
    {
        var filter = For(caller: null, SiteA);

        (await filter.AuthorizedSlugsAsync()).Should().BeNull();
    }

    [Fact]
    public async Task AMemberSeesOnlyTheirOwnSites()
    {
        var filter = For(CallerInfo.ForUser(User(), null, null, "corr-1"), SiteB);

        (await filter.IsAuthorizedAsync(SiteB)).Should().BeTrue();
        (await filter.IsAuthorizedAsync(SiteA)).Should().BeFalse(
            "?site= must not open a site the user has no membership for");
    }

    [Fact]
    public async Task ListsAreNarrowedToTheAuthorizedSites()
    {
        var filter = For(CallerInfo.ForUser(User(), null, null, "corr-1"), SiteB);
        var sites = new[] { SiteA, SiteB, "site-c" };

        var visible = await filter.FilterAsync(sites, s => s);

        visible.Should().ContainSingle().Which.Should().Be(SiteB);
    }

    [Fact]
    public async Task AnUnfilteredCallerKeepsTheWholeList()
    {
        var filter = For(CallerInfo.LocalNoAuth(null, null, null), SiteB);
        var sites = new[] { SiteA, SiteB };

        (await filter.FilterAsync(sites, s => s)).Should().BeEquivalentTo(sites);
    }

    [Fact]
    public async Task TheFallbackIsTheDefaultSiteWhenItIsAllowed()
    {
        var filter = For(
            CallerInfo.ForUser(User(), null, null, "corr-1"),
            SiteManagementService.DefaultSiteSlug, SiteB);

        (await filter.FallbackSlugAsync()).Should().Be(SiteManagementService.DefaultSiteSlug);
    }

    [Fact]
    public async Task TheFallbackIsAnAuthorizedSiteWhenTheDefaultIsNot()
    {
        var filter = For(CallerInfo.ForUser(User(), null, null, "corr-1"), SiteB);

        (await filter.FallbackSlugAsync()).Should().Be(SiteB,
            "landing a member on a site they cannot see would defeat the point of the filter");
    }

    [Fact]
    public async Task AMemberOfNothingFallsBackToTheDefaultSite()
    {
        var filter = For(CallerInfo.ForUser(User(), null, null, "corr-1"));

        (await filter.FallbackSlugAsync()).Should().Be(SiteManagementService.DefaultSiteSlug,
            "the page policies then refuse them rather than any site's data being exposed");
    }
}
