using FluentAssertions;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

public class SiteContextUrlTests
{
    [Theory]
    [InlineData("/", "/?site=branch")]
    [InlineData("/monitoring", "/monitoring?site=branch")]
    [InlineData("/monitoring?tab=live", "/monitoring?tab=live&site=branch")]
    [InlineData("/monitoring?site=main", "/monitoring?site=branch")]
    [InlineData("/monitoring?site=main&tab=live", "/monitoring?tab=live&site=branch")]
    [InlineData("https://example.com/monitoring?tab=live", "https://example.com/monitoring?tab=live&site=branch")]
    public void WithSiteParam_SetsSiteAndPreservesOtherParams(string url, string expected)
    {
        SiteContextService.WithSiteParam(url, "branch").Should().Be(expected);
    }

    [Fact]
    public void WithSiteParam_PreservesFragment()
    {
        SiteContextService.WithSiteParam("/monitoring?tab=live#section", "branch")
            .Should().Be("/monitoring?tab=live&site=branch#section");
    }

    [Fact]
    public void WithSiteParam_EscapesSlug()
    {
        SiteContextService.WithSiteParam("/", "a b").Should().Be("/?site=a%20b");
    }

    [Theory]
    [InlineData("/monitoring", "/monitoring")]
    [InlineData("/monitoring?site=main", "/monitoring")]
    [InlineData("/monitoring?site=main&tab=live", "/monitoring?tab=live")]
    [InlineData("/monitoring?tab=live&site=main", "/monitoring?tab=live")]
    [InlineData("/monitoring?SITE=main", "/monitoring")]
    [InlineData("/monitoring?site=main#frag", "/monitoring#frag")]
    [InlineData("/monitoring?tab=live", "/monitoring?tab=live")]
    public void RemoveSiteParam_StripsOnlySiteParam(string url, string expected)
    {
        SiteContextService.RemoveSiteParam(url).Should().Be(expected);
    }
}
