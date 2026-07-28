using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Authorization;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Identity;

/// <summary>
/// An account the site restriction leaves with no grants must not be able to READ a site either. The
/// page policies are global-role checks, and the site fallback parks a caller with no authorized sites
/// on the default one - so a global Operator with no memberships was refused every action while the
/// Dashboard, Monitoring and the speed-test device lists all rendered the main site to them.
/// </summary>
public sealed class NoSiteAccessTests
{
    [Fact]
    public async Task AnAccountWithNoAuthorizedSitesIsRefusedThePages()
    {
        var result = await AuthorizeAsync(Roles.Operator, authorized: new HashSet<string>());

        result.Should().BeFalse(
            "reaching no site means there is nothing on a site-scoped page they are entitled to see");
    }

    [Fact]
    public async Task AnAccountWithOneAuthorizedSiteIsAllowed()
    {
        var result = await AuthorizeAsync(Roles.Operator, authorized: new HashSet<string> { "branch" });

        result.Should().BeTrue("a grant on any site is what the pages are gated on");
    }

    /// <summary>
    /// Null is "no filtering applies" - authentication disabled, background work, or a single-admin
    /// install with no concept of membership. It must never be read as "no sites".
    /// </summary>
    [Fact]
    public async Task NoFilteringIsNotTheSameAsNoAccess()
    {
        var result = await AuthorizeAsync(Roles.Viewer, authorized: null);

        result.Should().BeTrue("an install that does not scope by membership must not be narrowed");
    }

    private static async Task<bool> AuthorizeAsync(string role, IReadOnlySet<string>? authorized)
    {
        var handler = new GlobalRoleHandler(new AuthenticationOnStub(), new FixedSiteAccess(authorized));
        var requirement = new GlobalRoleRequirement(Roles.Viewer);
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, "user-1"), new Claim(ClaimTypes.Role, role) },
            "test"));
        var context = new AuthorizationHandlerContext(new[] { requirement }, user, resource: null);

        await handler.HandleAsync(context);
        return context.HasSucceeded;
    }

    private sealed class FixedSiteAccess : ISiteAccessFilter
    {
        private readonly IReadOnlySet<string>? _authorized;
        public FixedSiteAccess(IReadOnlySet<string>? authorized) => _authorized = authorized;

        public Task<IReadOnlySet<string>?> AuthorizedSlugsAsync() => Task.FromResult(_authorized);

        public Task<bool> IsAuthorizedAsync(string? slug)
            => Task.FromResult(_authorized is null || (slug is not null && _authorized.Contains(slug)));

        public Task<List<T>> FilterAsync<T>(IEnumerable<T> sites, Func<T, string> slugSelector)
            => Task.FromResult(_authorized is null
                ? sites.ToList()
                : sites.Where(s => _authorized.Contains(slugSelector(s))).ToList());

        public Task<string> FallbackSlugAsync() => Task.FromResult("main");
    }
}
