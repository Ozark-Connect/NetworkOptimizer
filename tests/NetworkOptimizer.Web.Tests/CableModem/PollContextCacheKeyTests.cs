using FluentAssertions;
using NetworkOptimizer.Monitoring.Providers;
using Xunit;

namespace NetworkOptimizer.Web.Tests.CableModem;

/// <summary>
/// Providers are registered as singletons and shared by every site, while each
/// site's database numbers its own configurations from 1. Any provider-side
/// cache - sessions, auth tokens, discovered endpoints, last-seen counters -
/// must therefore key on CacheKey rather than Id.
/// </summary>
public class PollContextCacheKeyTests
{
    [Fact]
    public void CmPollContext_SeparatesTheSameConfigIdOnDifferentSites()
    {
        var siteA = new CmPollContext { Id = 1, SiteSlug = "site-a", Name = "CM", Host = "192.0.2.10" };
        var siteB = new CmPollContext { Id = 1, SiteSlug = "site-b", Name = "CM", Host = "192.0.2.20" };

        siteA.CacheKey.Should().NotBe(siteB.CacheKey);
    }

    [Fact]
    public void OntPollContext_SeparatesTheSameConfigIdOnDifferentSites()
    {
        var siteA = new OntPollContext { Id = 1, SiteSlug = "site-a", Name = "ONT", Host = "192.0.2.10" };
        var siteB = new OntPollContext { Id = 1, SiteSlug = "site-b", Name = "ONT", Host = "192.0.2.20" };

        siteA.CacheKey.Should().NotBe(siteB.CacheKey);
    }

    [Fact]
    public void StarlinkPollContext_SeparatesTheSameConfigIdOnDifferentSites()
    {
        var siteA = new StarlinkPollContext { Id = 1, SiteSlug = "site-a", Name = "Dish", Host = "192.0.2.10" };
        var siteB = new StarlinkPollContext { Id = 1, SiteSlug = "site-b", Name = "Dish", Host = "192.0.2.20" };

        siteA.CacheKey.Should().NotBe(siteB.CacheKey);
    }

    [Fact]
    public void ModemPollContext_SeparatesTheSameConfigIdOnDifferentSites()
    {
        var siteA = new ModemPollContext { Id = 1, SiteSlug = "site-a", Name = "Modem", Host = "192.0.2.10" };
        var siteB = new ModemPollContext { Id = 1, SiteSlug = "site-b", Name = "Modem", Host = "192.0.2.20" };

        siteA.CacheKey.Should().NotBe(siteB.CacheKey);
    }

    [Fact]
    public void CacheKey_IsStableForTheSameSiteAndConfig()
    {
        var first = new CmPollContext { Id = 3, SiteSlug = "site-a", Name = "CM", Host = "192.0.2.10" };
        var second = new CmPollContext { Id = 3, SiteSlug = "site-a", Name = "CM", Host = "192.0.2.99" };

        first.CacheKey.Should().Be(second.CacheKey);
    }
}
